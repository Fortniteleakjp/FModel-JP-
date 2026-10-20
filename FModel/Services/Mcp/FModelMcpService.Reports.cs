using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using FModel.Creator;
using FModel.Extensions;
using FModel.Settings;
using Newtonsoft.Json;
using SkiaSharp;

namespace FModel.Services.Mcp;

/// <summary>
/// 更新追跡向けのツール。
/// <list type="bullet">
/// <item>差分レポート: FModel のバックアップ (.fbkp) と現在のゲームのファイル一覧を比べ、追加・変更・削除をカテゴリ/フォルダ別に集計する。
/// 読み込みモード「すべて（新規）/（変更）」と同じ判定 (パス・サイズ・暗号化の有無) なので中身の同一性までは見ない。</item>
/// <item>アイコン生成: FModel がアイテム定義を開いたときに描くアイコン (Creator) を PNG にする。Creator は live のプロバイダと
/// 読み込み済みのフォント・ローカライズを使うので live セッション専用。</item>
/// </list>
/// </summary>
public sealed partial class FModelMcpService
{
    public const int MaxIconBatch = 100;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly object _diffCacheSync = new();
    private (string Key, McpDiffEntry[] Entries) _diffCache;

    #region diff report

    public object ListBackups()
    {
        var directory = McpPathPolicy.BackupsDirectory;
        var backups = Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*.fbkp").OrderByDescending(f => f.LastWriteTime)
                .Select(f => new { name = f.Name, sizeBytes = f.Length, modified = f.LastWriteTime }).ToArray()
            : [];
        return new
        {
            directory,
            backups,
            note = "Pass a name to fmodel_diff_report. Pick a backup made BEFORE the current game update; the newest one may already match the current files. FModel creates backups from Directory > Backup."
        };
    }

    public async Task<object> DiffReport(string sessionId, string backup, string change, string category, string prefix, string query,
        bool writeReport, int offset, int limit, CancellationToken ct)
    {
        ValidatePage(offset, limit);
        change = string.IsNullOrWhiteSpace(change) ? "all" : change.Trim().ToLowerInvariant();
        if (change is not ("all" or "added" or "modified" or "removed"))
            throw new ArgumentException("change must be all, added, modified or removed.");

        var backupFile = McpPathPolicy.BackupFile(backup);
        var entries = (McpDiffEntry[]) await InSession(sessionId, s => Diff(s, backupFile, ct), ct);

        var folder = NormalizeFolder(prefix);
        var filtered = entries.Where(e =>
            (change == "all" || e.Change == change) &&
            (string.IsNullOrWhiteSpace(category) || e.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (folder.Length == 0 || e.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) &&
            Contains(e.Path, query)).ToArray();

        string reportDirectory = null;
        if (writeReport)
        {
            reportDirectory = McpPathPolicy.NewOutputDirectory("diff");
            await System.IO.File.WriteAllTextAsync(McpPathPolicy.OutputFile(reportDirectory, "report.md"),
                BuildMarkdown(backupFile.Name, entries, filtered, change, category, folder, query), Utf8NoBom, ct);
            await System.IO.File.WriteAllTextAsync(McpPathPolicy.OutputFile(reportDirectory, "diff.json"),
                JsonConvert.SerializeObject(filtered, Formatting.Indented), Utf8NoBom, ct);
        }

        return new
        {
            comparison = "metadata_only",
            backup = new { name = backupFile.Name, modified = backupFile.LastWriteTime },
            summary = Summarize(entries),
            filter = new { change, category, prefix = folder, query, matches = filtered.Length },
            reportDirectory,
            reportFiles = reportDirectory is null ? null : new[] { "report.md", "diff.json" },
            note = entries.Length == 0
                ? "The backup matches the current file list. Use an older backup (fmodel_list_backups)."
                : "added/modified/removed compare path, size and encryption only, like FModel's 'All (New)/(Modified)' loading modes. 'removed' also lists files whose archives are not mounted now (on-demand content, missing AES keys), so check fmodel_list_archives before reporting removals. Inspect assets with fmodel_get_properties; render cosmetics with fmodel_render_icon(s).",
            data = Paginate(filtered, offset, limit)
        };
    }

    private McpDiffEntry[] Diff(McpSession s, FileInfo backupFile, CancellationToken ct)
    {
        var key = $"{s.Id}|{backupFile.FullName}|{backupFile.LastWriteTimeUtc.Ticks}|{backupFile.Length}|{s.Provider.Files.Count}";
        lock (_diffCacheSync)
            if (_diffCache.Key == key) return _diffCache.Entries;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var job = BackgroundJobs.Start($"MCP diff report: {backupFile.Name}", cancellation);
        ct = cancellation.Token;
        var backup = BackupFileReader.Read(backupFile.FullName, ct);

        // プロバイダの TryGetValue はパスを正規化するので 1 回 10µs 前後かかる (Fortnite のバックアップは 1000 万件近い)。
        // 列挙したキーで普通の辞書を作って引く
        var current = new Dictionary<string, GameFile>(s.Provider.Files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in s.Provider.Files)
        {
            ct.ThrowIfCancellationRequested();
            current[path] = file;
        }

        // 同じパスが (アーカイブ違いで) 何度も載っているバックアップがある。どれか 1 つでも今と一致すれば変更なし
        var inBackup = new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);
        var unchanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in backup)
        {
            ct.ThrowIfCancellationRequested();
            inBackup.TryAdd(entry.Path, entry);
            if (current.TryGetValue(entry.Path, out var file) && file.Size == entry.Size && file.IsEncrypted == entry.IsEncrypted)
                unchanged.Add(entry.Path);
        }

