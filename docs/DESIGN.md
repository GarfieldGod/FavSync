# FavSync 设计

> 手机 B 站缓存的**单向备份器**，产出外部播放器可直接消费的 `layout=dual` 源。
> 已实现并日常使用。
>
> 冷备份（把总库再打一层离线副本）→ [DESIGN-COLD-BACKUP.md](DESIGN-COLD-BACKUP.md)

## 1. 一句话与边界

**任何时刻出现在手机上的 B 站缓存，最终都完整存在于 PC 总库里。**

不是「两端保持一致」，而是「PC 持有手机缓存里出现过的全部**条目**」——每个条目保留一份字节，
换源/升档后是最新那份，旧份进回收站（DESIGN.md §7.2）。因此：

- 手机删视频 → **不触发任何动作**（备份已经完成，这就是目的）
- 总库删视频 → 只有「移到回收站」一种，且必须用户显式点；没有任何自动清理路径
- 冲突 → 不存在，因为没有双向写，只有「手机有 / 没有」这一个事实来源

**不做**：双向同步、删除传播、墓碑、转码、remux（旁路导出除外）、内容级去重（跨 avid 的搬运识别）、
自研手机侧程序（Tailscale 这类组网工具不算）、旧 mp4 库迁移、**番剧**（`s_` 前缀目录不是
`<数字>/c_*` 形状，枚举阶段直接跳过，不猜层级——猜错会产生错误 key 或漏内容）。

## 2. 与外部播放器的分工

| | FavSync | 外部播放器 |
|---|---|---|
| 职责 | 采集、落盘、建索引 | 读取、播放、组织视图 |
| 对 `archive/` | 唯一写入者 | 只读扫描 |
| 数据库 | `.favsync/catalog.db`（同步状态） | `library.db`（播放视图） |
| 是否合并音视频 | **不合并** | 不合并（运行时合成） |

因为外部播放器能直读 m4s 双流，FavSync 完全不合并。

## 3. 核心模型

### 3.1 单向备份

```
手机缓存（随时删）  --①只读清单-->  --②按需取字节-->  PC 总库（只增内容）  -->  外部播放器（只读）
```

**C1：已同步判定只查总库，永不查手机现状。** 手机随时会删；把「手机没有」当「未同步」会每次重下。

**C2：没有任何自动删除。** 只在三种场合动字节：清掉未提交的导入事务、把条目/旧份**移进** `.trash`、
用户点「清空回收站」。前两种都只是搬移，真正删文件只有第三种。

### 3.2 总库布局 = 手机缓存镜像

```
archive/
└── <avid>/c_<cid>/                # 主副本，第一次入库后路径永不变动
    ├── entry.json / danmaku.xml
    ├── cover.jpg / danmaku.pb     # 新版缓存才有，有则一起搬
    └── <tag>/                     # 实测恒为纯数字：120/112/116/80/64/32/16
        ├── index.json
        └── video.m4s / audio.m4s
```

**整目录原样搬，不筛选、不改名、不重写 JSON。** 「整目录」的边界是 `<avid>/c_<cid>/`，**不是 `<tag>/`**——
`entry.json` / 封面 / 弹幕都在上一层，只搬 tag 子目录会漏掉它们。

好处：彻底绕开 `entry.json` 的版本差异。字段不认识也能备份；索引解析失败只影响元数据展示，不影响完整性。

> 我们从不**造** json，只**搬** json。

换主也不新建目录：新份写进同一个 `<avid>/c_<cid>/`，旧份整目录先搬进回收站（DESIGN.md §7.2）。

### 3.3 四层身份键

| 层 | 键 | 存在哪 | 回答 |
|---|---|---|---|
| 内容 | `key = <avid>_c<cid>` | `item` + 路径 | 这个视频有没有 |
| 档位 | `tag_norm = q<quality>_c<codecid>` | `backup` | 这一档有没有 |
| 版本 | `content_id` | `backup` | 这一份字节有没有 |
| 备份条目 | `(key, tag_norm, content_id)` | `backup` 主键 | 总库存过没有 |

