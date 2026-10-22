# 迫击炮计算器：网页与源码研究

核实日期：2026-10-22。目标是先做可在 Windows 本地运行的标准部署式迫击炮计算器。

## 证据范围

- 已读取 [Squad.ICU 首页](https://squad.icu/)及其使用手册目录。首页公开介绍迫击炮高差计算、置顶标识、常用点热力图、OCR 同步游戏地图。
- 已读取 [SquadCalc 网站](https://squadcalc.app/)及作者的 [sh4rkman/SquadCalc 仓库](https://github.com/sh4rkman/SquadCalc)相关源码。网页的动态控件不能仅凭文本抓取完整核验；功能和算法判断主要以该仓库为依据。
- 本次没有运行 Squad 游戏，没有对游戏内命中精度做实测，也没有下载或运行 Squad.ICU 客户端。Squad.ICU 的实现细节及其源码许可证未经核实。
- Squad.ICU 的迫击炮手册链接本次经网页读取返回首页内容，因此不据此推断额外操作细节。

源码固定版本：[commit 24828d6d3081c7ce52b10ef39ec86036ebf010b5](https://github.com/sh4rkman/SquadCalc/commit/24828d6d3081c7ce52b10ef39ec86036ebf010b5)，提交日期为 2026-10-09，package.json 版本 46.1.0。下文源码链接固定到此版本，避免后续更新改变证据。

## 功能对照与首版范围

| 能力 | Squad.ICU 公开介绍 | SquadCalc 仓库 | 本地首版范围 |
| --- | --- | --- | --- |
| 迫击炮距离、方位、仰角 | 提供迫击炮模块 | 已有坐标和弹道计算 | 标准部署式迫击炮，支持坐标输入 |
| 高差修正 | 首页介绍支持 | 地图高程加人工炮位高度 | 人工输入目标相对炮位的地面高差 |
| 地图选点 | 首页介绍 OCR 同步地图 | 交互地图、武器和目标标记 | 本地网格；支持用户导入地图图片 |
| 置顶 | 首页介绍置顶标识 | 可与独立 Overlay 程序连接 | 本地窗口置顶 |
| 热力图 | 首页介绍常用点位 | 从服务端读取历史武器位置 | 不在首版范围 |
| OCR 游戏地图识别 | 首页介绍支持 | 与 SquadMortarOverlay 连接 | 不在首版范围 |
| 自动高程、地形剖面 | 未进一步核实 | 从 API 读取高程，可展示轨迹 | 首版不自动读取高程，不做遮挡判断 |
| 多武器、协同、服务器数据 | 其他工具不在本次实现范围 | 多武器、WebSocket 协同、实时服务器 | 不在首版范围 |
| 完全离线 | 未验证 | 源码仍依赖远端 API 和资源 | 核心计算与用户导入图片离线可用 |

本地首版是功能重建，采用独立桌面界面。它不等同于完整移植 Squad.ICU 或 SquadCalc 的全部模块。

来源：[Squad.ICU 首页](https://squad.icu/)、[SquadCalc README](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/README.md)、[API 模块](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadCalcAPI.js)。

## 标准部署式迫击炮参数

参数来自 SquadCalc 的 Mortar 条目，不能套用于车载迫击炮、地狱炮或模组武器。

| 参数 | 已核实值 | 说明 |
| --- | --- | --- |
| 初速 v | 110 m/s | constant velocity |
| 基准重力 g | 9.78 m/s² | 沿用 SquadCalc 的校准值 |
| gravityScale | 1 | 本条目的实际重力仍为 9.78 |
| deceleration / decelerationTime | 0 / 0 | 无该项目所建模的减速阶段 |
| 仰角范围 | 45°–88.875° | 800–1580 NATO mil |
| 密位换算 | 6400 mil = 360° | 不采用 6000 或 6283 周制 |
| 默认解 | high | 使用高抛弹道 |
| 炮口高度 heightOffset | 1 m | 相对炮位所处表面的模型偏移 |
| 最小距离元数据 minDistance | 51 m | 用于显示和范围圈；不作为独立求解硬限制 |
| 显示密位精度 | 0 位小数 | 实际可达性判断应使用未四舍五入的角度 |

源码：[weapons.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/data/weapons.js)、[app.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/app.js)、[密位换算与角度边界](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadFiringSolution.js)。

作者的 [Deducing Elevation Wiki](https://github.com/sh4rkman/SquadCalc/wiki/Deducing-Elevation)解释：SDK 的重力值为 9.8，作者根据测试选择 9.78。这里应称为“SquadCalc 校准参数”，不能把 9.78 描述为已独立读取并验证的 Squad SDK 原始值。Wiki 为动态文档，其内容版本不包含在上述固定 commit 中。

### 51 m 不是所有高差下的硬边界

全仓 `minDistance` 引用搜索及相关源码表明：

- `src/data/weapons.js` 定义它，`src/js/squadWeapons.js` 读取它。
- `src/js/squadMarker.js` 用它绘制最小范围圈并显示武器射程信息。
- `src/js/squadFiringSolution.js` 的求解代码不引用它；求解依赖判别式和实际仰角上下限。
- 3D 控制器中同名的 `controls.minDistance` 是相机距离，与迫击炮射程无关。

因此本地实现可将 51 m 作为名义提示。目标高于炮位等情形可能改变可达的最短水平距离；不应仅凭距离小于 51 m 删除本可成立的解。最大距离同样应结合高差和角度限制求解，不能对所有情况硬套平地射程圆。

来源：[squadMarker.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMarker.js)、[squadWeapons.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadWeapons.js)、[squadFiringSolution.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadFiringSolution.js)。

## 坐标、九宫格与方向

地图采用平面距离，以左上角为原点；向东 x 增大、向南 y 增大。大格默认边长 300 m，字母 A、B、C 向东排列，行号 01、02、03 向南排列。

九宫格遵循数字小键盘方向：

```text
北
7 8 9
4 5 6
1 2 3
南
```

每加一级数字，所指区域的边长缩小到原来的三分之一。输入取最细一级格子的中心：

| 输入 | x（东向，m） | y（南向，m） | 所指区域边长 |
| --- | --- | --- | --- |
| A01 | 150 | 150 | 300 m |
| A01-7 | 50 | 50 | 100 m |
| A01-1 | 50 | 250 | 100 m |
| A01-5 | 150 | 150 | 100 m |
| B01 | 450 | 150 | 300 m |

第 i 级子格宽度为 `300 / 3^i`，其中第一层 i=1。子格 n 的列为 `(n-1) % 3`，从北往南的行为 `2 - floor((n-1)/3)`。逐层累加偏移，最后加最细格半宽。

普通两点水平距离为 `sqrt(dx² + dy²)`。方位角按正北 0°、东 90°、南 180°、西 270°计算，即 `atan2(dx, -dy)` 后规范到 [0°, 360°)。

来源：[getPos](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadCalc.js#L2073)、[getKP](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMinimap.js#L444)、[网格默认尺寸](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMinimap.js#L128)。

个别模组地图可覆盖大格边长，例如 Rhenvar 配置为 900 m。本地首版按标准 300 m 网格处理，不能承诺所有模组地图通用。来源：[maps.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/data/maps.js)。

## 高差、炮口偏移与弹道计算

人工输入的地面高差定义为：

```text
Δground = 目标处表面高度 - 炮位处表面高度
Δballistic = Δground - 1 m
```

目标更高为正，更低为负。1 m 是标准部署式迫击炮的炮口偏移，仅扣除一次。如果以后加入炮位建筑高度，应把建筑相对地面的附加高度和 1 m 炮口高度分开处理。

令 d 为水平距离，Δ 为上述弹道高差，高抛解使用：

```text
D = v⁴ - g(g d² + 2Δv²)
θ = atan((v² + sqrt(D)) / (g d))
mil = θ × 3200 / π
```

当 d 为零、输入无效、D < 0，或 θ 超出 45°–88.875°时，不应输出可用射击解。地面高差为零时，炮口仍高出地面 1 m，因此弹道高差并非零。

本地核心的飞行时间应和求解使用同一弹道高差，可用 `t = d / (v cos θ)` 回代验证。原 SquadCalc `getTimeOfFlight` 使用的是未扣炮口偏移的 `heightDiff`，极端情形还有无高差近似回退。本地采用一致的弹道差计算飞行时间是有意的数值一致性改进；不应声称所有显示数字逐位复现原网站。

来源：[构造器中的 heightDiff 和 getElevation](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadFiringSolution.js)、[heightOffset 数据](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/data/weapons.js)。

## 地图与高程来源

SquadCalc 的 [README 署名](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/README.md)说明：

- 地图图片及地图标记来自 Offworld Industries 的 Squad SDK。
- 图层、阵营等数据由 [SquadPipeline](https://github.com/yobaNGE/squad-map-data-CUE4Parse)等提取工具生成。
- 高程提取署名指向 [SquadHeight](https://github.com/Metroseksuaali/SquadHeight)。

固定版本源码的实际读取路径为：

| 资源 | 代码读取方式 |
| --- | --- |
| 地图标定 | `src/data/maps.js` 中 SDK minimap 的两角坐标，求差得到地图米制宽高 |
| 普通地图 | `API_URL + mapURL + basemap.webp`，或 terrainmap/topomap |
| 高清地图 | `API_URL + mapURL + 图层名_hq/{z}_{x}_{y}.webp` |
| 高程 | `API_URL + mapURL + heightmap.png` |

`API_URL` 默认是 `https://squadcalc.app/api`。地图图像及对应 heightmap 不在本次检查的主分支文件树内。复制源码后仍需额外准备这些资源。

高程 PNG 通过 `fast-png` 解码：每像素高度为 `(255 + R - B) × landscapeScale[2]`，按地图配置的比例读取。选点采用最接近的像素。必须同时取得匹配版本的地图范围、像素对齐和 z 比例；普通灰度图不能直接冒充此格式。

来源：[maps.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/data/maps.js)、[squadMinimap.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMinimap.js)、[squadHeightmaps.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadHeightmaps.js)、[.env.template](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/.env.template)。

作者的高度 Wiki 提醒，传统地形高度图不包括建筑、桥等物体；较新的 SquadHeight 项目则另提供表面扫描。两者不应混为一谈。由于本次没有下载、检查线上各地图资产，不能保证当前服务上每幅高程是否包括建筑。来源：[Deducing Altitude](https://github.com/sh4rkman/SquadCalc/wiki/Deducing-Altitude)、[SquadHeight README](https://github.com/Metroseksuaali/SquadHeight)。

## 完全离线复用的边界

SquadCalc 的现有 service worker 只对地图图像、高程等作限量、30 天缓存。导航 HTML 和其他 API 采用 `NetworkOnly`。把网站装成浏览器应用或套一个窗口，不能据此保证断网冷启动和所有地图都可用。

完整的独立离线移植需要：

1. 本地保存静态程序、地图、匹配高程与地图标定。
2. 将远端资源地址改为本地读取，移除或关闭服务健康检查、位置统计上报及其他联网依赖。
3. 为需要在线能力的功能另行设计可用范围，例如协同、热力图、服务器状态。
4. 分别处理软件源代码和游戏资产的授权。

本地首版的坐标计算可独立实现，无需 npm、Leaflet、WebView、远端 API 或后台服务器。用户导入地图图片后，必须确保图像边缘对应完整地图的边界，输入正确的米制宽高；已有网格、图例、留白、截图裁边会影响选点标定。首版不从普通地图颜色推测地形高程，高差由用户填写。

来源：[sw.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/sw.js)、[squadCalcAPI.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadCalcAPI.js)。

## 许可证与分发

固定版本 [LICENSE](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/LICENSE)标题为自定义的 “MIT License (Non-Commercial)”。它不是通常所指的标准 MIT 许可。虽然 [package.json](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/package.json)中的短字段写着 MIT，复用时必须阅读并遵循 LICENSE 正文：

- 仅允许非商业使用；收费、广告、订阅等商业使用受限制。
- 再分发或重新上传时须署名原作者与原项目，并保留已有原项目仓库链接。
- 拷贝或重要部分必须包含版权和许可声明。
- 该许可仅覆盖源代码；SDK 地图、图标等仍归 Offworld Industries，部分插画归各创作者，不被此许可覆盖。

本项目保留原文于 `third_party/SquadCalc.LICENSE`。本次不下载或随包加入 SDK 地图、高程等游戏资产。若以后打包这些资产，应另行核实对应来源及分发许可，不能根据代码公开就推导出地图可自由再分发。

## 首版验证边界与后续顺序

首版的数学验证能证明输入解析、方向、高差符号、角度范围及轨迹方程内部一致；不能替代当前游戏版本的实测。实用验证应选择已知平地、明显上坡和明显下坡三组点位，对比计算器与游戏结果，并保留地图版本和输入。

建议后续按以下顺序扩展：

1. 合规取得单张地图与匹配高程、标定后，验证图片选点与坐标输入一致。
2. 加入可靠的自动高程，区分地形和建筑附加高度。
3. 再扩展其他武器、更多地图，以及用户确认需要的 OCR 或游戏内显示功能。

本次没有安装任何运行时、编译器、依赖或第三方客户端。
