using System.IO;
using System.Reflection;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using FModel.Creator;
using SkiaSharp;

internal static class TypefaceChecks
{
    internal static void Run(string root)
    {
        const string current = "FortniteGame/Plugins/FortUILibrary/Content/Fonts/";
        const string legacy = "FortniteGame/Content/UI/Foundation/Fonts/";
        const string name = "NotoSansJP-Bold.ufont";
        var bytes = File.ReadAllBytes(Path.Combine(root, "FModel/Resources/BurbankBigCondensed-Bold.ttf"));
        var provider = DispatchProxy.Create<IFileProvider, TypefaceProviderProxy>();
        var state = (TypefaceProviderProxy) provider;
        state.Fonts[legacy + name] = bytes;
        var fonts = new Typefaces(provider, ELanguage.Japanese);
        Require(!ReferenceEquals(fonts.DisplayName, fonts.Default), "旧配置の名前フォントが読み込まれません");
        Require(!ReferenceEquals(fonts.Description, fonts.Default), "旧配置の説明フォントが読み込まれません");
        Require(fonts.Bottom == null, "日本語の下部フォントのnull指定が変わりました");
        using (var font = fonts.OnTheFly("/FortUILibrary/Fonts/" + name, true))
            Require(font != null, "コミュニティデザインの新配置パスを旧配置へ変換できません");

        state.Fonts[current + name] = bytes;
        state.Requested.Clear();
        using (var font = fonts.OnTheFly(current + name, true))
            Require(font != null && state.Requested.SequenceEqual([current + name]), "指定したフォントの優先順位が変わりました");
        state.Fonts.Remove(legacy + name);
        using (var font = fonts.OnTheFly("/Game/UI/Foundation/Fonts/" + name, true))
            Require(font != null, "旧デザインから新配置のフォントを読み込めません");

        state.Fonts[current + name] = [0, 1, 2];
        state.Fonts[legacy + name] = bytes;
        using (var font = fonts.OnTheFly(current + name, true))
            Require(font != null, "不正なフォントから旧配置へフォールバックできません");
        state.Fonts.Clear();
        Require(ReferenceEquals(fonts.OnTheFly(current + name), fonts.Default), "欠落フォントの既定値が変わりました");
        Require(fonts.OnTheFly(current + name, true) == null, "任意フォントの欠落時にnullが返りません");

        // Newer UE fonts have a four-byte prefix; preserve support for that format.
        state.Version = new VersionContainer(EGame.GAME_UE5_6);
        state.Fonts[current + name] = [0, 0, 0, 0, .. bytes];
        using (var font = fonts.OnTheFly(current + name, true))
            Require(font != null, "新形式のフォントが読み込めません");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class TypefaceProviderProxy : DispatchProxy
{
    internal readonly Dictionary<string, byte[]> Fonts = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Requested = [];
    internal VersionContainer Version = new(EGame.GAME_UE4_26);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_ProjectName": return "FortniteGame";
            case "get_Versions": return Version;
            case "TrySaveAsset":
                var path = (string) args![0]!;
                Requested.Add(path);
                var found = Fonts.TryGetValue(path, out var bytes);
                args[1] = bytes;
                return found;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