**双轨制**：磁盘目录名保持手机原样（`120` / `112`），DB 里另存归一化的 `tag_norm`（拼上 codecid）。
跨设备比对需要 codecid 参与，但磁盘必须保持镜像，否则外部播放器与「回拷手机」都会失效。

**判定一律用 `tag_norm`，绝不用 `tag_raw`。** 否则同一档在不同 App 版本下写成 `80` 和 `80_7` 时会被当成两档，去重失效。

`content_id = v<video_md5[..10]>a<audio_md5[..6]>`；两者都缺才退化为 `s<video_size:x>a<audio_size:x>`。

**为什么身份不用字节、而「是不是同一份备份」用字节**：身份（`key` + `tag_norm`）永久，B 站改版式改不动；
版本（`content_id`）会变，UP 换源、平台重转码都会变。用字节判身份，内容一改就当新视频；
忽略字节，同档换源就会被静默丢掉。

### 3.4 key 解析：三级回退，永不「猜」

| 级 | 来源 | 说明 |
|---|---|---|
| 1 | `entry.json` 的 `avid` + `page_data.cid` | 明文，实测 5425/5427 可用；目录名只做交叉校验 |
| 2 | 目录名 `<avid>/c_<cid>` | App 的持久化格式，改了会让所有老用户缓存失效，比 json 字段稳定 |
| 3 | 路径形状不合 `<纯数字 avid>/c_<cid>` | **不猜、也不搬**：枚举阶段整条跳过，不入库、不落任何「待人工」目录 |

第 3 级守住一条底线：**宁可少算一条，不可错认成另一条。**
形状校验同时挡住了回收站、封面缓存这类误入目录（`.trash` / `covers` / 点开头目录都在枚举阶段就被跳过）。

## 4. 同步流程

### 4.0 `.incoming/` 是导入事务区

```
拷到 .incoming/<run_id>/<avid>/  →  全部校验通过  →  rename 到 archive/<avid>/
```

`rename` 同分区原子，因此 `archive/` 里永远不会出现「看起来完整但实际残缺」的条目。
它不是用户需要理解的概念——用户只面对总库和手机。

### 4.1 阶段 0：本地盘点

- 有 `catalog.db` → 直接读，O(1) 判定
- DB 丢失 / 新机器 → 扫 `archive/` 重建（路径即身份）；只能推回磁盘上看得见的东西，
  探测结论（`probe`）与 `synced_at` 这类推不回来的信息留空，交给下次同步补齐

### 4.2 阶段 1：远端盘点（只读清单，不读字节）

**判断「要不要传字节」只需要几 KB 的 JSON。** 只读每个目录下的 `entry.json`（约 1 KB）与
`<tag>/index.json`（1–3 KB），不读 m4s。几千条缓存的全量清单几分钟完成，传输量以 MB 计。

**通道：只有 adb**

| 通道 | 状态 |
|---|---|
| **adb over Tailscale（无线）** | 主路径。手机与电脑不必同地 |
| adb over USB | 同一套代码，改设备号即可 |
| MTP（WPD） | **未实现**：慢且易断，Android 11+ 多数 ROM 直接隐藏 `Android/data`，评估后放弃 |

硬理由：`adb shell` 对 `Android/data` 有读取特权，不受 SAF / MANAGE_EXTERNAL_STORAGE 沙箱限制；
MTP 在 Android 11+ 根本进不去。**adb 不是优化，是唯一通用通道。**

`inventory` 不携带 `is_completed` / `downloaded_bytes`——实测这两个字段不可信，完整性一律由 size/md5 判定。

### 4.3 阶段 2：分类（以 `backup` 为准）

| 总库 `backup` | 手机现状 | 判定 | `state` / `drift` | 动作 |
|---|---|---|---|---|
| 无 | 有，字节完整 | 待同步 | `phone_only` / none | 进入阶段 3 |
| 同 `tag_norm` + 同 `content_id` | 有 | 已备份 | `synced` / none | 跳过；文件集不同只记 `structure` |
| 同 `tag_norm`，`content_id` 不同 | 有 | 版本漂移 | `outdated` / `content` | 换主：旧份进回收站，新份落同一路径 |
| 有更高档 | 有更低档 | 低档 | `synced` / `tag_down` | 跳过——低档不入库 |
| 有更低档 | 有更高档 | 升档 | `outdated` / `tag_up` | 换主，同上 |
| 有 | 无（手机删了） | 已备份 | 不变 | 跳过（C1） |

