[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'

function Test-NameMatchesAnyPattern {
    param([string]$Name, [string[]]$Patterns)
    foreach ($pattern in $Patterns) {
        if ($Name -like $pattern) {
            return $true
        }
    }

    return $false
}

function Start-RedirectedProcess {
    param([string]$FileName, [string]$Arguments, [switch]$RedirectInput)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $RedirectInput.IsPresent
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'A required self-contained process could not start.'
    }

    [pscustomobject]@{
        Process = $process
        StandardOutput = $process.StandardOutput.ReadToEndAsync()
        StandardError = $process.StandardError.ReadToEndAsync()
    }
}

function Stop-ProcessIfRunning {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
            $null = $Process.WaitForExit(5000)
        }
    }
    catch [System.InvalidOperationException] {
        # The process exited between the state check and termination request.
    }
}

function Read-LineWithTimeout {
    param([System.IO.StreamReader]$Reader, [string]$FailureMessage)
    $read = $Reader.ReadLineAsync()
    if (-not $read.Wait(10000)) {
        throw $FailureMessage
    }

    $read.GetAwaiter().GetResult()
}

function Write-LineWithTimeout {
    param([System.IO.StreamWriter]$Writer, [string]$Value, [string]$FailureMessage)
    $write = $Writer.WriteLineAsync($Value)
    if (-not $write.Wait(10000)) {
        throw $FailureMessage
    }

    $null = $write.GetAwaiter().GetResult()
    $flush = $Writer.FlushAsync()
    if (-not $flush.Wait(10000)) {
        throw $FailureMessage
    }

    $null = $flush.GetAwaiter().GetResult()
}

$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$controlExecutable = Join-Path $publishRoot 'QiongTu.Control.exe'
$probeExecutable = Join-Path $publishRoot 'image-probe\QiongTu.ImageProbe.exe'
if (-not (Test-Path -LiteralPath $controlExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $probeExecutable -PathType Leaf)) {
    throw 'The self-contained control and image-probe executables are required.'
}

$forbiddenFilePatterns = @(
    'libdirp.*',
    'libv_*',
    'microia_release_*',
    'microjpeg_release_*',
    'microta_release_*',
    'dirp_*.h',
    'dji_irp*',
    'dji_ircm*',
    'libexif-12.dll',
    'libexif.dll',
    'libgcc_s_dw2-1.dll',
    'libiconv-2.dll',
    'libintl-8.dll',
    'libwinpthread-1.dll'
)
$forbiddenDirectoryPatterns = @('tsdk-core', 'dji_thermal_sdk*', 'dataset', 'datasets', 'sample', 'samples')
$forbidden = Get-ChildItem -LiteralPath $publishRoot -Recurse -Force |
    Where-Object {
        $name = $_.Name.ToLowerInvariant()
        if ($_.PSIsContainer) {
            Test-NameMatchesAnyPattern -Name $name -Patterns $forbiddenDirectoryPatterns
        }
        else {
            Test-NameMatchesAnyPattern -Name $name -Patterns $forbiddenFilePatterns
        }
    } |
    Select-Object -First 1
