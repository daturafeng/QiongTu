## Purpose

定义二维、点云、网格和三维高斯成果的发布、版本、空间元数据、质量标识和可移植导出行为，确保成果既能进入常见 GIS/三维工具链，也能作为分层 3D Tiles 在 Cesium 中流式加载。

## ADDED Requirements

### Requirement: 成果经过校验后原子发布
系统 SHALL 在成果文件完成格式、完整性、空间范围和坐标信息校验后一次性发布成果集合。校验失败或未完成的文件 MUST 与已发布成果隔离。

#### Scenario: 正射影像缺少坐标信息
- **WHEN** 一个预期具有绝对坐标的正射影像缺少有效坐标参考或地理变换
- **THEN** 系统拒绝将其作为有地理参考的正式成果发布，并记录校验错误

### Requirement: 成果记录完整的空间与处理元数据
系统 SHALL 为每项成果记录类型、版本、坐标参考系、范围、分辨率或点密度、文件大小、生成任务、引擎与参数版本、精度等级和质量报告。

#### Scenario: 查看成果元数据
- **WHEN** 用户打开正射影像、点云或三维网格的详情
- **THEN** 系统展示适用于该成果类型的空间信息、来源和质量等级

### Requirement: 用户可以导出标准成果格式
系统 SHALL 至少支持将二维正射影像和 DSM 导出为带坐标的 GeoTIFF，将稠密点云导出为 LAS、LAZ 或 PLY，将纹理网格导出为 OBJ 或 FBX，并为每次导出附带坐标、局部原点、轴向、单位和质量说明。OBJ/FBX 导出 SHALL 包含完整可解析的纹理与材质引用。

#### Scenario: 导出二维正射底图
- **WHEN** 用户选择导出已发布的二维正射成果
- **THEN** 系统提供可在 GIS 软件中读取的带坐标 GeoTIFF 及相应元数据

#### Scenario: 导出三维模型
- **WHEN** 用户选择导出已发布的纹理网格
- **THEN** 系统提供包含完整材质与纹理的 OBJ 或 FBX，并提供足以恢复地理位置的坐标说明

### Requirement: 系统生成网格 3D Tiles
系统 SHALL 将已发布的纹理网格转换为独立的网格 3D Tiles 成果，包含 `tileset.json`、有效包围体、分层 LOD 和可流式加载的 glTF/GLB 内容。Tileset SHALL 保存源网格版本、坐标参考和转换器版本，并 SHALL 在固定兼容版本的原生 CesiumJS 中正确定位和加载。

#### Scenario: 在 Cesium 中加载网格 Tileset
- **WHEN** 用户将网格成果目录通过 HTTP 服务并使用 `Cesium3DTileset.fromUrl` 加载其 `tileset.json`
- **THEN** 模型在声明的地理位置、方向和高度出现，并随视距按层级加载而无需下载完整模型后才显示

### Requirement: 系统保存可重新转换的原始高斯成果
系统 SHALL 保存包含位置、旋转、尺度、不透明度和颜色或球谐系数的原始高斯成果，并记录坐标约定、局部原点、训练版本和内容校验值。原始高斯成果 SHALL 独立于任何特定 Cesium 导出版本，以便格式或扩展升级后重新生成 Tileset。

#### Scenario: Cesium 高斯格式版本升级
- **WHEN** 当前 Cesium 兼容扩展或压缩版本发生不兼容变化
- **THEN** 用户能够从未丢失属性的原始高斯成果重新生成新的 Cesium 高斯 Tileset，而无需重新执行空三和高斯训练

### Requirement: 系统生成 Cesium 高斯 3D Tiles
系统 SHALL 生成由 `tileset.json` 空间索引、LOD 层级和高斯 glTF/GLB 内容组成的独立 Cesium 高斯 Tileset。高斯内容 SHALL 使用 `KHR_gaussian_splatting`，压缩内容 SHALL 使用 `KHR_gaussian_splatting_compression_spz_2` 与 SPZ，并 SHALL 通过兼容版本原生 CesiumJS 的加载测试，无需修改 Cesium 源码或安装自定义运行时插件。

#### Scenario: 在 Cesium 中加载高斯 Tileset
- **WHEN** 用户通过 HTTP 服务发布高斯成果目录，并使用兼容版本的 `Cesium3DTileset.fromUrl` 加载 `tileset.json`
- **THEN** Cesium 按正确地理位置、方向和尺度渲染高斯内容，并根据相机距离进行可观察的 LOD 切换

#### Scenario: Cesium 版本不兼容
- **WHEN** 用户选择的 CesiumJS 版本不支持成果声明的高斯扩展或压缩版本
- **THEN** 系统在发布或兼容性检查中明确报告所需版本与不兼容扩展，而不是生成表面成功但无法加载的成果

### Requirement: 系统生成适合在线浏览的派生成果
系统 SHALL 从正式成果生成分级、切片、压缩或简化的浏览版本，并 SHALL 保持其与原始正式成果的版本关联。浏览版本不得替代高精度导出文件或可重新转换的原始高斯成果。

#### Scenario: 浏览大型成果
- **WHEN** 用户打开超出客户端一次性加载阈值的正射影像或三维成果
- **THEN** 系统按当前视野和细节层级流式加载浏览数据，而不是下载完整成果后才显示