残片判定不看 `is_completed`：实际字节 ≠ `index.json` 声明的 size 之和 → 归入「残片」，**不备份**。

### 4.4 阶段 3：可下载性探测

对每条「待补」先问：**能不能不传文件？**

| 探测结论 | 判定依据 | 动作 |
|---|---|---|
| `exact` | `view` 返回 0，playurl 有同 `tag_norm` 且 size 相等 | PC 下载，下完比 md5 |
| `lower` | 稿件活着，但该档已下线 / size 不等（换源重转码）/ 只有更低档 | 手机拷贝（备份必须是手机那份） |
| `gone` | `-404` / `-403` / 版权 / 地区 / 会员区（playurl 拿不到原档） | 手机拷贝 |
| `charge` | 充电专属：稿件活着，账号没充电 | 手机拷贝 |
| `unknown` | 网络失败，或撞风控没探出结论 | 重试 3 次，仍失败则手机拷贝 |

撞风控（`Throttled`）时**不写库当结论**——写下的会是假 `unknown`，熔断退避后重探。

**等价性必须是 size + md5 双条件**：size 相等才值得试下载（快速筛），下载后 md5 必须等于手机 `index.json`
记录的 md5，才丢弃手机那份。

收益：**用 1% 的传输量完成 100% 保真的备份**。4K 几 GB 走 PC 有线网络下载，比从手机读快 2–5 倍。

探测结论落 `probe` 表缓存，TTL 按结论性质分档：`exact` / `lower` 1 天，`gone` / `charge` 30 天，
`unknown` 1 小时（原先是 10 分钟，撞一次风控写下的假结论会在 10 分钟后集体回到候选队列、
下一轮再冲一次墙）。

### 4.5 阶段 4：执行

| 方式 | 步骤 |
|---|---|
| PC 下载 | playurl → 取 baseUrl → 写 `.incoming/<run>/` → 算 md5 → 与手机 md5 比对 → 提交 |
| 手机拷贝 | adb 读 m4s → 写 `.incoming/<run>/` → 算 md5 → 与 `index.json` 比对 → 提交 |

提交 = `rename(.incoming/<run>/<avid>, archive/<avid>)` + 写 DB。校验失败 → 删该 `.incoming` 子目录、
标 `failed`，不污染总库。

### 4.6 阶段 5：收账

提交成功 → `RegisterBackup` 写 `backup` 行，并把 `item.state` 抬到 `synced`、`drift` 清成 `none`，
上一次的 `issue_note` 一并作废（字节既然验过了，旧的报错理由不再成立）。

没有单独的「运行记录表」：每次作业的统计只进作业日志，形如
「探测完成：5427 条 · 失效 X · 仅拷贝 Y · 可替代 Z · 未知 W」，进度走 SSE。

## 5. 数据模型

表定义见 `src/FavSync.Core/Store/`。四张表各答一个问题：

| 表 | 回答 | 硬约束 |
|---|---|---|
| `item` | 视频身份 + **手机现状** | `phone_*` 每次扫描覆盖，**永不**当作总库基线 |
| `backup` | 总库里那一份在哪、是什么 | 一个 key 至多一行 `role='primary'`；换主是「删旧行 + 插新行」的**单事务**，不是原地改 `path`（那会让账与盘脱节） |
| `item_file` | 手机当前完整文件集 | L1 校验的期望值来源 |
| `probe` | 可下载性结论 + TTL | 可丢，重探即可 |

冷备份另有四张表（`cold_bucket` / `cold_item` / `cold_block` / `cold_meta`），见
[DESIGN-COLD-BACKUP.md](DESIGN-COLD-BACKUP.md)。

派生状态在 `item` 上：`state`（`phone_only` / `synced` / `outdated` / `incomplete` / `mismatch`）、
`drift`（`none` / `content` / `tag_up` / `tag_down` / `structure`，可复合）、`backup_count`、`best_tag_norm`。

**可重建性**：`item` / `backup` / `item_file` 全部可从 `archive/` 路径 + `entry.json` / `index.json` 重建。
只有 `probe` 与 `synced_at` 会丢，损失可接受。

