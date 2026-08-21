$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$controlExecutable = Join-Path $repositoryRoot "services\control\src\QiongTu.Control\bin\Debug\net10.0\win-x64\QiongTu.Control.exe"
$electronExecutable = Join-Path $repositoryRoot "node_modules\electron\dist\electron.exe"
$desktopDirectory = Join-Path $repositoryRoot "apps\desktop"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("qiongtu-control-smoke-" + [Guid]::NewGuid().ToString("N"))
$discoveryFile = Join-Path $testRoot "runtime\control.json"
$controlProcess = $null
$workerProcessId = $null

function Invoke-QiongTuControlRequest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PipeName,
        [Parameter(Mandatory = $true)]
        [string]$Method,
        [AllowNull()]
        [object]$Parameters
    )

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe, [System.Text.UTF8Encoding]::new($false), $false, 1024, $true)
        try {
            $request = @{
                apiVersion = "qiongtu.control-api.v1"
                requestId = [Guid]::NewGuid().ToString("N")
                method = $Method
                parameters = $Parameters
            }
            $writer.WriteLine(($request | ConvertTo-Json -Depth 8 -Compress))
            $writer.Flush()
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) {
                throw "Control pipe closed without a response."
            }

            return $line | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
            $writer.Dispose()
        }
    }
    finally {
        $pipe.Dispose()
    }
}

function Invoke-ElectronControlSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DiscoveryPath
    )

    $previousDiscovery = $env:QIONGTU_CONTROL_DISCOVERY_FILE
    $standardOutput = Join-Path $testRoot ("electron-" + [Guid]::NewGuid().ToString("N") + ".stdout.log")
    $standardError = Join-Path $testRoot ("electron-" + [Guid]::NewGuid().ToString("N") + ".stderr.log")
    try {
        $env:QIONGTU_CONTROL_DISCOVERY_FILE = $DiscoveryPath
        $electronProcess = Start-Process -FilePath $electronExecutable `
            -ArgumentList @($desktopDirectory, "--control-smoke") `
            -WindowStyle Hidden `
            -RedirectStandardOutput $standardOutput `
            -RedirectStandardError $standardError `
            -PassThru `
            -Wait
        $output = @(
            Get-Content -LiteralPath $standardOutput -ErrorAction SilentlyContinue
            Get-Content -LiteralPath $standardError -ErrorAction SilentlyContinue
        )
        if ($electronProcess.ExitCode -ne 0) {
            throw "Electron control smoke failed with exit code $($electronProcess.ExitCode). Output: $output"
        }

        $result = $output | Where-Object { $_ -match '^\{"status":"ok","mode":"control-connection"' } | Select-Object -Last 1
        if ($null -eq $result) {
            throw "Electron control smoke did not produce the expected success record. Output: $output"
        }
    }
    finally {
        $env:QIONGTU_CONTROL_DISCOVERY_FILE = $previousDiscovery
    }
}

if (-not (Test-Path -LiteralPath $controlExecutable -PathType Leaf)) {
    throw "Build QiongTu.Control Debug before running the lifecycle smoke test."
}
if (-not (Test-Path -LiteralPath $electronExecutable -PathType Leaf)) {
    throw "Install the pinned Electron workspace dependencies before running the lifecycle smoke test."
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $controlProcess = Start-Process -FilePath $controlExecutable `
        -ArgumentList @("--runtime-dir", $testRoot, "--enable-lifecycle-probe") `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $discoveryFile -PathType Leaf)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Control discovery file was not published within 15 seconds."
        }
        Start-Sleep -Milliseconds 100
    }

    $discovery = Get-Content -Raw -LiteralPath $discoveryFile | ConvertFrom-Json
    if ($discovery.processId -ne $controlProcess.Id -or $discovery.endpointKind -ne "named-pipe") {
        throw "Control discovery identity did not match the launched process."
    }

    $startedWorker = Invoke-QiongTuControlRequest `
        -PipeName $discovery.pipeName `
        -Method "worker.start" `
        -Parameters @{ workerType = "lifecycle-probe" }
    if (-not $startedWorker.ok) {
        throw "Lifecycle probe worker did not start: $($startedWorker.error.code)"
    }
    $workerId = $startedWorker.result.workerId
    $workerProcessId = [int]$startedWorker.result.processId

    Invoke-ElectronControlSmoke -DiscoveryPath $discoveryFile
    Invoke-ElectronControlSmoke -DiscoveryPath $discoveryFile

    $workerList = Invoke-QiongTuControlRequest `
        -PipeName $discovery.pipeName `
        -Method "worker.list" `
        -Parameters $null
    $sameWorker = $workerList.result | Where-Object { $_.workerId -eq $workerId } | Select-Object -First 1
    if ($null -eq $sameWorker `
        -or [int]$sameWorker.processId -ne $workerProcessId `
        -or $sameWorker.state -ne "running" `
        -or $controlProcess.HasExited) {
        throw "Electron restart changed or terminated the control/worker lifecycle."
    }

    $cancelled = Invoke-QiongTuControlRequest `
        -PipeName $discovery.pipeName `
        -Method "worker.cancel" `
        -Parameters @{ workerId = $workerId }
    if (-not $cancelled.ok -or $cancelled.result.state -ne "cancelled") {
        throw "Lifecycle probe worker did not reach the cancelled state."
    }
    $workerProcessId = $null

    $stopped = Invoke-QiongTuControlRequest `
        -PipeName $discovery.pipeName `
        -Method "control.stop-if-idle" `
        -Parameters $null
    if (-not $stopped.ok -or -not $stopped.result.accepted) {
        throw "Control process did not accept the idle stop request."
    }
    if (-not $controlProcess.WaitForExit(5000)) {
        throw "Control process did not exit after the idle stop request."
    }

    [ordered]@{
        status = "ok"
        electronRestarts = 2
        controlProcessPreserved = $true
        workerProcessPreserved = $true
        workerCancelled = $true
        controlStoppedWhenIdle = $true
    } | ConvertTo-Json -Compress
}
finally {
    if ($null -ne $workerProcessId) {
        $workerProcess = Get-Process -Id $workerProcessId -ErrorAction SilentlyContinue
        if ($null -ne $workerProcess) {
            Stop-Process -Id $workerProcessId -Force
        }
    }
    if ($null -ne $controlProcess -and -not $controlProcess.HasExited) {
        Stop-Process -Id $controlProcess.Id -Force
        $controlProcess.WaitForExit()
    }

    $expectedPrefix = Join-Path ([System.IO.Path]::GetTempPath()) "qiongtu-control-smoke-"
    if ($testRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