if ($null -ne $forbidden) {
    throw "The first-release package contains a forbidden DJI TSDK artifact: $($forbidden.Name)"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("qiongtu-package-probe-" + [Guid]::NewGuid().ToString('N'))
$controlRun = $null
$controlPipe = $null
$controlReader = $null
$controlWriter = $null
$probeRun = $null
$positioningProbeRun = $null
try {
    $controlRuntime = Join-Path $temporaryRoot 'control-runtime'
    $controlArguments = "--runtime-dir `"$controlRuntime`""
    $controlRun = Start-RedirectedProcess -FileName $controlExecutable -Arguments $controlArguments
    $discoveryPath = Join-Path $controlRuntime 'runtime\control.json'
    $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $discoveryPath -PathType Leaf)) {
        if ($controlRun.Process.HasExited) {
            throw 'The self-contained control service exited before product initialization completed.'
        }
        if ([DateTimeOffset]::UtcNow -ge $startupDeadline) {
            throw 'The self-contained control service product startup timed out.'
        }
        Start-Sleep -Milliseconds 50
    }

    $discovery = Get-Content -LiteralPath $discoveryPath -Raw | ConvertFrom-Json
    if ($discovery.apiVersion -ne 'qiongtu.control-api.v1' -or
        $discovery.endpointKind -ne 'named-pipe' -or
        $discovery.processId -ne $controlRun.Process.Id -or
        [string]::IsNullOrWhiteSpace($discovery.pipeName)) {
        throw 'The self-contained control service published invalid runtime discovery.'
    }

    $controlPipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        [string]$discovery.pipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $controlPipe.Connect(5000)
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $controlReader = [System.IO.StreamReader]::new($controlPipe, $utf8, $false, 1024, $true)
    $controlWriter = [System.IO.StreamWriter]::new($controlPipe, $utf8, 1024, $true)
    $controlWriter.AutoFlush = $true

    $statusRequest = [ordered]@{
        apiVersion = 'qiongtu.control-api.v1'
        requestId = 'package-acceptance-status'
        method = 'control.status'
        parameters = $null
    } | ConvertTo-Json -Compress
    Write-LineWithTimeout -Writer $controlWriter -Value $statusRequest -FailureMessage 'The control status request timed out.'
    $statusLine = Read-LineWithTimeout -Reader $controlReader -FailureMessage 'The control status response timed out.'
    $status = $statusLine | ConvertFrom-Json
    if (-not $status.ok -or $status.result.processId -ne $controlRun.Process.Id) {
        throw 'The self-contained control service did not return a valid product status.'
    }

    $stopRequest = [ordered]@{
        apiVersion = 'qiongtu.control-api.v1'
        requestId = 'package-acceptance-stop'
        method = 'control.stop-if-idle'
        parameters = $null
    } | ConvertTo-Json -Compress
    Write-LineWithTimeout -Writer $controlWriter -Value $stopRequest -FailureMessage 'The control stop request timed out.'
    $stopLine = Read-LineWithTimeout -Reader $controlReader -FailureMessage 'The control stop response timed out.'
    $stop = $stopLine | ConvertFrom-Json
    if (-not $stop.ok -or -not $stop.result.accepted) {
        throw 'The self-contained control service did not accept an idle product shutdown.'
    }

    $controlWriter.Dispose()
    $controlWriter = $null
    $controlReader.Dispose()
    $controlReader = $null
    $controlPipe.Dispose()
    $controlPipe = $null
    if (-not $controlRun.Process.WaitForExit(20000)) {
        Stop-ProcessIfRunning -Process $controlRun.Process
        throw 'The self-contained control service shutdown timed out.'
    }

    $controlRun.Process.WaitForExit()
    $null = $controlRun.StandardOutput.GetAwaiter().GetResult()
    $null = $controlRun.StandardError.GetAwaiter().GetResult()
    if ($controlRun.Process.ExitCode -ne 0) {
        throw 'The self-contained control service returned a failure exit code.'
    }

    $formalRoot = Join-Path $temporaryRoot 'published'
    $jpeg = [Convert]::FromBase64String('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAADAAQDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAHCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/ADoDFU3/2Q==')
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha.ComputeHash($jpeg))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    $objectKey = "sha256/$($hash.Substring(0, 2))/$hash"
    $objectPath = Join-Path $formalRoot ($objectKey -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $objectPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($objectPath, $jpeg)
    $header = [ordered]@{
        schemaVersion = 'qiongtu.image-probe.cas-image.v1'
        profile = 'cas-image.v1'
        objectKind = 'source_image'
        formalObjectRoot = $formalRoot
        objectKey = $objectKey
        expectedSha256 = $hash
        expectedByteLength = $jpeg.Length
    } | ConvertTo-Json -Compress

    $probeRun = Start-RedirectedProcess -FileName $probeExecutable -Arguments '--stdio' -RedirectInput
    $probeRun.Process.StandardInput.WriteLine($header)
    $probeRun.Process.StandardInput.Close()
    if (-not $probeRun.Process.WaitForExit(20000)) {
        Stop-ProcessIfRunning -Process $probeRun.Process
        throw 'The self-contained image probe timed out.'
    }

    $probeRun.Process.WaitForExit()
    $output = $probeRun.StandardOutput.GetAwaiter().GetResult()
    $null = $probeRun.StandardError.GetAwaiter().GetResult()
    if ($probeRun.Process.ExitCode -ne 0) {
        throw 'The self-contained image probe returned a failure exit code.'
    }

    $result = $output | ConvertFrom-Json
    Write-Verbose $output
    if ($result.status -ne 'completed' -or
        $result.container -ne 'jpeg' -or
        $result.frames.Count -ne 1 -or
        $result.privacy.pathsIncluded -or
        $result.privacy.contentHashesIncluded -or
        $result.privacy.objectKeysIncluded) {
        throw 'The self-contained image probe returned an invalid public synthetic result.'
    }

    $mrk = [System.Text.Encoding]::UTF8.GetBytes("1`t12345.123456`t[2200]`t1,N`t-2,E`t3,V`t30.12345678,Lat`t120.12345678,Lon`t100.123,Ellh`t0.001000,`t0.001000,`t0.002000`t50,Q`r`n")
    $mrkSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $mrkHash = ([BitConverter]::ToString($mrkSha.ComputeHash($mrk))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $mrkSha.Dispose()
    }
    $mrkObjectKey = "sha256/$($mrkHash.Substring(0, 2))/$mrkHash"
    $mrkObjectPath = Join-Path $formalRoot ($mrkObjectKey -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $mrkObjectPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($mrkObjectPath, $mrk)
    $positioningHeader = [ordered]@{
        schemaVersion = 'qiongtu.image-probe.cas-positioning-aux.v1'
        profile = 'cas-positioning-aux.v1'
        objectKind = 'positioning_aux'
        auxiliaryType = 'mrk'
        associationItemCount = 1
        formalObjectRoot = $formalRoot
        objectKey = $mrkObjectKey
        expectedSha256 = $mrkHash
        expectedByteLength = $mrk.Length
    } | ConvertTo-Json -Compress

    $positioningProbeRun = Start-RedirectedProcess -FileName $probeExecutable -Arguments '--stdio' -RedirectInput
    $positioningProbeRun.Process.StandardInput.WriteLine($positioningHeader)
    $positioningProbeRun.Process.StandardInput.Close()
    if (-not $positioningProbeRun.Process.WaitForExit(20000)) {
        Stop-ProcessIfRunning -Process $positioningProbeRun.Process
        throw 'The self-contained positioning auxiliary probe timed out.'
    }

    $positioningProbeRun.Process.WaitForExit()
    $positioningOutput = $positioningProbeRun.StandardOutput.GetAwaiter().GetResult()
    $null = $positioningProbeRun.StandardError.GetAwaiter().GetResult()
    if ($positioningProbeRun.Process.ExitCode -ne 0) {
        throw 'The self-contained positioning auxiliary probe returned a failure exit code.'
    }

    $positioningResult = $positioningOutput | ConvertFrom-Json
    Write-Verbose $positioningOutput
    if ($positioningResult.parseState -ne 'parsed' -or
        $positioningResult.qualityState -ne 'passed' -or
        $positioningResult.sequenceState -ne 'contiguous' -or
        $positioningResult.coverageState -ne 'complete' -or
        $positioningResult.privacy.pathsIncluded -or
        $positioningResult.privacy.contentHashesIncluded -or
        $positioningResult.privacy.objectKeysIncluded -or
        $positioningResult.privacy.rawMetadataIncluded -or
        $positioningResult.privacy.coordinatesIncluded) {
        throw 'The self-contained positioning auxiliary probe returned an invalid public synthetic result.'
    }

    [ordered]@{
        schemaVersion = 'qiongtu.package-image-probe-acceptance.v1'
        status = 'passed'
        controlProductBoot = 'passed'
        publicSyntheticProbe = 'passed'
        publicSyntheticPositioningAuxProbe = 'passed'
        djiThermalSdkIncluded = $false
    } | ConvertTo-Json -Compress
}
finally {
    if ($null -ne $controlWriter) { $controlWriter.Dispose() }
    if ($null -ne $controlReader) { $controlReader.Dispose() }
    if ($null -ne $controlPipe) { $controlPipe.Dispose() }
    if ($null -ne $controlRun) {
        Stop-ProcessIfRunning -Process $controlRun.Process
        $controlRun.Process.Dispose()
    }
    if ($null -ne $probeRun) {
        Stop-ProcessIfRunning -Process $probeRun.Process
        $probeRun.Process.Dispose()
    }
    if ($null -ne $positioningProbeRun) {
        Stop-ProcessIfRunning -Process $positioningProbeRun.Process
        $positioningProbeRun.Process.Dispose()
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
