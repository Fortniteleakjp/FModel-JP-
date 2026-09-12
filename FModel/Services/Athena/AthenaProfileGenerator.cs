using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.Services.Athena;

/// <summary>
/// 選択されたコスメティクスから profile_athena.json を生成して出力フォルダに保存する。
/// </summary>
public static class AthenaProfileGenerator
{
    public const string FILE_NAME = "profile_athena.json";

    public static string ProfilesDirectory => Path.Combine(UserSettings.Default.OutputDirectory, "Profiles");

    public static void Generate(IEnumerable<GameFile> entries, AbstractFileProvider provider, CancellationToken cancellationToken)
    {
        var builder = new AthenaProfileBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Yield();

            var itemId = entry.NameWithoutExtension;
            if (!seen.Add(itemId))
                continue;

            try
            {
                var export = provider.SafeLoadPackageObject(entry.PathWithoutExtension);
                if (export is null)
                {
                    skipped.Add(itemId);
                    continue;
                }

                // クラス名で解決できないものは、アセット名の接頭辞から推測する
                var backendType = AthenaItemTable.IsValidClass(export.ExportType)
                    ? AthenaItemTable.GetBackendTypeByClass(export.ExportType)
                    : AthenaItemTable.GetBackendTypeByItemId(itemId);

                if (backendType == "TBD")
                {
                    skipped.Add(itemId);
                    continue;
                }

                var variants = AthenaVariantReader.GetCosmeticVariants(export);
                builder.AddCosmetic(itemId, backendType, variants);

                Log.Information("Added \"{name}\" to the athena profile (Type: {exportType}, Variants: {variantsCount})",
                    itemId, export.ExportType, variants.Count);
            }
            catch (Exception e)
            {
                skipped.Add(itemId);
                Log.Warning("Failed to add {name} to the athena profile: {msg}", itemId, e.Message);
            }
        }

        if (builder.Count == 0)
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("No cosmetics found in the selection, the athena profile was not created", Constants.WHITE, true));
            return;
        }

        Directory.CreateDirectory(ProfilesDirectory);
        var savePath = Path.Combine(ProfilesDirectory, FILE_NAME);
        File.WriteAllText(savePath, builder.Build());

        var count = builder.Count;
        FLogger.Append(ELog.Information, () =>
        {
            FLogger.Text($"Successfully created an athena profile with {count} cosmetics in ", Constants.WHITE);
            FLogger.Link(FILE_NAME, savePath, true);
        });

        if (skipped.Count > 0)
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"Skipped {skipped.Count} non-cosmetic assets: {string.Join(", ", skipped.Take(5))}{(skipped.Count > 5 ? "..." : string.Empty)}", Constants.WHITE, true));
        }
    }
}
