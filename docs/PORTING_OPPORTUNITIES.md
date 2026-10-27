# SquadCalc 后续功能移植评估

完成更新：第 1–4 项已在 0.7.0 实现，操作、模型和验证见 [SHOT_ANALYSIS.md](SHOT_ANALYSIS.md) 与 [RAAS.md](RAAS.md)。下文保留实现前的调研快照。

核查日期：2026-10-28。本地基线：`132199c`，README 标注版本 0.6.2。

本次重新读取了 [sh4rkman/SquadCalc](https://github.com/sh4rkman/SquadCalc) 的 README 和相关源码。GitHub 默认分支最新提交仍为 [`24828d6d3081c7ce52b10ef39ec86036ebf010b5`](https://github.com/sh4rkman/SquadCalc/commit/24828d6d3081c7ce52b10ef39ec86036ebf010b5)，与本项目现有参数基线一致。下文上游链接固定到该提交。

本文是源码与本地数据的功能差距评估；没有运行上游网站的交互界面，没有进行当前游戏版本中的实测。本轮仅添加本文，不修改运行代码、不安装环境、不重新构建。

## 已有能力

当前代码已经具备 9 种原版武器、26 张底图、25 张当前地图的自动地表高程、23 张战斗地图的 39 个 RAAS 图层，以及 EOS 服务器列表、主动启用的地图跟随和双方基地阵营旗帜。不能再沿用早期 `RESEARCH.md` 的首版范围，把这些列为待移植项。

本次对照的本地关键代码：

- `src/WeaponCatalog.cs`：弹药当前只保存标识、名称、初速和名义最小距离，尚无 MOA、爆炸伤害和衰减参数。
- `src/Ballistics.cs`、`src/Main.cs`：已经求出高低抛及其飞行时间；界面按武器默认偏好选解，默认解无效时回退，尚无手动切换。
- `src/MapView.cs`：当前是一组炮位/目标，绿色边界为平地理论射程，尚无散布、占领区或战术绘图图层。
- `src/RaasPrediction.cs`、`src/RaasView.cs`：已有约束求解、确认、撤销和重置；悬停只提示点名和顺位，尚无假设选点后的路线预览或唯一候选自动推进。
- `scripts/fetch-raas.mjs`、`src/RaasData.cs`：运行数据只保留基地、候选点、点簇、链接和默认阵营。

## 优先考虑的离线功能

工作量为相对判断，包含数据、界面和必要验证，不是工期承诺。

| 建议顺序 | 功能 | 使用价值与本地实现方式 | 工作量 |
| --- | --- | --- | --- |
| 1 | RAAS 悬停预览、唯一候选自动推进 | 悬停候选即可预览其导致的下一旗与剩余路线变化；唯一候选可自动补齐。复用现有求解器和 39 层数据，无需新资源。预览不能改变确认链，自动推进应可撤销，并明确属于模型推导。 | 小 |
| 2 | 手动高低抛切换与双解比较 | 对两种解均可用的武器，直接比较仰角和飞行时间。现有核心已有两解，主要补界面选择和结果联动；无效解不可选。 | 小 |
| 3 | 散布椭圆、爆炸伤害范围 | 看清目标附近的理论覆盖与误伤范围，尤其适合 UB-32、BM-21。补入弹药 MOA、爆炸半径和衰减参数，按方位旋转椭圆，可显示上游模型中的 100/25 伤害范围。 | 中 |
| 4 | 地形剖面与弹道曲线 | 展示炮位到目标之间的山脊、轨迹和两种抛物线，辅助选择炮位及射击方式。复用本地高程采样和当前弹道解，使用独立详情面板呈现。 | 中 |
| 5 | 占领区、主基地保护区、禁止部署区、图层边界 | 让猜点页进一步支持进点、建点和炮位规划。把完整图层快照中的几何信息转换为本地米制坐标，再添加可开关的地图图层。 | 中到大 |
| 6 | 阵营单位/载具清单、手动重生计时、生成点 | 展示当前图层和单位可用载具、数量、票值、延迟及重生时间，点击后开始本地倒计时。生成点来自图层配置，需要区分允许生成的类型和当前单位实际载具。 | 中 |
| 7 | 多目标、双炮位 | 保留多个目标的射击解，便于切换火力任务；随后可扩展两个炮位对同一目标的读数。上游支持两个炮位，且共享当前所选武器类型；不能把它描述成任意多种武器同时求解。 | 中到大 |
| 8 | 方案存档与导入导出 | 保存地图、武器、炮位、目标、RAAS 确认链和战术标记，便于下次继续或把文件交给队友。现有 `settings.xml` 只保存当前设置，不等同于命名方案库。 | 中 |
| 9 | 战术标记、FOB 参考圈、路线箭头和区域绘制 | 将火力点、无线电位置和行动路线保留在同一张地图；用本地矢量标记即可实现。相关范围参数需要按游戏版本核实，不直接视为通用固定规则。 | 中 |
| 10 | 修正射击网格、线形/矩形批量目标 | 在目标附近显示仰角和方位变化对应的参考网格；按散布间距生成一串或一片目标。适合在散布与多目标完成后再做。 | 中 |
| 11 | 地形图、等高线图、高清瓦片 | 增强选点时的地貌辨识和放大清晰度。上游已有普通、地形和地形等高线底图切换，以及高清瓦片路径；需要另外准备、校验和打包资源，不能只改一个显示开关。 | 中 |
| 12 | 随地形高差变化的射程边界 | 将现有平地圆扩展为随各方向目标地形高度变化的参考边界。可参考上游算法，但应使用本地统一的求解器、武器角度与弹丸寿命限制，并控制重算开销。 | 中到大 |

第 1、2 项最适合先做一轮小改动；第 3、4 项对射击页提升最直接。若优先发展猜点和战术规划，则可把第 5、6 项提前。

## 现成数据比当前运行格式更丰富

本机 `build/raas-raw/` 中实际存在 39 份完整图层 JSON，总计 23,366,663 字节，约 22.3 MiB。它们是此前取得的快照，不能视为本轮重新下载或已验证与当前游戏一致。`assets/layers/catalog.json` 记录的快照日期为 2026-10-25。

本轮逐文件解析确认，39 份快照均包含以下非空字段：

| 原始字段 | 可以支持的功能 | 当前精简运行数据 |
| --- | --- | --- |
| `objectives.*.points[].objects` | 旗点占领区几何 | 未保留 |
| `mapAssets.protectionZones`、`deployableLockDistance` | 基地保护区和禁止部署范围 | 未保留 |
| `border` 及切线字段 | 图层可玩边界 | 未保留 |
| `units.team1Units`、`units.team2Units` | 单位资料、载具列表和重生参数 | 未保留 |

抽查的完整快照还包括 `assets.vehicleSpawners`、`deployables`、`helipads`、出生组和出生点。载具条目具备 `count`、`delay`、`respawnTime`、`ticketValue` 等字段。`teamConfigs` 保存默认单位与筛选条件；候选单位列表不能整体冒充服务器当前选中的单位。

这意味着第 5、6 项可以先利用本地已有原始资料开发转换器和样例，无须从零寻找数据源。但 `build/` 原始快照未随源码和便携包交付，正式功能仍需扩展下载脚本、运行数据格式与打包流程；不能让程序依赖本机 `build/` 目录。AAS、Invasion 等模式还需另外取得图层，本次检查的 39 份均为 RAAS 范围。

几何数据含球体、旋转盒体和胶囊体；本轮遍历到的候选形状记录分别为 1019、713 和 343 条，包含跨候选/图层重复，不是独立旗点数。样本还存在负半径字段，移植时需核对符号、比例、旋转和厘米到米的转换，不能把所有区域画成固定圆圈。

## 移植时需要保留的含义

- **散布和伤害圈是理论模型。** 上游按 MOA、飞行时间及伤害衰减计算；不能承诺某个圈内必定命中或致死，也没有自动计入掩体。其纵向散布公式使用简化射程关系，本地已有更一致的炮口高度和时间计算，应明确采用的模型。
- **二维剖面和自动遮挡检测是两个工作项。** 本轮读取的 `squadSimulation.js` 绘制地形和轨迹，动画终止条件主要是画布边界/目标横坐标，没有完整的逐段地形碰撞判定。先移植剖面，再增加并验证沿程地表交叉检查；当前高程不含房顶、桥面等建筑附加高度。
- **地形射程边界也不等于全程可达。** `squadMarker.js` 的精细边界扫描主要比较落点地形与弹道高度，不能据此声称已经检测沿途山体或建筑遮挡。
- **载具计时由用户开始。** 上游点击载具条目启动倒计时；本地 EOS 字段不提供击毁事件，不能描述成自动识别击毁或实时得知敌方载具状态。服务器当前单位缺失或无法匹配时，应明确显示资料来源与未知状态。
- **存档应保存真实米制坐标。** 不能从显示的三级网格反向恢复点击位置，也不能把上游 Leaflet 坐标直接套入本地坐标。优先定义带版本号的本地格式，再考虑兼容 `.squadcalc`。
- **保留现有地图手势。** 新增绘图和标记工具通过明确模式进入，避免占用用户已经使用的右键拖动、右键双击炮位和左键目标操作。

## 适合后续单独立项的功能

| 功能 | 上游情况 | 本地额外条件 |
| --- | --- | --- |
| 多人共享地图 | `squadSession.js` 通过 WebSocket 同步标记、武器、目标和图层；README 介绍最多 10 人 | 需要可用的协同服务及状态同步、断线重连设计；本轮未核验上游后端能否独立部署。先有本地方案导出更容易形成可用结果。 |
| 常用炮位热力图 | 从 `/get/weapons` 获取玩家历史位置；API 模块另有位置上报接口 | 依赖服务端样本和接口；可先设计用户自建点位收藏，若使用社区统计需另行核实服务使用条件。 |
| 游戏地图叠合与读数覆盖层 | `smcConnector.js` 连接外部 SquadMortarOverlay 的本机端口 | SquadCalc 只是连接端；截图、识别和游戏覆盖需另外审查 [SquadMortarOverlay](https://github.com/Devil4ngle/SquadMortarOverlay) 的实现、许可和运行条件。本轮未检查该仓库源码，不能把它算作已经具备的可直接移植模块。 |
| 模组地图与武器 | 武器数据包含模组条目，图层 API 支持模组参数 | 需要完整地图标定、高程、武器参数和图层覆盖；部分地图采用特殊网格，不能直接复用当前固定 300 m 大格的假设。 |

## 上游源码依据

| 对照功能 | 本轮读取的固定版本源码 |
| --- | --- |
| 散布参数和纵向/横向散布 | [squadFiringSolution.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadFiringSolution.js#L128) |
| 各弹药 MOA、伤害与爆炸参数 | [weapons.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/data/weapons.js)、[squadWeapons.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadWeapons.js#L105) |
| 地形剖面和弹道绘制 | [squadSimulation.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadSimulation.js#L228)、[调用入口](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadTargetMarker.js#L568) |
| RAAS 预览和自动确认 | [squadLayer.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadLayer.js#L1184) |
| 高低抛切换和地形射程边界 | [切换逻辑](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMarker.js#L410)、[射程扫描](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMarker.js#L248) |
| 占领区和基地保护区 | [squadCapZone.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadCapZone.js)、[squadLayer.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadLayer.js#L920) |
| 载具清单和手动计时 | [squadFactions.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadFactions.js#L278) |
| 两个炮位和多目标 | [squadMinimap.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMinimap.js#L512) |
| 地图与标记方案导入导出 | [squadCalc.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadCalc.js#L2121) |
| 战术形状和标记工具 | [squadShapes.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadShapes.js)、[squadContextMenu.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadContextMenu.js) |
| 射击网格和批量目标 | [squadTargetGrid.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadTargetGrid.js)、[线形/矩形目标](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadTargetMarker.js#L796) |
| 地图底图与高清瓦片 | [squadMinimap.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMinimap.js#L141) |
| 协同、热力图和外部覆盖层接口 | [squadSession.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadSession.js)、[squadCalcAPI.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadCalcAPI.js)、[smcConnector.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/smcConnector.js) |

SquadCalc 的 [LICENSE](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/LICENSE) 是自定义 **MIT License (Non-Commercial)**，本轮重新读取后确认仍限制非商业用途并要求保留署名与许可。游戏地图、图标和其他第三方素材不由该代码许可授予使用或分发权；新增代码移植和新增素材取得应分别处理。