## 6. 磁盘与启动预检

```
<ArchiveRoot>/
├── <avid>/c_<cid>/                 # 唯一的字节副本；换主后仍落这里，路径不变
├── .incoming/<run_id>/             # 导入事务区，可随时整体删除
├── .trash/<key>/                   # 软删除，带 meta.json，面板可见可清
├── .trash/<key>-swap-<ts>/         # 换主时腾出的旧字节（与上一行同级，同样带 meta.json）
└── .favsync/                       # 状态全在这里，不进镜像
    ├── catalog.db                  # 账
    ├── fav-scan.json               # 收藏夹扫描摘要（10 分钟复用，过期重扫）
    └── restore-tmp/                # 冷恢复解包临时区，必须与库同卷
```

- **总库下不写任何自有文件**（没有 `manifest.json`）。理由：`archive/` 必须能原样回拷到手机，
  任何多余文件都破坏「镜像」语义。状态全在 `.favsync/`。
- 总库**就是一个普通文件夹**，路径由用户指定，默认 `<用户盘>:\FavSyncLibrary`（不默认放程序目录，
  否则重装/升级连带丢库）。配置写在 `%LOCALAPPDATA%\FavSync\config.json`。

**启动三项预检**，任一不过拒绝启动并给出修复建议：

| 预检 | 不过的后果 |
|---|---|
| 本地固定盘 + 可写 | 网络盘上 SQLite 文件锁不可靠，会**静默损坏** catalog.db（UNC / SMB / 可移动盘一律拒绝） |
| 同分区 rename | 跨分区 `rename` 退化成「复制+删除」，原子性消失 |
| 剩余空间 ≥ 待同步体积 × 1.05 | 搬到一半空间爆掉，留混合状态 |

库不可用时降级到 `<用户盘>:\FavSyncLibrary` 并在面板挂黄条提示，**绝不写回 config.json**。

## 7. 失效与版本差异

### 7.1 三种失效

| 类型 | 表现 | 处理 |
|---|---|---|
| 稿件失效 | `view` 返回 -404/-403 | 只能手机拷贝；手机也没有 → 无解，接受 |
| 档位失效 | 稿件在但该清晰度下线 | 手机拷贝该档；PC 可补下载其他档但**不替代** |
| 缓存残片 | 实际字节 ≠ 声明字节 | **不备份**，标 `incomplete`，下次重查 |

**不要信 `is_completed`。** 实测 5427 条里 `is_completed=false` 的 6 条**全部完整**——
`downloaded_bytes` 有时只统计了单条流。把状态字段当硬闸门会误杀真实完整内容，
而这些恰好是断点续传中断、用户可能永远不再打开的条目。

判定闸门按优先级：

```
1. 实际 video+audio 字节 == index.json 的 size 之和  → 完整（忽略 is_completed）
2. 不等                                              → 残片
3. 无 index.json                                     → 用 entry.json.total_bytes 兜底
```

**唯一可信的是字节数与 md5。**

### 7.2 增量库语义：不删、不覆盖、一个条目只留一份字节

三条硬规则（代码层面就没有对应能力，不是「约定不要用」）：

1. **没有任何静默删除总库内容的路径**——删除只发生在 `.incoming/` 事务区、校验失败的临时目录，
   以及用户显式点的「移到回收站 / 清空回收站」
2. **没有任何原地覆盖**：换主是「搬走旧字节 → 入新字节 → 单事务替账」，不是往旧文件上写
3. **一个 key 只留一份字节**：`archive/<avid>/c_<cid>/` 是唯一副本，不存在「附加版本」长期仓库

于是「手机上的东西变了」只有两种结局：**换成新的那份**，或**不收**。不会出现两份并存。

| 变化 | 判定 | 结果 |
|---|---|---|
| 同档位字节变了（换源/重转码） | `tag_norm` 同、`content_id` 不同 | 新份更优 → **换主**：旧整目录 Move 到 `.trash/<key>-swap-<ts>/`，新份落同一路径，`replaceExistingPrimary` 单事务替账 |
| 手机档位更高（含同 q 更高编码） | `QualityRank` 更高 | **换主**，同上 |
| 手机档位更低 / 同档同编码 | `QualityRank` 不更高 | **不入库**，只记 `tag_down` |
| 只是文件集变了（多 cover.jpg） | `content_id` 相同 | 不新增，只记 `structure` |
| 手机删了又重下同一份 | `content_id` 相同 | 无动作 |

