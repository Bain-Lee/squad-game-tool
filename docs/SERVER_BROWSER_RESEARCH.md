# Squad 服务器列表、异常人数识别与一键加入：调研

调研日期：2026-10-25（Asia/Shanghai）。本轮完成公开网络资料与源码核对；未执行 EOS 认证、未取得当前服务器原始样本、未连接游戏验证。本文不代表功能已经实现。

## 结论

可以在现有 Windows 桌面程序中实现服务器浏览、重复项合并、疑似虚报人数提示，以及 Steam 加入入口。关键是采用当前 EOS 数据链路，并分别验证人数校验和加入流程。

不能承诺通过公开接口识别所有假玩家、还原所有服务器的真实活跃人数。关联大厅成员、会话登记人数和服务器上报人数是不同口径；登记了账号不等于真人正在游玩。高置信度重复记录可以自动折叠，证据不足的记录应标记、保留查看入口。

Squad.ICU 首页确实介绍了目标功能，但本次没有找到其公开的检测算法或服务端源码。下述方案来自其他公开实现与独立设计，不应称为已查明 ICU 内部实现。[S1]

## 查到的主要资料

| 资料 | 本次实际核对内容 | 对实现的价值与边界 |
| --- | --- | --- |
| GameDig 的 Squad 实现 | `protocols/squad.js`、`protocols/epic.js`、合并 PR #731、MIT 许可 | 当前源码走 EOS，可参考认证、查询及端口匹配；并未因此获得反假人数能力。[S2][S3][S4] |
| SquadExtended API Toolkit | `auth.js`、`query.js`、`lobby.js` | 展示 Steam 票据换 EOS token、列表过滤和 Steam 大厅桥接。示例需要修正与当前版本实测。[S5] |
| SquadExtended Knowledge Base | README、`squad-api/player-resolution.md` | 提供由服务器关联信息查询 EOS 大厅、取得成员标识的候选路径。属于社区协议资料，不是官方稳定接口承诺。[S6] |
| WeLoveSquad 服务器信息机器人 | README | 使用授权 RCON 的 `ShowServerInfo`、`ListPlayers`、`ListSquads` 获取数据。[S7] |
| SquadJS | `squad-server/rcon.js` | 存在 `ListPlayers` 解析实现，可参考服主配合场景。[S8] |
| OWI 10.6 官方更新 | 2026-10-07 更新说明 | `ShowServerInfo` 修复为读取当前人数与两类排队人数，而非 EOS 会话缓存；`ListPlayers` 输出也有扩展。[S9] |
| Steamworks 官方文档 | `ISteamMatchmaking` | 说明游戏未启动和已运行时的大厅加入机制；不能单独证明当前 Squad 兼容社区桥接方法。[S10] |

## 1. 服务器发现：采用 EOS

GameDig 于 2025-09-02 合并的 #731 已将 Squad 查询从 Valve 协议迁移到 EOS。旧的 Steam 主服务器/A2S 教程不能直接当作当前版本通用方案；A2S 查询失败也不能判定服务器是假服。[S2][S4]

现有源码可供参考的字段包括：

- `SERVERNAME_s`、`MAPNAME_s`、`GAMEVERSION_s`：名称、地图、版本。
- `ADDRESS_s`，以及 `ADDRESSBOUND_s` 或 `GAMESERVER_PORT_l`：定位端点。公网地址应与游戏端口组合，不能把绑定地址 `0.0.0.0` 当连接地址。
- `PLAYERCOUNT_l`：Squad 属性中的上报人数。
- `totalPlayers`、`publicPlayers`、`settings.maxPublicPlayers`：EOS 会话层字段；不要未经校验就与上报人数互换。
- `LICENSEDSERVER_b`、`COOPSERVER_b`：候选分类字段，实际存在性与当前语义须由样本验证。

其中 GameDig 的通用 EOS 代码把 `totalPlayers` 映射为 `numplayers`，并读取 `publicPlayers`；这只是数据映射，不证明人数真实。[S3][S5]

### 认证存在两条公开实现，不能提前断言一定要求用户交出 Steam 账号

- 当前 GameDig 源码先取得 device ID token，再兑换 EOS access token。[S3]
- SquadExtended 示例使用拥有 Squad 的 Steam 账号生成会话票据，再兑换 EOS token。[S5]

两条路径的现行权限、有效期、接口限额均未实测。先验证 GameDig 路径能否完成所需只读查询；需要 Steam 身份时再设计本地票据流程。不要把 Steam 密码或 refresh token 当作普通配置文件内容。

