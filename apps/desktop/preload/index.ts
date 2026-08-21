import { contextBridge, ipcRenderer } from "electron";
import {
  CONTROL_STATUS_CHANGED_CHANNEL,
  CONTROL_STATUS_CHANNEL,
  DESKTOP_API_VERSION,
  type ControlConnectionStatus,
  type QiongTuDesktopBridge,
  isControlConnectionStatus
} from "@qiongtu/contracts";

async function getControlStatus(): Promise<ControlConnectionStatus> {
  const value: unknown = await ipcRenderer.invoke(CONTROL_STATUS_CHANNEL);
  if (isControlConnectionStatus(value)) {
    return value;
  }

  throw new Error("Invalid control status payload.");
}

const bridge: QiongTuDesktopBridge = Object.freeze({
  apiVersion: DESKTOP_API_VERSION,
  getAppVersion: () => ipcRenderer.invoke("qiongtu:app-version") as Promise<string>,
  getControlStatus,
  onControlStatusChanged: (listener: (status: ControlConnectionStatus) => void) => {
    const subscription = (_event: Electron.IpcRendererEvent, value: unknown): void => {
      if (isControlConnectionStatus(value)) {
        listener(value);
      }
    };
    ipcRenderer.on(CONTROL_STATUS_CHANGED_CHANNEL, subscription);
    return () => {
      ipcRenderer.off(CONTROL_STATUS_CHANGED_CHANNEL, subscription);
    };
  }
});

contextBridge.exposeInMainWorld("qiongtu", bridge);
