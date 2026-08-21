import { useEffect, useState } from "react";
import {
  CONTROL_API_VERSION,
  type ControlConnectionStatus
} from "@qiongtu/contracts";
import { ControlStatus } from "./components/ControlStatus.js";
import { CesiumSurface } from "./viewer/CesiumSurface.js";

const initialControlStatus: ControlConnectionStatus = {
  apiVersion: CONTROL_API_VERSION,
  state: "not-connected",
  endpointKind: "named-pipe",
  reason: "not-started",
  detail: "正在等待独立 QiongTu.Control 生命周期接入。",
  retryAttempt: 0,
  checkedAt: new Date(0).toISOString()
};

export function App() {
  const [controlStatus, setControlStatus] = useState(initialControlStatus);
  const [appVersion, setAppVersion] = useState("0.1.0");

  useEffect(() => {
    let cancelled = false;
    const bridge = window.qiongtu;
    if (bridge === undefined) {
          setControlStatus({
            apiVersion: CONTROL_API_VERSION,
            state: "unavailable",
            endpointKind: "named-pipe",
            reason: "not-started",
            retryAttempt: 0,
            checkedAt: new Date().toISOString(),
            detail: "安全 preload bridge 不可用；桌面能力保持关闭。"
          });
      return () => {
        cancelled = true;
      };
    }

    void Promise.all([bridge.getAppVersion(), bridge.getControlStatus()])
      .then(([version, status]) => {
        if (!cancelled) {
          setAppVersion(version);
          setControlStatus(status);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setControlStatus({
            apiVersion: CONTROL_API_VERSION,
            state: "unavailable",
            endpointKind: "named-pipe",
            reason: "not-started",
            retryAttempt: 0,
            checkedAt: new Date().toISOString(),
            detail: "无法读取本地控制边界；没有启用网络或模拟数据回退。"
          });
        }
      });

    const unsubscribe = bridge.onControlStatusChanged((status) => {
      if (!cancelled) {
        setControlStatus(status);
      }
    });

    return () => {
      cancelled = true;
      unsubscribe();
    };
  }, []);

  return (
    <div className="app-shell">
      <header className="titlebar">
        <div>
          <span className="titlebar__eyebrow">QIONGTU DESKTOP</span>
          <h1>穹图</h1>
        </div>
        <span className="titlebar__version">v{appVersion}</span>
      </header>

      <aside className="project-sidebar" aria-label="项目导航">
        <h2>项目</h2>
        <div className="empty-panel">
          <strong>尚未打开项目</strong>
          <p>项目创建与影像导入将在后续 OpenSpec 任务中实现。</p>
        </div>
        <nav aria-label="工作区模块">
          <button type="button" disabled>影像与质检</button>
          <button type="button" disabled>处理任务</button>
          <button type="button" disabled>成果与导出</button>
        </nav>
      </aside>

      <main className="workspace">
        <ControlStatus status={controlStatus} />
        <CesiumSurface />
      </main>

      <aside className="inspector" aria-label="属性与任务面板">
        <h2>工作区状态</h2>
        <dl>
          <div>
            <dt>桌面 API</dt>
            <dd>{window.qiongtu?.apiVersion ?? "不可用"}</dd>
          </div>
          <div>
            <dt>控制 API</dt>
            <dd>{controlStatus.apiVersion}</dd>
          </div>
          <div>
            <dt>计算 Worker</dt>
            <dd>尚未接入</dd>
          </div>
        </dl>
      </aside>
    </div>
  );
}
