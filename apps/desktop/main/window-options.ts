import type { BrowserWindowConstructorOptions } from "electron";

export function createWindowOptions(preloadPath: string): BrowserWindowConstructorOptions {
  return {
    width: 1440,
    height: 920,
    minWidth: 1100,
    minHeight: 720,
    show: false,
    backgroundColor: "#0b1220",
    title: "穹图 QiongTu",
    webPreferences: {
      preload: preloadPath,
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      allowRunningInsecureContent: false
    }
  };
}

export function isSecureWindowOptions(options: BrowserWindowConstructorOptions): boolean {
  const preferences = options.webPreferences;
  return preferences?.nodeIntegration === false
    && preferences.contextIsolation === true
    && preferences.sandbox === true
    && preferences.webSecurity === true
    && preferences.allowRunningInsecureContent === false;
}
