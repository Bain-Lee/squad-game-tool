# 服务器跟随与基地旗帜（0.5.1）

更新：2026-10-27。

## 使用

在服务器列表点选行只查看详情。点击“启用地图跟随”后仍停留在列表，用户手动点击顶部射击计算或 RAAS 猜点时才应用地图。跟随目标与列表选中行独立；浏览其他行不会换目标，需明确点击“改为跟随所选服务器”。点击“停止跟随此服务器”或射击页的停止按钮即可关闭。

射击页可跟随所有已支持底图。猜点页只跟随已收录的具体 RAAS 图层；AAS、Invasion 和未收录图层保持手动选择，不清除手动地图和确认链。射击页换图时，手动猜点状态独立保留；返回猜点页恢复其底图。配置不跨重启保存。

## 阵营来源

EOS 公开会话中的 `TEAMONE_s`、`TEAMTWO_s` 分别对应队伍 1/2 的当前单位。已检查本地保存的实际 EOS 响应，存在 `USA_LO_CombinedArms`、`USMC_S_CombinedArms_Seed`、`FSTemplate_RGF` 等格式。解析已知原版前缀为 USA、USMC、RGF 等阵营 ID；未知或模组编码保留为未知，不按包含字符串猜国家。

跟随匹配的 RAAS 图层时，以服务器当前双方为准，绑定到图层标有 Team1/Team2 的基地；反向计数不交换国旗。当前字段缺失时显示未知，不能用该层的默认阵营冒充。手动模式使用图层 `teamConfigs.team1/2.defaultFaction`，已补入全部 39 层，并明确标注“图层默认阵营（手动模式）”。

国旗由 WPF 矢量绘制，随 EXE 离线可用，无需 WebP 解码器或额外图片。支持美国、俄罗斯、英国、加拿大、澳大利亚、乌克兰、中国和土耳其国旗简图；军种保留独立名称，例如 USA 与 USMC 共用美国国旗。其他已识别阵营用代号徽标，未知用问号。本版不复制游戏阵营徽章素材。

## 上游对应

固定参考源码为 `24828d6d3081c7ce52b10ef39ec86036ebf010b5`：

- [squadServersBrowser.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadServersBrowser.js)：服务器使用 `team1FactionId/team2FactionId`，并把阵营和单位传给地图。
- [squadFactions.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadFactions.js)：`updateMainIcon` 按 Team1/Team2 匹配基地，更新阵营名。
- [squadObjective.js](https://github.com/sh4rkman/SquadCalc/blob/24828d6d3081c7ce52b10ef39ec86036ebf010b5/src/js/squadObjective.js)：`updateMainIcon` 以阵营 ID 加载 `/img/flags/<mod>/<id>.webp`，无阵营时回退通用基地标记。

本地采用相同的“当前阵营 ID → 对应队伍基地”关系，以本地矢量旗帜替代上游 WebP 图像。阵营显示依赖服务器公开字段是否准确，不代表读取了当前游戏进程。

## 验证

离线 WPF 回归覆盖单击/Enter 不跟随、不跳页，显式按钮启用后延迟同步，列表停留时延迟换图，非 RAAS 射击跟随与手动猜点往返，换服务器同层清空旧链，当前阵营刷新、缺失阵营回退和反向基地对应。全部图层继续通过双向完整旗点链与上游参考状态对照。生成的 `build/server-follow-preview.png`、`build/raas-preview.png` 和 `build/faction-flags-preview.png` 用于界面检查；没有把模拟测试当作当前游戏中的实测结果。
