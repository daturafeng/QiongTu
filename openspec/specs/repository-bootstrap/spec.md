# repository-bootstrap Specification

## Purpose

建立一个可公开克隆、可审计且能由后续会话可靠恢复开发上下文的穹图项目仓库，同时明确版本控制边界并保护仓库外的原始无人机数据与本机敏感配置。

## Requirements

### Requirement: 项目工件必须集中在独立根目录
项目 SHALL 使用 `QiongTu` 作为独立项目根目录，并 MUST 将现有 `AGENTS.md`、`.codex/` 与 `openspec/` 完整保留在该根目录中。迁移 MUST NOT 移动、改写或删除未明确列入迁移清单的父目录内容或仓库外数据。

#### Scenario: 迁移现有项目工件
- **WHEN** 初始化操作在已确认的当前工作区父目录中执行
- **THEN** `QiongTu` 根目录包含三个现有项目工件，父目录中不存在它们的重复活动副本，且未列入清单的内容保持不变

#### Scenario: 保护参考影像
- **WHEN** 项目目录迁移和 Git 初始化完成
- **THEN** 用户指定的仓库外源影像与伴随文件未被移动、改写、删除或纳入仓库

### Requirement: 仓库必须具备明确的 Git 与远端身份
项目根目录 SHALL 是默认分支名为 `main` 的 Git 仓库，并 MUST 将名为 `origin` 的远端配置为 `https://github.com/daturafeng/QiongTu.git`。初始化操作 MUST NOT 修改用户的全局 Git 身份、凭据或 NVM 当前版本。

#### Scenario: 检查本地仓库身份
- **WHEN** 初始化完成后查询本地分支和远端配置
- **THEN** 当前分支为 `main`，且 `origin` 的抓取与推送地址均为指定 GitHub 仓库

#### Scenario: 远端认证或网络失败
- **WHEN** 推送因认证、权限或网络问题失败
- **THEN** 本地提交和项目文件保持完整，任务保持未完成并报告首个相关错误，不切换账户或覆盖用户配置

### Requirement: 公开仓库必须排除敏感和大型运行数据
仓库 MUST 提供版本控制排除规则，并 MUST NOT 提交密钥、令牌、许可证、本机环境文件、依赖目录、构建缓存、原始航拍输入或大型处理成果。允许提交的首批内容 SHALL 限于项目入口、OpenSpec、开发规则和必要的仓库配置。

#### Scenario: 审核首批提交
- **WHEN** 首批内容被暂存准备提交
- **THEN** 暂存清单中不存在敏感文件、仓库外源数据、依赖目录、构建缓存或大型处理成果

#### Scenario: 后续产生本地输入与输出目录
- **WHEN** 开发者在项目根目录使用约定的数据集、输入、输出、工作区、检查点或模型目录
- **THEN** Git 默认不将这些目录内容列为待提交文件

### Requirement: 仓库入口必须指向唯一事实来源
项目 SHALL 提供 `README.md`，以当前真实状态描述穹图产品定位，并 MUST 指向活动产品定义变更与开发治理主规格。README MUST NOT 宣称尚未实现的影像处理能力已经可用，也不得成为与 OpenSpec 竞争的需求或设计来源。

#### Scenario: 新会话从克隆仓库恢复上下文
- **WHEN** 开发者或后续会话打开仓库入口
- **THEN** 能从 README 找到项目定位、当前阶段以及 OpenSpec 产品定义和治理入口

### Requirement: 初始化结果必须可验证和可继续
完成状态 SHALL 同时满足：OpenSpec 严格校验通过、本地工作区无未提交变更、本地 `main` 已设置跟踪 `origin/main`，且远端可查询到对应分支。任何一项未满足时 MUST NOT 宣称仓库初始化完成。

#### Scenario: 成功发布初始仓库
- **WHEN** 初始化提交已推送且执行最终检查
- **THEN** `main` 与 `origin/main` 同步、工作区干净、远端分支可查询并且 OpenSpec 全量严格校验通过