> 旧迁移遗留：`backup.role='version'` 与 `.versions/` 目录是早期设计的附加版本仓库，现已取消。
> 打开库时 `RetireVersionCopies` 会把整棵 `.versions` 搬进 `.trash/versions-retired-*` 再删账，
> 面板的 `/versions` 页面只是这套存量的清理出口，正常库恒为空。

#### 档位高水位

总库对一个 key 只保留见过的**最高档**。低档不备份——否则每换一次清晰度就多一份副本。

**例外必须写死**：该 key 一份备份都没有时，无论多低都要入。否则内容会因「档位不够高」永久丢失。

比较**含编码**：先比 `quality`，`quality` 相同时 HEVC / AV1 优于 AVC（同画质编码效率更高）。
这条统一收在 `QualityRank`，分类、入库拒收、换主判据、冷恢复的计划环节全用它——
否则「库里 q120/AVC、手机 q120/HEVC」会被当成等价而永远换不进来。

#### 换主不动路径

换主时新份落在同一个 `<avid>/c_<cid>/`，路径不变。外部播放器按路径认条目，看到的就是这个 key 的最新一份。

旧字节不留在库里：整目录 Move 到 `.trash/<key>-swap-<ts>/` 并写 `meta.json`，回收站可见、可清。
顺序是「先搬、搬成功才动账」，动账失败整体还原——不允许出现「账上指向已搬走的目录」。

> 为什么不干脆只落指针不落字节：那就失去「失效内容唯一副本」的价值。

### 7.3 校验分级

| 级别 | 查什么 | 成本 | 能抓出 | 抓不出 | 现状 |
|---|---|---|---|---|---|
| **L1 结构** | 文件集 + 每文件 size | 秒级 | 缺文件、多文件、截断 | 同大小的内容替换 | 已实现：入库必经 |
| **L2 标准** | 本地 m4s md5 vs `index.json` md5 | 分钟级（256 GB） | 下载替代错误、落盘损坏 | index.json 说谎；手机文件与它不符 | 已实现：下载/拷贝入库时逐流比对 |
| **L3 深度** | 手机侧 `md5sum` 全量 vs 本地 | **约 35 min** | L2 全部 + 手机文件被篡改/未真正下完 | — | **未实现**（设计保留） |

L1 / L2 不是独立动作，而是入库闸门的一部分——验不过就不提交，条目落 `incomplete` 或 `mismatch`。
没有「全库定期体检」的定时器：要主动核对走**扫库**（账实核对，等同 L1 批量版）与冷备份的桶校验（7z CRC）。

L3 之所以留了这么久没做，是因为它一旦做就极便宜：手机自己算 md5，只回传约 200 KB 文本
（UFS 直读约 125 MB/s），把「深度校验」从不可行（要 6.9 天传字节）变成「每周可跑一次」。
**它也是唯一能证明「archive 与手机完全一致」的手段**——不和任何声明值比，只和手机的字节比。

## 8. 形态与接口

**没有命令行客户端。** 实际形态是托盘壳 `FavSync.exe` + 内嵌 Kestrel（默认端口 **8720**），
用户只面对一块 Web 面板：列表 + 勾选 + 进度条。数据全走同一套 HTTP API，进度用 SSE 推。

所有 API 都挂在 **`/api/v1`** 下（不带前缀直接打 `/stats` 是 404，排障时最容易踩）；
下表境内省略该前缀。

