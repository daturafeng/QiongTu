# Change: 建立总控式并行开发与有限阻塞处理

## Why

穹图的摄影测量、桌面工程、高斯、Cesium 兼容与许可证评审可以并行推进，但多个 Codex 窗口若缺少统一所有权、写入隔离和阻塞上限，容易修改同一工件、越过依赖门禁或重复执行失败操作。项目需要把“当前窗口总控、其他窗口受控并行、阻塞先采用安全推荐方案、仍无法解决再交由用户决定”固化为后续会话可执行的治理规则。

## What Changes

- 建立总控窗口与执行窗口的职责边界、任务分配和统一集成门禁。
- 写入型并行任务优先使用独立 Git worktree；无法隔离时只允许单写者，其余窗口保持只读研究。
- 定义有限阻塞处理流程、重复尝试上限和升级条件，禁止无新增证据的循环重试。
- 允许在既有授权范围内优先安装来源可信、版本固定、可回退的推荐依赖，同时保留对系统级、架构级、许可证和安全影响的用户选择门禁。
- 要求并行任务以 OpenSpec 任务和验收条件为边界，并由总控同步状态、验证和集成。

## Impact

- Affected spec: `development-governance`
- Affected operational entrypoint: `AGENTS.md`
- No product runtime behavior or user data is changed by this governance change.
