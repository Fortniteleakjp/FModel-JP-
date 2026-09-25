using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;
using Serilog;

namespace FModel.Services.Verse;

/// <summary>
/// A virtual .verse file backed by the cooked *_Verse.uasset that FModel already has mounted.
/// The source is reconstructed lazily so adding these entries to the asset tree does not require
/// parsing every Verse package during the initial file scan.
/// </summary>
public sealed class RecoveredVerseGameFile : GameFile
{
    private const string CookedSuffix = "_Verse";

    private readonly IFileProvider _provider;
    private readonly IPackage _package;
    private readonly byte[] _rawPackage;
    private readonly bool _wholePackage;
    private readonly Lazy<byte[]> _content;

    public GameFile CookedAsset { get; }
    public string SourcePath { get; }

    private RecoveredVerseGameFile(GameFile cookedAsset, IFileProvider provider, IPackage package,
        byte[] rawPackage, string sourcePath, string versePath, bool wholePackage = false)
        : base(versePath, 0)
    {
        CookedAsset = cookedAsset;
        SourcePath = sourcePath;
        _provider = provider;
        _package = package;
        _rawPackage = rawPackage;
        _wholePackage = wholePackage;
        _content = new Lazy<byte[]>(Recover, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override bool IsEncrypted => CookedAsset.IsEncrypted;
    public override CompressionMethod CompressionMethod => CookedAsset.CompressionMethod;

    public override byte[] Read(FByteBulkDataHeader? header = null) => _content.Value;

    public override FArchive CreateReader(FByteBulkDataHeader? header = null) =>
        new FByteArchive(Path, _content.Value, _provider.Versions);

    private static readonly object ScriptDataLock = new();
    private static int _scriptDataUsers;
    private static bool _savedReadScriptData;

    private byte[] Recover() => Encoding.UTF8.GetBytes(Recover(VerseBodyMode.Rebuilt));

    /// <summary>the source file recovered with the bodies written the given way</summary>
    public string Recover(VerseBodyMode mode) => WithScriptData(_provider, () => _wholePackage
        ? VerseDeclarationRecovery.FromPackage(_package, mode)
        : VerseDeclarationRecovery.FromPackageSource(_package, SourcePath, mode, _rawPackage));

    /// <summary>
    /// Function bodies are synthesised from the Kismet bytecode, which UStruct only deserializes
    /// while the provider's ReadScriptData is on. That user setting is off by default, and with it
    /// off every recovered body reads "the cook holds no bytecode". The exports are deserialized
    /// lazily during recovery, so it is forced on for exactly that window and restored afterwards.
    /// </summary>
    public static T WithScriptData<T>(IFileProvider provider, Func<T> work)
    {
        lock (ScriptDataLock)
        {
            if (_scriptDataUsers++ == 0)
            {
                _savedReadScriptData = provider.ReadScriptData;
                provider.ReadScriptData = true;
            }
        }

        try
        {
            return work();
        }
        finally
        {
            lock (ScriptDataLock)
            {
                if (--_scriptDataUsers == 0)
                    provider.ReadScriptData = _savedReadScriptData;
            }
        }
    }

    public static IReadOnlyList<GameFile> AddRecoveredFiles(IReadOnlyCollection<GameFile> entries, IFileProvider provider)
    {
        var result = new List<GameFile>(entries.Count);
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
            knownPaths.Add(entry.Path);

        foreach (var entry in entries)
        {
            result.Add(entry);
            if (!IsCookedVersePackage(entry)) continue;

            try
            {
                var package = provider.LoadPackage(entry);
                var rawPackage = entry.Read();
                var sourceFiles = VerseDeclarationRecovery.SourceFiles(package, rawPackage);
                if (sourceFiles.Count == 0)
                {
                    AddFallback(entry, provider, package, rawPackage, knownPaths, result);
                    continue;
                }

                var separator = entry.Path.LastIndexOf('/');
                var directory = separator >= 0 ? entry.Path[..(separator + 1)] : string.Empty;
                foreach (var sourcePath in sourceFiles)
                {
                    var recoveredPath = directory + sourcePath;
                    if (!knownPaths.Add(recoveredPath)) continue;
                    result.Add(new RecoveredVerseGameFile(entry, provider, package, rawPackage, sourcePath, recoveredPath));
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not enumerate recovered Verse source files from {VersePackage}", entry.Path);
            }
        }

        return result;
    }

    private static bool IsCookedVersePackage(GameFile cookedAsset) =>
        cookedAsset.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) &&
        cookedAsset.NameWithoutExtension.EndsWith(CookedSuffix, StringComparison.OrdinalIgnoreCase);

    private static void AddFallback(GameFile cookedAsset, IFileProvider provider, IPackage package, byte[] rawPackage,
        HashSet<string> knownPaths, List<GameFile> result)
    {
        var sourceName = cookedAsset.NameWithoutExtension[..^CookedSuffix.Length];
        if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "RecoveredVerse";

        var separator = cookedAsset.Path.LastIndexOf('/');
        var directory = separator >= 0 ? cookedAsset.Path[..(separator + 1)] : string.Empty;
        var sourcePath = $"{sourceName}.verse";
        var recoveredPath = directory + sourcePath;
        if (!knownPaths.Add(recoveredPath)) return;

        result.Add(new RecoveredVerseGameFile(cookedAsset, provider, package, rawPackage, sourcePath, recoveredPath,
            wholePackage: true));
    }
}
