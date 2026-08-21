# 穹图 QiongTu

穹图是一款规划中的 Windows x64 本地无人机影像处理软件，目标是把航拍照片转换为二维正射底图、DSM、点云、纹理三维模型、网格 3D Tiles，以及可在 Cesium 中加载的三维高斯成果。

## 当前状态

项目目前处于产品骨架与引擎基准阶段，尚未接入正式影像处理引擎。仓库已包含固定版本的 Electron/React 桌面壳、自包含 .NET 控制服务、Worker 生命周期边界，以及在 Electron 窗口无法启动时仍可工作的原生 `QiongTu.Launcher` 启动诊断骨架。当前活动产品定义见 [`define-drone-mapping-platform`](openspec/changes/define-drone-mapping-platform/proposal.md)。

## 开发方式

OpenSpec 是需求、规格、设计、任务状态和变更记录的唯一事实来源。开始软件工作前，请先阅读：

- [`AGENTS.md`](AGENTS.md)
- [`development-governance` 主规格](openspec/specs/development-governance/spec.md)
- [`openspec/config.yaml`](openspec/config.yaml)

无人机原始影像和处理成果不纳入本仓库。仓库外数据默认按只读方式使用，除非项目所有者明确授权其他操作。

## 骨架验证

开发基线为 Node.js `22.23.1`、npm `10.9.8` 和 .NET SDK 10。安装工作区依赖后可执行：

```powershell
npm run typecheck
npm run lint
npm run test
npm run build
dotnet test services\control\tests\QiongTu.Control.Tests\QiongTu.Control.Tests.csproj
dotnet test apps\launcher\tests\QiongTu.Launcher.Tests\QiongTu.Launcher.Tests.csproj
```

原生启动器提供不打开界面的边界自检与只读环境探测：

```powershell
apps\launcher\src\QiongTu.Launcher\bin\Debug\net10.0-windows\win-x64\QiongTu.Launcher.exe --self-test
apps\launcher\src\QiongTu.Launcher\bin\Debug\net10.0-windows\win-x64\QiongTu.Launcher.exe --probe-only
```

Launcher 的生产入口只接受安装包固定布局中的 `desktop\QiongTu.exe`，不会执行用户传入的任意程序，不会自动停用显示设备、修改驱动或终止独立控制服务与 Worker。正式安装布局由后续打包任务完成。
