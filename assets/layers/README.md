# 原版 RAAS 猜点数据

本目录是按图层保存的离线数据，覆盖当前内置的 23 张官方战斗地图和 39 个 RAAS 图层。Jensen's Range、Pacific Proving Grounds 没有 RAAS，旧版 Belaya 的图层接口返回 404；这些情况在目录中明确标记。

来源为 SquadCalc 的公开 `/api/get/layers?map=...` 与 `/api/get/layer?name=...` 接口。取得日期见 catalog.json 的 fetchedAt，底图原点元数据固定到 sourceCommit。各图层只保留双方主基地、候选旗点坐标、唯一标识、点簇及有向链接；未打包载具生成点等无关数据。

坐标先由 SDK 厘米换算为米，再减去与地图素材一致的 SDK minimap.corner0 原点。catalog.json 将完整图层标识映射到基础地图，不能把 RAAS v1/v2/v3 或 CL 变体混用。

源码中的求解逻辑参考 sh4rkman/SquadCalc 的 squadLaneSolver.js；现有第三方非商业许可继续随工程保留。图层/地图数据取自游戏资源，权利归相应权利人，代码许可不授予游戏素材权利。本数据用于本地辅助显示，需随游戏数据更新。

更新方法：使用现有 Node 20+ 运行项目中的 scripts/fetch-raas.mjs；它不会安装依赖。新数据全部下载并归一化成功后才替换本目录，旧数据保留在 build/raas-backup-*；下载失败不覆盖当前目录。更新后应执行完整构建测试，核对 build/raas-coverage.md 的逐层结果。若上游数据改变，参考样例也需针对固定源码版本重新核验。