列表查询可能受结果上限影响。不能把示例中的 `maxResults: 500` 或文档里的更大值当作服务保证。需要识别截断，按接口实际支持的筛选条件分段查询，并用会话标识去重；无法确认完整性时显示“部分结果”。

## 2. “阴兵”：先区分上报、登记与可核验人数

可供验证的候选链路是：服务器列表 → 关联会话信息 → EOS 大厅查询 → 去重后的成员标识计数。SquadExtended 的玩家解析文档给出了 `ADVERTISEDSESSIONID_s` 的查询条件以及 `USERID_s` 成员标识线索；具体关联关系必须通过原始响应确认。[S6]

建议并列保存以下数据，避免把不同口径压成一个“真实人数”：

| 字段 | 含义 |
| --- | --- |
| reportedPlayers | 服务器发布的列表人数 |
| sessionPlayers | EOS 会话报告的登记人数 |
| observedLobbyMembers | 关联大厅中本次可见的去重成员数；无法确认完整性时为空 |
| rconPlayers | 服主授权查询到的服务器侧连接人数；未接入时为空 |
| queuePublic / queueReserved | 普通和预留排队人数，分开展示 |
| observedAt / source / status | 获取时间、来源、是否成功以及是否完整 |

**建议规则，尚未以真实样本校准：**

1. 对齐同一目标、相近时间的多个来源，再比较差异。
2. 连续多次显著不一致才标记“疑似人数异常”；差额阈值和采样周期在实测后决定。
3. 换图、重启、排队变化、网络中断和读取权限不足应有独立状态。
4. 查询超时、空响应或缺字段不能换算成 0 人。
5. 不用 `min(各来源人数)` 作为所谓真实人数；来源可能缺失、滞后或口径不同。
6. 仅核对人数无需解析玩家姓名、关联 Steam 主页或建立长期个人历史。

如果有合作服主，使用其授权的 RCON 查询作为对照。OWI 10.6 对 `ShowServerInfo` 的修复使它成为值得优先测试的服务器侧来源，但仍需核对解析兼容性，且连接账号数不等于真人活跃人数。[S7][S8][S9]

对于任意陌生服务器，没有 RCON 权限就不能采用这条路径。高级假客户端或挂机账号也可能出现在大厅和连接名单中，因此无法从计数本身证明真人身份。

## 3. “克隆服”：重复项合并与冒牌服判断分开

以下为建议实现规则，不是已经找到并验证的通用反克隆算法：

- 同一来源、同一会话 ID 的重复查询结果可以合并。
- 不同会话若指向相同公网 IP 和游戏端口，列为“疑似重复入口”；再检查关联会话、时间、版本和目标信息后决定是否折叠。
- 大厅关联、连接目标与多次采样的成员集合可以补充判断，但这些数据本身未必独立可信。
- 只因名称相似、地图相同、人数相同、同一 IP 或同一服主就合并，均可能误伤正常多服。
- 即使判断两条记录重复，也不一定知道哪条是原服。可靠的身份确认需要服主核验、可信端点记录或独立来源。
- 名称近似而目标不同只能提示可能冒牌；不要自动把认证标志当作身份真实性证明。

界面保留“显示已折叠项”、合并原因与撤销入口。优先自动处理确定的重复查询记录，再逐步扩展经样本验证的规则。

OWI 的许可政策禁止仿冒名称和人数操纵，但政策存在不等于列表数据已经经过实时校验。[S11]

## 4. 一键加入：优先验证 Steam 大厅链路

公开社区示例使用 `steam://joinlobby/393380/{lobbyId}`，通过大厅元数据关联目标 EOS 会话。Steam 官方文档也说明了大厅加入在游戏启动前后如何交给游戏处理。[S5][S10]

实现前必须分清 Steam 大厅 ID、EOS 会话 ID、EOS 大厅 ID，不能把任意一种 ID 填入同一个 URI。

本次源码核对发现，SquadExtended 的 `lobby.js` 声明了 `steamID`，却使用未定义的 `me`；`buildid` 也写死。它只能作为协议线索，不能称为拿来即可运行的成品。GitHub 仓库元数据的 `license` 为 null，所列根目录未见 LICENSE；后续应独立实现或先澄清复用许可。[S5]

建议顺序：

1. 优先取得并验证合法现存大厅与目标服务器的对应关系。
2. 如需临时大厅桥接，单独验证当前 Squad 的兼容性、必要元数据、生命周期与可见性，避免产生无用公开列表项。
3. 在目标服务器前后对比，验证实际进入的服务器、地图与预期一致。
4. 测试游戏未启动、已启动、服务器满员、空服、会话过期和密码服。
5. 入口失败时保留服务器名称及游戏内搜索指引；未实测前不宣称传统 `steam://connect/IP:port` 能覆盖当前 Squad。

