# FavSync

把手机 B 站缓存的视频**单向备份**到 PC，产出一个外部播放器可直接扫描播放的 `layout=dual` 源。

> 手机随时会删缓存、B 站随时会下架投稿。目标是：
> **任何时刻出现在手机上的缓存，最终都完整存在于 PC 总库里。**

- 只读手机、只写 PC —— 没有双向同步、没有删除传播，「冲突」这个概念不存在
- 总库只增不删，一个条目只留一份字节
- 手机只缓存了 480P 也照样补 1080P；已失效 / 充电专属的稿件只有手机那份，也能救
- 还能把总库再打一层加密 7z 离线副本（冷备份）

## 形态

托盘图标 + 内嵌 Web 面板（默认 <http://localhost:8720>）。没有命令行客户端，
所有操作都在面板上：列表、勾选、进度条，进度走 SSE。

## 快速开始

### 运行环境

| 项 | 要求 |
|---|---|
| 系统 | Windows 10/11 x64 |
| 手机 | USB 调试，或 adb 直连可达（同一 Wi-Fi / 组网工具均可） |
| 可选 | ffmpeg（面板内播放要 remux 双流）、外部播放器、7-Zip（冷备份已自带 `tools/7z/7za.exe`） |

### 从源码运行

需要 .NET 10 SDK。

```bash
dotnet build src/FavSync.Desktop/FavSync.Desktop.csproj
src/FavSync.Desktop/bin/Debug/net10.0-windows/FavSync.exe
```

> **只改前端也要整项目 build。** `wwwroot` 是 EmbeddedResource，会被编译进 `mfav.dll`；
> 只 build 服务端项目的话，跑起来的是上一版页面。

### 发布

```bash
./publish.sh     # Git Bash / WSL（在仓库根执行）
```

产物 `publish/FavSync-win-x64/` 只有一个 exe 加一个 `tools\`：

```
FavSync.exe              # 自包含单文件，约 147 MB
tools\7z\7za.exe         # 冷备份（7z 加密归档）
tools\7z\License-7zip.txt
tools\adb\adb.exe        # 自带的 adb（+ AdbWinApi.dll、AdbWinUsbApi.dll）
```

另有 `FavSync-win-x64.zip`（约 60 MB）。解压即用，**零配置也能起**。
`tools\` 必须和 exe 一起留着——两个工具都是被当子进程启动的，删了对应功能直接失效。

推 `main` 会自动跑同一条 `./publish.sh`（`.github/workflows/release.yml`），只留一份 workflow
artifact（90 天），**不发 Release**。想要一个正式版本，打个 tag：

```bash
git tag v1.0.0
git push origin v1.0.0        # 只有 v* tag 触发，tag 名即版本号
```

两种情况都会跑 CI 里那道**校验产物**：解开 zip 逐个确认那 6 个文件真在包里 —— 漏写
`ExcludeFromSingleFile` 时构建照样成功、exe 照样能双击启动，只是扫描手机和冷备份会静默失效，
得在发出去之前挡住。

### 首次配置

面板「设置」页（`#/settings`）：

1. **总库目录** —— 必须是本地固定盘。网络盘 / 可移动盘上 SQLite 文件锁不可靠，会静默损坏账目；
   跨分区 rename 会退化成「复制 + 删除」。没配也能启动，会降级到 `<用户盘>:\FavSyncLibrary`
   并挂黄条提示，**绝不写回配置**。
2. **手机** —— 缓存根目录（默认 `/sdcard/Android/data/tv.danmaku.bili/download`）、adb 路径、端口。
   接了多台设备（USB + 无线）时才需要在「指定设备」里挑一台，否则留「自动」取第一台在线的。
