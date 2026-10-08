using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using FModel;
using FModel.Services;
using FModel.Services.AssetEditing;
using FModel.Services.Mcp;
using FModel.Services.Verse;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        var output = Path.Combine(root, "artifacts", "compatibility-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var tests = new (string Name, Action Run)[]
        {
            ("日本語リソース・検索強調", CheckInterface),
            ("MCP起動引数", () => Require(McpRelay.IsRelayInvocation(["--mcp", "--no-launch"]), "MCP引数が認識されません")),
            ("MCP中継の埋め込み・更新対象からの分離", () => CheckMcpRelay(output)),
            ("ネイティブACL", () => Require(CUE4ParseNatives.IsFeatureAvailable("ACL"), "ACL DLLが読み込まれません")),
            ("UE6ネイティブVerseダイジェスト", () => CheckDigest(output)),
            ("Verseパッケージ形式のソース情報", CheckPackageDebug),
            ("Verseコンパクト形式のソース情報", CheckCompactDebug),
            ("アセット編集・再読み込み", () => CheckAssetEditing(root, output)),
            ("pak作成・再読み込み", () => CheckPak(output)),
            ("新旧OfferDisplayData・欠落画像参照", () => OfferPreviewChecks.Run(root, output))
        };
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"合格: {name}"); }
            catch (Exception e) { failed++; Console.Error.WriteLine($"失敗: {name}\n{e}"); }
        }
        Console.WriteLine($"合格 {tests.Length - failed}/{tests.Length}、出力: {output}");
        return failed == 0 ? 0 : 1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckMcpRelay(string output)
    {
        var assembly = typeof(McpHost).Assembly;
        using var resource = assembly.GetManifestResourceStream("FModel.Mcp.exe");
        Require(resource is not null && resource.Length > 0, "単独起動できるMCP中継が埋め込まれていません");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(resource!));
        var cache = Path.Combine(output, "mcp-relay");
        var application = Path.Combine(output, "FModel.exe");
        var install = assembly.GetType("FModel.Services.Mcp.McpRelayInstaller")!.GetMethod("Install",
            BindingFlags.NonPublic | BindingFlags.Static, [typeof(string), typeof(string)])!;
        var command = (string) install.Invoke(null, [cache, application])!;
        var executable = Path.Combine(cache, hash, "FModel.Mcp.exe");
        Require(command == $"\"{executable}\" --application \"{application}\"", "接続コマンドのパスが違います");
        using var locked = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(locked)) == hash, "配置した中継が壊れています");
        Require((string) install.Invoke(null, [cache, application])! == command, "実行中の中継を上書きしようとしました");
    }

    private static void CheckInterface()
    {
        UserSettings.Default = new UserSettings { InterfaceLanguage = EInterfaceLanguage.Japanese };
        var app = new FModel.App();
        app.InitializeComponent();
        FModel.App.ApplyInterfaceLanguage();
        Require(app.Resources.MergedDictionaries.Any(d =>
            d.Source?.OriginalString.EndsWith("Japanese.xaml") == true), "日本語リソースがありません");
        const string text = "Game/Textures/TestTexture.uasset";
        var block = new TextBlock();
        SearchPathHighlight.SetText(block, text);
        SearchPathHighlight.SetPattern(block, new SearchHighlightPattern(["texture", "textures"], false));
        var runs = block.Inlines.OfType<Run>().ToArray();
        Require(string.Concat(runs.Select(r => r.Text)) == text, "強調表示でパスが変わりました");
        Require(runs.Count(r => r.Foreground is System.Windows.Media.SolidColorBrush b &&
            b.Color == System.Windows.Media.Color.FromRgb(0xE8, 0x79, 0xF9)) == 2, "一致箇所の色が違います");
    }

    private static FAssetArchive Archive(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        write(writer);
        return new FAssetArchive(new FByteArchive("CompatibilityCheck", stream.ToArray(),
            new VersionContainer(EGame.GAME_UE6_0)), null);
    }

    private static void WriteUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void CheckDigest(string output)
    {
        const string text = "sample := class:\n    Value:int = external {}\n";
        using var archive = Archive(writer =>
        {
            var code = Encoding.UTF8.GetBytes(text);
            writer.Write(code.Length); writer.Write(code);
            for (var i = 0; i < 5; i++) writer.Write(0);
        });
        var digest = new UVerseDigest { Name = "$Digest", Class = new ResolvedLoadedObject(new UObject { Name = "VerseDigest" }) };
        digest.LoadDigestPayload(archive);
        Require(archive.Position == archive.Length, "ダイジェストペイロードの境界が違います");
        Require(Encoding.UTF8.GetString(digest.ReadableCode!) == text, "ネイティブダイジェストが読めません");
        VerseDigestIndex.SearchRoots = [];
        VerseDigestIndex.UserFolder = Path.Combine(output, "digests");
        VerseDigestIndex.Reset();
        var package = PackageProxy.Create(digest);
        Require(VerseDigestIndex.ForPackage(package).Find("sample", "")?.Fields.ContainsKey("Value") == true,
            "パッケージのネイティブダイジェストが復元に使われません");
        digest.DigestCode = null;
        Require(digest.ReadableCode is null && VerseDigestIndex.ForPackage(package).IsEmpty,
            "削除済みダイジェストを安全に扱えません");
    }

    private static object Snapshot(Type type, params object[] args) => typeof(VerseDeclarationRecovery)
        .GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Static,
            args.Length == 1 ? [type] : [typeof(IPackage), type])!.Invoke(null, args)!;

    private static void CheckSnapshot(object snapshot, int expectedFunctions)
    {
        var type = snapshot.GetType();
        var snippets = (string[]) type.GetProperty("Snippets")!.GetValue(snapshot)!;
        Require(snippets.SequenceEqual(["Example/source.verse"]), "Verseファイル名が失われました");
        var functions = ((IEnumerable) type.GetProperty("Functions")!.GetValue(snapshot)!).Cast<object>().ToArray();
        Require(functions.Length == expectedFunctions, "関数のソース情報が失われました");
        foreach (var function in functions)
        {
            var points = ((IEnumerable) function.GetType().GetProperty("Tracepoints")!.GetValue(function)!).Cast<object>().ToArray();
            Require(points.Length == 1 && (uint) points[0].GetType().GetProperty("Row")!.GetValue(points[0])! == 23,
                "トレースポイントの行が変わりました");
        }
    }

    private static void CheckPackageDebug()
    {
        using var archive = Archive(writer =>
        {
            writer.Write(1); WriteUtf8(writer, "/Example/source.verse");
            writer.Write(1);
            var path = Encoding.UTF8.GetBytes("/Example/_Verse.sample:Run\0");
            writer.Write(path.Length); writer.Write(path);
            writer.Write(1); writer.Write(12u); writer.Write(0); writer.Write(23u); writer.Write(4u);
        });
        CheckSnapshot(Snapshot(typeof(FSolarisPackageDebugData), new FSolarisPackageDebugData(archive)), 1);
    }

    private static void CheckCompactDebug()
    {
        using var archive = Archive(writer =>
        {
            writer.Write(1); WriteUtf8(writer, "/Example/source.verse");
            writer.Write(3); // narrow, wide, invalid pool reference
            writer.Write(0); writer.Write(1); writer.Write(1); writer.Write(1); writer.Write(int.MaxValue); writer.Write(1);
            writer.Write(1); writer.Write((ushort) 12); writer.Write((ushort) 0); writer.Write((ushort) 23); writer.Write((ushort) 4);
            writer.Write(1); writer.Write(12u); writer.Write(0); writer.Write(23u); writer.Write(4u);
            writer.Write(0); // no hashed functions; collision paths still resolve
            writer.Write(3);
            for (var i = 0; i < 3; i++)
            {
                var path = Encoding.UTF8.GetBytes($"/Example/_Verse.sample:Run{i}\0");
                writer.Write(path.Length); writer.Write(path); writer.Write(i);
            }
        });
        CheckSnapshot(Snapshot(typeof(FSolarisClientDebugData), PackageProxy.Create(), new FSolarisClientDebugData(archive)), 2);
    }

    private static void CheckAssetEditing(string root, string output)
    {
        var fixture = Path.Combine(root, "UAssetAPI/UAssetAPI.Tests/TestAssets/TestUE5_5/BlankGame");
        using var provider = new DefaultFileProvider(fixture, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_UE5_5), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(Path.Combine(fixture, "BlankUE5_5.usmap"));
        var entry = provider.Files.Values.Single(f => f.Name == "T_Test.uasset");
        var source = provider.LoadPackage(entry);
        var original = JArray.Parse(JsonConvert.SerializeObject(source.GetExports()));
        File.WriteAllText(Path.Combine(output, "asset-original.json"), original.ToString());
        var edited = (JArray) original.DeepClone();
        Require(edited[0]["Properties"]?["LightingGuid"] is not null, "編集用のfixtureプロパティがありません");
        edited[0]["Properties"]!["LightingGuid"] = "00112233-44556677-8899AABB-CCDDEEFF";
        var report = EditedAssetWriter.Write(new EditedAssetWriter.Request
        {
            Provider = provider, Entry = entry, OriginalJson = original.ToString(), EditedJson = edited.ToString(),
            OutputDirectory = Path.Combine(output, "edited")
        });
        Require(!report.HasErrors && report.Verified && report.VerificationMismatches.Count == 0,
            string.Join("\n", report.Errors.Concat(report.VerificationMismatches)));
        Require(report.OutputFiles.Count > 0 && report.OutputFiles.All(File.Exists), "編集ファイルがありません");
    }

    private static void CheckPak(string output)
    {
        var source = Path.Combine(output, "input.ini");
        File.WriteAllText(source, "[Compatibility]\nValue=日本語\n", Encoding.UTF8);
        var files = new[] { (source, "Example/Config/input.ini") };
        foreach (var version in new[] { 3, 7 })
        {
            var result = PakWriter.Write(files, Path.Combine(output, $"test-v{version}.pak"), version, "../../../");
            PakWriter.Verify(result, files, new VersionContainer(EGame.GAME_UE6_0));
            Require(result.VerificationErrors.Count == 0, string.Join("\n", result.VerificationErrors));
        }
    }
}

public class PackageProxy : DispatchProxy
{
    public IFileProvider? Provider;
    private UObject[] _exports = [];
    public static IPackage Create(params UObject[] exports)
    {
        var package = Create<IPackage, PackageProxy>();
        ((PackageProxy) package)._exports = exports;
        return package;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
    {
        "get_Name" => "/Example/_Verse",
        "get_Provider" => Provider,
        "get_ExportMapLength" => _exports.Length,
        "ResolvePackageIndex" => new ResolvedLoadedObject(_exports[((CUE4Parse.UE4.Objects.UObject.FPackageIndex) args![0]!).Index - 1]),
        _ => throw new NotSupportedException(targetMethod.Name)
    };
}
