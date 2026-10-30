using System.Text.Json.Serialization;

namespace FavSync.Core.Cold;

/// <summary>
/// 一个桶（bucket）= 一个 7z 文件 = 一个恢复单位。
/// <para><b>桶是持久实体，不是作业。</b>打包要十几小时、上传要跨几天，状态必须落库：
/// 作业内存重启即清空，桶号会重号、已上传的会被当成没打。</para>
/// </summary>
public sealed record ColdBucketRow(
    string BucketId, string BatchId, string Label, int Seq, string Origin,
    string FileName, long Bytes, string? Sha256, int ItemCount, int TargetMiB,
    string State, string? Note, string CreatedAt, string? PackedAt, string? UploadedAt,
    string? Dir);

public static class ColdState
{
    public const string Packing = "packing";
    public const string Packed = "packed";
    public const string Uploaded = "uploaded";
    public const string Pruned = "pruned";
    public const string Aborted = "aborted";

    public static string Label(string state) => state switch
    {
        Packing => "打包中",
        Packed => "待上传",
        Uploaded => "已上传",
        Pruned => "已上传·本地已删",
        Aborted => "已作废",
        _ => state,
    };
}

/// <summary>打包计划里的一条（= 一行 primary backup + 它在磁盘上的实际字节）。</summary>
public sealed class ColdPlanItem
{
    public required string Key { get; init; }
    public required string TagNorm { get; init; }
    public required string ContentId { get; init; }
    public required string RelDir { get; init; }
    public required string TagRaw { get; init; }
    public long Avid { get; init; }
    public long Cid { get; init; }
    public string? Title { get; init; }
    public string? Owner { get; init; }
    public long? OwnerMid { get; init; }
    public string? Bvid { get; init; }
    public string? Cover { get; init; }
    public int? Page { get; init; }
    public int? Quality { get; init; }
    public int? Codecid { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMs { get; init; }
    public string Source { get; init; } = "backup";
    public string? Via { get; init; }
    public string BackupCreatedAt { get; init; } = "";

    public long VideoSize { get; set; }
    public string? VideoMd5 { get; set; }
    public long AudioSize { get; set; }
    public string? AudioMd5 { get; set; }

    /// <summary>磁盘上实际文件集（相对 rel_dir）。打包时写进清单，恢复时按它核对。</summary>
    public List<ColdFile> Files { get; } = new();

    /// <summary>整个 rel_dir 实际占用的字节（含 entry.json / cover.jpg / 弹幕）。</summary>
    public long DiskBytes => Files.Sum(f => f.Size);

    /// <summary>账实核对没通过时的原因（非空 = 本条踢出本桶，不允许带病打包）。</summary>
    public string? Blocked { get; set; }
}

public sealed class ColdFile
{
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required long Size { get; init; }

    /// <summary>index.json 声明的 md5（没有 index.json 的条目为 null）。恢复后做全量 md5 校验时用它。</summary>
    public string? Md5 { get; set; }
}

/// <summary>一次「打包」请求的进度统计（也是 /cold/estimate 的返回）。</summary>
public sealed record ColdEstimate(
    int ItemCount, long Bytes, int BucketCount, int BlockedCount,
    long BlockedBytes, int TargetMiB, IReadOnlyList<string> BlockedSample);

/// <summary>面板上的四个数字（DESIGN-COLD-BACKUP.md §10）。</summary>
public sealed record ColdStatus(
    // 库里有字节、桶里没有 → 该导出
    int UncoldItems, long UncoldBytes,
    // 库里有、桶里也有
    int ColdItems, long ColdBytes,
    // 桶里有、本机库没有 → 该恢复（跨机搬运的中间态）
    int OnlyInBucketItems, long OnlyInBucketBytes,
    // 账实不符，先修库
    int BlockedItems,
    // 桶汇总
    int BucketTotal, int BucketPendingUpload, int BucketUploaded, int BucketLocalMissing,
    long BucketBytes, long PendingUploadBytes,
    string? LastCheckAt, string? LastVerifyAt, string? BatchId,
    // 文件在冷备份目录之外的桶（导入 / 恢复进来的）—— 不参与"待上传"，
    // 因为"上传"指的是"把冷备目录里这份推到云上"，它们本来就在别处
    int ExternalBuckets = 0, long ExternalBytes = 0);

/// <summary>无法冷备清单的一行（账上有、盘上不符）。</summary>
public sealed record ColdBlockedRow(
    string Key, string TagNorm, string ContentId, string Reason, string At, string? Title);

/// <summary>
/// 清单文件（<c>&lt;batch&gt;.manifest.7z</c> 里的 manifest.json）。
/// <para>它是这个方案里唯一的"目录"：7z 的索引在文件末尾，不下载完整个桶读不到里面有什么，
/// 换一台没有 catalog 的机器就无从判断"能恢复哪些视频"。所以它必须和桶一起上传。</para>
/// </summary>
public sealed class ColdManifest
{
    public string App { get; set; } = "FavSync";
    public int Format { get; set; } = 1;
    public string Batch { get; set; } = "";
    public string Label { get; set; } = "cold";
    public string Origin { get; set; } = "M";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int BucketMiB { get; set; } = 2048;
    public List<ColdManifestBucket> Buckets { get; set; } = new();

