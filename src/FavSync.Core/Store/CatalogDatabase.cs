using System.Globalization;
using System.Text.Json;
using FavSync.Core.Bili;
using FavSync.Core.Scan;
using Microsoft.Data.Sqlite;

namespace FavSync.Core.Store;

public static class ItemState
{
    public const string PhoneOnly = "phone_only";    // 手机有、总库一份都没有
    public const string Synced = "synced";           // 手机这份已在总库里
    public const string Outdated = "outdated";       // 总库有备份，但手机这份是另一份（内容变/升档）
    public const string Mismatch = "mismatch";       // 总库有但校验失败
    public const string Incomplete = "incomplete";   // 手机侧本身残片，禁止同步
}

/// <summary>
/// 手机现状与总库不一致的类型。可以复合（逗号分隔），因为"内容变了"和"结构变了"会同时发生。
/// 这些值只描述**差异**，不触发任何删除或覆盖动作 —— 总库只增不改。
/// </summary>
public static class Drift
{
    public const string None = "none";
    public const string Content = "content";         // 同档位，字节变了（换源 / 重新转码）
    public const string TagUp = "tag_up";            // 手机档位比总库已备份的更高
    public const string TagDown = "tag_down";        // 手机档位更低（库里更高画质那份为准 → 「已升级」，低档不重复入库）
    public const string Structure = "structure";     // 文件集不同（新版多 cover.jpg 之类）
    public const string CodecDiff = "codec_diff";    // 同画质但编码不同（手机 HEVC / 库里 AVC），字节本就不同
    public const string WebMux = "webmux";           // 同画质同编码，但库里那份是网页下载的（web mux），
                                                     // 手机这份是原版缓存：内容一致、字节天生不同。
                                                     // 不是"内容被改"——只补元数据（entry.json/弹幕），不拷 m4s。

    public static bool Has(string? drift, string what) =>
        !string.IsNullOrEmpty(drift) && drift != None &&
        drift.Split(',').Any(d => d.Trim() == what);
}

public sealed record ItemRow(
    string Key, long Avid, long Cid, string RelDir,
    string? Title, string? Owner, int? Page,
    // 手机现状
    string? PhoneTag, string? PhoneTagNorm, int? PhoneQuality, int? PhoneCodecid,
    long PhoneSize, long PhoneVideoSize, long PhoneAudioSize,
    string? PhoneVideoMd5, string? PhoneAudioMd5, string? PhoneContentId, int PhoneFileCount,
    int? Width, int? Height,
    // 派生
    string State, string Drift, string? DriftNote, int BackupCount,
    string? BestTagNorm, int? BestQuality,
    bool OnPhone, string? ScannedAt, string? SyncedAt,
    // 探测缓存（DESIGN.md §4.4 四档；null = 待探测）
    string? ProbeVerdict = null, string? ProbeNote = null, string? ProbeCheckedAt = null,
    // 「仅低档」时 B 站能给到的最高档位（quality = q 值，codecid = 12=HEVC / 7=AVC）
    int? ProbeMaxQuality = null, int? ProbeMaxCodecid = null,
    // 最高档备份的来源标签（source=backup|download|unknown；via=copy|download，仅 backup 有）
    string? BackupSource = null, string? BackupVia = null,
    // 上一次没入库的具体原因（作业侧写：校验哪一步没过），成功入库时清空
    string? IssueNote = null, string? IssueAt = null,
    // UP 主数字 ID（B站 mid）：补它要一次 view 请求，落库后不再重复付
    long? OwnerMid = null);

public sealed record ProbeRow(string Key, string Verdict, string? Note, string CheckedAt,
    // B 站当前能给到的最高档（只在 verdict=lower 时有意义）—— 用来区分"仅拷贝"与"可升档下载"
    int? MaxQuality = null);

public sealed record ProbeCandidate(
    string Key, long Avid, long Cid, int? Quality, int? Codecid, long Size, string State,
    // 手机侧 video 流字节数：探测时拿它和 web 流长比一次，长度不同就是"同档同编码但字节不同"
    long VideoSize = 0);

public sealed record ProbeSnapshot(int Exact, int Lower, int Gone, int Unknown, int Pending);

/// <summary>
/// 同步页统计卡（两行口径）。**全部限定在手机侧**（<c>on_phone=1</c>）：
/// <c>OnPhone == Synced + Upgraded + Remaining</c>，卡片之间能直接对账。
/// 只存在库里、手机侧已删除的条目不算「已同步」，单独记在 <see cref="LibraryOnly"/>。
/// 第二行（可下载替代/已失效/仅低档/待探测等）只统计
/// <c>state ≠ 'synced'</c> 的"剩余待同步"条目 —— 下载入库即减，不再挂历史总数。
/// </summary>
public sealed record SyncActionStats(
    int OnPhone, int Synced, int Remaining,
    int Exact, int Gone, int Drifted, int Lower, int Incomplete, int Unprobed,
    // 残片/校验失败现在单列成卡（第一行），也要能从 Remaining 里对上账
    int Mismatch = 0,
    // "待探测"里有多少条是探过但没结论（探测失败），用于提示文案
    int PendingUnknown = 0,
    // 「已升级」= 库里那份画质高于手机缓存（synced 且 drift 带 tag_down）。
    // 从 Synced 里拆出来单独计数，否则「已同步」卡会混着"库里比手机好"的条目看不清。
    int Upgraded = 0,
    // 充电视频数。**已计入 Lower（仅拷贝）** —— 它确实只能从手机拷贝，
    // 单独存一份只为在卡片上写清"这 N 条里有多少是充电"，以及在筛选条上给一个子集入口。
    int Charge = 0,
    // 只在库里、手机侧已经删掉的已同步条目（on_phone=0）。它们不属于"手机条目"，
    // 所以不计入 Synced，但要在脚注里说明，否则「总库备份数」会莫名大于卡片之和。
    int LibraryOnly = 0,
    // on_phone=0 且非 synced：手机没了、库里也没有 → 数据已永久丢失。正常应为 0，
    // 非 0 说明上一次扫描后发现手机文件被删而从未入库，需要人工看一眼。
    int Lost = 0,
    // 「冲突」= 库里有东西、但和手机那份对不上（有变化 outdated + 校验失败 + 残片）。
    // 这三类**不算「待同步」**：待办不是"搬过来"，而是"决定留哪份"。
    // 第一行对账：OnPhone = Synced + Upgraded + Remaining + Conflict。
    int Conflict = 0);

public sealed record BackupRow(
    string Key, string TagNorm, string ContentId,
    string TagRaw, int? Quality, int? Codecid,
    long Size, long VideoSize, string? VideoMd5, long AudioSize, string? AudioMd5,
    int FileCount, string Path, string Role, string CreatedAt,
    string Source = "backup", string? Via = null);

/// <summary>
/// <see cref="CatalogDatabase.RegisterBackup"/> 的结果。
/// <para><b>为什么要把三元组带回来</b>：三元组是由 <c>ScanItem</c> 现算的，
/// 任何"另一套算法算出同一份字节的键"的调用方都可能算错（恢复就是这样翻过车 ——
/// 清单里的 md5 与库里的 backup 不同源时，冷备覆盖记到一个永远 join 不上的键上）。
/// 所以规则是：<b>写完以库为准，拿回真实的 (tag_norm, content_id) 再去记别的账</b>。</para>
/// </summary>
public sealed record BackupAddResult(bool Added, string Reason, string Path, string Role,
    string TagNorm = "", string ContentId = "");

/// <summary>「版本重复」页一行：一份附加版本副本（与主副本同 key，字节独立）。</summary>
public sealed record VersionRow(
    string Key, string TagRaw, string TagNorm, string ContentId,
    int? Quality, int? Codecid, long Size, long VideoSize, long AudioSize,
    string Path, string CreatedAt, string Source, string? Via,
    string? Title, string? Owner, int? BestQuality, string? RelDir);

/// <summary>存量迁移一行：收藏夹直下的条目 + 补齐 entry.json 所需的字段。</summary>
public sealed record BackfillRow(
    string Key, long Avid, long Cid, string RelDir,
    string? Title, string? Owner, long? OwnerMid, string? Bvid,
    int? Width, int? Height, int? Page, string? TagRaw, int? Quality);

/// <summary>库页卡片（只读视图：总库里已入库的条目，按入库时间倒序）。</summary>
public sealed record LibraryRow(
    string Key, long Avid, long Cid, string RelDir,
    string? Title, string? Owner, int? Page, string? Cover,
    long DurationMs, long Size, int? Quality,
    int BackupCount, string? Source, string? Via,
    string? SyncedAt, string? BackupPath, bool OnPhone,
    int? Codecid = null);

/// <summary>播放页需要的单条目信息 + 同 avid 的其他分 P（多 P 连播用）。</summary>
public sealed record PlayInfo(
    string Key, long Avid, long Cid, string? Title, string? Owner, int? Page,
    long DurationMs, long Size, int? Quality, int? Codecid, int BackupCount);

public sealed record SiblingRow(string Key, int? Page, string? Title);

/// <summary>库页"视频来源"选项卡的计数（口径与 QueryLibrary 的最高档口径一致）。</summary>
public sealed record LibrarySourceCounts(int All, int Backup, int Download, int Other);

/// <summary>总库已入库 key 集合 + 由 key 反推的 avid 集合（判断"收藏夹里这个视频在不在库"用）。</summary>
public sealed record BackupIndex(HashSet<string> Keys, HashSet<long> Avids);

public sealed record CatalogStats(
    int Total, int OnPhone, int Synced, int PhoneOnly, int Outdated, int Incomplete, int Mismatch,
    int Drifted, int Backups, long BytesOnPhone, long BytesBackedUp,
    // 「手机侧已同步」= on_phone=1 且 state='synced'。同步页卡片必须用这个口径：
    // Synced 是全库（含手机侧已删除的历史条目），拿它当「手机条目中已同步数」会与 OnPhone 对不上。
    int SyncedOnPhone = 0);