发起加入不等于加入成功，仍受版本、密码、排队和网络状态影响。

## 5. 对现有 SquadTools 的建议

本地 README 确认现有项目是 .NET Framework 4.8 Windows 桌面程序，目前功能围绕离线迫击炮计算。服务器列表可以作为独立的在线页面接入。

建议拆成服务器数据获取、异常判定、Steam 加入三个模块；普通列表界面保持现有桌面技术。公开实现采用 Node.js 不代表正式程序必须增加 Node.js 运行依赖。Steam 加入是否需要额外原生组件，取决于当前版本验证结果。

可选择两种数据部署方式：

- **本地查询 EOS**：少依赖一个聚合服务，但客户端需要处理认证、限流和版本适配。
- **自建或获准使用的聚合 API**：可集中缓存与多次采样，桌面端较轻；需要维护在线服务并说明数据更新时间。

先验证本地查询的可用权限和数据质量，再决定是否需要后台。不能把未公开许可的第三方网页内部接口当作长期数据服务。

## 6. 下一步应完成的最小验证

1. 获取少量当前版本服务器原始样本，确认字段、分类、认证与查询限制。
2. 验证服务器到大厅的对应关系及成员查询覆盖率，区分读取失败和真实空服。
3. 对同一服务器连续采样；选择有明确现场人数或服主 RCON 对照的样本比较误差。
4. 用真实重复记录和正常同 IP 多服检验合并规则。
5. 单独跑通一次 Steam 加入，并确认目标正确；之后再接界面。

验收应给出样本范围、人数差异、误判情况、数据新鲜度和加入成功情况，不能只展示一张有数字的列表截图。

## 7. 检索覆盖与证据边界

- Squad.ICU：核对目标功能文案；手册和技术鸣谢地址在本次网页读取中返回首页内容，未读到对应算法。
- GitHub：读取上述协议、认证、加入和 RCON 源码，发现并记录示例缺陷；GameDig 协议文件最近一次相关提交为 `87f7c097059326b1eb11d0c2f9a5ae74a244f730`。
- Steam 社区：读到玩家描述“列表人数高、进入后人数少”的讨论。这能证明有人报告现象，不能证明检测算法有效。[S12]
- 百度贴吧：搜索命中 squad 吧相关内容的用户主页摘要，但打开原页返回 403；未核实帖子全文，也未找到可验证的完整技术方案。[S13]
- 同类网站：Squad ovo 与 SquadBrowser 可供界面和功能对照；本次没有验证其内部数据算法和加入成功率。[S14][S15]
- 未下载安装 ICU、未登录或调用 Steam/EOS 认证、未发送 RCON 命令、未实际加入服务器。本轮仅新增调研文档。

## 来源

- [S1：Squad.ICU 首页](https://squad.icu/)
- [S2：GameDig Squad 协议实现](https://github.com/gamedig/node-gamedig/blob/master/protocols/squad.js)
- [S3：GameDig EOS 认证及查询实现](https://github.com/gamedig/node-gamedig/blob/master/protocols/epic.js)
- [S4：GameDig #731，迁移到 EOS](https://github.com/gamedig/node-gamedig/pull/731)
- [S5：SquadExtended API Toolkit](https://github.com/SquadExtended/squad-api-toolkit)，[加入示例](https://github.com/SquadExtended/squad-api-toolkit/blob/main/lib/lobby.js)
- [S6：SquadExtended 玩家解析说明](https://github.com/SquadExtended/knowledge-base/blob/main/squad-api/player-resolution.md)
- [S7：WeLoveSquad 服务器信息机器人](https://github.com/WeLoveSquad/squad-server-info-discord-bot)
- [S8：SquadJS RCON 实现](https://github.com/Team-Silver-Sphere/SquadJS/blob/master/squad-server/rcon.js)
- [S9：OWI Squad 10.6 更新说明](https://www.joinsquad.com/updates/squad-10-6-release-notes)
- [S10：Steamworks ISteamMatchmaking](https://partner.steamgames.com/doc/api/ISteamMatchmaking)
- [S11：OWI 服务器许可与管理政策](https://www.joinsquad.com/server-licensing-and-administration-policies)
- [S12：Steam 社区 Choosing a server is difficult](https://steamcommunity.com/app/393380/discussions/6/598526361078926083/)
- [S13：贴吧搜索命中的用户主页；原页访问受限](https://tieba.baidu.com/home/main?lp=home_main_fans_main&un=403FBD)
- [S14：Squad ovo](https://www.squadovo.cn/)
- [S15：SquadBrowser](https://squadbrowser.app/)
