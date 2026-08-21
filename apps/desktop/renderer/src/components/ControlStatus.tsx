import type { ControlConnectionStatus } from "@qiongtu/contracts";

interface ControlStatusProps {
  readonly status: ControlConnectionStatus;
}

const statusLabels: Readonly<Record<ControlConnectionStatus["state"], string>> = {
  "not-connected": "未连接",
  connecting: "连接中",
  starting: "启动中",
  reconnecting: "重连中",
  connected: "已连接",
  unavailable: "不可用"
};

export function ControlStatus({ status }: ControlStatusProps) {
  return (
    <section className={`control-status control-status--${status.state}`} aria-live="polite">
      <span className="control-status__indicator" aria-hidden="true" />
      <div>
        <strong>本地控制服务：{statusLabels[status.state]}</strong>
        <p>{status.detail}</p>
        <small>
          边界：{status.endpointKind} · 原因：{status.reason} · 重试：{status.retryAttempt}
          {status.nextRetryDelayMs === undefined ? "" : " · 下次重连约 " + String(status.nextRetryDelayMs) + " ms"}
        </small>
      </div>
    </section>
  );
}