        var result = new List<McpDiffEntry>();
        foreach (var (path, entry) in inBackup)
        {
            ct.ThrowIfCancellationRequested();
            if (!current.TryGetValue(path, out var file))
            {
                // .uexp/.ubulk は本体の .uasset と一緒に消えるので数えない (IoStore 化でまとめて消えたように見えるのも防ぐ)
                if (!GameFile.UePackagePayloadExtensionsSet.Contains(Path.GetExtension(path).TrimStart('.')))
                    result.Add(new McpDiffEntry(entry.Path, "removed", Category(entry.Path), entry.Size, null));
            }
            else if (!file.IsUePackagePayload && !unchanged.Contains(path))
                result.Add(new McpDiffEntry(file.Path, "modified", Category(file.Path), entry.Size, file.Size));
        }

        foreach (var (path, file) in current)
        {
            ct.ThrowIfCancellationRequested();
            if (file.IsUePackagePayload || inBackup.ContainsKey(path)) continue;
            result.Add(new McpDiffEntry(file.Path, "added", Category(file.Path), null, file.Size));
        }

        var entries = result.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_diffCacheSync) _diffCache = (key, entries);
        return entries;
    }

    private static string Category(string path)
    {
        var category = AssetCategoryGuesser.Guess(path);
        return category == AssetCategoryGuesser.Unknown ? "Other" : category.GetBaseCategory().ToString();
    }

    /// <summary>フォルダ別集計のキー。GameFeatures プラグインはプラグイン単位、それ以外は先頭 3 階層</summary>
    private static string FolderKey(string path)
    {
        var parts = path.Split('/');
        var depth = parts.Length > 3 && parts[1].Equals("Plugins", StringComparison.OrdinalIgnoreCase) &&
                    parts[2].Equals("GameFeatures", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        return string.Join('/', parts.Take(Math.Min(depth, parts.Length - 1)));
    }

    private static object Summarize(McpDiffEntry[] entries) => new
    {
        added = entries.Count(e => e.Change == "added"),
        modified = entries.Count(e => e.Change == "modified"),
        removed = entries.Count(e => e.Change == "removed"),
        byCategory = entries.GroupBy(e => e.Category).OrderByDescending(g => g.Count())
            .Select(g => new { category = g.Key, added = g.Count(e => e.Change == "added"), modified = g.Count(e => e.Change == "modified"), removed = g.Count(e => e.Change == "removed") })
            .ToArray(),
        topFolders = entries.GroupBy(e => FolderKey(e.Path), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(30)
            .Select(g => new { folder = g.Key, added = g.Count(e => e.Change == "added"), modified = g.Count(e => e.Change == "modified"), removed = g.Count(e => e.Change == "removed") })
            .ToArray()
    };

    private static string BuildMarkdown(string backupName, McpDiffEntry[] all, McpDiffEntry[] filtered, string change, string category, string folder, string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 差分レポート: {backupName} → 現在のゲーム");
        sb.AppendLine();
        sb.AppendLine($"- 作成: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- 比較: パス・サイズ・暗号化の有無（中身の一致までは見ていない）。削除には今マウントされていないアーカイブ（オンデマンド・鍵未入力）の分も含まれる");
        sb.AppendLine($"- 追加 {all.Count(e => e.Change == "added"):N0} / 変更 {all.Count(e => e.Change == "modified"):N0} / 削除 {all.Count(e => e.Change == "removed"):N0}");
        if (change != "all" || !string.IsNullOrWhiteSpace(category) || folder.Length > 0 || !string.IsNullOrEmpty(query))
            sb.AppendLine($"- 絞り込み: change={change} category={category} prefix={folder} query={query} → {filtered.Length:N0} 件");
        sb.AppendLine();

        sb.AppendLine("## カテゴリ別");
        sb.AppendLine();
        sb.AppendLine("| カテゴリ | 追加 | 変更 | 削除 |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var g in all.GroupBy(e => e.Category).OrderByDescending(g => g.Count()))
            sb.AppendLine($"| {g.Key} | {g.Count(e => e.Change == "added"):N0} | {g.Count(e => e.Change == "modified"):N0} | {g.Count(e => e.Change == "removed"):N0} |");
        sb.AppendLine();

        sb.AppendLine("## フォルダ別（上位 30）");
        sb.AppendLine();
        sb.AppendLine("| フォルダ | 追加 | 変更 | 削除 |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var g in all.GroupBy(e => FolderKey(e.Path), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(30))
            sb.AppendLine($"| `{g.Key}` | {g.Count(e => e.Change == "added"):N0} | {g.Count(e => e.Change == "modified"):N0} | {g.Count(e => e.Change == "removed"):N0} |");
        sb.AppendLine();

        foreach (var (kind, title) in new[] { ("added", "追加"), ("modified", "変更"), ("removed", "削除") })
        {
            var rows = filtered.Where(e => e.Change == kind).ToArray();
            if (rows.Length == 0) continue;

            sb.AppendLine($"## {title}（{rows.Length:N0} 件）");
            foreach (var g in rows.GroupBy(e => e.Category).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine();
                sb.AppendLine($"### {g.Key}（{g.Count():N0}）");
                sb.AppendLine();
                foreach (var e in g)
                {
                    var size = kind switch
                    {
                        "modified" => $" ({e.SizeBefore:N0} → {e.SizeAfter:N0} bytes)",
                        "added" => $" ({e.SizeAfter:N0} bytes)",
                        _ => ""
                    };
                    sb.AppendLine($"- `{e.Path}`{size}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region icons

    public Task<object> RenderIcon(string path, string objectName, string style, int maxSize, CancellationToken ct) => InSession(McpSession.LiveId, s =>
    {
        if (maxSize is < 64 or > 2048) throw new ArgumentException("maxSize must be 64..2048.");

        var iconStyle = ParseIconStyle(style);
        var file = File(s, path);
        var bitmaps = DrawIcons(s, file.Path, objectName, iconStyle, ct);
        var directory = McpPathPolicy.NewOutputDirectory("icon");
        var pngs = new List<byte[]>();
        var saved = new List<string>();
        try
        {
            for (var i = 0; i < bitmaps.Length; i++)
            {
                saved.Add(SavePng(bitmaps[i], directory, IconFileName(file.Path, i, bitmaps.Length)));
                using var scaled = ScaleDown(bitmaps[i], maxSize);
                using var data = (scaled ?? bitmaps[i]).Encode(SKEncodedImageFormat.Png, 100);
                pngs.Add(data.ToArray());
            }
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }

        if (pngs.Sum(p => p.Length) > 8 * 1024 * 1024) throw new InvalidOperationException("Icon images exceed 8 MiB. Request a smaller maxSize.");
        return new McpImages($"Rendered {pngs.Count} icon image(s) for {file.Path} with style {iconStyle}. Full-size PNG saved to: {string.Join(", ", saved)}", pngs);
    }, ct);

    public Task<object> RenderIcons(string[] paths, string style, CancellationToken ct) => InSession(McpSession.LiveId, s =>
    {
        if (paths is not { Length: > 0 }) throw new ArgumentException("paths is required.");
        if (paths.Length > MaxIconBatch) throw new ArgumentException($"At most {MaxIconBatch} paths per call.");

        var iconStyle = ParseIconStyle(style);
        var directory = McpPathPolicy.NewOutputDirectory("icons");
        var results = new List<McpExportItem>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var job = BackgroundJobs.Start($"MCP icons ({paths.Length:N0})", cancellation);
        ct = cancellation.Token;
        for (var i = 0; i < paths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            job.Report(i, paths.Length, paths[i]);
            try
            {
                var file = File(s, paths[i]);
                var bitmaps = DrawIcons(s, file.Path, null, iconStyle, ct);
                try
                {
                    results.Add(new McpExportItem(file.Path, true,
                        bitmaps.Select((b, n) => SavePng(b, directory, IconFileName(file.Path, n, bitmaps.Length))).ToArray()));
                }
                finally
                {
                    foreach (var bitmap in bitmaps) bitmap.Dispose();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new McpExportItem(paths[i], false, [], ErrorMessage(ex)));
            }
        }

        return new
        {
            outputDirectory = directory,
            style = iconStyle.ToString(),
            succeeded = results.Count(r => r.Success),
            failed = results.Count(r => !r.Success),
            results
        };
    }, ct);

    /// <summary>FModel のタブと同じ手順 (CreatorPackage → ParseForInfo → Draw) でアイコンを描く</summary>
    private static SKBitmap[] DrawIcons(McpSession s, string path, string objectName, EIconStyle style, CancellationToken ct)
    {
        Creator.Utils.Typefaces ??= new Typefaces(Cue4Parse);

        var package = LoadPackage(s, path);
        IEnumerable<UObject> candidates = string.IsNullOrEmpty(objectName) ? package.GetExports() : [package.GetExport(objectName)];
        foreach (var export in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (export is null) continue;

            using var creatorPackage = new CreatorPackage(package.Name, export.ExportType, new Lazy<UObject>(export), style);
            if (!creatorPackage.TryConstructCreator(out var creator)) continue;

            creator.ParseForInfo();
            var bitmaps = creator.Draw()?.Where(b => b is not null).ToArray();
            if (bitmaps is { Length: > 0 }) return bitmaps;
        }

        throw new NotSupportedException("FModel has no icon renderer for this asset. Icons exist for item definitions (cosmetics, weapons, quests, challenges...) and similar data assets.");
    }

    private static EIconStyle ParseIconStyle(string style) =>
        string.IsNullOrWhiteSpace(style) ? UserSettings.Default.CosmeticStyle : ParseEnum<EIconStyle>(style);

    private static string IconFileName(string path, int index, int count)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return count > 1 ? $"{name}_{index}.png" : $"{name}.png";
    }

    private static string SavePng(SKBitmap bitmap, string directory, string fileName)
    {
        var target = McpPathPolicy.OutputFile(directory, fileName);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = System.IO.File.Create(target);
        data.SaveTo(stream);
        return target;
    }

    private static SKBitmap ScaleDown(SKBitmap bitmap, int maxSize)
    {
        var scale = (double) maxSize / Math.Max(bitmap.Width, bitmap.Height);
        return scale < 1
            ? bitmap.Resize(new SKImageInfo(Math.Max(1, (int) (bitmap.Width * scale)), Math.Max(1, (int) (bitmap.Height * scale))), SKFilterQuality.Medium)
            : null;
    }

    #endregion
}

public sealed record McpDiffEntry(string Path, string Change, string Category, long? SizeBefore, long? SizeAfter);
internal sealed record McpImages(string Summary, IReadOnlyList<byte[]> Pngs);