    /// <summary>
    /// 唯一不可重建的那部分账（DESIGN-COLD-BACKUP.md §5.1）。
    /// 条目元数据、画质、字节都能从 entry.json / index.json 扫回来，<b>只有 probe 结论</b>
    /// （gone / charge / exact / lower + TTL）必须靠对 B 站逐条 view 才能重算，所以带走它。
    /// </summary>
    public ColdLedger? Ledger { get; set; }
}

public sealed class ColdManifestBucket
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public long Bytes { get; set; }
    public string? Sha256 { get; set; }
    public string State { get; set; } = ColdState.Packed;
    public List<ColdManifestItem> Items { get; set; } = new();
}

public sealed class ColdManifestItem
{
    public string Key { get; set; } = "";
    public long Avid { get; set; }
    public long Cid { get; set; }
    public string RelDir { get; set; } = "";
    public string Tag { get; set; } = "";
    public string TagNorm { get; set; } = "";
    public string ContentId { get; set; } = "";
    public int? Quality { get; set; }
    public int? Codecid { get; set; }
    public string? Title { get; set; }
    public string? Owner { get; set; }
    public long? OwnerMid { get; set; }
    public string? Bvid { get; set; }
    public string? Cover { get; set; }
    public int? Page { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }
    public long VideoSize { get; set; }
    public string? VideoMd5 { get; set; }
    public long AudioSize { get; set; }
    public string? AudioMd5 { get; set; }
    public string Source { get; set; } = "backup";
    public string? Via { get; set; }
    public string BackupCreatedAt { get; set; } = "";

    /// <summary>这一条的字节（文件集之和），与 <c>cold_item.bytes</c> 一致。</summary>
    public long Bytes { get; set; }

    /// <summary>条目内容指纹（文件集摘要），与 <c>cold_item.sha256</c> 一致。</summary>
    public string Sha256 { get; set; } = "";

    public List<ColdManifestFile> Files { get; set; } = new();
}

public sealed class ColdManifestFile
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "other";
    public long Size { get; set; }
    public string? Md5 { get; set; }
}

public sealed class ColdLedger
{
    public List<ColdLedgerProbe> Probes { get; set; } = new();
}

public sealed class ColdLedgerProbe
{
    public string Key { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? Note { get; set; }
    public string CheckedAt { get; set; } = "";
    public int? MaxQuality { get; set; }
    public int? MaxCodecid { get; set; }
}

/// <summary>恢复向导：清单里的一条 + 与本机库的对账结论。</summary>
public sealed class ColdRestoreItem
{
    public required string Key { get; init; }
    public required string TagNorm { get; init; }
    public required string ContentId { get; init; }
    public required string BucketId { get; init; }
    public string? Title { get; init; }
    public string? Owner { get; init; }
    public string? Tag { get; init; }
    public int? Quality { get; init; }
    public long Bytes { get; init; }

    /// <summary>missing=本机缺（默认勾） · exists=已有（跳过） · other=本机有该视频的其它档位。</summary>
    public string Reconcile { get; set; } = "missing";
    public string? ReconcileNote { get; set; }
}

public sealed record ColdPreview(
    string Dir, string? Batch, int BucketCount, int ItemCount, long Bytes,
    IReadOnlyList<string> Buckets, IReadOnlyList<ColdRestoreItem> Items,
    int Missing, int Exists, int Other, string? Warning);

/// <summary>清单 JSON 的序列化设置（manifest.json 用 camelCase，和前端同一套写法）。</summary>
public static class ColdJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
