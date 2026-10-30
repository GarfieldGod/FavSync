using System.Globalization;
using FavSync.Core.Cold;
using Microsoft.Data.Sqlite;

namespace FavSync.Core.Store;

/// <summary>
/// 冷备份的账（DESIGN-COLD-BACKUP.md §2）。
///
/// <para><b>为什么这四张表要落库、不能只放内存</b>：打包 234 GiB 要十几小时、上传要跨几天。
/// 作业状态进程重启即清空，桶号会重号、已上传的会被当成没打。项目既有铁律是"账在盘上"，这里照办。</para>
///
/// <para><b>覆盖判定只认 <c>backup</c> 的三元组</b>（key + tag_norm + content_id），不认"视频"：
/// 同一个 key 可能有 64/80 两份画质，按视频判定会得出"已冷备"却冷的是旧画质，
/// 升档后的新字节永远带不走。</para>
/// </summary>
public sealed partial class CatalogDatabase
{
    private const string ColdSchema = """
        -- 一个桶 = 一个 7z 文件 = 一个恢复单位
        CREATE TABLE IF NOT EXISTS cold_bucket(
            bucket_id   TEXT PRIMARY KEY,          -- 'gone-A-20261026-0007' = 文件名去扩展名
            batch_id    TEXT NOT NULL,             -- 'gone-A-20261026'
            label       TEXT NOT NULL DEFAULT 'cold',
            seq         INTEGER NOT NULL,          -- 机内序号，永不复用
            origin      TEXT NOT NULL DEFAULT 'M', -- 导出机标识
            file_name   TEXT NOT NULL,
            bytes       INTEGER NOT NULL,
            sha256      TEXT,
            item_count  INTEGER NOT NULL,
            target_mib  INTEGER NOT NULL,
            state       TEXT NOT NULL,             -- packing|packed|uploaded|pruned|aborted
            note        TEXT,
            created_at  TEXT NOT NULL,
            packed_at   TEXT,
            uploaded_at TEXT,
            dir         TEXT                        -- 非 NULL = 外来桶（导入时所在的目录）
        );
        CREATE INDEX IF NOT EXISTS idx_cold_bucket_state ON cold_bucket(state);

        -- 一行 = 一份已经进了桶的字节，与 backup 的三元组一一对应
        CREATE TABLE IF NOT EXISTS cold_item(
            key         TEXT NOT NULL,
            tag_norm    TEXT NOT NULL,
            content_id  TEXT NOT NULL,
            bucket_id   TEXT NOT NULL,
            bytes       INTEGER NOT NULL,
            sha256      TEXT NOT NULL,
            at          TEXT NOT NULL,
            PRIMARY KEY(key, tag_norm, content_id)
        );
        CREATE INDEX IF NOT EXISTS idx_cold_item_bucket ON cold_item(bucket_id);

        -- 账上有、盘上不符 → 不允许带病打包，列成"无法冷备"清单
        CREATE TABLE IF NOT EXISTS cold_block(
            key        TEXT NOT NULL,
            tag_norm   TEXT NOT NULL,
            content_id TEXT NOT NULL,
            reason     TEXT NOT NULL,
            at         TEXT NOT NULL,
            PRIMARY KEY(key, tag_norm, content_id)
        );

        -- 少量元信息（上次核对时间 / 上次抽验时间 / 上次导出的批次号）
        CREATE TABLE IF NOT EXISTS cold_meta(
            k TEXT PRIMARY KEY,
            v TEXT
        );
        """;

