using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using FModel.Services.Verse;

namespace FModel.Services.Mcp;

public sealed partial class FModelMcpService
{
    // CUE4Parse の Blueprint 逆コンパイラは静的な状態を持つので、プロセス全体で 1 本ずつ
    private static readonly object DecompilerLock = new();

    public Task<object> DecompileBlueprint(string id, string path, string objectName, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        ValidatePage(offset, limit);
        var file = File(s, path);
        string code;
        lock (DecompilerLock)
        {
            ct.ThrowIfCancellationRequested();
            if (s.IsLive && objectName is null)
            {
                // FModel の「逆コンパイル」と同じ処理 (クラス名の後処理込み)。
                // バイトコードは ReadScriptData 中にしか読まれないので、その間だけ有効にして読み直す
                code = RecoveredVerseGameFile.WithScriptData(s.Provider, () => Cue4Parse.DecompileToPseudoCpp(file));
            }
            else
            {
                if (!s.Provider.ReadScriptData)
                    throw new InvalidOperationException("Reopen the session with readScriptData=true to deserialize Blueprint bytecode.");

                var package = s.Provider.LoadPackage(file);
                var classes = package.GetExports().OfType<UClass>().Where(c => objectName is null || c.Name == objectName).ToArray();
                UClassCookedMetaData metadata = null;
                if (s.Provider.TryGetGameFile(file.PathWithoutExtension + ".o.uasset", out var editorFile))
                    metadata = s.Provider.LoadPackage(editorFile).GetExports().OfType<UClassCookedMetaData>().FirstOrDefault();
                code = classes.Length == 0 ? null : string.Join("\n\n", classes.Select(c => c.DecompileBlueprintToPseudo(metadata)));
            }
        }

        if (code is null) throw new NotSupportedException("No Blueprint class export found. Inspect package exports first.");
        return new
        {
            path = file.Path, language = "cpp-like-pseudocode",
            note = "Generated from cooked Blueprint data. This is approximate pseudocode, not the original C++ or Blueprint source.",
            lines = Paginate(code.Replace("\r\n", "\n").Split('\n'), offset, limit)
        };
    }, ct);

    /// <summary>
    /// クック済み Verse (Fortnite / UEFN) の宣言と関数本体を Verse ソースとして復元する。
    /// FModel の右クリック「Verse 宣言を復元」と同じ処理 (<see cref="ViewModels.CUE4ParseViewModel.RecoverVerseSource"/>)。
    /// </summary>
    public Task<object> RecoverVerse(string id, string path, bool listing, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        ValidatePage(offset, limit);
        RejectLiveOnly(s);
        var file = File(s, path);
        string source;
        lock (DecompilerLock)
        {
            ct.ThrowIfCancellationRequested();
            source = Cue4Parse.RecoverVerseSource(file, listing);
        }

        if (source is null) throw new NotSupportedException("This package does not hold any cooked Verse type.");
        return new
        {
            path = file.Path, language = listing ? "verse-bytecode-listing" : "verse",
            note = "Recovered from cooked Verse metadata and bytecode. Names and structure follow the cook; comments and formatting of the original source are lost.",
            lines = Paginate(source.Replace("\r\n", "\n").Split('\n'), offset, limit)
        };
    }, ct);

    private static void RejectLiveOnly(McpSession s)
    {
        if (!s.IsLive) throw new NotSupportedException("Verse recovery uses FModel's own provider and is only available on the live session.");
    }
}
