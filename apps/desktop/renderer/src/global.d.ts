import type { QiongTuDesktopBridge } from "@qiongtu/contracts";

declare global {
  interface Window {
    readonly qiongtu?: QiongTuDesktopBridge;
  }
}

export {};
