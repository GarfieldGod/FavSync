# AGENTS.md

给 AI 编码代理的操作约定。人类读者看两份就够：
[README.md](README.md)（怎么跑、怎么发布）与 [docs/DESIGN.md](docs/DESIGN.md)（模型与取舍）。
本文件只写 Agent 会踩的坑：构建顺序、账目规则、API 陷阱、协作纪律。

## 项目一句话

手机 B 站缓存 → PC 总库的**单向备份**，附内嵌 Web 面板（盘点 / 收藏夹 / 预览 / 冷备）。
托盘壳（WinForms）+ Kestrel（程序集名 `mfav`，默认端口 8720）+ SQLite 账本 + EmbeddedResource 前端。

## 构建与验证

- `dotnet build src/FavSync.Desktop/FavSync.Desktop.csproj` —— **必须 build Desktop**，
  它才把 Core 一并编进去；只 build Server 跑的是旧的 Core.dll
- **`wwwroot/` 是 EmbeddedResource**，编译进 `mfav.dll`：改前端必须
  停进程 → build → 重启 → `curl` 复验，否则跑的是上一版页面
- Git Bash 下路径用正斜杠；面板的滚动容器是 `<main>`，不是 `body`
- 提交前自检：`python scripts/check/check_hygiene.py --strict`

## 代码地图

| 位置 | 职责 |
|---|---|
| `src/FavSync.Core/Bili/BiliClient.cs` | B 站 API：登录、收藏夹、playurl、探测 |
| `src/FavSync.Core/Store/CatalogDatabase*.cs` | SQLite 账本：item / backup / item_file / probe / cold_* |
| `src/FavSync.Core/Jobs/JobManager*.cs` | 作业（scan / probe / sync / fav / cold），SSE 推进度 |
| `src/FavSync.Core/Cold/` | 冷备份：分桶、清单、恢复 |
| `src/FavSync.Core/Scan/` | `IScanSource`（adb / 本地库）共用同一个 Scanner |
| `src/FavSync.Server/` | minimal API 端点、收藏夹快照服务、播放、回收站 |
| `src/FavSync.Desktop/` | 托盘壳：单实例互斥、adb/7z 解析、重启链路 |

## 账目规则（README 六条之外的代码级细节）

- `RegisterBackup` 是**纯 UPDATE**，不建 item 行。恢复时必须先
  `UpsertLibraryItem`（on_phone=0, state=synced）再 `RegisterBackup`，
  顺序反了产生孤儿 backup 行
- 冷备的清单与对账一律取 **backup 行**的 `(tag_norm, content_id, md5, size)`，
  取 `item.phone_*` 在换源 / 升档后必错，恢复完会被判「未冷备」再打一遍
- 迁移顺序：`RetireVersionCopies` 必须排在 `FixContentIds` **之前**，
  反过来时正确行会让脏 content_id 跳过修正
- 对账恒等式：`onPhone = synced + upgraded + remaining + conflict`；
  统计卡全部限定 `on_phone=1`，「仅拷贝 / 已失效 / 待探测」只算 `state='phone_only'`
- 本地源路径拼接必须 `rel + "/" + name`：少了分隔符会把文件名记成粘连串、role 退化
- TarReader 对 0 字节 RegularFile 的 DataStream 返回 null（手机空弹幕是合法情况）；
  判目录只看 `EntryType`，否则给文件建空目录、整条失败
- webmux 条目：同步的 tar 排除 `*.m4s` 与 `index.json`（**index.json 绝不能拷**）；
  扫描即判 synced，`StartSync` 不得跳过 `synced + webmux`
- 画质比较统一走 `Store/QualityRank`（先比档位，同档位 HEVC/AV1 优于 AVC），
  收低档判定、换主、冷恢复计划全用它，不要各自再写比较器

## B 站 API 陷阱

- wbi 签名只认 avid；无 buvid3 → -400；越界页返回 `data:null` → total=0，要 `if(t>0)`
- 62012 = 收藏夹不可见（作者删号/隐藏），归「已失效」
- 充电视频两种表现：无试看 → playurl 87008；有试看 → `code=0` 但只有 durl 没有 dash。
  打标链路有三处（sync catch / 87008 / MarkChargeVideo），漏一处就漏标
- -412 = 触发风控，等 5~10 分钟自愈，**不要加并发硬冲**
- 下载瓶颈在手机侧上行（多路并发实测有硬上限），加并发无收益
- entry.json 三要素：顶层 title、owner_id、page_data 宽高；「补齐元数据」按此回填且幂等

## 发布

- `./publish.sh`（仓库根）→ 单 exe（约 147MB）+ `tools\`（7z/adb），产物 6 个文件 + zip
- `ExcludeFromSingleFile="true"` 是硬要求：adb/7za 是被 `Process.Start` 拉起的子进程，
  需要真实路径；漏了会构建成功、能启动，但扫描手机与冷备份**静默失效** ——
  CI 里那道「校验产物」就是为它设的
- `SelfContained` 只能用命令行全局属性传，写进 csproj 不传给被引用项目（NETSDK1150）
- 测发布包前先停生产：托盘壳有单实例互斥，第二个实例**静默退出、无日志**；
  独立测试 = 停生产 → 改用户配置目录的 `config.json` → 跑发布 exe → 还原 → 重启生产

## 协作约定

- 先对齐方案再动代码；报告先行（分节 + 表格 + 数据）；事实性判断先实测再下结论
- 改完只做本地 `git commit`，**绝不自动 push**（CI 与 Release 都挂在 push 上，
  误推等于误发版；推送只能由用户本人执行或明确要求时做）
- 文档卫生六条（检查器在 `scripts/check/check_hygiene.py`）：不写本机路径、
  私网 IP、其它私有项目名、内部里程碑编号、变更历史日期、不入库目录；
  示例地址用 `192.0.2.x` 段