    private static void MigrateColdBucketDir(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('cold_bucket') WHERE name='dir'";
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
                Exec(conn, "ALTER TABLE cold_bucket ADD COLUMN dir TEXT");
        }
        NormalizeColdBucketDirs(conn);
    }

    /// <summary>
    /// <c>dir</c> 按前端传来的原样落库时可能带双斜杠或尾部斜杠
    /// （File.Exists 能用，但显示难看，而且和冷备目录比"是不是同一个目录"会比错）。统一规范化。
    /// </summary>
    private static void NormalizeColdBucketDirs(SqliteConnection conn)
    {
        var rows = new List<(string Id, string Dir)>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT bucket_id, dir FROM cold_bucket WHERE dir IS NOT NULL AND dir <> ''";
            using var r = sel.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (id, dir) in rows)
        {
            string norm;
            try { norm = Path.GetFullPath(dir); } catch { continue; }
            if (string.Equals(norm, dir, StringComparison.Ordinal)) continue;

            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE cold_bucket SET dir=$d WHERE bucket_id=$id";
            upd.Parameters.AddWithValue("$d", norm);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 存量修正：<c>content_id</c> 是 <c>ScanItem.ContentId</c> 的**纯函数**
    /// （video+audio 的 md5 前缀，缺 md5 时退化为 <c>s&lt;video_size&gt;a&lt;audio_size&gt;</c>），
    /// 但历史行里出现过占位值（如 <c>vDEADBEEF00000000a0</c>），连自己的 <c>video_md5</c> 都对不上。
    ///
    /// <para>这种行在"恢复时重算三元组"的路径上必然漂移：恢复后 join 不回 <c>cold_item</c>，
    /// 条目被判成未冷备，下一批打包把同样的字节再打一遍。所以按当前算法重算一遍。</para>
    ///
    /// <para>约束：只在**目标键不存在**时改（同 key 下已经有正确行就跳过），绝不制造重复三元组。</para>
    /// </summary>
    private static void FixContentIds(SqliteConnection conn)
    {
        var rows = new List<(string Key, string TagNorm, string ContentId,
            string? VMd5, string? AMd5, long VSize, long ASize)>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = """
                SELECT key, tag_norm, content_id, video_md5, audio_md5, video_size, audio_size FROM backup
                """;
            using var r = sel.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    r.IsDBNull(6) ? 0 : r.GetInt64(6)));
        }

        using var tx = conn.BeginTransaction();
        var fixed_n = 0;
        foreach (var (key, tagNorm, contentId, vm, am, vs, asz) in rows)
        {
            var want = ExpectedContentId(vm, am, vs, asz);
            if (string.Equals(want, contentId, StringComparison.Ordinal)) continue;

            using (var chk = conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = """
                    SELECT COUNT(*) FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid
                    """;
                chk.Parameters.AddWithValue("$k", key);
                chk.Parameters.AddWithValue("$tn", tagNorm);
                chk.Parameters.AddWithValue("$cid", want);
                if (Convert.ToInt64(chk.ExecuteScalar()) > 0) continue;   // 已有正确行，不动
            }

            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE backup SET content_id=$new WHERE key=$k AND tag_norm=$tn AND content_id=$old
                    """;
                upd.Parameters.AddWithValue("$new", want);
                upd.Parameters.AddWithValue("$k", key);
                upd.Parameters.AddWithValue("$tn", tagNorm);
                upd.Parameters.AddWithValue("$old", contentId);
                upd.ExecuteNonQuery();
            }

            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE cold_item SET content_id=$new WHERE key=$k AND tag_norm=$tn AND content_id=$old
                    """;
                upd.Parameters.AddWithValue("$new", want);
                upd.Parameters.AddWithValue("$k", key);
                upd.Parameters.AddWithValue("$tn", tagNorm);
                upd.Parameters.AddWithValue("$old", contentId);
                upd.ExecuteNonQuery();
            }
            fixed_n++;
        }
        tx.Commit();

        if (fixed_n > 0)
        {
            using var meta = conn.CreateCommand();
            meta.CommandText = "INSERT OR REPLACE INTO cold_meta(k,v) VALUES('contentIdFix', $v)";
            meta.Parameters.AddWithValue("$v", $"{DateTime.UtcNow:O}|{fixed_n}");
            meta.ExecuteNonQuery();
        }
    }

    /// <summary>与 <c>ScanItem.ContentId</c> 完全同一套算法（两处必须一致，故在此复刻并注明来源）。</summary>
    private static string ExpectedContentId(string? videoMd5, string? audioMd5, long videoSize, long audioSize)
    {
        if (!string.IsNullOrEmpty(videoMd5))
        {
            var vp = videoMd5.Length >= 10 ? videoMd5[..10] : videoMd5;
            var ap = string.IsNullOrEmpty(audioMd5) ? "0"
                : (audioMd5.Length >= 6 ? audioMd5[..6] : audioMd5);
            return $"v{vp}a{ap}";
        }
        return $"s{videoSize:x}a{audioSize:x}";
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    // ───────────────────────── 覆盖统计 ─────────────────────────

    /// <summary>
    /// 面板的四个数字（DESIGN-COLD-BACKUP.md §10）。<b>纯 SQL，不碰磁盘</b>：
    /// 未冷备 / 已冷备 / 仅在桶里 / 无法冷备。桶的"本地缺失"要 File.Exists，放在服务层补。
    ///
    /// <para><b>「待上传」只算冷备份目录里的桶</b>：<paramref name="coldRoot"/> 传进来后，
    /// <c>dir</c> 指向别处的桶归为「外来桶」单列。否则从网盘下回来的那批会被算成
    /// "待上传 84 GiB"，纯属吓人（实测 44 个桶全是这种）。</para>
    /// </summary>
    public ColdStatus ColdStatus(string? coldRoot = null)
    {
        using var conn = Open();

        (int, long) Pair(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt32(0), r.IsDBNull(1) ? 0L : r.GetInt64(1)) : (0, 0L);
        }

        var uncold = Pair("""
            SELECT COUNT(*), COALESCE(SUM(b.size),0) FROM backup b
            LEFT JOIN cold_item c ON c.key=b.key AND c.tag_norm=b.tag_norm AND c.content_id=b.content_id
            WHERE b.role='primary' AND c.key IS NULL
            """);

        var cold = Pair("""
            SELECT COUNT(*), COALESCE(SUM(b.size),0) FROM backup b
            JOIN cold_item c ON c.key=b.key AND c.tag_norm=b.tag_norm AND c.content_id=b.content_id
            WHERE b.role='primary'
            """);

        var only = Pair("""
            SELECT COUNT(*), COALESCE(SUM(c.bytes),0) FROM cold_item c
            WHERE NOT EXISTS (SELECT 1 FROM backup b
                              WHERE b.key=c.key AND b.tag_norm=c.tag_norm AND b.content_id=c.content_id)
            """);

        var blocked = Pair("SELECT COUNT(*), 0 FROM cold_block");

        // 桶的归属要拿 dir 和冷备目录比，SQL 里比不了（路径大小写 / 分隔符），所以逐行判
        int total = 0, pending = 0, uploaded = 0, external = 0;
        long bucketBytes = 0, pendingBytes = 0, externalBytes = 0;
        var rows = new List<(string State, string? Dir, int N, long Bytes)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT state, dir, COUNT(*), COALESCE(SUM(bytes),0) FROM cold_bucket
                WHERE state <> 'aborted' GROUP BY state, dir
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetInt32(2), r.IsDBNull(3) ? 0L : r.GetInt64(3)));
        }

        foreach (var (st, dir, n, by) in rows)
        {
            total += n;
            bucketBytes += by;
            var isExternal = IsExternalDir(dir, coldRoot);
            if (isExternal) { external += n; externalBytes += by; }
            switch (st)
            {
                case ColdState.Packed or ColdState.Packing:
                    if (isExternal) break;                       // 外来桶：不在本机冷备目录，谈不上"等上传"
                    pending += n; pendingBytes += by;
                    break;
                case ColdState.Uploaded or ColdState.Pruned:
                    uploaded += n;
                    break;
            }
        }

        return new ColdStatus(
            uncold.Item1, uncold.Item2,
            cold.Item1, cold.Item2,
            only.Item1, only.Item2,
            blocked.Item1,
            total, pending, uploaded, 0,
            bucketBytes, pendingBytes,
            GetMeta("lastCheckAt"), GetMeta("lastVerifyAt"), GetMeta("lastBatch"),
            external, externalBytes);
    }

    /// <summary>
    /// <c>dir</c> 指向冷备目录之外 ⇒ 外来桶（导入 / 恢复时从别处来的文件）。
    /// <c>dir</c> 为空 = 本机打的桶，文件在冷备目录里。路径比较走规范化，避免
    /// <c>H://a//b</c> 与 <c>H:\a\b</c> 被当成两个目录。
    /// </summary>
    public static bool IsExternalDir(string? dir, string? coldRoot)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        if (string.IsNullOrWhiteSpace(coldRoot)) return true;
        try
        {
            return !string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'),
                Path.GetFullPath(coldRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    // ───────────────────────── 计划（未冷备） ─────────────────────────

    /// <summary>
    /// 全部未冷备条目（primary，且 <c>cold_item</c> 里没有同三元组）。按 key 升序 —— 顺序固定，
    /// 同一批条目两次打包的封桶结果完全一致，所以"继续"不会把上次打过的内容再打一遍。
    /// </summary>
    public List<ColdPlanItem> UncoldItems()
    {
        var list = new List<ColdPlanItem>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.key, b.tag_norm, b.content_id, b.tag_raw, b.quality, b.codecid,
                   b.video_size, b.video_md5, b.audio_size, b.audio_md5,
                   b.source, b.via, b.created_at, b.path,
                   i.rel_dir, i.avid, i.cid, i.title, i.owner, i.owner_mid, i.bvid, i.cover,
                   i.page, i.width, i.height, i.duration_ms
            FROM backup b
            LEFT JOIN cold_item c ON c.key=b.key AND c.tag_norm=b.tag_norm AND c.content_id=b.content_id
            LEFT JOIN item i ON i.key=b.key
            WHERE b.role='primary' AND c.key IS NULL
            ORDER BY b.key, b.tag_norm
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // item 行缺失（孤儿 backup）时退回 backup.path —— 它就是主副本的 rel_dir
            var rel = r.IsDBNull(14) ? r.GetString(13) : r.GetString(14);
            list.Add(new ColdPlanItem
            {
                Key = r.GetString(0),
                TagNorm = r.GetString(1),
                ContentId = r.GetString(2),
                TagRaw = r.GetString(3),
                Quality = r.IsDBNull(4) ? null : r.GetInt32(4),
                Codecid = r.IsDBNull(5) ? null : r.GetInt32(5),
                VideoSize = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                VideoMd5 = r.IsDBNull(7) ? null : r.GetString(7),
                AudioSize = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                AudioMd5 = r.IsDBNull(9) ? null : r.GetString(9),
                Source = r.IsDBNull(10) ? "backup" : r.GetString(10),
                Via = r.IsDBNull(11) ? null : r.GetString(11),
                BackupCreatedAt = r.IsDBNull(12) ? "" : r.GetString(12),
                RelDir = rel,
                Avid = r.IsDBNull(15) ? 0 : r.GetInt64(15),
                Cid = r.IsDBNull(16) ? 0 : r.GetInt64(16),
                Title = r.IsDBNull(17) ? null : r.GetString(17),
                Owner = r.IsDBNull(18) ? null : r.GetString(18),
                OwnerMid = r.IsDBNull(19) ? null : r.GetInt64(19),
                Bvid = r.IsDBNull(20) ? null : r.GetString(20),
                Cover = r.IsDBNull(21) ? null : r.GetString(21),
                Page = r.IsDBNull(22) ? null : r.GetInt32(22),
                Width = r.IsDBNull(23) ? null : r.GetInt32(23),
                Height = r.IsDBNull(24) ? null : r.GetInt32(24),
                DurationMs = r.IsDBNull(25) ? null : r.GetInt64(25),
            });
        }
        return list;
    }

    // ───────────────────────── 无法冷备清单 ─────────────────────────

    public void ReplaceBlocks(IEnumerable<ColdBlockedRow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM cold_block";
            del.ExecuteNonQuery();
        }
        foreach (var row in rows)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO cold_block(key,tag_norm,content_id,reason,at)
                VALUES($k,$tn,$cid,$r,$at)
                """;
            ins.Parameters.AddWithValue("$k", row.Key);
            ins.Parameters.AddWithValue("$tn", row.TagNorm);
            ins.Parameters.AddWithValue("$cid", row.ContentId);
            ins.Parameters.AddWithValue("$r", row.Reason);
            ins.Parameters.AddWithValue("$at", row.At);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<ColdBlockedRow> ListBlocks(int offset, int limit, out int total)
    {
        using var conn = Open();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM cold_block";
            total = Convert.ToInt32(c.ExecuteScalar());
        }
        var list = new List<ColdBlockedRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bl.key, bl.tag_norm, bl.content_id, bl.reason, bl.at, i.title
            FROM cold_block bl LEFT JOIN item i ON i.key=bl.key
            ORDER BY bl.key LIMIT $lim OFFSET $off
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ColdBlockedRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    // ───────────────────────── 桶 ─────────────────────────

    public void InsertBucket(ColdBucketRow b)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cold_bucket(bucket_id,batch_id,label,seq,origin,file_name,bytes,sha256,
                                    item_count,target_mib,state,note,created_at,packed_at,uploaded_at,dir)
            VALUES($id,$batch,$label,$seq,$origin,$file,$bytes,$sha,$n,$tmi,$state,$note,$now,NULL,NULL,$dir)
            """;
        cmd.Parameters.AddWithValue("$id", b.BucketId);
        cmd.Parameters.AddWithValue("$batch", b.BatchId);
        cmd.Parameters.AddWithValue("$label", b.Label);
        cmd.Parameters.AddWithValue("$seq", b.Seq);
        cmd.Parameters.AddWithValue("$origin", b.Origin);
        cmd.Parameters.AddWithValue("$file", b.FileName);
        cmd.Parameters.AddWithValue("$bytes", b.Bytes);
        cmd.Parameters.AddWithValue("$sha", (object?)b.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", b.ItemCount);
        cmd.Parameters.AddWithValue("$tmi", b.TargetMiB);
        cmd.Parameters.AddWithValue("$state", b.State);
        cmd.Parameters.AddWithValue("$note", (object?)b.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", b.CreatedAt);
        cmd.Parameters.AddWithValue("$dir", (object?)b.Dir ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>下一个机内序号。**按 origin 各自递增**：跨机导入的桶不会把本机序号顶上去。</summary>
    public int NextColdSeq(string origin)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM cold_bucket WHERE origin=$o";
        cmd.Parameters.AddWithValue("$o", origin);
        return Convert.ToInt32(cmd.ExecuteScalar()) + 1;
    }

    public void SetBucketState(string bucketId, string state, string? note = null,
        long? bytes = null, string? sha256 = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE cold_bucket SET state=$s, note=$note,
                bytes = COALESCE($b, bytes),
                sha256 = COALESCE($sha, sha256),
                packed_at   = CASE WHEN $s='packed'   AND packed_at   IS NULL THEN $now ELSE packed_at END,
                uploaded_at = CASE WHEN $s IN ('uploaded','pruned') AND uploaded_at IS NULL THEN $now ELSE uploaded_at END
            WHERE bucket_id=$id
            """;
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)bytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", bucketId);
        cmd.ExecuteNonQuery();
    }

    public ColdBucketRow? GetBucket(string bucketId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cold_bucket WHERE bucket_id=$id";
        cmd.Parameters.AddWithValue("$id", bucketId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapBucket(r) : null;
    }

    public bool BucketExists(string bucketId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cold_bucket WHERE bucket_id=$id";
        cmd.Parameters.AddWithValue("$id", bucketId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public IReadOnlyList<ColdBucketRow> ListBuckets(string? state, int offset, int limit, out int total)
    {
        // 「待上传」= packed + packing：打包中断的桶既不完整也没法下载，只藏在「全部」里
        // 会让人以为没有待办 —— 而冷备统计（ColdStatus）本来就把两者算一起，列表必须同口径。
        var filtered = !string.IsNullOrEmpty(state) && state != "all";
        var byParam = filtered && state != ColdState.Packed;
        var where = !filtered ? "" : byParam ? " WHERE state=$s" : " WHERE state IN ('packed','packing')";
        using var conn = Open();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM cold_bucket" + where;
            if (byParam) c.Parameters.AddWithValue("$s", state!);
            total = Convert.ToInt32(c.ExecuteScalar());
        }
        var list = new List<ColdBucketRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cold_bucket" + where + " ORDER BY seq DESC LIMIT $lim OFFSET $off";
        if (byParam) cmd.Parameters.AddWithValue("$s", state!);
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapBucket(r));
        return list;
    }

    public IReadOnlyList<ColdBucketRow> BucketsOfBatch(string batchId)
    {
        var list = new List<ColdBucketRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM cold_bucket WHERE batch_id=$b AND state <> 'aborted' ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$b", batchId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapBucket(r));
        return list;
    }

    private static ColdBucketRow MapBucket(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("bucket_id")),
        r.GetString(r.GetOrdinal("batch_id")),
        r.GetString(r.GetOrdinal("label")),
        r.GetInt32(r.GetOrdinal("seq")),
        r.GetString(r.GetOrdinal("origin")),
        r.GetString(r.GetOrdinal("file_name")),
        r.GetInt64(r.GetOrdinal("bytes")),
        r.IsDBNull(r.GetOrdinal("sha256")) ? null : r.GetString(r.GetOrdinal("sha256")),
        r.GetInt32(r.GetOrdinal("item_count")),
        r.GetInt32(r.GetOrdinal("target_mib")),
        r.GetString(r.GetOrdinal("state")),
        r.IsDBNull(r.GetOrdinal("note")) ? null : r.GetString(r.GetOrdinal("note")),
        r.GetString(r.GetOrdinal("created_at")),
        r.IsDBNull(r.GetOrdinal("packed_at")) ? null : r.GetString(r.GetOrdinal("packed_at")),
        r.IsDBNull(r.GetOrdinal("uploaded_at")) ? null : r.GetString(r.GetOrdinal("uploaded_at")),
        r.IsDBNull(r.GetOrdinal("dir")) ? null : r.GetString(r.GetOrdinal("dir")));

    // ───────────────────────── 桶里的条目 ─────────────────────────

    /// <summary>
    /// 一个桶打包成功后一次性落账。<b>先写 cold_item 再置 packed</b>（同一个事务）：
    /// 反过来会出现"桶是 packed、条目还没记"，续打时把同一批字节再打一遍。
    /// </summary>
    public void CommitPackedBucket(string bucketId, long bytes, string sha256,
        IReadOnlyList<(string Key, string TagNorm, string ContentId, long Bytes, string Sha)> items)
    {
        var now = Now();
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        foreach (var it in items)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO cold_item(key,tag_norm,content_id,bucket_id,bytes,sha256,at)
                VALUES($k,$tn,$cid,$b,$by,$sha,$at)
                """;
            ins.Parameters.AddWithValue("$k", it.Key);
            ins.Parameters.AddWithValue("$tn", it.TagNorm);
            ins.Parameters.AddWithValue("$cid", it.ContentId);
            ins.Parameters.AddWithValue("$b", bucketId);
            ins.Parameters.AddWithValue("$by", it.Bytes);
            ins.Parameters.AddWithValue("$sha", it.Sha);
            ins.Parameters.AddWithValue("$at", now);
            ins.ExecuteNonQuery();
        }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE cold_bucket SET state='packed', bytes=$by, sha256=$sha,
                       item_count=$n, packed_at=$now, note=NULL
                WHERE bucket_id=$id
                """;
            upd.Parameters.AddWithValue("$by", bytes);
            upd.Parameters.AddWithValue("$sha", sha256);
            upd.Parameters.AddWithValue("$n", items.Count);
            upd.Parameters.AddWithValue("$now", now);
            upd.Parameters.AddWithValue("$id", bucketId);
            upd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 释放一个桶：条目退回"未冷备"，桶置 <c>aborted</c>（桶号保留、不再复用）。
    /// 用于"桶文件丢了但没上传" —— 这是唯一能让这批字节重新进入打包队列的操作。
    /// </summary>
    public int ReleaseBucket(string bucketId, string reason)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int n;
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM cold_item WHERE bucket_id=$id";
            del.Parameters.AddWithValue("$id", bucketId);
            n = del.ExecuteNonQuery();
        }
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE cold_bucket SET state='aborted', note=$r WHERE bucket_id=$id";
            upd.Parameters.AddWithValue("$r", reason);
            upd.Parameters.AddWithValue("$id", bucketId);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>某个桶里的条目三元组 + 指纹（导出清单时直接照抄，不重算）。</summary>
    public IReadOnlyList<(string Key, string TagNorm, string ContentId, long Bytes, string Sha)> ColdItemsOfBucket(
        string bucketId)
    {
        var list = new List<(string, string, string, long, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key,tag_norm,content_id,bytes,sha256 FROM cold_item WHERE bucket_id=$id ORDER BY key
            """;
        cmd.Parameters.AddWithValue("$id", bucketId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)));
        return list;
    }

    /// <summary>导入外部桶时把清单里的条目挂到本机账上（已存在则跳过，绝不改归属）。</summary>
    public int ImportColdItems(string bucketId, IReadOnlyList<(string Key, string TagNorm, string ContentId,
        long Bytes, string Sha)> items)
    {
        var now = Now();
        var n = 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var it in items)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR IGNORE INTO cold_item(key,tag_norm,content_id,bucket_id,bytes,sha256,at)
                VALUES($k,$tn,$cid,$b,$by,$sha,$at)
                """;
            ins.Parameters.AddWithValue("$k", it.Key);
            ins.Parameters.AddWithValue("$tn", it.TagNorm);
            ins.Parameters.AddWithValue("$cid", it.ContentId);
            ins.Parameters.AddWithValue("$b", bucketId);
            ins.Parameters.AddWithValue("$by", it.Bytes);
            ins.Parameters.AddWithValue("$sha", it.Sha);
            ins.Parameters.AddWithValue("$at", now);
            n += ins.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    // ───────────────────────── 元信息 / 账目辅助 ─────────────────────────

    public string? GetMeta(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM cold_meta WHERE k=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string? value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO cold_meta(k,v) VALUES($k,$v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>本机库里是否有这份字节（恢复对账用：已有就跳过，不覆盖）。</summary>
    public bool BackupExists(string key, string tagNorm, string contentId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM backup WHERE key=$k AND tag_norm=$tn AND content_id=$cid";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$tn", tagNorm);
        cmd.Parameters.AddWithValue("$cid", contentId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool ItemRowExists(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool HasAnyBackup(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM backup WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>probe 表全量快照（写进清单的 ledger 段）。这是唯一"丢了就再也算不出来"的账。</summary>
    public List<ColdLedgerProbe> ProbeLedger()
    {
        var list = new List<ColdLedgerProbe>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, verdict, note, checked_at, max_quality, max_codecid FROM probe ORDER BY key
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ColdLedgerProbe
            {
                Key = r.GetString(0),
                Verdict = r.GetString(1),
                Note = r.IsDBNull(2) ? null : r.GetString(2),
                CheckedAt = r.GetString(3),
                MaxQuality = r.IsDBNull(4) ? null : r.GetInt32(4),
                MaxCodecid = r.IsDBNull(5) ? null : r.GetInt32(5),
            });
        }
        return list;
    }

    /// <summary>
    /// 导入 ledger：**只补缺、不覆盖**，而且过期的结论直接丢弃。
    /// 过期的 gone（TTL 30 天）比没有更危险 —— 它会让下载闸门以为"不用下载了"。
    /// </summary>
    public (int Imported, int Skipped, int Expired) ImportProbeLedger(IEnumerable<ColdLedgerProbe> probes)
    {
        int imported = 0, skipped = 0, expired = 0;
        var now = DateTime.UtcNow;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var p in probes)
        {
            if (string.IsNullOrEmpty(p.Key) || string.IsNullOrEmpty(p.Verdict)) { skipped++; continue; }
            // 直接复用 IsStale：它按 verdict 分了 TTL（gone/charge 30 天、exact/lower 1 天、unknown 1 小时），
            // 过期结论一律不收 —— 一条过期的 gone 比"没有结论"更危险，会让下载闸门以为不用下载了
            var probe = new ProbeRow(p.Key, p.Verdict, p.Note, p.CheckedAt, p.MaxQuality);
            if (IsStale(probe, now)) { expired++; continue; }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO probe(key,verdict,note,checked_at,max_quality,max_codecid)
                VALUES($k,$v,$n,$c,$mq,$mc)
                ON CONFLICT(key) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("$k", p.Key);
            cmd.Parameters.AddWithValue("$v", p.Verdict);
            cmd.Parameters.AddWithValue("$n", (object?)p.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", p.CheckedAt);
            cmd.Parameters.AddWithValue("$mq", (object?)p.MaxQuality ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mc", (object?)p.MaxCodecid ?? DBNull.Value);
            if (cmd.ExecuteNonQuery() > 0) imported++; else skipped++;
        }
        tx.Commit();
        return (imported, skipped, expired);
    }

    /// <summary>某个条目在本机的其它档位（恢复对账要区分"已有这份"与"有另一个版本"）。</summary>
    public IReadOnlyList<(string TagNorm, string ContentId, int? Quality, int? Codecid, string Path)> BackupsOfKey(
        string key)
    {
        var list = new List<(string, string, int?, int?, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag_norm, content_id, quality, codecid, path FROM backup WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.GetString(4)));
        }
        return list;
    }

    /// <summary>
    /// 写清单要用的条目元数据 —— <b>以 <c>backup</c> 行为准</b>（档位 / md5 / 两个流的字节），
    /// <c>item</c> 只负责补标题、封面、UP 主这些展示字段。
    ///
    /// <para><b>为什么不能从 <c>item.phone_*</c> 取</b>：清单描述的是"封进桶里的那份字节"，
    /// 而 <c>phone_*</c> 描述的是"手机上的那份"。两者在 <c>outdated</c> / 换源之后本来就不一样，
    /// 拿 phone 的值去算 <c>content_id</c>，恢复时就 join 不回 <c>cold_item</c>，
    /// 于是刚恢复的条目又被判成"未冷备"，下一批打包把同样的字节再打一遍。</para>
    /// </summary>
    public ColdManifestSource? ColdManifestSource(string key, string tagNorm, string contentId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(i.avid,0), COALESCE(i.cid,0), COALESCE(i.rel_dir, b.path),
                   i.title, i.owner, i.owner_mid, i.bvid, i.cover, i.page, i.width, i.height, i.duration_ms,
                   b.tag_raw, b.quality, b.codecid, b.size,
                   b.video_size, b.video_md5, b.audio_size, b.audio_md5,
                   COALESCE(i.synced_at, b.created_at)
            FROM backup b LEFT JOIN item i ON i.key=b.key
            WHERE b.key=$k AND b.tag_norm=$tn AND b.content_id=$cid
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$tn", tagNorm);
        cmd.Parameters.AddWithValue("$cid", contentId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ColdManifestSource(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetInt32(8),
            r.IsDBNull(9) ? null : r.GetInt32(9),
            r.IsDBNull(10) ? null : r.GetInt32(10),
            r.IsDBNull(11) ? null : r.GetInt64(11),
            r.IsDBNull(12) ? tagNorm : r.GetString(12),
            r.IsDBNull(13) ? null : r.GetInt32(13),
            r.IsDBNull(14) ? null : r.GetInt32(14),
            r.IsDBNull(15) ? 0 : r.GetInt64(15),
            r.IsDBNull(16) ? 0 : r.GetInt64(16),
            r.IsDBNull(17) ? null : r.GetString(17),
            r.IsDBNull(18) ? 0 : r.GetInt64(18),
            r.IsDBNull(19) ? null : r.GetString(19),
            r.IsDBNull(20) ? "" : r.GetString(20));
    }
}

/// <summary>清单条目所需的元数据（见 <see cref="CatalogDatabase.ColdManifestSource"/>）。</summary>
public sealed record ColdManifestSource(
    long Avid, long Cid, string RelDir,
    string? Title, string? Owner, long? OwnerMid, string? Bvid, string? Cover,
    int? Page, int? Width, int? Height, long? DurationMs,
    string TagRaw, int? Quality, int? Codecid, long Size,
    long VideoSize, string? VideoMd5, long AudioSize, string? AudioMd5,
    string SyncedAt);
