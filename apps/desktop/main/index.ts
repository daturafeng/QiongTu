import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { app, BrowserWindow, ipcMain } from "electron";
import {
  CONTROL_STATUS_CHANGED_CHANNEL,
  CONTROL_STATUS_CHANNEL,
  CONTROL_API_VERSION,
  type ControlConnectionStatus
} from "@qiongtu/contracts";
import {
  ControlProcessManager,
  getControlDiscoveryFilePath
} from "./control-manager.js";
import { createLauncherReadinessReporter } from "./launcher-readiness.js";
import { createWindowOptions, isSecureWindowOptions } from "./window-options.js";

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const isSmokeRun = process.argv.includes("--smoke");
const isControlSmokeRun = process.argv.includes("--control-smoke");
const isLauncherReadinessSmokeRun = process.argv.includes("--launcher-readiness-smoke");
const repositoryRoot = join(currentDirectory, "..", "..", "..", "..");
const smokeUserDataDirectory = isSmokeRun || isControlSmokeRun || isLauncherReadinessSmokeRun
  ? mkdtempSync(join(tmpdir(), "qiongtu-electron-smoke-"))
  : undefined;
if (smokeUserDataDirectory !== undefined) {
  app.setPath("userData", smokeUserDataDirectory);
}
const controlDiscoveryOverride = isControlSmokeRun
  ? process.env.QIONGTU_CONTROL_DISCOVERY_FILE
  : undefined;
const launcherReadiness = createLauncherReadinessReporter();
void launcherReadiness.report("main.started");

const controlManager = new ControlProcessManager({
  discoveryFilePath: controlDiscoveryOverride
    ?? getControlDiscoveryFilePath(process.env.LOCALAPPDATA ?? app.getPath("userData")),
  executableCandidates: [
    join(process.resourcesPath, "QiongTu.Control", "QiongTu.Control.exe"),
    join(repositoryRoot, "services", "control", "src", "QiongTu.Control", "bin", "Release", "net10.0-windows", "win-x64", "publish", "QiongTu.Control.exe"),
    join(repositoryRoot, "services", "control", "src", "QiongTu.Control", "bin", "Release", "net10.0", "win-x64", "publish", "QiongTu.Control.exe"),
    join(repositoryRoot, "services", "control", "src", "QiongTu.Control", "bin", "Debug", "net10.0-windows", "QiongTu.Control.exe"),
    join(repositoryRoot, "services", "control", "src", "QiongTu.Control", "bin", "Debug", "net10.0", "win-x64", "QiongTu.Control.exe")
  ],
  connectTimeoutMs: 750,
  allowStartControlProcess: !(isControlSmokeRun && controlDiscoveryOverride !== undefined)
});

function registerDesktopIpc(): void {
  ipcMain.handle("qiongtu:app-version", () => app.getVersion());
  ipcMain.handle(CONTROL_STATUS_CHANNEL, (): ControlConnectionStatus => controlManager.getStatus());
  controlManager.onStatusChanged((status) => {
    if (status.state === "connected") {
      void launcherReadiness.report("control.connected");
    } else if (status.state === "unavailable") {
      void launcherReadiness.report("control.unavailable");
    }
    for (const window of BrowserWindow.getAllWindows()) {
      window.webContents.send(CONTROL_STATUS_CHANGED_CHANNEL, status);
    }
  });
}

async function createMainWindow(
  showWhenReady: boolean,
  requireReadyToShow = showWhenReady
): Promise<BrowserWindow> {
  await launcherReadiness.report("browser-window.creating");
  const preloadPath = join(currentDirectory, "..", "preload", "index.js");
  const rendererPath = join(currentDirectory, "..", "..", "dist", "renderer", "index.html");
  const window = new BrowserWindow(createWindowOptions(preloadPath));

  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  window.webContents.on("will-navigate", (event) => {
    event.preventDefault();
  });
  window.webContents.on("will-attach-webview", (event) => {
    event.preventDefault();
  });
  window.webContents.session.setPermissionRequestHandler((_contents, _permission, callback) => {
    callback(false);
  });
  window.webContents.on("render-process-gone", () => {
    void launcherReadiness.report("renderer.failed");
  });

  const readyToShow = new Promise<void>((resolve) => {
    window.once("ready-to-show", resolve);
  });
  await window.loadFile(rendererPath);
  await launcherReadiness.report("renderer.loaded");
  if (requireReadyToShow) {
    await readyToShow;
    await launcherReadiness.report("ready-to-show");
  }
  if (showWhenReady) {
    window.show();
  }
  return window;
}

