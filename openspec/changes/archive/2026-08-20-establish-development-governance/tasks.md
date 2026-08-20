## 1. 项目级会话规则

- [x] 1.1 在根目录创建精简且自动生效的 `AGENTS.md`，规定启动检查、OpenSpec 唯一事实来源和请求分类
- [x] 1.2 在 `AGENTS.md` 固化生命周期阶段、Definition of Ready、Definition of Done 和发布门禁
- [x] 1.3 在 `AGENTS.md` 固化任务状态同步、风险分级验证、Git 条件门禁和会话交接要求
- [x] 1.4 确保 `AGENTS.md` 引用 OpenSpec 开发治理主规格，并声明冲突时主规格优先

## 2. OpenSpec 配置治理

- [x] 2.1 在 `openspec/config.yaml` 记录无人机摄影测量 Windows 桌面软件的项目背景与高风险领域
- [x] 2.2 为 proposal 配置目标用户、价值、成功指标、MVP 与非目标规则
- [x] 2.3 为 specs、design 和 tasks 配置可验收场景、备选方案、风险验证和小步任务规则
- [x] 2.4 为 apply 与 archive 配置 Ready 检查、任务同步、验证证据、严格校验和归档门禁

## 3. 一致性与自动发现验证

- [x] 3.1 检查 `AGENTS.md`、`openspec/config.yaml` 与 development-governance 规格不存在相互冲突
- [x] 3.2 验证新会话仅通过根目录规则与 OpenSpec 状态即可识别活动变更、事实来源和下一安全步骤
- [x] 3.3 执行 `openspec validate establish-development-governance --strict` 并修复全部问题
- [x] 3.4 确认当前非 Git 状态被如实记录，且规则不会假装分支、提交或 CI 门禁已生效

## 4. 完成与归档

- [x] 4.1 在每项实现与验证完成后同步本任务清单状态
- [x] 4.2 最终复核所有治理工件和本地规则，记录实际验证与已知限制
- [x] 4.3 将治理 delta spec 同步为 `openspec/specs/development-governance/spec.md`，并验证要求与场景完全等价
- [x] 4.4 归档前确认产品功能变更仍保持活动状态、治理主规格可读取，并准备归档后复核

## Verification Record

- `openspec validate establish-development-governance --strict`：通过。
- `openspec validate define-drone-mapping-platform --strict`：通过，证明新增配置未破坏产品变更。
- `openspec instructions proposal --change define-drone-mapping-platform --json`：成功返回新增项目 context 与 proposal rules，证明配置已被 CLI 加载。
- `openspec list`：能够识别治理变更和产品变更及其任务进度。
- `AGENTS.md` 内容检查：启动检查、唯一事实来源、Ready、Done、Release、Git 条件门禁、会话交接和主规格引用均存在。
- `git rev-parse --is-inside-work-tree`：明确返回当前不是 Git 仓库；这是已知限制，未声称分支、提交或 CI 门禁已经生效。
- 主规格同步检查：`development-governance` 的 delta 与主规格包含相同的 12 项要求，主规格无 ADDED/MODIFIED/REMOVED/RENAMED 操作标题。
- `openspec validate --all --strict`：治理变更、治理主规格和产品功能变更共 3 项全部通过。
