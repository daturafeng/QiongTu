# QiongTu benchmark baseline

本目录保存 `define-drone-mapping-platform` 任务 1.1、1.2 的数据集登记、测量格式、示例和检查工具。产品行为、技术决策与任务状态仍以 OpenSpec 为唯一事实来源。

## 多来源组合

正式登记见 [`dataset-registry.json`](dataset-registry.json)，来源与许可证据见 [`SOURCE_REVIEW.md`](SOURCE_REVIEW.md)。组合不依赖单一样例：

- `owner-oblique-sample-v1`：项目所有者提供的私有多角度 JPEG/MPO 与定位辅助文件样例，只读且不可再分发。
- `usgs-fall-creek-20161109-v1`：公共领域的独立规则正射、GCP/参考成果和跨来源回归发布。
- `usgs-medina-river-2019-2022-v1`：公共领域的第三个物理来源，包含两期 UAS 影像、GCP 和参考成果。
- `usgs-fall-creek-gaussian-holdout-v1`：从公共领域影像确定性生成的高斯训练/留出视角配方。
- `usgs-public-domain-fault-fixtures-v1`：从公共领域素材的隔离副本生成重复、损坏、缺定位、模糊、低重叠和空批次故障。

两个 USGS 发布目前仅完成官方来源、持久标识和许可证据登记，尚未下载。内容清单哈希、实际图像数量、派生集哈希和最终阈值必须在用户批准下载后补齐；在此之前可以使用所有者私有样例执行任务 1.3 的本地可行性预演，但不得以该预演完成多来源引擎选型门禁。

## 私有样例清点边界

所有者样例的内容指纹、文件和影像数量、字节数、设备型号、帧尺寸、采集分布、相对范围与持续时间均为私有本地信息。公开仓库只保存逻辑别名、适用角色、只读政策和私有清单绑定，不保存可用于识别该批次的派生统计。

详细清点必须写入 Git 忽略的 `artifacts/benchmark-inventory/`，且不得复制到提交、Issue、日志或公开基准记录。清单只证明输入身份和格式覆盖；定位字段或辅助文件存在不证明它们已参与处理，也不证明达到测绘精度。

只读复现方式：

```powershell
$env:QIONGTU_OWNER_SAMPLE = '<private local dataset directory>'
python benchmarks/tools/inspect_reference_dataset.py `
  --source $env:QIONGTU_OWNER_SAMPLE `
  --source-id owner-oblique-sample-v1 `
  --output artifacts/benchmark-inventory/owner-oblique-sample.inventory.local.json
```

## 统一基准格式

[`benchmark-record.schema.json`](schemas/benchmark-record.schema.json) 强制记录：

- 数据集身份状态、持久发布标识、许可证和官方许可证据；
- 输入数量、引擎/训练器/转换器/CesiumJS 版本、参数哈希和主机环境；
- CRS、垂直参考、局部原点、轴向、单位与实际地理配准来源；
- 注册率、覆盖、GSD、重投影误差、点云/检查点几何指标；
- 网格拓扑/纹理/视觉评分，高斯属性、PSNR、SSIM、LPIPS 和视觉评分；
- 总耗时、阶段耗时、CPU/内存/GPU/显存/临时磁盘峰值；
- 格式与空间回读、验收证据和已知限制。

未测量或不适用的指标必须保留空值并说明原因，不能填入猜测数字。结构示例 [`benchmark-record.example.json`](examples/benchmark-record.example.json) 明确标记为非基准证据。

## 验证

```powershell
python benchmarks/tools/validate_baseline_assets.py
Test-Json -Json (Get-Content -Raw benchmarks/dataset-registry.json) `
  -SchemaFile benchmarks/schemas/dataset-registry.schema.json
Test-Json -Json (Get-Content -Raw benchmarks/examples/benchmark-record.example.json) `
  -SchemaFile benchmarks/schemas/benchmark-record.schema.json
```