async function runSmokeCheck(): Promise<void> {
  const options = createWindowOptions(join(currentDirectory, "..", "preload", "index.js"));
  if (!isSecureWindowOptions(options)) {
    console.error("Electron security smoke check failed.");
    app.exit(2);
    return;
  }

  const window = await createMainWindow(false);
  const rendererUrl = window.webContents.getURL();
  const rendererTitle = window.webContents.getTitle();
  const rendererLoaded = rendererUrl.startsWith("file://") && rendererTitle === "穹图 QiongTu";
  window.destroy();

  if (!rendererLoaded) {
    console.error("Electron renderer smoke check failed.");
    app.exit(3);
    return;
  }

  console.log(JSON.stringify({
    status: "ok",
    mode: "hidden-renderer-window",
    networkRequested: false,
    rendererLoaded,
    rendererTitle,
    controlApiVersion: CONTROL_API_VERSION
  }));
  app.quit();
}

async function runControlSmokeCheck(): Promise<void> {
  const terminalStatus = new Promise<ControlConnectionStatus>((resolve, reject) => {
    const timeout = setTimeout(() => {
      unsubscribe();
      reject(new Error("Control connection smoke check timed out."));
    }, 15_000);
    const unsubscribe = controlManager.onStatusChanged((status) => {
      if (status.state === "connected" || status.state === "unavailable") {
        clearTimeout(timeout);
        unsubscribe();
        resolve(status);
      }
    });
  });

  await controlManager.start();
  const status = controlManager.getStatus().state === "connected"
    ? controlManager.getStatus()
    : await terminalStatus;
  if (status.state !== "connected") {
    throw new Error("Control connection smoke check did not reach the connected state.");
  }

  console.log(JSON.stringify({
    status: "ok",
    mode: "control-connection",
    controlState: status.state,
    endpointKind: status.endpointKind,
    apiVersion: status.apiVersion
  }));
  controlManager.disconnectOnly();
  app.quit();
}

if (isSmokeRun || isControlSmokeRun) {
  app.disableHardwareAcceleration();
}

const hasSingleInstanceLock = app.requestSingleInstanceLock();
if (!hasSingleInstanceLock) {
  void launcherReadiness.report("existing-instance").finally(() => {
    app.quit();
  });
} else {
  app.on("second-instance", () => {
    const window = BrowserWindow.getAllWindows()[0];
    if (window !== undefined) {
      if (window.isMinimized()) {
        window.restore();
      }
      window.focus();
    }
  });

  void app.whenReady().then(async () => {
    await launcherReadiness.report("app.ready");
    registerDesktopIpc();
    if (isLauncherReadinessSmokeRun) {
      const window = await createMainWindow(false, true);
      window.destroy();
      app.quit();
      return;
    }
    if (isSmokeRun) {
      await runSmokeCheck();
      return;
    }

    if (isControlSmokeRun) {
      try {
        await runControlSmokeCheck();
      } catch (error: unknown) {
        console.error(error instanceof Error ? error.message : "Control connection smoke check failed.");
        controlManager.disconnectOnly();
        app.exit(4);
      }
      return;
    }

    await launcherReadiness.report("control.connecting");
    void controlManager.start();
    await createMainWindow(true);
    app.on("activate", () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        void createMainWindow(true);
      }
    });
  }).catch(async () => {
    await launcherReadiness.report("renderer.failed");
    app.exit(5);
  });
}

app.on("child-process-gone", (_event, details) => {
  if (details.type === "GPU") {
    void launcherReadiness.report("gpu-process.failed");
  }
});

app.on("before-quit", () => {
  controlManager.disconnectOnly();
});

app.on("will-quit", () => {
  if (smokeUserDataDirectory !== undefined
    && smokeUserDataDirectory.startsWith(join(tmpdir(), "qiongtu-electron-smoke-"))) {
    try {
      rmSync(smokeUserDataDirectory, { recursive: true, force: true });
    } catch {
      // A crashed Chromium child can briefly retain a test-only file handle.
    }
  }
  launcherReadiness.disconnect();
});

app.on("window-all-closed", () => {
  app.quit();
});