| 组 | 端点 |
|---|---|
| 页面 | `/`、`/{file}`（静态资源） |
| 状态与配置 | `/status`、`/config`（GET / POST）、`/config/restart`、`/devices`、`/devices/connect`、`/devices/disconnect`、`/open-folder` |
| 盘点与同步 | `/stats`、`/items`、`/items/{key}/backups`、`/library`、`/scan`（手机）、`/library/scan`（总库认领）、`/sync`、`/cancel`、`/trash`（GET / POST）、`/trash/empty`、`/versions`（存量清理）、`/versions/delete`、`/maintenance/backfill-entry` |
| 作业 | `/jobs`、`/jobs/latest`、`/jobs/{id}`、`/events`（SSE） |
| 播放 | `/cover/{key}`、`/play/{key}/info`、`/play/{key}/prepare`、`/play/{key}/stream`、`/play/{key}`（POST，交给外部播放器）、`/media/preflight` |
| B 站 | `/bili/state`、`/bili/qr/start`、`/bili/qr/poll`、`/bili/logout`、`/bili/cover`、`/probe`、`/fav/download` |
| 收藏夹 | `/bili/fav/folders`、`/bili/fav/items`、`/bili/fav/scan`、`/bili/fav/scan/cancel`、`/bili/fav/index`、`/bili/fav/index/summary`、`/bili/fav/pages` |
| 在线预览 | `/bili/preview/info`、`/bili/preview/stream`（必须带 Referer 的 Range 代理） |
| 冷备份 | `/cold/status`、`/cold/estimate`、`/cold/pack`、`/cold/buckets`、`/cold/blocked`、`/cold/preview`、`/cold/import`、`/cold/restore`、`/cold/bucket/{id}/*`（`uploaded` / `uploaded-prune` / `prune` / `release` / `verify` / `open`） |
| 排障 | `/bili/probe-test`、`/bili/url-test`、`/bili/dash-test` |

`/debug/dash` 只在 Debug 构建里注册（`#if DEBUG`），发布包里不存在。

**UI 不持有任何独立状态**，所有判定在服务端——否则面板与 API 会各自演化出不同的「已同步」定义。

`/config/restart` 只有桌面壳能兑现（`IServerControl`）；直接跑 `mfav.exe` 时返回 false。

## 9. 关键实测数字

| 项 | 值 | 意味着 |
|---|---|---|
| 手机上行吞吐 | **约 390–440 KB/s，硬上限** | 1/2/4/8/16 路并发 = 264/327/327/372/390 KB/s，**并发无用** |
| 全量拷贝 256 GB | 需 6.9 天 | 「能下载就不拷贝」是唯一可行主路径 |
| 全量扫描（只读 json） | 5427 条 / 28 MB / 537 s | 瓶颈是 FUSE 单文件 open，不是带宽 |
| 上行瓶颈位置 | 手机 → PC | 下行单流可达 6 MB/s |

**一切大批量传输走二进制单流，禁止逐行回读**：元数据改走 tar 单流后 575s → 99s（5.8x）。
逐行 printf 的小写入会触发 Nagle/延迟 ACK 乒乓。

## 10. 风险

| # | 风险 | 缓解 |
|---|---|---|
| R1 | adb 链路中断（休眠/切网/VPN 掉线） | 按 avid 分批 + 断点续 + 只拉 json；失败按目录粒度重试 |
| R4 | 下载版 md5 与缓存版不等（CDN 差异） | 落到「手机拷贝」，不影响正确性 |
| R5 | B 站 API 风控/频控 | QPS 限制 + probe 缓存 + 登录态 |
| R8 | 手机常驻 VPN 耗电 | 只在同步时开；或退化为「回家插 USB」 |
| R9 | `is_completed` 谎报 | 完整性与残片一律由 size/md5 判定（DESIGN.md §7.1） |
| R10 | 手机拷贝吞吐 0.44 MB/s | 「能下载就不拷贝」为唯一主路径 |
| R11 | 手机磁盘已用 94% | 优先把「仅手机有且不可下载」的条目先落盘 |
| R13 | 总库放网络盘/跨分区 | 启动预检拒绝 UNC/SMB；`.incoming` 强制跟随总库根 |
| R14 | 手机 `md5sum` 在部分 ROM 超时 | 分批（每批 ≤500 文件）+ 单批超时重试；失败降级 L2 并标记 |
| R15 | 换源/升档触发换主，旧字节落回收站占空间 | 旧字节搬 `.trash/<key>-swap-<ts>/` 并写 meta，面板可看可清；`outdated` **默认不勾选**；一个 key 恒只留一份，`backup/item` 不会膨胀 |