/// <summary>
/// catalog.db。每次操作开一条新连接（busy_timeout），不做单连接加锁 ——
/// 扫描/同步进行中，UI 仍要能查列表。
///
/// 模型（DESIGN.md §5）：
/// <list type="bullet">
/// <item><c>item</c>：一个 key 一行 = 身份 + **手机现状**（phone_*，每次扫描覆盖）</item>
/// <item><c>backup</c>：一个备份条目 = 总库里一份实际字节，唯一键 <c>(key, tag_norm, content_id)</c></item>
/// </list>
/// 两条硬规则：**总库只增不改**（没有 DELETE / UPDATE backup 的能力）、
/// **低档不入库**（已有更高档时不新增低档条目）。
/// </summary>
public sealed partial class CatalogDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _dbPath;
    private readonly string _connectionString;

    public CatalogDatabase(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string Path_ => _dbPath;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>旧库判定：item 表存在，但没有 <c>phone_size</c> 列。</summary>
    private static bool IsLegacyItem(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='item') > 0
               AND (SELECT COUNT(*) FROM pragma_table_info('item') WHERE name='phone_size') = 0
            """;
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private const string ItemSchema = """
            CREATE TABLE IF NOT EXISTS item(
                key              TEXT PRIMARY KEY,
                avid             INTEGER NOT NULL,
                cid              INTEGER NOT NULL,
                rel_dir          TEXT NOT NULL,
                title            TEXT,
                owner            TEXT,
                bvid             TEXT,
                cover            TEXT,
                page             INTEGER,
                width            INTEGER,
                height           INTEGER,
                duration_ms      INTEGER,

                -- 手机现状：每次扫描覆盖，**永不**被当成总库基线
                phone_tag        TEXT,
                phone_tag_norm   TEXT,
                phone_quality    INTEGER,
                phone_codecid    INTEGER,
                phone_size       INTEGER NOT NULL DEFAULT 0,
                phone_video_size INTEGER NOT NULL DEFAULT 0,
                phone_video_md5  TEXT,
                phone_audio_size INTEGER NOT NULL DEFAULT 0,
                phone_audio_md5  TEXT,
                phone_content_id TEXT,
                phone_file_count INTEGER NOT NULL DEFAULT 0,

                on_phone         INTEGER NOT NULL DEFAULT 1,
                scanned_at       TEXT NOT NULL,

                -- 派生状态
                state            TEXT NOT NULL,
                drift            TEXT NOT NULL DEFAULT 'none',
                drift_note       TEXT,
                backup_count     INTEGER NOT NULL DEFAULT 0,
                best_tag_norm    TEXT,
                best_quality     INTEGER,
                synced_at        TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_item_state ON item(state);
            CREATE INDEX IF NOT EXISTS idx_item_drift ON item(drift);
            CREATE INDEX IF NOT EXISTS idx_item_phone_size ON item(phone_size DESC);
            """;

    private const string SupportSchema = """
            -- 总库里的实际字节。一行 = 一份可独立取回的备份，永不删除、永不覆盖。
            CREATE TABLE IF NOT EXISTS backup(
                key         TEXT NOT NULL,
                tag_norm    TEXT NOT NULL,
                content_id  TEXT NOT NULL,
                tag_raw     TEXT NOT NULL,
                quality     INTEGER,
                codecid     INTEGER,
                size        INTEGER NOT NULL DEFAULT 0,
                video_size  INTEGER NOT NULL DEFAULT 0,
                video_md5   TEXT,
                audio_size  INTEGER NOT NULL DEFAULT 0,
                audio_md5   TEXT,
                file_count  INTEGER NOT NULL DEFAULT 0,
                path        TEXT NOT NULL,      -- 相对 archive 根
                role        TEXT NOT NULL,      -- 恒为 primary（version 只剩历史遗留行，由 RetireVersionCopies 清掉）
                manifest    TEXT,               -- 入库时的文件集快照（JSON）
                source      TEXT NOT NULL DEFAULT 'backup', -- backup=来自手机同步 | download=总库自下 | unknown=手动入库来源不明
                via         TEXT,               -- backup 来源的产生方式：copy=USB拷贝 | download=网页下载替代；其他 source 为 NULL
                created_at  TEXT NOT NULL,
                PRIMARY KEY(key, tag_norm, content_id)
            );
            CREATE INDEX IF NOT EXISTS idx_backup_key ON backup(key);
            CREATE INDEX IF NOT EXISTS idx_backup_quality ON backup(key, quality DESC);

            -- 手机当前的完整文件集（L1 期望值来源）
            CREATE TABLE IF NOT EXISTS item_file(
                key          TEXT NOT NULL,
                name         TEXT NOT NULL,
                role         TEXT NOT NULL,
                size         INTEGER NOT NULL,
                md5_declared TEXT,
                PRIMARY KEY(key, name)
            );
            CREATE TABLE IF NOT EXISTS probe(
                key        TEXT PRIMARY KEY,
                verdict    TEXT NOT NULL,
                note       TEXT,
                checked_at TEXT NOT NULL
            );
            """;

    public void EnsureCreated()
    {
        using var conn = Open();
        Exec(conn, SupportSchema);                          // 迁移要用到 backup，先建
        if (IsLegacyItem(conn)) MigrateLegacyItem(conn);    // RENAME 旧表 → 建新 item → 搬数据
        Exec(conn, ItemSchema);                             // item 表 + 索引（已存在则跳过）
        MigrateProbeNote(conn);                             // 旧 probe 表没有 note 列
        MigrateBackupSource(conn);                          // 旧 backup 表没有 source/via 列
        MigrateProbeQuality(conn);                          // 旧 probe 表没有 max_quality/max_codecid 列
        MigrateIssueNote(conn);                             // 旧 item 表没有 issue_note/issue_at 列
        MigrateOwnerMid(conn);                              // 旧 item 表没有 owner_mid 列（UP 主数字 ID）
        BackfillCodecDrift(conn);                           // 存量"内容已变"里的同画质不同编码 → 改判 + 写准原因
        Exec(conn, ColdSchema);                             // 冷备份账（桶 / 每条已入桶的字节 / 无法冷备清单）
        MigrateColdBucketDir(conn);                         // 旧 cold_bucket 没有 dir 列（外来桶的实际位置）
        // 顺序有讲究：**先**退休 .versions 的附加版本行，**再**修历史占位 content_id。
        // 反过来的话，"同 key 已有正确行"的检查会让脏行跳过修正
        // （<avid> 就是这样：正确的那份在 .versions 里，脏行在 primary 上）。
        RetireVersionCopies(conn);                          // .versions 取消：清掉附加版本账，字节挪进回收站
        FixContentIds(conn);                                // 历史占位 content_id（vDEADBEEF…）与 md5 不自洽
    }

    /// <summary>
    /// 一个条目只保留一份字节，<c>.versions</c>（附加版本的长期仓库）取消。
    /// 理由：那里面的字节<b>不被扫库看见、不参与冷备、不进预览</b>，只是纯占空间
    /// （生产库建了这么久只攒到 1 条 352 KB）。今后替换主副本时旧字节直接进回收站。
    /// <para>
    /// 顺序：先把 <c>.versions</c> 整棵挪进 <c>.trash/versions-retired-*</c>，挪成功才删账 ——
    /// 挪不动就原样返回，下次启动再试，绝不出现"字节看不见、账上也没了"的双重丢失。
    /// 幂等：没有 role='version' 的行就直接返回。
    /// </para>
    /// </summary>
    private void RetireVersionCopies(SqliteConnection conn)
    {
        long n;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM backup WHERE role='version'";
            n = Convert.ToInt64(cmd.ExecuteScalar());
        }
        if (n == 0) return;

        // db 在 <archive>/.favsync/catalog.db；只在目录名确实是 .favsync 时才推断库根（自定义路径不猜）
        var favsyncDir = Path.GetDirectoryName(_dbPath);
        var archive = favsyncDir is null ? null : Path.GetDirectoryName(favsyncDir);
        if (archive is not null &&
            string.Equals(Path.GetFileName(favsyncDir), ".favsync", StringComparison.OrdinalIgnoreCase))
        {
            var vs = Path.Combine(archive, ".versions");
            if (Directory.Exists(vs))
            {
                var dst = Path.Combine(archive, ".trash",
                    "versions-retired-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                try
                {
                    Directory.CreateDirectory(dst);
                    Directory.Move(vs, Path.Combine(dst, ".versions"));
                }
                catch { return; }   // 挪不动就先不动账
            }
        }

        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM backup WHERE role='version'";
            del.ExecuteNonQuery();
        }
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE item SET
                    backup_count  = (SELECT COUNT(*) FROM backup WHERE key=item.key),
                    best_tag_norm = (SELECT tag_norm FROM backup WHERE key=item.key ORDER BY quality DESC LIMIT 1),
                    best_quality  = (SELECT quality  FROM backup WHERE key=item.key ORDER BY quality DESC LIMIT 1)
                """;
            upd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 存量回填：13 条 outdated/content 实际上全是"同画质不同编码"
    /// （手机 HEVC / 库里 AVC），旧文案"内容已变（换源/重新转码）"是误报。
    /// 这里按新规则改判为 codec_diff 并把差异写进 note，不必等下次扫描。
    /// 幂等：每次都按当前文案重算这些行的 note（条件：state=outdated 且同 q、不同 codecid），
    /// 所以文案调整后重启即可刷新存量，不必等重扫。
    /// </summary>
    private static void BackfillCodecDrift(SqliteConnection conn)
    {
        var hits = new List<(string Key, int Q, int Cc, string? AMd5, long VSize, int BQ, int BCc, string? BAMd5, long BVSize)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT i.key, i.phone_quality, i.phone_codecid, i.phone_audio_md5, i.phone_video_size,
                       (SELECT quality    FROM backup b WHERE b.key=i.key ORDER BY quality DESC LIMIT 1),
                       (SELECT codecid    FROM backup b WHERE b.key=i.key ORDER BY quality DESC LIMIT 1),
                       (SELECT audio_md5  FROM backup b WHERE b.key=i.key ORDER BY quality DESC LIMIT 1),
                       (SELECT video_size FROM backup b WHERE b.key=i.key ORDER BY quality DESC LIMIT 1)
                FROM item i
                WHERE i.state='outdated' AND i.drift IN ('content','codec_diff')
                  AND i.phone_quality IS NOT NULL AND i.phone_codecid IS NOT NULL
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(5) || r.IsDBNull(6)) continue;
                var q = r.GetInt32(1);
                var bq = r.GetInt32(5);
                var cc = r.GetInt32(2);
                var bcc = r.GetInt32(6);
                if (q != bq || cc == bcc) continue;
                hits.Add((r.GetString(0), q, cc, r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetInt64(4), bq, bcc, r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? 0 : r.GetInt64(8)));
            }
        }

        foreach (var h in hits)
        {
            var fake = new ScanItem
            {
                Key = h.Key, Avid = 0, Cid = 0, Tag = "", RelDir = "",
                Quality = h.Q, Codecid = h.Cc, VideoSize = h.VSize, AudioMd5 = h.AMd5,
            };
            var note = CodecNote(fake, h.BQ, h.BCc, h.BAMd5, h.BVSize);
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE item SET drift='codec_diff', drift_note=$n WHERE key=$k AND drift IN ('content','codec_diff')";
            upd.Parameters.AddWithValue("$n", note);
            upd.Parameters.AddWithValue("$k", h.Key);
            upd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 「校验失败」要能回答"校验哪一步没过"。扫描会整行重写 drift_note，
    /// 所以失败原因必须单独一列存：由作业侧写入，成功入库时清空。
    /// </summary>
    private static void MigrateIssueNote(SqliteConnection conn)
    {
        foreach (var (col, type) in new[] { ("issue_note", "TEXT"), ("issue_at", "TEXT") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('item') WHERE name=$c";
            cmd.Parameters.AddWithValue("$c", col);
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
                Exec(conn, $"ALTER TABLE item ADD COLUMN {col} {type}");
        }
    }

    /// <summary>
    /// item 表补 owner_mid（UP 主数字 ID）。
    /// 用途：外部播放器的 dual 源按 entry.json 的 owner_id 生成合集，缺了会把所有下载件
    /// 塌进同一个「未识别 UP 主」。存量 681 条要补它得走 680 次 view（约 40 分钟），
    /// 查到就必须落库——否则换个机器/重跑一遍又得再付一次。
    /// </summary>
    private static void MigrateOwnerMid(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('item') WHERE name='owner_mid'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            Exec(conn, "ALTER TABLE item ADD COLUMN owner_mid INTEGER");
    }

    /// <summary>
    /// S4.x「仅低档」要标出 B 站能给到的最高档位 → probe 表补两列。
    /// 同时做一次性存量回填：
    /// ① note 里"最高 qNNN"能直接解析出 max_quality（存量 39 条）；
    /// ② view code 62012 的 unknown 其实也是"稿件不可见"（archive/desc 对同稿件返 62002），
    ///    归到 gone，TTL 30 天，从此不再每轮重探。
    /// </summary>
    private static void MigrateProbeQuality(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('probe') WHERE name='max_quality'
                """;
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
                Exec(conn, "ALTER TABLE probe ADD COLUMN max_quality INTEGER");
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('probe') WHERE name='max_codecid'
                """;
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
                Exec(conn, "ALTER TABLE probe ADD COLUMN max_codecid INTEGER");
        }

        // ① 从 note 回填最高档位："同档不可用，最高 q120" → 120
        Exec(conn, """
            UPDATE probe SET max_quality = CAST(substr(note, instr(note, '最高 q') + 4) AS INTEGER)
            WHERE max_quality IS NULL AND note LIKE '%最高 q%'
            """);

        // ② 62012 = 稿件不可见（view 端点给 62012，desc 端点给 62002）
        Exec(conn, """
            UPDATE probe SET verdict='gone', note='稿件不可见（62012）'
            WHERE verdict='unknown' AND note='view code 62012'
            """);

        // ③ 87008 = 充电专属视频（playurl 直接回码，稿件本身还在）。
        //    以前不认这个码 → 记成 unknown，同步页把它当"待探测"反复重试。
        Exec(conn, """
            UPDATE probe SET verdict='charge', note='充电视频：未充电或已过期，B站不给流'
            WHERE verdict='unknown' AND note LIKE '%87008%'
            """);
    }

    /// <summary>S5.x 来源标签：旧 backup 表补 source（存量都是 USB 拷贝）与 via 两列。</summary>
    private static void MigrateBackupSource(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('backup') WHERE name='source'
            """;
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            Exec(conn, "ALTER TABLE backup ADD COLUMN source TEXT NOT NULL DEFAULT 'backup'");

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM pragma_table_info('backup') WHERE name='via'";
        if (Convert.ToInt64(cmd2.ExecuteScalar()) == 0)
            Exec(conn, "ALTER TABLE backup ADD COLUMN via TEXT");
    }

    /// <summary>S4 探测结果带 note（如"同档不可用，最高 q80"）；旧 probe 表补列。</summary>
    private static void MigrateProbeNote(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('probe') WHERE name='note'
            """;
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            Exec(conn, "ALTER TABLE probe ADD COLUMN note TEXT");
    }

    /// <summary>
    /// 旧库（item 里基线字段与手机现状混在一起）迁移。关键一步：
    /// 已 synced 的条目要落成一条 <c>backup</c>，否则它在新模型里会变成"总库一份都没有"。
    /// </summary>
    private static void MigrateLegacyItem(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "ALTER TABLE item RENAME TO item_legacy";
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = ItemSchema;
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = """
                INSERT INTO item(key,avid,cid,rel_dir,title,owner,bvid,cover,page,width,height,duration_ms,
                                 phone_tag,phone_tag_norm,phone_quality,phone_codecid,
                                 phone_size,phone_video_size,phone_video_md5,phone_audio_size,phone_audio_md5,
                                 phone_content_id,phone_file_count,on_phone,scanned_at,state,synced_at)
                SELECT key,avid,cid,rel_dir,title,owner,bvid,cover,page,width,height,duration_ms,
                       tag,
                       CASE WHEN quality IS NULL THEN NULL
                            WHEN codecid IS NULL THEN 'q'||quality
                            ELSE 'q'||quality||'_c'||codecid END,
                       quality,codecid,
                       size,video_size,video_md5,audio_size,audio_md5,
                       CASE WHEN video_md5 IS NULL THEN NULL
                            ELSE 'v'||substr(video_md5,1,10)||'a'||
                                 CASE WHEN audio_md5 IS NULL THEN '0' ELSE substr(audio_md5,1,6) END END,
                       file_count,on_phone,scanned_at,state,synced_at
                FROM item_legacy
                """;
            c.ExecuteNonQuery();
        }

        // 已 synced 的旧条目 → 补一条 backup，路径按当时的 primary 布局。
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = """
                INSERT OR IGNORE INTO backup(key,tag_norm,content_id,tag_raw,quality,codecid,
                                             size,video_size,video_md5,audio_size,audio_md5,
                                             file_count,path,role,manifest,created_at)
                SELECT i.key,
                       COALESCE(i.phone_tag_norm,'tag:'||COALESCE(i.phone_tag,'?')),
                       COALESCE(i.phone_content_id,'legacy:'||i.key),
                       COALESCE(i.phone_tag,'?'), i.phone_quality, i.phone_codecid,
                       i.phone_size, i.phone_video_size, i.phone_video_md5,
                       i.phone_audio_size, i.phone_audio_md5,
                       i.phone_file_count, i.rel_dir, 'primary', NULL,
                       COALESCE(i.synced_at, i.scanned_at)
                FROM item i
                WHERE i.state='synced' AND i.synced_at IS NOT NULL;
                UPDATE item SET
                    backup_count = (SELECT COUNT(*) FROM backup b WHERE b.key=item.key),
                    best_tag_norm = (SELECT b.tag_norm FROM backup b WHERE b.key=item.key ORDER BY b.quality DESC LIMIT 1),
                    best_quality   = (SELECT b.quality  FROM backup b WHERE b.key=item.key ORDER BY b.quality DESC LIMIT 1);
                """;
            c.ExecuteNonQuery();
        }

        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "DROP TABLE item_legacy";
            c.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ───────────────────────── 扫描合并 ─────────────────────────

    /// <summary>
    /// 合并一次扫描结果。四条不变式：
    /// <list type="number">
    /// <item>只写 <c>phone_*</c> 与元数据，**绝不**改 <c>backup</c>（总库只增不改）；</item>
    /// <item>本次没扫到的条目置 <c>on_phone=0</c>，不删除；</item>
    /// <item>存在备份时，手机这份与备份不同 → <c>outdated</c> + drift，而不是"未同步"；</item>
    /// <item>手机档位低于已备份档位 → 保持 <c>synced</c>，只记 <c>tag_down</c>（低档不入库）。</item>
    /// </list>
    /// </summary>
    public (int Added, int Updated, int Gone, int Drifted) MergeScan(ScanResult scan)
    {
        var now = DateTime.UtcNow.ToString("O");
        int added = 0, updated = 0, drifted = 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        foreach (var item in scan.Items)
        {
            var prev = ReadBaseline(conn, tx, item.Key);

            var (state, drift, note) = Classify(conn, tx, item, prev);

            UpsertPhone(conn, tx, item, state, drift, note, now);
            if (prev is null) added++; else updated++;
            if (drift != Drift.None) drifted++;

            using var delFiles = conn.CreateCommand();
            delFiles.Transaction = tx;
            delFiles.CommandText = "DELETE FROM item_file WHERE key=$k";
            delFiles.Parameters.AddWithValue("$k", item.Key);
            delFiles.ExecuteNonQuery();

            foreach (var f in item.Files)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO item_file(key,name,role,size,md5_declared)
                    VALUES($k,$n,$r,$s,$m)
                    """;
                ins.Parameters.AddWithValue("$k", item.Key);
                ins.Parameters.AddWithValue("$n", f.Name);
                ins.Parameters.AddWithValue("$r", f.Role);
                ins.Parameters.AddWithValue("$s", f.Size);
                ins.Parameters.AddWithValue("$m", (object?)f.Md5Declared ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }
        }

        using var seen = conn.CreateCommand();
        seen.Transaction = tx;
        seen.CommandText = "UPDATE item SET on_phone=0 WHERE scanned_at <> $now";
        seen.Parameters.AddWithValue("$now", now);
        var gone = seen.ExecuteNonQuery();

        tx.Commit();
        return (added, updated, gone, drifted);
    }

    private sealed record Baseline(long BackupCount, int? BestQuality, int? BestCodecid, string? BestTagNorm,
                                   string? PhoneTagNorm, string? PhoneContentId, int PhoneFileCount);

    private static Baseline? ReadBaseline(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM backup WHERE key=$k),
                   (SELECT quality FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT codecid FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT tag_norm FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   phone_tag_norm, phone_content_id, phone_file_count
            FROM item WHERE key=$k
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Baseline(
            r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetInt32(1),
            r.IsDBNull(2) ? null : r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetInt32(6));
    }

    /// <summary>判定手机现状相对总库的位置。这是"标出来"的唯一来源。</summary>
    private static (string State, string Drift, string? Note) Classify(
        SqliteConnection conn, SqliteTransaction tx, ScanItem item, Baseline? prev)
    {
        // 残片：把"缺哪个流 / 差多少字节"直接写进 note，UI 才能说清少了什么
        if (item.IncompleteIssue is { } issue) return (ItemState.Incomplete, Drift.None, issue);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 前四列：是否已有完全相同的备份（tag_norm + content_id）与文件数
        // 后五列：库里**最高档**那份备份的档位与两路 md5/大小 —— 用来讲清"到底哪一个流变了"
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM backup WHERE key=$k),
                   (SELECT COUNT(*) FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid),
                   (SELECT MAX(quality) FROM backup WHERE key=$k),
                   (SELECT file_count FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid LIMIT 1),
                   (SELECT manifest  FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid LIMIT 1),
                   (SELECT tag_norm  FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT content_id FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT video_md5 FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT audio_md5 FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT video_size FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT audio_size FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT codecid    FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                   (SELECT source     FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1)
            """;
        cmd.Parameters.AddWithValue("$k", item.Key);
        cmd.Parameters.AddWithValue("$tn", item.TagNorm);
        cmd.Parameters.AddWithValue("$cid", item.ContentId);
        using var r = cmd.ExecuteReader();
        r.Read();
        var total = r.GetInt64(0);
        var sameExact = r.GetInt64(1) > 0;
        int? bestQ = r.IsDBNull(2) ? null : r.GetInt32(2);
        int? sameFileCount = r.IsDBNull(3) ? null : r.GetInt32(3);
        string? sameManifest = r.IsDBNull(4) ? null : r.GetString(4);
        string? bestTagNorm = r.IsDBNull(5) ? null : r.GetString(5);
        string? bestContentId = r.IsDBNull(6) ? null : r.GetString(6);
        string? bestVMd5 = r.IsDBNull(7) ? null : r.GetString(7);
        string? bestAMd5 = r.IsDBNull(8) ? null : r.GetString(8);
        long bestVSize = r.IsDBNull(9) ? 0 : r.GetInt64(9);
        long bestASize = r.IsDBNull(10) ? 0 : r.GetInt64(10);
        int? bestCodecid = r.IsDBNull(11) ? null : r.GetInt32(11);
        string? bestSource = r.IsDBNull(12) ? null : r.GetString(12);
        r.Close();

        if (total == 0) return (ItemState.PhoneOnly, Drift.None, null);

        var drifts = new List<string>();
        string state;
        string? note = null;

        if (sameExact)
        {
            state = ItemState.Synced;
            if (sameFileCount is { } fc && fc != item.Files.Count)
            {
                drifts.Add(Drift.Structure);
                note = StructureNote(item, sameManifest, fc);
            }
        }
        else if (QualityRank.IsBetter(bestQ, bestCodecid, item.Quality, item.Codecid))
        {
            // 库里那份更好 → 记为「已升级」（synced 的子集），手机这份不入库。
            // "更好"的判据见 QualityRank：先比 q，q 相同再比编码（HEVC/AV1 优于 AVC）。
            state = ItemState.Synced;
            drifts.Add(Drift.TagDown);
            note = $"已升级：{BetterNote(bestQ, bestCodecid, item.Quality, item.Codecid)}（库里已有更好的，这份不重复入库），" +
                   $"两者差异：{StreamDiff(item, bestVMd5, bestAMd5, bestVSize, bestASize)}";
        }
        else
        {
            state = ItemState.Outdated;
            var sameQ = item.Quality is { } qx && bestQ is { } bqx && qx == bqx;
            var diffCodec = sameQ && item.Codecid is { } pc && bestCodecid is { } bc && pc != bc;

            if (QualityRank.IsBetter(item.Quality, item.Codecid, bestQ, bestCodecid))
            {
                drifts.Add(Drift.TagUp);
                note = $"手机那份更好（{BetterNote(item.Quality, item.Codecid, bestQ, bestCodecid)}），可替换库里那份：" +
                       StreamDiff(item, bestVMd5, bestAMd5, bestVSize, bestASize);
            }
            else if (diffCodec)
            {
                // 同画质不同编码：HEVC 比 AVC 小一半是常态，绝不能报成"内容被改"
                drifts.Add(Drift.CodecDiff);
                note = CodecNote(item, bestQ, bestCodecid, libAMd5: bestAMd5, libVSize: bestVSize);
            }
            else if (sameQ && !diffCodec && bestSource == "download")
            {
                // 同画质同编码，但库里那份是「收藏夹直下」的网页版字节（source=download）。
                // web mux 与手机缓存封装不同 → 字节天生不同，**不是内容被改**。
                // 落进 else 会报「同档位内容已变」，纯属假警报（实证：
                // 手机 q120/c12 video 136.9 MB vs 库里 137.4 MB，audio 反而完全一致）。
                //
                // 用户拍板：这类**直接判已同步**，不再挂在「冲突」里占注意力。
                // 留 webmux 标记只有一个用途 —— 表示"元数据（entry.json/弹幕）还是网页版的，可以补"，
                // 同步时走补齐分支（只写元数据，不搬 m4s）。
                state = ItemState.Synced;
                drifts.Add(Drift.WebMux);
                note = $"库里是收藏夹直下的网页版、手机是原版缓存（同 q{item.Quality} 同编码，内容一致、封装不同）：" +
                       StreamDiff(item, bestVMd5, bestAMd5, bestVSize, bestASize);
            }
            else if (bestTagNorm is not null && bestContentId == item.ContentId && bestTagNorm != item.TagNorm)
            {
                // 档位标注不同但内容指纹一致：不是字节变了，只是 tag 写法不一致
                drifts.Add(Drift.Content);
                note = $"档位标注不同（手机 {item.TagNorm} / 库里 {bestTagNorm}），字节指纹相同（{item.ContentId}）";
            }
            else
            {
                drifts.Add(Drift.Content);
                note = "同档位内容已变（换源 / 重新转码）：" + StreamDiff(item, bestVMd5, bestAMd5, bestVSize, bestASize);
            }
        }

        return (state, drifts.Count == 0 ? Drift.None : string.Join(',', drifts), note);
    }

    /// <summary>
    /// "哪份更好、好在哪里"的一句话。q 不同就讲画质，q 相同就讲编码 —— 两种情况理由完全不同，
    /// 混成一句会让"库里 q116 / 手机 q116"看着像没差别。
    /// </summary>
    private static string BetterNote(int? betterQ, int? betterCodec, int? worseQ, int? worseCodec)
    {
        if (betterQ is null || worseQ is null) return "档位信息不全，按库里那份为准";
        if (betterQ.Value != worseQ.Value) return $"q{betterQ} 高于 q{worseQ}";
        return $"同 q{betterQ}，{CodecName(betterCodec)} 的码率效率高于 {CodecName(worseCodec)}";
    }

    /// <summary>
    /// 同画质、不同编码。手机缓存常是 HEVC（体积约为 AVC 的一半），库里那份可能是早期下载的 AVC。
    /// 这种"字节不同"是编码决定的，不是 UP 改了内容 —— 文案必须说清，否则每次扫描都在制造假警报。
    /// </summary>
    private static string CodecNote(ScanItem item, int? bestQ, int? bestCodecid, string? libAMd5, long libVSize)
    {
        var size = libVSize > 0 && item.VideoSize > 0
            ? (libVSize == item.VideoSize
                ? $"video 大小相同（{FmtSize(item.VideoSize)}）但 md5 不同，"
                : $"video {FmtSize(libVSize)} → {FmtSize(item.VideoSize)}，")
            : "";
        string tail;
        if (string.IsNullOrEmpty(libAMd5) || string.IsNullOrEmpty(item.AudioMd5))
            tail = "audio 缺 md5，无法对比";
        else if (string.Equals(item.AudioMd5, libAMd5, StringComparison.OrdinalIgnoreCase))
            tail = "audio 完全一致";
        else
            tail = $"audio md5 {Short(libAMd5)} → {Short(item.AudioMd5)}";

        return $"同画质 q{bestQ} 但编码不同（手机 {CodecName(item.Codecid)} / 库里 {CodecName(bestCodecid)}）：" +
               $"{size}{tail}。属编码差异，不是内容被改；入库后两种编码各存一份";
    }

    private static string CodecName(int? codecid) => codecid switch
    {
        7 => "AVC",
        12 => "HEVC",
        13 => "AV1",
        null => "未知编码",
        _ => $"codecid {codecid}",
    };

    /// <summary>
    /// "到底哪个流变了"：分 video / audio 两路比 md5，md5 缺失时退化为比大小。
    /// 这是最常被问到的一句话，写进 note 就不用再翻日志。
    /// </summary>
    private static string StreamDiff(ScanItem item, string? libVMd5, string? libAMd5, long libVSize, long libASize)
    {
        // 手机侧没有 index.json 的 md5 → 内容指纹是大小组合，只能讲大小
        if (string.IsNullOrEmpty(item.VideoMd5) && string.IsNullOrEmpty(item.AudioMd5))
        {
            var vSame = item.VideoSize == libVSize;
            var aSame = item.AudioSize == libASize;
            var bits = new List<string>();
            if (!vSame) bits.Add($"video {FmtSize(libVSize)} → {FmtSize(item.VideoSize)}");
            if (!aSame) bits.Add($"audio {FmtSize(libASize)} → {FmtSize(item.AudioSize)}");
            var which = bits.Count == 0 ? "大小完全一致（但库里那份没有 md5 记录，无法确认字节）" : string.Join("，", bits);
            return $"手机侧无 md5（index.json 缺失），只能比大小：{which}";
        }

        var vChanged = item.VideoMd5 is not null && libVMd5 is not null
            ? !string.Equals(item.VideoMd5, libVMd5, StringComparison.OrdinalIgnoreCase)
            : item.VideoMd5 is not null && libVMd5 is null ? (bool?)null : false;
        var aChanged = item.AudioMd5 is not null && libAMd5 is not null
            ? !string.Equals(item.AudioMd5, libAMd5, StringComparison.OrdinalIgnoreCase)
            : item.AudioMd5 is not null && libAMd5 is null ? (bool?)null : false;

        var parts = new List<string>();
        if (vChanged == true) parts.Add($"video md5 {Short(libVMd5)} → {Short(item.VideoMd5)}");
        if (aChanged == true) parts.Add($"audio md5 {Short(libAMd5)} → {Short(item.AudioMd5)}");
        if (vChanged == null) parts.Add("video 库里无 md5 记录，无法对比");
        if (aChanged == null) parts.Add("audio 库里无 md5 记录，无法对比");
        if (parts.Count == 0) parts.Add("两路 md5 都相同");

        var head = vChanged == true && aChanged == true ? "video、audio 都变了"
                 : vChanged == true ? "只有 video 变了（audio 相同，通常是 UP 重传/重转码）"
                 : aChanged == true ? "只有 audio 变了（video 相同）"
                 : "";
        var detail = string.Join("；", parts);
        return head.Length > 0 ? $"{head}（{detail}）" : detail;
    }

    /// <summary>文件集差异：拿备份入库时的 manifest 快照与手机当前文件集对账，报出多/少的具体文件名。</summary>
    private static string StructureNote(ScanItem item, string? manifestJson, int backupFileCount)
    {
        var phone = item.Files.Select(f => f.Name).ToList();
        if (string.IsNullOrWhiteSpace(manifestJson))
            return $"文件集不同：备份 {backupFileCount} 个文件，手机 {phone.Count} 个";

        List<string> lib;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            lib = doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("Name", out var n) ? n.GetString()
                           : e.TryGetProperty("name", out var n2) ? n2.GetString() : null)
                .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
        }
        catch
        {
            return $"文件集不同：备份 {backupFileCount} 个文件，手机 {phone.Count} 个";
        }

        var extra = phone.Except(lib, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = lib.Except(phone, StringComparer.OrdinalIgnoreCase).ToList();
        var bits = new List<string>();
        if (extra.Count > 0) bits.Add($"手机多 {NameList(extra)}");
        if (missing.Count > 0) bits.Add($"手机少 {NameList(missing)}");
        if (bits.Count == 0) bits.Add($"备份 {backupFileCount} 个 / 手机 {phone.Count} 个");
        return $"文件集不同（字节指纹相同）：{string.Join("，", bits)}";
    }

    private static string NameList(List<string> names) =>
        names.Count <= 5 ? $"[{string.Join("、", names)}]" : $"[{string.Join("、", names.Take(5))} 等 {names.Count} 个]";

    private static string Short(string? md5) =>
        string.IsNullOrEmpty(md5) ? "(无)" : md5.Length <= 10 ? md5 : md5[..10];

    private static string FmtSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:F2} GB",
    };

    private static void UpsertPhone(SqliteConnection conn, SqliteTransaction tx, ScanItem item,
        string state, string drift, string? note, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO item(key,avid,cid,rel_dir,title,owner,owner_mid,bvid,cover,page,width,height,duration_ms,
                             phone_tag,phone_tag_norm,phone_quality,phone_codecid,
                             phone_size,phone_video_size,phone_video_md5,phone_audio_size,phone_audio_md5,
                             phone_content_id,phone_file_count,on_phone,scanned_at,
                             state,drift,drift_note,backup_count,best_tag_norm,best_quality)
            VALUES($key,$avid,$cid,$rel,$title,$owner,$omid,$bvid,$cover,$page,$w,$h,$dur,
                   $ptag,$ptn,$pq,$pcc,$psize,$pvsize,$pvmd5,$pasize,$pamd5,$pcid,$pfc,1,$now,
                   $state,$drift,$note,
                   (SELECT COUNT(*) FROM backup WHERE key=$key),
                   (SELECT tag_norm FROM backup WHERE key=$key ORDER BY quality DESC LIMIT 1),
                   (SELECT quality  FROM backup WHERE key=$key ORDER BY quality DESC LIMIT 1))
            ON CONFLICT(key) DO UPDATE SET
                avid=excluded.avid, cid=excluded.cid, rel_dir=excluded.rel_dir,
                title=excluded.title, owner=excluded.owner, bvid=excluded.bvid, cover=excluded.cover,
                -- owner_mid 补起来要走网络请求（一次 view ≈ 0.3s），已查到的绝不能被后来的空值抹掉
                owner_mid=COALESCE(excluded.owner_mid, item.owner_mid),
                page=excluded.page, width=excluded.width, height=excluded.height,
                duration_ms=excluded.duration_ms,
                phone_tag=excluded.phone_tag, phone_tag_norm=excluded.phone_tag_norm,
                phone_quality=excluded.phone_quality, phone_codecid=excluded.phone_codecid,
                phone_size=excluded.phone_size, phone_video_size=excluded.phone_video_size,
                phone_video_md5=excluded.phone_video_md5, phone_audio_size=excluded.phone_audio_size,
                phone_audio_md5=excluded.phone_audio_md5, phone_content_id=excluded.phone_content_id,
                phone_file_count=excluded.phone_file_count,
                on_phone=1, scanned_at=excluded.scanned_at,
                state=excluded.state, drift=excluded.drift, drift_note=excluded.drift_note,
                backup_count=excluded.backup_count, best_tag_norm=excluded.best_tag_norm,
                best_quality=excluded.best_quality
            """;
        cmd.Parameters.AddWithValue("$key", item.Key);
        cmd.Parameters.AddWithValue("$avid", item.Avid);
        cmd.Parameters.AddWithValue("$cid", item.Cid);
        cmd.Parameters.AddWithValue("$rel", item.RelDir);
        cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)item.Owner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$omid", (object?)item.OwnerMid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bvid", (object?)item.Bvid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cover", (object?)item.Cover ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$page", (object?)item.Page ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)item.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)item.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", (object?)item.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ptag", item.Tag);
        cmd.Parameters.AddWithValue("$ptn", item.TagNorm);
        cmd.Parameters.AddWithValue("$pq", (object?)item.Quality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pcc", (object?)item.Codecid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$psize", item.TotalSize);
        cmd.Parameters.AddWithValue("$pvsize", item.VideoSize);
        cmd.Parameters.AddWithValue("$pvmd5", (object?)item.VideoMd5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pasize", item.AudioSize);
        cmd.Parameters.AddWithValue("$pamd5", (object?)item.AudioMd5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pcid", item.ContentId);
        cmd.Parameters.AddWithValue("$pfc", item.Files.Count);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$drift", drift);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ───────────────────────── 备份条目 ─────────────────────────

    /// <summary>
    /// 登记一条备份。**只插入，不更新已有行** —— 没有覆盖路径。
    /// <paramref name="source"/>：backup=来自手机同步（此时 <paramref name="via"/>=copy/download）|
    /// download=总库自己下载入库 | unknown=手动复制来源不明。
    /// <para>
    /// <paramref name="replaceExistingPrimary"/>=true 时是**替换**语义：
    /// 先把库里那条主副本的账删掉，再把新行按主副本插入，单事务。用于"手机那份更好 / 内容变了
    /// → 直接替换主副本"；旧字节由调用方在动账前搬进回收站（.versions 不再做长期仓库）。
    /// </para>
    /// 返回 <c>Added=false</c> 时 <paramref name="Reason"/> 说明为什么没落库（重复 / 低档）。
    /// </summary>
    public BackupAddResult RegisterBackup(ScanItem item, string archiveRoot,
        string source = "backup", string? via = null, bool forcePrimary = false,
        bool replaceExistingPrimary = false)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var row = ReadBaseline(conn, tx, item.Key);

        if (replaceExistingPrimary)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM backup WHERE key=$k AND role='primary'";
            del.Parameters.AddWithValue("$k", item.Key);
            del.ExecuteNonQuery();
            // 旧主账已删 → 后面的"低档拒收"与 role 判定都按"库里没有"来算，
            // 这样新份自然成为主副本（不需要 forcePrimary）
            row = null;
        }

        using (var chk = conn.CreateCommand())
        {
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid";
            chk.Parameters.AddWithValue("$k", item.Key);
            chk.Parameters.AddWithValue("$tn", item.TagNorm);
            chk.Parameters.AddWithValue("$cid", item.ContentId);
            if (Convert.ToInt64(chk.ExecuteScalar()) > 0)
            {
                var dupPath = FindBackupPath(conn, tx, item.Key, item.TagNorm, item.ContentId);
                tx.Commit();
                return new BackupAddResult(false, "同一档位同一内容的备份已存在，未重复写入", dupPath, "primary",
                    item.TagNorm, item.ContentId);
            }
        }

        // 低档不入库。判据是 QualityRank（先比 q，q 相同比编码），不再只比 q ——
        // 否则"库里 q120/AVC、手机 q120/HEVC"会被当成等价放进来，两份同画质白占空间。
        // best_quality 未知时放行（历史脏数据不拦，扫描会重新判定）。
        if (row is { BestQuality: not null } &&
            QualityRank.Compare(item.Quality, item.Codecid, row.BestQuality, row.BestCodecid) <= 0)
        {
            tx.Commit();
            return new BackupAddResult(false,
                $"手机这份（q{item.Quality?.ToString() ?? "?"}/{QualityRank.CodecName(item.Codecid)}）" +
                $"不优于已备份（q{row.BestQuality}/{QualityRank.CodecName(row.BestCodecid)}），不重复入库",
                "", "rejected", item.TagNorm, item.ContentId);
        }

        // 一个条目只保留一份字节（.versions 已取消）：
        // 库里已有另一份时不再新增"附加版本"，要么替换（replaceExistingPrimary）、要么不收。
        // 这条同时挡住了"账上写 .versions、盘上却在 rel_dir"的账实不符 —— 那个分支已经不存在了。
        var isPrimary = forcePrimary || row is null || row.BackupCount == 0;
        if (!isPrimary)
        {
            tx.Commit();
            return new BackupAddResult(false,
                "库里已有这个条目的另一份字节，不再新增附加版本（要换请走替换：同步时会自动替换）",
                "", "rejected", item.TagNorm, item.ContentId);
        }
        var role = "primary";
        var relPath = item.RelDir;

        var manifest = JsonSerializer.Serialize(
            item.Files.Select(f => new { f.Name, f.Role, f.Size, f.Md5Declared }), JsonOptions);

        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO backup(key,tag_norm,content_id,tag_raw,quality,codecid,
                                   size,video_size,video_md5,audio_size,audio_md5,
                                   file_count,path,role,manifest,source,via,created_at)
                VALUES($k,$tn,$cid,$traw,$q,$cc,$s,$vs,$vmd5,$as_,$amd5,$fc,$path,$role,$mf,$source,$via,$now)
                """;
            ins.Parameters.AddWithValue("$k", item.Key);
            ins.Parameters.AddWithValue("$tn", item.TagNorm);
            ins.Parameters.AddWithValue("$cid", item.ContentId);
            ins.Parameters.AddWithValue("$traw", item.Tag);
            ins.Parameters.AddWithValue("$q", (object?)item.Quality ?? DBNull.Value);
            ins.Parameters.AddWithValue("$cc", (object?)item.Codecid ?? DBNull.Value);
            ins.Parameters.AddWithValue("$s", item.TotalSize);
            ins.Parameters.AddWithValue("$vs", item.VideoSize);
            ins.Parameters.AddWithValue("$vmd5", (object?)item.VideoMd5 ?? DBNull.Value);
            ins.Parameters.AddWithValue("$as_", item.AudioSize);
            ins.Parameters.AddWithValue("$amd5", (object?)item.AudioMd5 ?? DBNull.Value);
            ins.Parameters.AddWithValue("$fc", item.Files.Count);
            ins.Parameters.AddWithValue("$path", relPath);
            ins.Parameters.AddWithValue("$role", role);
            ins.Parameters.AddWithValue("$mf", manifest);
            ins.Parameters.AddWithValue("$source", source);
            ins.Parameters.AddWithValue("$via", (object?)via ?? DBNull.Value);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE item SET
                    backup_count  = (SELECT COUNT(*) FROM backup WHERE key=$k),
                    best_tag_norm = (SELECT tag_norm FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                    best_quality  = (SELECT quality  FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                    synced_at     = $now,
                    state = CASE WHEN state='incomplete' THEN state ELSE 'synced' END,
                    drift = CASE WHEN state='incomplete' THEN drift ELSE 'none' END,
                    drift_note = CASE WHEN state='incomplete' THEN drift_note ELSE NULL END,
                    -- 字节已经成功入库 → 上一次"校验失败"的原因作废
                    issue_note = NULL,
                    issue_at = NULL
                WHERE key=$k
                """;
            upd.Parameters.AddWithValue("$k", item.Key);
            upd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            upd.ExecuteNonQuery();
        }

        tx.Commit();
        _ = archiveRoot;
        return new BackupAddResult(true, "已作为主副本入库", relPath,
            role, item.TagNorm, item.ContentId);
    }

    /// <summary>账实核对用的一行：主副本账目 + 落盘位置。</summary>
    public sealed record LibraryPrimary(string Key, string RelDir, string TagRaw,
        long VideoSize, long AudioSize);

    /// <summary>
    /// 全部主副本账目（供扫库做账实核对）。只读 db，不碰磁盘。
    /// </summary>
    public List<LibraryPrimary> LibraryPrimaries()
    {
        var list = new List<LibraryPrimary>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.key, i.rel_dir, b.tag_raw, b.video_size, b.audio_size
            FROM item i JOIN backup b ON b.key = i.key AND b.role = 'primary'
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LibraryPrimary(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    /// <summary>
    /// 全部已入账的 <c>(key, tag_norm, content_id)</c>。扫库时先查这个集合，
    /// 命中的直接算重复，不必再走 <see cref="RegisterBackup"/> 的完整事务
    /// （6200 条里绝大多数是这种情况，能省掉一半时间）。
    /// </summary>
    public HashSet<string> ExistingBackupIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, tag_norm, content_id FROM backup";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(BackupId(r.GetString(0), r.GetString(1), r.GetString(2)));
        return set;
    }

    /// <summary>全部已存在的条目 key。扫库时用来判断「磁盘上有但 item 表没有」。</summary>
    public HashSet<string> ExistingItemKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key FROM item";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>把 <c>(key, tag_norm, content_id)</c> 拼成一个可直接比对的字符串。</summary>
    public static string BackupId(string key, string tagNorm, string contentId)
        => $"{key}\u0001{tagNorm}\u0001{contentId}";

    private static string FindBackupPath(SqliteConnection conn, SqliteTransaction tx,
        string key, string tagNorm, string contentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$tn", tagNorm);
        cmd.Parameters.AddWithValue("$cid", contentId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    /// <summary>
    /// 收藏夹直下（S6）：为"手机上不存在、总库直接下载"的条目落 item 行。
    /// 只在条目不存在时插入（已有手机条目原样保留，其状态交由 RegisterBackup 翻转）；
    /// phone_* 字段填的是**实际下载账目**（档位/大小/md5），on_phone=0 表示手机侧无此条目。
    /// 返回 true = 新插入。
    /// </summary>
    public bool UpsertLibraryItem(ScanItem item)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO item(key,avid,cid,rel_dir,title,owner,owner_mid,bvid,cover,page,width,height,duration_ms,
                                 phone_tag,phone_tag_norm,phone_quality,phone_codecid,
                                 phone_size,phone_video_size,phone_video_md5,phone_audio_size,phone_audio_md5,
                                 phone_content_id,phone_file_count,on_phone,scanned_at,state)
                VALUES($key,$avid,$cid,$rel,$title,$owner,$omid,$bvid,$cover,$page,$w,$h,$dur,
                       $ptag,$ptn,$pq,$pcc,$psize,$pvsize,$pvmd5,$pasize,$pamd5,$pcid,$pfc,0,$now,'synced')
                ON CONFLICT(key) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("$key", item.Key);
            cmd.Parameters.AddWithValue("$avid", item.Avid);
            cmd.Parameters.AddWithValue("$cid", item.Cid);
            cmd.Parameters.AddWithValue("$rel", item.RelDir);
            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owner", (object?)item.Owner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$omid", (object?)item.OwnerMid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bvid", (object?)item.Bvid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cover", (object?)item.Cover ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$page", (object?)item.Page ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$w", (object?)item.Width ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$h", (object?)item.Height ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", (object?)item.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ptag", item.Tag);
            cmd.Parameters.AddWithValue("$ptn", item.TagNorm);
            cmd.Parameters.AddWithValue("$pq", (object?)item.Quality ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pcc", (object?)item.Codecid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$psize", item.TotalSize);
            cmd.Parameters.AddWithValue("$pvsize", item.VideoSize);
            cmd.Parameters.AddWithValue("$pvmd5", (object?)item.VideoMd5 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pasize", item.AudioSize);
            cmd.Parameters.AddWithValue("$pamd5", (object?)item.AudioMd5 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pcid", item.ContentId);
            cmd.Parameters.AddWithValue("$pfc", item.Files.Count);
            cmd.Parameters.AddWithValue("$now", now);
            var inserted = cmd.ExecuteNonQuery() > 0;

            if (inserted)
            {
                foreach (var f in item.Files)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT OR REPLACE INTO item_file(key,name,role,size,md5_declared)
                        VALUES($k,$n,$r,$s,$m)
                        """;
                    ins.Parameters.AddWithValue("$k", item.Key);
                    ins.Parameters.AddWithValue("$n", f.Name);
                    ins.Parameters.AddWithValue("$r", f.Role);
                    ins.Parameters.AddWithValue("$s", f.Size);
                    ins.Parameters.AddWithValue("$m", (object?)f.Md5Declared ?? DBNull.Value);
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return inserted;
        }
    }

    /// <summary>
    /// 彻底删除一个条目的全部记录（item / backup / item_file / probe 四表）。
    ///
    /// 这是全库**唯一**的删除入口，且只能由用户主动触发（条目已先被移进 .trash）。
    /// 「总库只增不删」约束的是系统自动行为不删备份字节，不约束用户删除自己下错的条目。
    /// </summary>
    public void DeleteItem(string key)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
        {
            "DELETE FROM item_file WHERE key=$k",
            "DELETE FROM probe    WHERE key=$k",
            "DELETE FROM backup   WHERE key=$k",
            "DELETE FROM item     WHERE key=$k",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 删除一条**历史附加版本**（role='version'）的账目，并重算 item 的 backup_count / best_*。
    /// 字节由调用方先搬进 .trash —— 顺序铁律仍然是「先搬磁盘、成功才动库」。
    /// 只服务存量清理（<c>/versions/delete</c>）：现行入库不再产生 version 行，正常库永远查不到东西。
    /// 主副本（role='primary'）删不掉：那会让整个条目失去唯一真相。
    /// </summary>
    public bool DeleteBackupVersion(string key, string tagNorm, string contentId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int n;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM backup
                WHERE key=$k AND tag_norm=$tn AND content_id=$cid AND role='version'
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$tn", tagNorm);
            cmd.Parameters.AddWithValue("$cid", contentId);
            n = cmd.ExecuteNonQuery();
        }
        if (n == 0) { tx.Commit(); return false; }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE item SET
                    backup_count  = (SELECT COUNT(*) FROM backup WHERE key=$k),
                    best_tag_norm = (SELECT tag_norm FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1),
                    best_quality  = (SELECT quality  FROM backup WHERE key=$k ORDER BY quality DESC LIMIT 1)
                WHERE key=$k
                """;
            upd.Parameters.AddWithValue("$k", key);
            upd.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    /// <summary>
    /// 当前主副本（role='primary'）那条。换主时先知道它的 tag/content_id，才能算旧字节该挪去哪。
    /// 一个条目只留一份字节，所以正常情况下必有；没有说明账目是历史遗留，返回 null 交调用方决定。
    /// </summary>
    public BackupRow? GetPrimary(string key)
    {
        var all = ListBackups(key);
        return all.FirstOrDefault(b => b.Role == "primary") ?? all.FirstOrDefault();
    }

    /// <summary>
    /// 「收藏夹直下」入库的条目（backup.source='download'）—— 存量迁移的目标集。
    /// 它们落盘时 entry.json 是我们合成的极简版（缺顶层 title / owner_id / 宽高），
    /// 外部播放器扫到会退化。补齐所需的字段 db 里**全都有**，只有 owner_id 要额外请求。
    /// </summary>
    public IReadOnlyList<BackfillRow> ListDownloadEntries()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.key, i.avid, i.cid, i.rel_dir, i.title, i.owner, i.owner_mid, i.bvid,
                   i.width, i.height, i.page,
                   (SELECT b.tag_raw  FROM backup b WHERE b.key=i.key ORDER BY b.quality DESC LIMIT 1),
                   (SELECT b.quality  FROM backup b WHERE b.key=i.key ORDER BY b.quality DESC LIMIT 1)
            FROM item i
            WHERE EXISTS(SELECT 1 FROM backup b WHERE b.key=i.key AND b.source='download')
            ORDER BY i.key
            """;
        var list = new List<BackfillRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BackfillRow(
                r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetInt32(10),
                r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetInt32(12)));
        }
        return list;
    }

    /// <summary>
    /// 所有历史附加版本（role='version'）—— 存量清理接口的数据源。
    /// 现行换主把旧字节搬进 <c>.trash/&lt;key&gt;-swap-&lt;ts&gt;/</c>，不再产生 version 行；
    /// 这里能查到东西只说明库是从「一个 key 可有多份字节」的旧版本升上来的。
    /// </summary>
    public IReadOnlyList<VersionRow> ListVersions()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.key, b.tag_raw, b.tag_norm, b.content_id, b.quality, b.codecid, b.size,
                   b.video_size, b.audio_size, b.path, b.created_at, b.source, b.via,
                   i.title, i.owner, i.best_quality, i.rel_dir
            FROM backup b LEFT JOIN item i ON i.key = b.key
            WHERE b.role='version'
            ORDER BY b.size DESC
            """;
        var list = new List<VersionRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VersionRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.GetInt64(6), r.GetInt64(7), r.GetInt64(8),
                r.GetString(9), r.GetString(10),
                r.IsDBNull(11) ? "backup" : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetInt32(15),
                r.IsDBNull(16) ? null : r.GetString(16)));
        }
        return list;
    }

    public IReadOnlyList<BackupRow> ListBackups(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key,tag_norm,content_id,tag_raw,quality,codecid,size,video_size,video_md5,
                   audio_size,audio_md5,file_count,path,role,created_at,source,via
            FROM backup WHERE key=$k ORDER BY quality DESC, created_at ASC
            """;
        cmd.Parameters.AddWithValue("$k", key);
        var list = new List<BackupRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BackupRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.GetInt64(6), r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.GetInt32(11), r.GetString(12), r.GetString(13), r.GetString(14),
                r.IsDBNull(15) ? "backup" : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetString(16)));
        }
        return list;
    }

    // ───────────────────────── 查询 ─────────────────────────

    public CatalogStats Stats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              COUNT(*),
              COALESCE(SUM(CASE WHEN on_phone=1 THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='synced' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='phone_only' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='outdated' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='incomplete' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN state='mismatch' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN drift<>'none' THEN 1 ELSE 0 END),0),
              (SELECT COUNT(*) FROM backup),
              COALESCE(SUM(CASE WHEN on_phone=1 THEN phone_size ELSE 0 END),0),
              (SELECT COALESCE(SUM(size),0) FROM backup),
              COALESCE(SUM(CASE WHEN on_phone=1 AND state='synced' THEN 1 ELSE 0 END),0)
            FROM item
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new CatalogStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new CatalogStats(
            (int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3),
            (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6), (int)r.GetInt64(7),
            (int)r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), (int)r.GetInt64(11));
    }

    /// <summary>
    /// 列表查询。默认排序 = 风险优先（不可下载的排前面）：
    /// 待同步 → 版本漂移 → 残片/校验失败 → 其余，组内按体积降序。
    /// </summary>
    /// <summary>
    /// 默认排序 = **不可替代性优先**：
    /// ① gone/lower（体积降序）② unknown ③ exact（体积降序）④ 待探测 ⑤ synced 置底。
    /// 没有探测数据时退化为"待同步 → 有变化 → 残片 → 其余"，组内体积降序。
    /// </summary>
    public (IReadOnlyList<ItemRow> Rows, int Total) Query(
        string filter = "all", string? keyword = null, int offset = 0, int limit = 200)
    {
        var where = filter switch
        {
            // 待同步 = 手机上有、**库里一份都没有**（phone_only），与「待同步」卡同口径。
            // **必须带 on_phone=1**：手机侧已删且没入库的条目无从同步，不算待办。
            // 口径：outdated/mismatch/incomplete 不算待同步，它们归「冲突」
            // —— 这三类库里有东西，缺的不是"搬过来"，而是"用户决定留哪份"。
            "todo" => "WHERE item.state='phone_only' AND item.on_phone=1",
            // 冲突 = 手机那份与库里那份对不上，需要人决定（替换 / 以库里为准）。
            // 三类合一：有变化（outdated）+ 校验失败（mismatch）+ 残片（incomplete）。
            "conflict" => "WHERE item.state IN ('outdated','mismatch','incomplete')",
            // 冲突的子类（前端在「冲突」标签下挂一排子筛选）。webmux / structure 不在这里：
            // 它们是 synced（内容一致、只差封装或文件集），归「已同步」。
            "conflict-upgrade" => "WHERE item.state='outdated' AND item.drift LIKE '%tag_up%'",
            "conflict-codec" => "WHERE item.state='outdated' AND item.drift LIKE '%codec_diff%'",
            "conflict-content" => "WHERE item.state='outdated' AND item.drift LIKE '%content%'",
            "conflict-structure" => "WHERE item.drift LIKE '%structure%'",
            // 「已同步」与「已升级」互斥：已升级 = 库里那份画质比手机缓存高（tag_down），
            // 两者相加 == 「已同步」卡的数字，这样列表点进去的条数与卡片能一眼对上。
            // 口径是**手机侧面**（on_phone=1）：只在库里的那批单独走 libraryonly 标签
            "synced" => "WHERE item.state='synced' AND item.on_phone=1" +
                        " AND item.drift NOT LIKE '%tag_down%'",
            "upgraded" => "WHERE item.state='synced' AND item.on_phone=1" +
                          " AND item.drift LIKE '%tag_down%'",
            // 手机侧已删除、只剩库里那份（不计入「已同步」卡，但要能点开看）
            "libraryonly" => "WHERE item.state='synced' AND item.on_phone=0",
            "outdated" => "WHERE item.state='outdated'",
            // 「有变化」卡 = outdated 计数。标签必须同口径：drift<>'none' 会把已同步的
            // 降档/文件集变化也算进来（它们是 synced + 徽标，属于「已同步」）
            "drift" => "WHERE item.state='outdated'",
            "incomplete" => "WHERE item.state='incomplete'",
            "mismatch" => "WHERE item.state='mismatch'",
            "onphone" => "WHERE item.on_phone=1",
            // ── 以下四个可下载性口径与 SyncActions() 卡片严格互斥（一物一篮）──
            // 只统计「库里一份都没有」的条目（phone_only）。outdated 不再参与：
            // 库里有东西，要做的不是"下载替代"，而是在「冲突」里决定留哪份。
            // 可替代下载：探测 exact（同档同编码、可 md5 校验）且结论没过期；
            // 或者 B 站最高档**高于**手机档（升档下载，内容只会更好）—— 与 DownloadGate 严格一致
            "downloadable" => "WHERE item.state='phone_only'" +
                              " AND (p.verdict='exact'" +
                              "      OR (p.verdict='lower' AND p.max_quality IS NOT NULL" +
                              "          AND item.phone_quality IS NOT NULL AND p.max_quality > item.phone_quality))" +
                              " AND julianday('now')-julianday(p.checked_at) < 1",
            // 仅拷贝：B站给不出手机那份的同一档位，只能从手机拷原件。三种原因分开显示：
            //   ① max_quality < 手机档位  → 画质更低
            //   ② max_quality == 手机档位 → 同画质但编码不同（手机 HEVC / B站 AVC·AV1）
            //   ③ charge 充电视频（playurl 87008）→ 稿件活着，但账号没充电，B站不给流
            // ①② TTL 1 天；③ 是账号级权限，30 天（与 gone 同档，不天天重探）
            "lower" => "WHERE item.state='phone_only'" +
                       " AND ((p.verdict='lower' AND NOT (p.max_quality IS NOT NULL AND item.phone_quality IS NOT NULL" +
                       "          AND p.max_quality > item.phone_quality)" +
                       "      AND julianday('now')-julianday(p.checked_at) < 1)" +
                       "   OR (p.verdict='charge' AND julianday('now')-julianday(p.checked_at) < 30))",
            // 充电视频 = 「仅拷贝」的子集，单开一个列表入口（想看自己有哪些要充电的）
            "charge" => "WHERE p.verdict='charge' AND item.state='phone_only'" +
                        " AND julianday('now')-julianday(p.checked_at) < 30",
            // 已失效：探测 gone（不可替代，只能趁手机还在赶紧拷贝），TTL 30 天
            "gone" => "WHERE p.verdict='gone' AND item.state='phone_only'" +
                      " AND julianday('now')-julianday(p.checked_at) < 30",
            // 待探测 = 没有可用结论：没探过 / 结论过期 / 探测失败（unknown 等）
            // 范围**只有 phone_only**（库里一份都没有）：已经入过库的条目探测结果不影响
            // 任何自动决策，把它们算成"待探测"只会常年挂着吓人。
            // 过期的不再占着 exact/lower/gone/charge 的位置，只进这里 —— 这样四卡相加 == 待同步
            "pending" => "WHERE item.state='phone_only' AND (p.verdict IS NULL" +
                         " OR p.verdict NOT IN ('exact','lower','gone','charge')" +
                         " OR (p.verdict IN ('exact','lower') AND julianday('now')-julianday(p.checked_at) >= 1)" +
                         " OR (p.verdict='gone' AND julianday('now')-julianday(p.checked_at) >= 30)" +
                         " OR (p.verdict='charge' AND julianday('now')-julianday(p.checked_at) >= 30))",
            _ => "",
        };
        var kw = string.IsNullOrWhiteSpace(keyword) ? null : $"%{keyword.Trim()}%";
        if (kw is not null)
        {
            where += where.Length == 0 ? "WHERE " : " AND ";
            where += "(item.title LIKE $kw OR item.owner LIKE $kw OR item.key LIKE $kw)";
        }

        using var conn = Open();

        int total;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM item LEFT JOIN probe p ON p.key=item.key {where}";
            if (kw is not null) countCmd.Parameters.AddWithValue("$kw", kw);
            total = Convert.ToInt32(countCmd.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ItemColumns}, {ProbeColumns},
                   (SELECT b.source FROM backup b WHERE b.key=item.key ORDER BY b.quality DESC LIMIT 1),
                   (SELECT b.via    FROM backup b WHERE b.key=item.key ORDER BY b.quality DESC LIMIT 1),
                   item.issue_note, item.issue_at, item.owner_mid
            FROM item LEFT JOIN probe p ON p.key=item.key
            {where}
            ORDER BY
              CASE WHEN item.state='synced' THEN 4
                   WHEN p.verdict IN ('gone','lower','charge') THEN 0
                   WHEN p.verdict='unknown' THEN 1
                   WHEN p.verdict='exact' THEN 2
                   ELSE 3 END,
              item.phone_size DESC
            LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        if (kw is not null) cmd.Parameters.AddWithValue("$kw", kw);

        var rows = new List<ItemRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(MapItem(r));
        return (rows, total);
    }

    /// <summary>
    /// 库页视图：总库里真正有字节的条目（synced_at 非空），按入库时间倒序。
    /// 每条 item 取"最高档那份备份"的账目（size/quality/source/via/path）。
    /// 与 <see cref="Query"/> 的区别是视角：那边看"手机现状"，这边只看总库。
    /// </summary>
    /// <param name="source">
    /// 视频来源选项卡：<c>null</c>/<c>all</c> 全部；<c>backup</c> 手机备份；
    /// <c>download</c> 库内下载；<c>other</c> 其余（unknown / 手工入库等）。
    /// </param>
    public (IReadOnlyList<LibraryRow> Rows, int Total) QueryLibrary(
        string? keyword = null, int offset = 0, int limit = 60, string? source = null)
    {
        var kw = string.IsNullOrWhiteSpace(keyword) ? null : $"%{keyword.Trim()}%";
        var kwClause = kw is null ? "" : " AND (i.title LIKE $kw OR i.owner LIKE $kw OR i.key LIKE $kw)";

        var srcClause = SourceClause(source);

        using var conn = Open();

        int total;
        using (var countCmd = conn.CreateCommand())
        {
            // srcClause 里的 b 别名属于 EXISTS 子查询（外层 count 没有 join）
            countCmd.CommandText = $"""
                SELECT COUNT(*) FROM item i
                WHERE i.synced_at IS NOT NULL{kwClause}
                  AND EXISTS (SELECT 1 FROM backup b WHERE b.key = i.key{srcClause})
                """;
            if (kw is not null) countCmd.Parameters.AddWithValue("$kw", kw);
            total = Convert.ToInt32(countCmd.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT i.key, i.avid, i.cid, i.rel_dir, i.title, i.owner, i.page, i.cover,
                   i.duration_ms, b.size, b.quality, i.backup_count, b.source, b.via,
                   i.synced_at, b.path, i.on_phone, b.codecid
            FROM item i
            JOIN backup b ON b.key = i.key
            WHERE i.synced_at IS NOT NULL{kwClause}{srcClause}
              AND b.rowid = (SELECT b2.rowid FROM backup b2 WHERE b2.key = i.key
                              ORDER BY b2.quality DESC, b2.created_at ASC LIMIT 1)
            ORDER BY i.synced_at DESC, i.key
            LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        if (kw is not null) cmd.Parameters.AddWithValue("$kw", kw);

        var rows = new List<LibraryRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new LibraryRow(
                r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? 0 : r.GetInt64(8),
                r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetInt32(10),
                r.GetInt32(11),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.GetInt32(16) != 0,
                r.IsDBNull(17) ? null : r.GetInt32(17)));
        }
        return (rows, total);
    }

    /// <summary>来源选项卡的 SQL 片段。<paramref name="source"/> 为 null/all 时返回空串。</summary>
    private static string SourceClause(string? source) => source?.ToLowerInvariant() switch
    {
        "backup" => " AND b.source = 'backup'",
        "download" => " AND b.source = 'download'",
        "other" => " AND b.source NOT IN ('backup','download')",
        _ => "",
    };

    /// <summary>各来源条目数（同一"最高档备份"口径），给库页选项卡上的角标用。</summary>
    public LibrarySourceCounts LibrarySourceCounts()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.source, COUNT(*)
            FROM item i
            JOIN backup b ON b.key = i.key
            WHERE i.synced_at IS NOT NULL
              AND b.rowid = (SELECT b2.rowid FROM backup b2 WHERE b2.key = i.key
                              ORDER BY b2.quality DESC, b2.created_at ASC LIMIT 1)
            GROUP BY b.source
            """;
        int all = 0, backup = 0, download = 0, other = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var src = r.IsDBNull(0) ? "unknown" : r.GetString(0);
            var n = (int)r.GetInt64(1);
            all += n;
            if (src == "backup") backup += n;
            else if (src == "download") download += n;
            else other += n;
        }
        return new LibrarySourceCounts(all, backup, download, other);
    }

    /// <summary>
    /// 总库已入库的 key / avid 集合。收藏夹扫描判定"在不在库"用：
    /// cid 已知按 key 精确判，cid 缺失（fav/resource/list 偶尔不回）退化成 avid 粒度。
    /// </summary>
    public BackupIndex BackupIndex()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT key FROM backup";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var avids = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var k = r.GetString(0);
            keys.Add(k);
            var cut = k.IndexOf('_');
            if (cut > 0 && long.TryParse(k.AsSpan(0, cut), out var avid)) avids.Add(avid);
        }
        return new BackupIndex(keys, avids);
    }

    /// <summary>
    /// 该稿件在库里是否已有任意一条备份（avid 粒度，与 <see cref="BackupIndex"/> 的 Avids 同口径）。
    /// <para>收藏夹作业回报用它复核"这条是不是真进去了"：下载中途失败
    /// （audio 流 0 字节、整条没入库）却回报成 synced，快照被标成「已在库」——之后整夹/选P下载
    /// 都会把它预剔除，等于把条目永久锁死、每次扫描都挂在待办里。</para>
    /// </summary>
    public bool HasBackupForAvid(long avid)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // key 形如 "<avid>_c<cid>"：带上分隔符一起比，免得 avid=116 把 1164 的条目算进来
        cmd.CommandText = "SELECT 1 FROM backup WHERE key LIKE $p LIMIT 1";
        cmd.Parameters.AddWithValue("$p", $"{avid}_c%");
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// 已判定为「充电专属视频」的 avid 集合（probe.verdict='charge'）。
    /// 收藏夹扫描用它给条目打「充电」标：这些稿件的 playurl 恒回 87008，
    /// 重跑下载必然再失败一次 —— 打标后前端单独成桶，且下载作业直接跳过不再白跑。
    /// <para>注意：这些条目大多**没有 item 行**（不在手机缓存里，是收藏夹直下失败留下的），
    /// 所以同步页口径（state IN phone_only/outdated）看不到它们；标记只在收藏夹页生效。</para>
    /// </summary>
    public HashSet<long> ChargeAvids()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT key FROM probe WHERE verdict='charge'";
        var avids = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var k = r.GetString(0);
            // key 形如 "<avid>_c<cid>"，single 类型条目是 "s_<sha1>"（不会出现在 probe 里，兜个底）
            var cut = k.IndexOf('_');
            if (cut > 0 && long.TryParse(k.AsSpan(0, cut), out var avid)) avids.Add(avid);
        }
        return avids;
    }

    /// <summary>播放页要的单条目信息（取最高档 backup）。条目不存在返回 null。</summary>
    public PlayInfo? GetPlayInfo(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.key, i.avid, i.cid, i.title, i.owner, i.page,
                   i.duration_ms, b.size, b.quality, b.codecid, i.backup_count
            FROM item i
            JOIN backup b ON b.key = i.key
            WHERE i.key = $k
            ORDER BY b.quality DESC, b.created_at ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new PlayInfo(
            r.GetString(0), r.GetInt64(1), r.GetInt64(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt32(5),
            r.IsDBNull(6) ? 0 : r.GetInt64(6),
            r.GetInt64(7),
            r.IsDBNull(8) ? null : r.GetInt32(8),
            r.IsDBNull(9) ? null : r.GetInt32(9),
            r.GetInt32(10));
    }

    /// <summary>同一个 avid 下其他分 P（按 page 升序）。用于播放器"下一 P"。</summary>
    public IReadOnlyList<SiblingRow> Siblings(long avid, string excludeKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.key, i.page, i.title
            FROM item i
            WHERE i.avid = $a AND i.key <> $k AND i.synced_at IS NOT NULL
            ORDER BY COALESCE(i.page, 0), i.key
            """;
        cmd.Parameters.AddWithValue("$a", avid);
        cmd.Parameters.AddWithValue("$k", excludeKey);
        var list = new List<SiblingRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SiblingRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetString(2)));
        }
        return list;
    }

    /// <summary>条目的 B 站封面 URL（可能为 null）。</summary>
    public string? GetCoverUrl(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT cover FROM item WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    private const string ItemColumns = """
        item.key,item.avid,item.cid,item.rel_dir,item.title,item.owner,item.page,
        item.phone_tag,item.phone_tag_norm,item.phone_quality,item.phone_codecid,
        item.phone_size,item.phone_video_size,item.phone_audio_size,item.phone_video_md5,item.phone_audio_md5,
        item.phone_content_id,item.phone_file_count,item.width,item.height,
        item.state,item.drift,item.drift_note,item.backup_count,item.best_tag_norm,item.best_quality,
        item.on_phone,item.scanned_at,item.synced_at
        """;

    /// <summary>probe 侧的列（探测结论 + 「仅低档」时 B 站能给的最高档位）。</summary>
    private const string ProbeColumns = "p.verdict, p.note, p.checked_at, p.max_quality, p.max_codecid";

    private static ItemRow MapItem(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetInt32(9),
        r.IsDBNull(10) ? null : r.GetInt32(10),
        r.GetInt64(11), r.GetInt64(12), r.GetInt64(13),
        r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15),
        r.IsDBNull(16) ? null : r.GetString(16),
        r.GetInt32(17),
        r.IsDBNull(18) ? null : r.GetInt32(18),
        r.IsDBNull(19) ? null : r.GetInt32(19),
        r.GetString(20), r.GetString(21),
        r.IsDBNull(22) ? null : r.GetString(22),
        r.GetInt32(23),
        r.IsDBNull(24) ? null : r.GetString(24),
        r.IsDBNull(25) ? null : r.GetInt32(25),
        r.GetInt32(26) != 0,
        r.IsDBNull(27) ? null : r.GetString(27),
        r.IsDBNull(28) ? null : r.GetString(28),
        r.FieldCount > 29 && !r.IsDBNull(29) ? r.GetString(29) : null,
        r.FieldCount > 30 && !r.IsDBNull(30) ? r.GetString(30) : null,
        r.FieldCount > 31 && !r.IsDBNull(31) ? r.GetString(31) : null,
        r.FieldCount > 32 && !r.IsDBNull(32) ? r.GetInt32(32) : null,
        r.FieldCount > 33 && !r.IsDBNull(33) ? r.GetInt32(33) : null,
        r.FieldCount > 34 && !r.IsDBNull(34) ? r.GetString(34) : null,
        r.FieldCount > 35 && !r.IsDBNull(35) ? r.GetString(35) : null,
        r.FieldCount > 36 && !r.IsDBNull(36) ? r.GetString(36) : null,
        r.FieldCount > 37 && !r.IsDBNull(37) ? r.GetString(37) : null,
        // UP 主 mid（迁移加的列，老 reader 可能没有）
        r.FieldCount > 38 && !r.IsDBNull(38) ? r.GetInt64(38) : null);

    public ItemRow? Get(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ItemColumns}, {ProbeColumns},
                   (SELECT b.source FROM backup b WHERE b.key=item.key ORDER BY b.quality DESC LIMIT 1),
                   (SELECT b.via    FROM backup b WHERE b.key=item.key ORDER BY b.quality DESC LIMIT 1),
                   item.issue_note, item.issue_at, item.owner_mid
            FROM item LEFT JOIN probe p ON p.key=item.key
            WHERE item.key=$k
            """;
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapItem(r) : null;
    }

    // ---------------- 探测缓存 ----------------
    // probe 是**缓存**，不是备份事实 —— 可以覆盖（"总库只增不改"只约束 backup 表）。

    public void SaveProbe(string key, string verdict, string? note,
                           int? maxQuality = null, int? maxCodecid = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO probe(key,verdict,note,checked_at,max_quality,max_codecid)
            VALUES($k,$v,$n,$t,$mq,$mc)
            ON CONFLICT(key) DO UPDATE SET
                verdict=$v, note=$n, checked_at=$t, max_quality=$mq, max_codecid=$mc
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", verdict);
        cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$mq", (object?)maxQuality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mc", (object?)maxCodecid ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public ProbeRow? GetProbe(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key,verdict,note,checked_at,max_quality FROM probe WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ProbeRow(r.GetString(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetInt32(4));
    }

    public IReadOnlyDictionary<string, ProbeRow> AllProbes()
    {
        var dict = new Dictionary<string, ProbeRow>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key,verdict,note,checked_at,max_quality FROM probe";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            dict[r.GetString(0)] = new ProbeRow(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4));
        return dict;
    }

    /// <summary>probe 表现有 verdict 分布 + 仍需探测的条数（含缓存过期的）。</summary>
    /// <summary>
    /// 同步页两行卡片统计。第二行各数 = "剩余待同步"（state ≠ 'synced'）里的数量：
    /// 之前 <see cref="ProbeSnapshot"/> 全表数 verdict，条目下载入库后 probe 行仍在，
    /// "可下载替代"永远不减（恒挂 2969 的根因）。待探测仍含缓存过期（IsStale）。
    /// </summary>
    /// <summary>
    /// 第二行卡片：**一个条目只进一个篮子**。
    /// 可动手条目（phone_only / outdated）按探测结论互斥分配：
    ///   有结论且缓存未过期 → exact / lower / gone 之一
    ///   其余（没探过 / 结论过期 / 探测失败没结论）→ 待探测
    /// 待探测 = actionable − exact − lower − gone（差值法，永远不会漏也不会重）。
    /// 于是 四卡之和 == actionable，且 actionable + 残片 + 校验失败 == 剩余待同步。
    /// </summary>
    public SyncActionStats SyncActions()
    {
        var probes = AllProbes();
        var now = DateTime.UtcNow;
        int onPhone = 0, synced = 0, upgraded = 0, remaining = 0,
            exact = 0, gone = 0, drifted = 0, lower = 0, incomplete = 0, mismatch = 0,
            actionable = 0, unknownFresh = 0, charge = 0, libraryOnly = 0, lost = 0, conflict = 0;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, on_phone, state, drift, phone_quality FROM item";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            bool onP = Convert.ToInt32(r.GetValue(1)) != 0;
            var state = r.GetString(2);
            var drift = r.IsDBNull(3) ? "none" : r.GetString(3);
            int? phoneQ = r.IsDBNull(4) ? null : r.GetInt32(4);
            if (onP) onPhone++;
            if (state == "synced")
            {
                // 手机侧口径：这些都是"本次扫描手机上还在的条目"。
                // on_phone=0 = 手机那份早删了、只剩库里那份 → 不是"手机条目中已同步"，
                // 单列 LibraryOnly，否则卡片「已同步」会变成库总量、与「手机缓存条目」对不上。
                if (!onP) { libraryOnly++; continue; }
                // 「已升级」= 手机那份是较低档、库里已有更高画质 → 从已同步里单列
                if (drift.Contains("tag_down")) upgraded++; else synced++;
                continue;
            }

            // 非 synced 又不在手机上：手机侧已经无从同步（库里有没入库另说），
            // 不能算「待同步」——否则 OnPhone ≠ Synced + Upgraded + Remaining。
            if (!onP) { lost++; continue; }

            // 冲突：库里有东西、但和手机那份对不上。待办是"决定留哪份"，不是"搬过来"，
            // 所以**不并入 Remaining**。第一行对账：
            //   OnPhone = Synced + Upgraded + Remaining + Conflict
            if (state is "outdated" or "mismatch" or "incomplete")
            {
                conflict++;
                if (state == "outdated") drifted++;
                else if (state == "incomplete") incomplete++;
                else mismatch++;
                continue;
            }

            if (state != ItemState.PhoneOnly) continue;
            remaining++;
            actionable++;
            if (!probes.TryGetValue(key, out var p)) continue;   // 从没探过 → 待探测
            if (IsStale(p, now)) continue;                       // 结论过期 → 待探测（不再占着旧位置）
            switch (p.Verdict)
            {
                case "exact": exact++; break;
                // 「仅拷贝」= 同画质异编码 或 画质更低（B站救不回来）；
                // 但 lower 里还有第三种：B站最高档**高于**手机档 → 能升档下载，
                // 与 DownloadGate 的放行条件严格一致，算进「可替代下载」而不是「仅拷贝」
                case "lower":
                    if (p.MaxQuality is { } mq && phoneQ is { } pq && mq > pq) exact++;
                    else lower++;
                    break;
                // 充电视频：稿件活着但账号没权限 → 同样是"只能从手机拷贝"，
                // 所以并入「仅拷贝」（Lower），再单独记一份数字给卡片/筛选条用
                case "charge": charge++; lower++; break;
                case "gone": gone++; break;
                default: unknownFresh++; break;                  // 探测失败 = 没结论 → 待探测
            }
        }

        var pending = Math.Max(0, actionable - exact - lower - gone);
        return new SyncActionStats(onPhone, synced, remaining, exact, gone, drifted,
            lower, incomplete, pending, mismatch, unknownFresh, upgraded, charge,
            libraryOnly, lost, conflict);
    }

    public ProbeSnapshot ProbeSnapshot()
    {
        var probes = AllProbes();
        int exact = 0, lower = 0, gone = 0, unknown = 0, pending = 0;
        var now = DateTime.UtcNow;

        foreach (var p in probes.Values)
        {
            switch (p.Verdict)
            {
                case ProbeVerdict.Exact: exact++; break;
                case ProbeVerdict.Lower: lower++; break;
                case ProbeVerdict.Gone: gone++; break;
                case ProbeVerdict.Unknown: unknown++; break;
            }
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, state FROM item
            WHERE state='phone_only' AND on_phone=1
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (!probes.TryGetValue(key, out var p) || IsStale(p, now)) pending++;
        }
        return new ProbeSnapshot(exact, lower, gone, unknown, pending);
    }

    /// <summary>探测候选：待同步/有变化且在机、缓存已过期的条目。outdated 优先，体积降序。</summary>
    /// <summary>
    /// 探测候选。<paramref name="onlyKeys"/> 为空 = 常规模式（只挑没备份、结论已过期的 phone_only）；
    /// 指定 keys 时 = 强制重探这几条（**忽略 TTL 与备份状态**），用于补「仅拷贝」缺的 B 站侧编码、
    /// 或人工复核某条结论。
    /// </summary>
    public IReadOnlyList<ProbeCandidate> ProbeCandidates(IReadOnlyList<string>? onlyKeys = null)
    {
        var probes = AllProbes();
        var now = DateTime.UtcNow;
        var list = new List<ProbeCandidate>();
        var want = onlyKeys is { Count: > 0 } ? onlyKeys.ToHashSet() : null;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = want is null
            ? """
              SELECT key, avid, cid, phone_quality, phone_codecid, phone_size, state, phone_video_size
              FROM item
              WHERE state='phone_only' AND on_phone=1 AND backup_count=0
              ORDER BY phone_size DESC
              """
            : """
              SELECT key, avid, cid, phone_quality, phone_codecid, phone_size, state, phone_video_size
              FROM item
              WHERE on_phone=1
              ORDER BY phone_size DESC
              """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (want is not null) { if (!want.Contains(key)) continue; }
            else if (probes.TryGetValue(key, out var p) && !IsStale(p, now)) continue;
            list.Add(new ProbeCandidate(
                key, r.GetInt64(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt32(4),
                r.GetInt64(5), r.GetString(6), r.IsDBNull(7) ? 0 : r.GetInt64(7)));
        }
        return list;
    }

    /// <summary>
    /// 下载替代批量候选：待同步、在机、探测结果允许尝试下载（exact/lower/unknown）。
    /// lower/unknown 的最终可下性在下载现场由 playurl 决策（有 ≥手机档位 才下）。
    /// 体积降序——大文件先跑，避免收尾时只剩长尾小文件拖慢进度感。
    /// </summary>
    /// <summary>
    /// 自动挑「仅下载替代」的候选（用户没指定条目时）。
    /// 准入只有两种（用户拍板）：同档同编码（exact，能 md5 逐字节校验），
    /// 或 B 站能给到比手机更高的画质（升档）。**同画质不同编码一律不收** ——
    /// 手机里那份 HEVC 才是原件，下个 AVC/AV1 副本会把"原件还在手机"这件事盖掉。
    /// </summary>
    public List<string> DownloadCandidateKeys()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.key FROM item i JOIN probe p ON p.key=i.key
            WHERE i.state IN ('phone_only','outdated') AND i.on_phone=1
              AND (p.verdict='exact'
                   OR (p.verdict='lower' AND p.max_quality IS NOT NULL
                       AND i.phone_quality IS NOT NULL AND p.max_quality > i.phone_quality))
              AND julianday('now')-julianday(p.checked_at) < 1
            ORDER BY i.phone_size DESC
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// 缓存寿命（DESIGN.md §4.4）：可下载类 1 天、失效/充电 30 天、unknown 1 小时。
    /// 分档理由见下面 switch 里的注释。
    /// </summary>
    public static bool IsStale(ProbeRow p, DateTime nowUtc)
    {
        // 必须按 UTC 解释时间串：TryParse 默认会把 "…Z" 转成本地墙钟，
        // 与 UtcNow 相减得负差值，刚过期的 unknown 会被误判为仍有效
        if (!DateTimeOffset.TryParse(p.CheckedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at)) return true;
        var ttl = p.Verdict switch
        {
            ProbeVerdict.Exact => TimeSpan.FromDays(1),
            ProbeVerdict.Lower => TimeSpan.FromDays(1),
            ProbeVerdict.Gone => TimeSpan.FromDays(30),
            // 充电视频是账号级权限，不会天天变（而且探一次要打 playurl）—— 与 gone 同档 30 天
            ProbeVerdict.Charge => TimeSpan.FromDays(30),
            // unknown 原本只有 10 分钟：撞一次风控写下的 51 条假结论，10 分钟后又集体回到候选队列，
            // 下一轮再从 1.6 QPS 冲一次墙 —— 自我复制的慢。拉到 1 小时，让"这轮探不出"能撑到风控窗口过去。
            _ => TimeSpan.FromHours(1),
        };
        return nowUtc - at.UtcDateTime >= ttl;
    }

    /// <summary>搬运所需的手机侧信息：目录、文件集、md5 期望值。</summary>
    public IReadOnlyList<ScanFile> GetPhoneFiles(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name,role,size,md5_declared FROM item_file WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        var list = new List<ScanFile>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ScanFile
            {
                Name = r.GetString(0),
                Role = r.GetString(1),
                Size = r.GetInt64(2),
                Md5Declared = r.IsDBNull(3) ? null : r.GetString(3),
            });
        }
        return list;
    }

    /// <summary>
    /// 只改状态。note 非空时同时记下"为什么"（写 issue_note / issue_at）——
    /// 「校验失败」必须能回答"校验哪一步没过"，而这个原因不能被下次扫描的 drift_note 冲掉。
    /// </summary>
    /// <summary>
    /// 补记 UP 主数字 ID。只在还空着的时候写 —— 查它要一次 view 请求（≈0.3s），
    /// 已经查到的绝不能被后来扫到的空值抹掉。
    /// </summary>
    public void SetOwnerMid(string key, long mid)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE item SET owner_mid=$m WHERE key=$k AND (owner_mid IS NULL OR owner_mid=0)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$m", mid);
        cmd.ExecuteNonQuery();
    }

    public void SetState(string key, string state, string? note = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (note is null)
        {
            cmd.CommandText = "UPDATE item SET state=$s WHERE key=$k";
        }
        else
        {
            cmd.CommandText = """
                UPDATE item SET state=$s, issue_note=$n, issue_at=strftime('%Y-%m-%dT%H:%M:%SZ','now')
                WHERE key=$k
                """;
            cmd.Parameters.AddWithValue("$n", note);
        }
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }
}
