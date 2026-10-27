# 游戏地图覆盖与来源

核对日期：2026-10-22（北京时间）。本地地图资源的实际下载、转换与校验结果，以地图目录及其资源清单为准；本文说明地图清单和版本边界。

## 覆盖范围

当前地图清单采用 SquadCalc 主分支启用的 **25 张官方地图**：23 张战斗地图，以及 Jensen's Range 和 Pacific Proving Grounds 两张训练/试验地图。另收录 Belaya 的历史版本时，应在选择器中明确标注“旧版／暂退役”。这样合计为 26 张，不能把 Belaya 表述为当前正式服可用地图。

[官方 Squad Wiki 的地图清单](https://squad.fandom.com/wiki/Maps)仍列出上述 26 张历史官方地图，但其更新可能滞后：[Squad 9.0 官方说明](https://www.joinsquad.com/updates/squad-9-0-release-notes)明确说明 Belaya 因迁移 Unreal Engine 5 的技术问题暂时退役。[Squad 10.6 官方说明](https://www.joinsquad.com/updates/squad-10-6-release-notes)将 Oskil 列入下一次大版本的计划，不能算作截至核对日期已经发布的地图。

以下不计为当前独立官方地图：

- Narva Flooded：Narva 的洪水变体；AAS、RAAS、Invasion、Seed、Skirmish 等也是地图层/玩法，不是独立地图。
- Forest、Operation First Light、Nanisivik：官方 Wiki 列为已退出游戏的旧地图。
- Minimal、Infantry Tutorial：测试或教学场景，不属于此处的正式地图清单。
- SuperMod、Steel Division、Galactic Contention 等模组地图：SquadCalc 数据中另有 `mod` 标记，不纳入官方地图数量。

## 尺寸与网格

尺寸单位为米，按 SquadCalc 的 `SDK_data.minimap.corner1 - corner0` 分别计算宽和高。原始世界坐标的负数只表示 SDK 原点位置；本软件的坐标以地图左上角为零点。

| 显示名称 | 资源标识 | 宽 | 高 |
| --- | --- | ---: | ---: |
| Al Basrah | albasrah | 4000 | 4000 |
| Anvil | anvil | 3060 | 3060 |
| Black Coast | blackcoast | 4598 | 4599 |
| Chora | chora | 4064 | 4064 |
| Fallujah | fallujah | 3005 | 3005 |
| Fool's Road | foolsroad | 1774 | 1774 |
| Goose Bay | goosebay | 4031 | 4031 |
| Gorodok | gorodok | 4064 | 4064 |
| Harju | harju | 4032 | 4032 |
| Jensen's Range | jensen | 4008 | 4008 |
| Kamdesh Highlands | kamdesh | 4032 | 4032 |
| Kohat Toi | kohat | 4617 | 4617 |
| Kokan | kokan | 2496 | 2496 |
| Lashkar Valley | lashkar | 4334 | 4334 |
| Logar Valley | logar | 1761 | 1761 |
| Manicouagan | manicouagan | 4031 | 4031 |
| Mestia | mestia | 2400 | 2400 |
| Mutaha | mutaha | 2755 | 2755 |
| Narva | narva | 2800 | 2800 |
| Pacific Proving Grounds | pacific | 4032 | 4032 |
| Sanxian Islands | sanxian | 4600 | 4600 |
| Skorpo | skorpo | 6849 | 6869 |
| Sumari Bala | sumari | 1300 | 1300 |
| Tallil Outskirts | tallil | 4680 | 4680 |
| Yehorivka | yehorivka | 6350 | 6350 |
| Belaya（历史版） | belaya | 3904 | 3905 |

所有地图的宽和高均小于 7800 米，因此每大格 300 米、横向 A–Z 的网格范围足够。地图图像边界仍应按每张地图真实宽和高绘制，不能强行当成完全相同的正方形。Skorpo 原始数据的宽和高相差 20 米；SquadCalc 源码另提示其图像约有 2.02° 旋转，游戏网格约为 6900 米，精度存在该上游边界。

## 地图与高程资源

元数据采用 [SquadCalc `src/data/maps.js`](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/data/maps.js)（本次核对的提交为 `24828d6d3081c7ce52b10ef39ec86036ebf010b5`）。其 [环境模板](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/.env.template)指定公共资源根路径为 `https://squadcalc.app/api`。

- 底图：`/img/maps/<资源标识>/basemap.webp`。
- 高程：`/img/maps/<资源标识>/heightmap.png`。
- 其他底图样式和高清切片的路径由 [SquadCalc `squadMinimap.js`](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadMinimap.js)定义。
- 高程像素编码与缩放由 [SquadCalc `squadHeightmaps.js`](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadHeightmaps.js)定义：`(255 + R - B) * landscapeScale[2]`。本地再加 `SDK_data.zOffset` 恢复 SDK 世界 Z 基准；该零点并非海平面，负高程合法。计算两点相对高差时常量偏移会抵消；导入高程并不等于已完成游戏内命中验证。

WPF 原生不支持 WebP，打包时将底图转换为 PNG 或 JPEG；只要不改变尺寸比例、旋转或裁切，坐标映射保持一致。转换工具仅使用本机已有运行环境；本次任务不授权安装工具或依赖。

另外发现 [frobinsonj/squad-json](https://github.com/frobinsonj/squad-json)提供完整历史 PNG，但其 [metadata.json](https://github.com/frobinsonj/squad-json/blob/main/metadata.json)明确标为 **v8.2.0**。Al Basrah 在 9.0 已经重制、扩大，不能把这个旧 PNG 与现行 4000 米元数据混配。此源只作为历史资料，本次现行 25 张地图不从此旧资源替代。若将来单独加入历史 Belaya，应明确使用的资源版本和来源。

## 许可与验证边界

地图视觉素材来自 Squad 游戏及其社区提取资源，相关权利属于 Offworld Industries 及对应权利人。SquadCalc 使用其仓库 [LICENSE](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/LICENSE)；历史 squad-json 资源使用 CC BY-NC-SA 4.0。保留上游署名和许可，不据此声称拥有地图原始素材的版权。

地图清单核对、资源可加载、坐标范围检查以及游戏内实测是不同层次的验证。本文的联网核对不代表已经在每一张地图的当前游戏版本中校准或命中实测。
