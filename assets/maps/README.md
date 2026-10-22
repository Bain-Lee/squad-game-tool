# 离线地图资源

本目录包含 25 张当前官方地图（含 Jensen's Range 和 Pacific Proving Grounds 训练场），另附已退役的 Belaya Pass 旧版底图。地图切换和高度采样均读取本地文件，不需要联网。

- `catalog.xml`：地图名称、米制尺寸、图片路径和高度换算参数。
- `images/*.jpg`：26 张 4096 × 4096 的地图底图，由上游 WebP 转为 JPEG，以兼容本机 .NET Framework/WPF。
- `heights/*.png`：25 张原始 RGB 高程 PNG。必须保留 R/B 像素，不能作为普通图片重压缩或转 JPEG。
- `SHA256SUMS.txt`：51 个地图数据文件的 SHA-256，便于检查复制完整性。
- `source/`：下载时的上游尺寸与采样公式快照，便于追溯。

资源来自 [SquadCalc](https://github.com/sh4rkman/SquadCalc)，元数据固定为提交 [`24828d6d3081c7ce52b10ef39ec86036ebf010b5`](https://github.com/sh4rkman/SquadCalc/tree/24828d6d3081c7ce52b10ef39ec86036ebf010b5)，图片取自 `https://squadcalc.app/api/img/maps/`，下载日期为 2026-10-22。图片服务内容可能更新；本次文件校验值记录在 `SHA256SUMS.txt` 中。游戏地图素材权利归其权利人所有；使用与分发前请同时查看项目第三方许可说明。

高度换算：`(255 + R - B) × heightScale + heightOffset`。高差计算中固定的 `heightOffset` 相互抵消。地图宽高由 Squad SDK 小地图的两个角点相减得出，少数地图不是正方形。

Belaya 的上游记录保留了旧版底图与尺寸，但没有可靠的当前 R/B 高度换算比例，因此其 `heightmap` 为空、`heightScale` 为 0，只使用手动高差。它不计入当前 25 张官方地图。

重新取得资源可在项目根目录运行：

```powershell
.\scripts\fetch-maps.ps1 -SourceCommit 24828d6d3081c7ce52b10ef39ec86036ebf010b5
```

脚本使用已有 Python、Pillow 和 WebP 解码器，不会安装任何环境或依赖。可通过 `-PythonPath` 指定已有 Python。重复运行复用已有文件；`-Force` 会重新下载。软件运行与普通编译不需要 Python；只有重新下载及转换地图时需要它。
