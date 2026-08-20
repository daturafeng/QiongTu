# 穹图 QiongTu

穹图是一款规划中的 Windows x64 本地无人机影像处理软件，目标是把航拍照片转换为二维正射底图、DSM、点云、纹理三维模型、网格 3D Tiles，以及可在 Cesium 中加载的三维高斯成果。

## 当前状态

项目目前处于产品需求、规格与架构定义阶段，尚未提供可运行的影像处理程序。当前活动产品定义见 [`define-drone-mapping-platform`](openspec/changes/define-drone-mapping-platform/proposal.md)。

## 开发方式

OpenSpec 是需求、规格、设计、任务状态和变更记录的唯一事实来源。开始软件工作前，请先阅读：

- [`AGENTS.md`](AGENTS.md)
- [`development-governance` 主规格](openspec/specs/development-governance/spec.md)
- [`openspec/config.yaml`](openspec/config.yaml)

无人机原始影像和处理成果不纳入本仓库。仓库外数据默认按只读方式使用，除非项目所有者明确授权其他操作。
