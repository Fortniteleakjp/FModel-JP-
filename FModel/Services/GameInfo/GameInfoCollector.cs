using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Settings;

namespace FModel.Services.GameInfo;

/// <summary>ゲーム情報ウィンドウに渡す、今読み込んでいるゲームの状態</summary>
public sealed record GameInfoContext(
    AbstractVfsFileProvider Provider,
    DirectorySettings Directory,
    string MappingsFilePath,
    string LiveBuildVersion);

/// <summary>
/// 読み込み中のゲームについて、FModel の設定・実行ファイル・パッケージ・アーカイブ・マッピングから
/// UE バージョンの手がかりを集め、設定とずれていないかを確かめる
/// </summary>
public static class GameInfoCollector
{
    private const int MaxPackageSamples = 40;
    private const long MaxPackageSampleSize = 4 * 1024 * 1024;

    public static GameInfoReport Collect(GameInfoContext context, CancellationToken cancellationToken)
    {
        var report = new GameInfoReport();
        var evidence = new List<EngineVersionEvidence>();
        var provider = context.Provider;
        var configured = provider?.Versions.Game ?? context.Directory?.UeVersion ?? EGame.GAME_UE4_LATEST;

        report.Sections.Add(GameSection(context));
        cancellationToken.ThrowIfCancellationRequested();

        var versionSection = new InfoSection("UE バージョン");
        report.Sections.Add(versionSection);
        AddConfiguredVersion(versionSection, provider, configured);

        var changelists = new List<uint>();
        if (MappingsInspector.ChangelistFromText(context.LiveBuildVersion) is { } liveChangelist) changelists.Add(liveChangelist);
        var executableSection = ExecutableSection(context.Directory?.GameDirectory, evidence, changelists);
        if (executableSection.Rows.Count > 0) report.Sections.Add(executableSection);
        cancellationToken.ThrowIfCancellationRequested();

        if (provider != null)
        {
            var packageSection = PackageSection(provider, evidence, cancellationToken);
            if (packageSection.Rows.Count > 0) report.Sections.Add(packageSection);
            cancellationToken.ThrowIfCancellationRequested();

            report.Sections.Add(ArchiveSection(provider));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var mappingsSection = MappingsSection(context, configured, evidence, changelists, report.Warnings);
        report.Sections.Add(mappingsSection);

        CompareVersions(versionSection, configured, evidence, report.Warnings);
        return report;
    }

    private static InfoSection GameSection(GameInfoContext context)
    {
        var section = new InfoSection("ゲーム");
        var provider = context.Provider;
        var directory = context.Directory;

        section.Add("名前", directory?.GameName);
        section.Add("プロジェクト名", provider?.ProjectName);
        if (provider?.GameDisplayName is { } displayName && displayName != provider.ProjectName)
            section.Add("表示名", displayName);
        section.Add("フォルダ", directory?.GameDirectory);
        section.Add("ビルド (LIVE)", context.LiveBuildVersion);
        if (provider == null) return section;

        section.Add("読み込み方式", provider.GetType().Name);
        section.Add("ファイル数", provider.Files.Count.ToString("N0"));
        section.Add("アーカイブ", $"{provider.MountedVfs.Count:N0} 個マウント済み / {provider.UnloadedVfs.Count:N0} 個未マウント",
            provider.UnloadedVfs.Count > 0 ? InfoSeverity.Warning : InfoSeverity.Normal,
            provider.UnloadedVfs.Count > 0 ? "AES キーが足りないアーカイブがあります" : null);
        section.Add("AES キー", $"必要 {provider.RequiredKeys.Count:N0} 個 / 入力済み {provider.Keys.Count:N0} 個");
        return section;
    }

    private static void AddConfiguredVersion(InfoSection section, AbstractVfsFileProvider provider, EGame configured)
    {
        var engine = EngineVersion.FromGame(configured);
        var text = engine is { } version ? $"{configured} (UE {version})" : configured.ToString();
        if (EngineVersion.IsGameSpecific(configured)) text += " ・ゲーム固有の設定";
        section.Add("FModel の設定", text);

        if (provider == null) return;
        var ver = provider.Versions.Ver;
        section.Add("パッケージバージョン (設定)", FormatPackageVersion(ver));
        section.Add("テクスチャプラットフォーム", provider.Versions.Platform.ToString());
    }

    #region Executables

    /// <param name="changelists">見つかった CL。エディタ (UEFN) の実行ファイルはゲームと別ビルドのことがあるので後ろに回す</param>
    private static InfoSection ExecutableSection(string gameDirectory, List<EngineVersionEvidence> evidence, List<uint> changelists)
    {
        var editorChangelists = new List<uint>();
        var section = new InfoSection("実行ファイル");
        foreach (var exe in CandidateExecutables(gameDirectory))
        {
            FileVersionInfo info;
            try
            {
                info = FileVersionInfo.GetVersionInfo(exe);
            }
            catch
            {
                continue;
            }

            var versionText = string.Join("  /  ", new[] { info.FileVersion, info.ProductVersion }
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct());
            // 文字列のバージョンが空でも数値のバージョン (FILEVERSION) は入っていることが多い
            if (string.IsNullOrEmpty(versionText) && info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart > 0)
                versionText = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            if (string.IsNullOrEmpty(versionText)) versionText = "(バージョン情報なし)";

            string note = null;
            if (EngineVersion.FromNumbers(info.FileMajorPart, info.FileMinorPart) is { } engine)
            {
                evidence.Add(new EngineVersionEvidence(Path.GetFileName(exe), engine, EvidenceReliability.Executable));
                note = $"UE {engine}";
            }

            if ((MappingsInspector.ChangelistFromText(info.ProductVersion) ?? MappingsInspector.ChangelistFromText(info.FileVersion)) is { } changelist)
                (Path.GetFileName(exe).Contains("Editor", StringComparison.OrdinalIgnoreCase) ? editorChangelists : changelists).Add(changelist);
            section.Add(Path.GetFileName(exe), versionText, note: note == null ? exe : $"{note} ・ {exe}");
        }

        changelists.AddRange(editorChangelists);
        return section;
    }

    /// <summary>
    /// Paks フォルダから見た実行ファイルの候補。<see cref="ViewModels.GameSelectorViewModel"/> の UE 版の自動判定と同じ場所を見る
    /// (プロジェクトの Binaries/Win64、エンジンの CrashReportClient、ゲームフォルダ直下のランチャー)
    /// </summary>
    private static IEnumerable<string> CandidateExecutables(string gameDirectory)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory)) yield break;

        var results = new List<string>();
        try
        {
            var paksDirectory = gameDirectory;
            if (!paksDirectory.TrimEnd('\\', '/').EndsWith("Paks", StringComparison.OrdinalIgnoreCase))
                results.AddRange(Directory.GetFiles(gameDirectory, "*.exe").Take(2));

            var projectDirectory = Path.GetFullPath(Path.Combine(paksDirectory, "..", ".."));
            var projectBinaries = Path.Combine(projectDirectory, "Binaries", "Win64");
            if (Directory.Exists(projectBinaries))
            {
                var shipping = Directory.GetFiles(projectBinaries, "*-Shipping.exe");
                results.AddRange(shipping.Length > 0 ? shipping.Take(3) : Directory.GetFiles(projectBinaries, "*.exe").Take(3));
            }

            var rootDirectory = Path.GetFullPath(Path.Combine(projectDirectory, ".."));
            var crashReporter = Path.Combine(rootDirectory, "Engine", "Binaries", "Win64", "CrashReportClient.exe");
            if (File.Exists(crashReporter)) results.Add(crashReporter);

            if (Directory.Exists(rootDirectory))
                results.AddRange(Directory.GetFiles(rootDirectory, "*.exe").Take(2));
        }
        catch
        {
            // 権限のないフォルダなど。見つかった分だけ使う
        }

        foreach (var exe in results.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return exe;
    }

    #endregion

    #region Packages

    /// <summary>
    /// pak 内の .uasset のヘッダー (FPackageFileSummary) から保存時のバージョンを読む。
    /// IoStore (zen) パッケージはバージョンを持たないので pak だけを見る
    /// </summary>
    private static InfoSection PackageSection(AbstractVfsFileProvider provider, List<EngineVersionEvidence> evidence, CancellationToken cancellationToken)
    {
        var section = new InfoSection("パッケージ (.uasset)");
        var candidates = provider.Files.Values
            .Where(f => f is VfsEntry { Vfs: PakFileReader } && f.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) && f.Size is > 0 and < MaxPackageSampleSize)
            .Take(2000)
            .OrderBy(f => f.Size)
            .Take(MaxPackageSamples)
            .ToList();

        if (candidates.Count == 0)
        {
            section.Add("サンプル", "pak 内の .uasset なし (IoStore のパッケージはバージョンを記録しません)");
            return section;
        }

        FPackageFileSummary versioned = null;
        string versionedPath = null;
        FPackageFileSummary anySummary = null;
        var unversionedCount = 0;
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!file.TryRead(out var data)) continue;
                // サマリーの読み込みは Ar.Ver を書き換えるので、プロバイダのバージョン設定を複製して渡す
                var summary = new FPackageFileSummary(new FByteArchive(file.Path, data, (VersionContainer) provider.Versions.Clone()));
                anySummary ??= summary;
                if (summary.bUnversioned)
                {
                    unversionedCount++;
                    continue;
                }

                versioned = summary;
                versionedPath = file.Path;
                break;
            }
            catch
            {
                // 独自形式のパッケージなど。次の候補へ
            }
        }

        if (versioned != null)
        {
            section.Add("サンプル", versionedPath);
            section.Add("パッケージバージョン", FormatPackageVersion(versioned.FileVersionUE));
            if (versioned.FileVersionLicenseeUE != 0)
                section.Add("ライセンシーバージョン", ((int) versioned.FileVersionLicenseeUE).ToString());

            if (InferFromPackageVersion(versioned.FileVersionUE) is { } inferred)
            {
                evidence.Add(new EngineVersionEvidence("パッケージバージョン", inferred.Version, EvidenceReliability.PackageVersion, inferred.IsRange));
                section.Add("推定 UE", inferred.Text);
            }
        }
        else if (anySummary != null)
        {
            section.Add("サンプル", $"{unversionedCount} 件ともバージョン無しで保存 (Unversioned)", note: "クック時にバージョンを省いたゲームです");
        }

        var engineSummary = versioned ?? anySummary;
        if (engineSummary?.SavedByEngineVersion is { Major: > 0 } savedBy)
        {
            section.Add("保存したエンジン", savedBy.ToString());
            if (EngineVersion.FromNumbers(savedBy.Major, savedBy.Minor) is { } engine)
                evidence.Add(new EngineVersionEvidence("保存したエンジン", engine, EvidenceReliability.SavedPackage));
        }

        if (engineSummary?.CompatibleWithEngineVersion is { Major: > 0 } compatible && compatible.ToString() != engineSummary.SavedByEngineVersion?.ToString())
            section.Add("互換エンジン", compatible.ToString());

        return section;
    }

    /// <summary>
    /// CUE4Parse の EGame → パッケージバージョン表 (<see cref="GameUtils.GetVersion"/>) を逆に引く。
    /// 同じ値が複数の版に割り当てられている場合 (4.17/4.18 など) は範囲で返す
    /// </summary>
    private static (EngineVersion Version, bool IsRange, string Text)? InferFromPackageVersion(FPackageFileVersion version)
    {
        var candidates = new List<EngineVersion>();
        EngineVersion? lowerBound = null;
        var isUe5 = version.FileVersionUE5 >= (int) EUnrealEngineObjectUE5Version.INITIAL_VERSION;
        var major = isUe5 ? 5 : 4;
        var fileValue = isUe5 ? version.FileVersionUE5 : version.FileVersionUE4;
        if (fileValue <= 0) return null;

        var lastMinor = -1;
        var reachedLatest = false;
        for (var minor = 0; minor < 30; minor++)
        {
            var game = (EGame) ((major == 5 ? GameUtils.GameUe5Base : GameUtils.GameUe4Base) + (minor << 16));
            if (!Enum.IsDefined(game)) break;

            var tableVersion = game.GetVersion();
            var tableValue = isUe5 ? tableVersion.FileVersionUE5 : tableVersion.FileVersionUE4;
            // AUTOMATIC_VERSION (表の末尾) は「最新」の意味なので比較に使わない
            if (tableValue >= (isUe5 ? (int) EUnrealEngineObjectUE5Version.AUTOMATIC_VERSION : (int) EUnrealEngineObjectUE4Version.AUTOMATIC_VERSION))
            {
                reachedLatest = true;
                break;
            }

            lastMinor = minor;
            if (tableValue == fileValue) candidates.Add(new EngineVersion(major, minor));
            if (tableValue <= fileValue) lowerBound = new EngineVersion(major, minor);
        }

        // 表の最後の版と同じ値なら、それ以降 (表に値の無い新しい版) も同じかもしれない (4.26 と 4.27 はどちらも 522)
        if (reachedLatest && candidates.Count > 0 && candidates[^1].Minor == lastMinor)
            return (candidates[0], true, $"UE {candidates[0]} 以降");
        if (candidates.Count == 1) return (candidates[0], false, $"UE {candidates[0]}");
        if (candidates.Count > 1) return (candidates[0], true, $"UE {candidates[0]} 〜 {candidates[^1]}");
        if (lowerBound is { } bound) return (bound, true, $"UE {bound} 以降");
        return null;
    }

    #endregion

    #region Archives

    private static InfoSection ArchiveSection(AbstractVfsFileProvider provider)
    {
        var section = new InfoSection("アーカイブ形式");
        var mounted = provider.MountedVfs.ToArray();

        var pakVersions = mounted.OfType<PakFileReader>()
            .GroupBy(p => p.Info.Version)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} ({(int) g.Key}) ×{g.Count()}")
            .ToArray();
        if (pakVersions.Length > 0) section.Add("pak", string.Join(", ", pakVersions));

        var ioStores = mounted.OfType<IoStoreReader>().ToArray();
        var local = ioStores.Where(r => r is not IoStoreOnDemandReader).ToArray();
        var tocVersions = local
            .GroupBy(r => r.TocResource.Header.Version)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} ({(int) g.Key}) ×{g.Count()}")
            .ToArray();
        if (tocVersions.Length > 0) section.Add("IoStore (utoc)", string.Join(", ", tocVersions));

        var onDemand = ioStores.Length - local.Length;
        if (onDemand > 0) section.Add("IoStore オンデマンド", $"{onDemand:N0} 個");

        // コンテナヘッダーは読むと 1 チャンク展開するので、ローカルのコンテナを 1 つだけ見る (オンデマンドはダウンロードが走るので触らない)
        foreach (var reader in local.Where(r => !r.Name.StartsWith("global", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (reader.ContainerHeader is not { } header) continue;
                section.Add("コンテナヘッダー", $"{header.Version} ({(int) header.Version})");
                break;
            }
            catch
            {
                // 暗号化キーが無いなど
            }
        }

        if (section.Rows.Count == 0) section.Add("アーカイブ", "マウント済みのアーカイブなし");
        return section;
    }

    #endregion

    #region Mappings

    private static InfoSection MappingsSection(GameInfoContext context, EGame configured, List<EngineVersionEvidence> evidence, List<uint> gameChangelists, List<string> warnings)
    {
        var section = new InfoSection("マッピング");
        var path = context.MappingsFilePath;
        var hasMappings = context.Provider?.MappingsContainer != null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (hasMappings)
            {
                section.Add("ファイル", "読み込み済み (ファイルの場所は不明)");
            }
            else if (configured >= EGame.GAME_UE5_0)
            {
                section.Add("ファイル", "読み込まれていません", InfoSeverity.Warning,
                    "UE5 以降のゲームはプロパティを読むのにマッピングが必要なことがあります (設定 > マッピング)");
            }
            else
            {
                section.Add("ファイル", "読み込まれていません", note: "UE4 のゲームでは通常は不要です");
            }

            return section;
        }

        try
        {
            var header = MappingsInspector.ReadHeader(path);
            MappingsInspector.AddHeaderRows(section, header);
            if (header.EngineVersion is { Major: > 0 } engine && EngineVersion.FromNumbers(engine.Major, engine.Minor) is { } version)
                evidence.Add(new EngineVersionEvidence("マッピング", version, EvidenceReliability.Mappings));

            if (gameChangelists.Count > 0 && header.Changelist is { } mappingCl)
            {
                var gameCl = gameChangelists[0];
                if (gameChangelists.Contains(mappingCl))
                {
                    section.Add("ゲームとの一致", $"CL {mappingCl} (一致)", InfoSeverity.Good);
                }
                else
                {
                    section.Add("ゲームとの一致", $"マッピング CL {mappingCl} / ゲーム CL {gameCl}", InfoSeverity.Warning,
                        "別のビルド用のマッピングです。プロパティが正しく読めないことがあります");
                    warnings.Add($"マッピングの CL ({mappingCl}) がゲームの CL ({gameCl}) と一致しません。");
                }
            }
        }
        catch (Exception e)
        {
            section.Add("ファイル", Path.GetFileName(path), note: path);
            section.Add("読み込み", e.Message, InfoSeverity.Warning);
        }

        if (context.Provider?.MappingsContainer?.MappingsForGame is { } mappings)
            MappingsInspector.AddStatisticsRows(section, MappingsInspector.GetStatistics(mappings));

        return section;
    }

    #endregion

    private static void CompareVersions(InfoSection section, EGame configured, List<EngineVersionEvidence> evidence, List<string> warnings)
    {
        if (evidence.Count == 0)
        {
            section.Add("検出結果", "実行ファイル・パッケージ・マッピングのいずれからも UE バージョンを特定できませんでした");
            return;
        }

        var ordered = evidence.OrderBy(e => e.Reliability).ThenBy(e => e.IsRange).ToList();
        var detected = string.Join(" / ", ordered
            .GroupBy(e => e.Version)
            .Select(g => $"UE {g.Key}{(g.All(e => e.IsRange) ? "?" : string.Empty)} ({string.Join(", ", g.Select(e => e.Source))})"));

        if (EngineVersion.FromGame(configured) is not { } configuredVersion)
        {
            section.Add("検出結果", detected);
            return;
        }

        // 最も確かな種類の根拠だけで判定する。Fortnite のように、古いエンジンで保存されたままのパッケージ (5.8) が
        // 今の実行ファイル (6.0) と食い違うことがあるので、下位の根拠では警告しない
        var deciding = ordered.Where(e => e.Reliability == ordered[0].Reliability).ToList();
        var mismatched = deciding.Where(e => !Matches(e, configuredVersion)).ToList();
        if (mismatched.Count == 0)
        {
            var note = "FModel の設定と一致しています";
            var others = ordered.Where(e => e.Reliability != ordered[0].Reliability && !Matches(e, configuredVersion)).Select(e => $"{e.Source}: UE {e.Version}").Distinct().ToArray();
            if (others.Length > 0)
                note += $" ({deciding[0].Source} を優先。{string.Join(", ", others)} は古いビルドのデータの可能性があります)";
            section.Add("検出結果", detected, InfoSeverity.Good, note);
            return;
        }

        var target = mismatched[0];
        section.Add("検出結果", detected, InfoSeverity.Warning,
            $"FModel の設定 (UE {configuredVersion}) と違います。ゲーム選択画面で UE バージョンを見直してください");
        warnings.Add($"FModel の設定は UE {configuredVersion} ですが、{target.Source} は UE {target.Version} を示しています。");

        // 範囲 (「5.6 以降」など) は下限以上なら一致とみなす
        static bool Matches(EngineVersionEvidence e, EngineVersion configured) => e.IsRange
            ? (configured.Major, configured.Minor).CompareTo((e.Version.Major, e.Version.Minor)) >= 0
            : e.Version == configured;
    }

    public static string FormatPackageVersion(FPackageFileVersion version)
    {
        var text = version.FileVersionUE5 > 0
            ? $"UE4 {version.FileVersionUE4} / UE5 {version.FileVersionUE5}"
            : $"UE4 {version.FileVersionUE4}";
        var name = version.ToString();
        return string.IsNullOrEmpty(name) || int.TryParse(name, out _) ? text : $"{text} ({name})";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F2} GB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):F2} MB",
        >= 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };
}
