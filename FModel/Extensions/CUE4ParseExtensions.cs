using System;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FModel.Settings;

namespace FModel.Extensions;

public static class CUE4ParseExtensions
{
    public class LoadPackageResult
    {
        // export indexes found in the document are relative to the page being displayed, the tab keeps
        // InclusiveStart around (TabItem.ExportPageStart) so inner package navigation can offset them back
        // we still reload the package on every page change instead of re-using it, which could be improved

        private const int PaginationThreshold = 5000;
        private static int MaxExportPerPage => UserSettings.Default.MaxExportPerPage;

        public IPackage Package;
        public int RequestedIndex;

        public bool IsPaginated => Package.ExportMapLength >= PaginationThreshold;

        /// <summary>
        /// index of the first export on the current page
        /// this index is the starting point for additional data preview
        ///
        /// it can be >0 even if <see cref="IsPaginated"/> is false if we want to focus data preview on a specific export
        /// in this case, we will display all exports but only the focused one will be checked for data preview
        /// </summary>
        public int InclusiveStart => Math.Max(0, RequestedIndex - RequestedIndex % MaxExportPerPage);
        /// <summary>
        /// last exclusive export index of the current page
        /// </summary>
        public int ExclusiveEnd => IsPaginated
            ? Math.Min(InclusiveStart + MaxExportPerPage, Package.ExportMapLength)
            : Package.ExportMapLength;
        public int PageSize => ExclusiveEnd - InclusiveStart;

        /// <summary>
        /// zero based index of the page <see cref="InclusiveStart"/> belongs to
        /// </summary>
        public int PageIndex => IsPaginated ? InclusiveStart / MaxExportPerPage : 0;
        /// <summary>
        /// how many pages the package is split into
        /// </summary>
        public int PageCount => IsPaginated ? (Package.ExportMapLength + MaxExportPerPage - 1) / MaxExportPerPage : 1;

        /// <summary>
        /// index the first export of <see cref="GetDisplayData"/> has in the package
        /// 0 when the whole export map is displayed, since indexes are then already absolute
        /// </summary>
        public int DocumentExportStart => IsPaginated ? InclusiveStart : 0;

        public string TabTitleExtra => IsPaginated ? $"Export{(PageSize > 1 ? "s" : "")} {InclusiveStart}{(PageSize > 1 ? $"-{ExclusiveEnd - 1}" : "")} of {Package.ExportMapLength - 1}" : null;

        /// <summary>
        /// display all exports unless paginated
        /// </summary>
        /// <param name="save">if we save the data we will display all exports even if <see cref="IsPaginated"/> is true</param>
        /// <returns></returns>
        public object GetDisplayData(bool save = false) => !save && IsPaginated
            ? Package.GetExports(InclusiveStart, PageSize)
            : Package.GetExports();
    }

    public static LoadPackageResult GetLoadPackageResult(this IFileProvider provider, GameFile file, string objectName = null)
    {
        var result = new LoadPackageResult { Package = provider.LoadPackage(file) };
        if (result.IsPaginated || (result.Package.HasFlags(EPackageFlags.PKG_ContainsMap) && UserSettings.Default.PreviewWorlds)) // focus on UWorld if it's a map we want to preview
        {
            result.RequestedIndex = result.Package.GetExportIndex(file.NameWithoutExtension);
            if (objectName != null)
            {
                result.RequestedIndex = int.TryParse(objectName, out var index) ? index : result.Package.GetExportIndex(objectName);
            }
        }

        return result;
    }

    /// <summary>
    /// the reverse of <see cref="AbstractFileProvider.FixPath"/>, turns a file path into the object path the engine uses
    /// "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Foo.uasset" -> "/BRCosmetics/Athena/Foo.Foo"
    /// </summary>
    public static string GetObjectPath(this IFileProvider provider, GameFile file)
    {
        const string content = "/Content/";
        // package names can't contain dots, "Foo.o.uasset" (optional package) holds the same object as "Foo.uasset"
        var objectName = file.NameWithoutExtension.SubstringBefore('.');
        var path = file.PathWithoutExtension[..^file.NameWithoutExtension.Length] + objectName;

        var index = path.IndexOf(content, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return $"/{path}.{objectName}"; // not mounted (Config, etc.), nothing better to give

        var root = path[..index];
        var tree = path[(index + content.Length)..];
        return $"/{GetMountPoint(provider, root)}/{tree}.{objectName}";
    }

    private static string GetMountPoint(IFileProvider provider, string root)
    {
        if (root.Equals(provider.ProjectName, StringComparison.OrdinalIgnoreCase)) return "Game";
        if (root.Equals("Engine", StringComparison.OrdinalIgnoreCase)) return "Engine";

        // plugins are usually mounted under their folder name, but the .uplugin name is what actually counts
        var folderName = root.SubstringAfterLast('/');
        if (provider.VirtualPaths.TryGetValue(folderName, out var mounted) && root.Equals(mounted, StringComparison.OrdinalIgnoreCase))
            return folderName;

        foreach (var (name, pluginPath) in provider.VirtualPaths)
        {
            if (root.Equals(pluginPath, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return folderName;
    }
}