3. **B 站** —— 扫码登录。登录态只落本机配置，不导出。
4. **冷备份**（可选）—— 归档输出目录、桶大小、7z 路径。
5. **高级**（可选，默认折叠）—— ffmpeg / 外部播放器路径、B 站是否走代理。
   全部留空即可：按 PATH 和常见安装位置自动查找，卡片里会显示「实际使用」的是哪个；
   要覆盖就填绝对路径，填错会当场拒绝保存。代理开关改完需重启（其余即时生效）。

配置在 `%LOCALAPPDATA%\FavSync\config.json`，不在仓库内。所有项都能在设置页改，不需要手改文件。

## 主要功能

| 模块 | 做什么 |
|---|---|
| 盘点与同步 | 扫手机缓存清单 → 分类 → 按需下载 / 拷贝 → 校验入库 |
| 收藏夹 | 拉全量收藏夹索引，标出「未同步 / 已失效 / 充电专属 / 多 P」，可整夹下载 |
| 在线预览 | 面板里直接播：MSE 直读 m4s 双流，不落合并文件 |
| 库管理 | 元数据补齐、扫库认领、回收站、统计 |
| 冷备份 | 总库打成加密 7z 桶（默认 2 GiB/桶）→ 网盘，需要时按桶恢复 |
| 质量策略 | 优先更高档位（含编码比较）；拒绝同画质异编码白占空间 |

## 目录结构

```
FavSync/
├── src/
│   ├── FavSync.Core/      # 核心：adb 扫描、B 站 API、下载、catalog.db、冷备份
│   ├── FavSync.Server/    # 内嵌 Kestrel + HTTP API + 面板（程序集名 mfav）
│   └── FavSync.Desktop/   # 托盘壳：单实例、开机自启、设置页、服务进程管理
├── docs/
│   ├── DESIGN.md          # 设计：模型、同步流程、增量语义、校验分级
│   └── DESIGN-COLD-BACKUP.md  # 冷备份方案：分桶、对账、恢复
├── tools/                 # 随包分发的外部工具，原样映到产物 tools\<工具名>\
│   ├── 7z/                #   7za.exe + License-7zip.txt（冷备份用）
│   ├── adb/               #   自带的 adb（adb.exe + 两个 WinApi dll）
│   └── archive/           #   归档用的打包脚本，**不发布**
├── scripts/check/         # 提交前的仓库卫生检查
├── publish.sh             # 一键发布（自包含单文件）
└── FavSync.slnx
```

## 总库布局

```
<总库>/
├── <avid>/c_<cid>/        # 一个条目的唯一字节副本，就是手机缓存的镜像
├── .incoming/             # 导入事务区，可随时整体删除
├── .trash/                # 回收站（软删除、换主腾出的旧字节）
└── .favsync/catalog.db    # 账
```

总库根下不写任何自有文件（没有 manifest.json），整个目录能原样回拷到手机。

## 几条不能破的规则

1. **已同步只查总库，不查手机现状。** 手机删了不代表没备份过，否则每次重扫都重下。
2. **总库没有静默删除路径。** 删除只发生在导入事务区、校验失败的临时目录，和用户点的回收站。
3. **扫库只能走 `RegisterBackup`（幂等）**，不能走扫描合并 —— 那会覆盖 `phone_*` 并把没扫到的置成「不在手机」。
4. **换主先搬后记账。** 旧字节整目录搬进回收站、搬成功才动账，失败整体还原。
5. **冷备清单的 md5 / size / tag 取 `backup` 行**，不取手机值 —— 换源、升档后两者不同，取错会导致恢复后又重备一遍。
6. **冷恢复：先建 item 再登记 backup，临时目录必须与库同卷。** 跨卷 Move 必失败。

细节、实测数字与取舍理由见 [docs/DESIGN.md](docs/DESIGN.md)，
冷备份的分桶与恢复流程见 [docs/DESIGN-COLD-BACKUP.md](docs/DESIGN-COLD-BACKUP.md)。

## 提交前自检

```bash
python scripts/check/check_hygiene.py --strict
```

检查开发者信息泄漏、忽略规则、断链、遗留时间戳与已废弃章节号。
