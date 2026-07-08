using FModel.Framework;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;

namespace FModel.ViewModels;

public class FileItem : ViewModel
{
    private string _name;
    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    private long _length;
    public long Length
    {
        get => _length;
        private set => SetProperty(ref _length, value);
    }

    private int _fileCount;
    public int FileCount
    {
        get => _fileCount;
        set => SetProperty(ref _fileCount, value);
    }

    private string _mountPoint;
    public string MountPoint
    {
        get => _mountPoint;
        set => SetProperty(ref _mountPoint, value);
    }

    private bool _isEncrypted;
    public bool IsEncrypted
    {
        get => _isEncrypted;
        set => SetProperty(ref _isEncrypted, value);
    }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private bool _isLooseFilesContainer;
    public bool IsLooseFilesContainer
    {
        get => _isLooseFilesContainer;
        set => SetProperty(ref _isLooseFilesContainer, value);
    }

    private string _key;
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    private FGuid _guid;
    public FGuid Guid
    {
        get => _guid;
        set => SetProperty(ref _guid, value);
    }

    public FileItem(string name, long length)
    {
        Name = name;
        Length = length;
    }

    public FileItem(string name, int fileCount, long length, bool isLooseFile)
    {
        Name = name;
        Length = length;
        FileCount = fileCount;
        IsLooseFilesContainer = isLooseFile;
        IsEnabled = true;
        Key = string.Empty;
        MountPoint = string.Empty;
    }

    public FileItem(IAesVfsReader reader)
    {
        Name = reader.Name;
        Length = reader.Length;
        Guid = reader.EncryptionKeyGuid;
        IsEncrypted = reader.IsEncrypted;
        IsEnabled = false;
        IsLooseFilesContainer = false;
        Key = string.Empty;
        FileCount = reader is IoStoreReader storeReader ? (int) storeReader.TocResource.Header.TocEntryCount - 1 : 0;
    }

    public override string ToString()
    {
        return $"{Name} | {Key}";
    }
}

public partial class GameDirectoryViewModel : ViewModel
{
    public bool HasNoFile => DirectoryFiles.Count < 1;
    public readonly ObservableCollection<FileItem> DirectoryFiles;
    public ICollectionView DirectoryFilesView { get; }

    private readonly Regex _hiddenArchives = ArchivesRegex();

    public GameDirectoryViewModel()
    {
        DirectoryFiles = new ObservableCollection<FileItem>();
        DirectoryFilesView = new ListCollectionView(DirectoryFiles)
        {
            SortDescriptions =
            {
                new SortDescription(nameof(FileItem.IsLooseFilesContainer), ListSortDirection.Ascending),
                new SortDescription(nameof(FileItem.Name), ListSortDirection.Ascending)
            }
        };

        // CollectionView の同期を有効にする
        BindingOperations.EnableCollectionSynchronization(DirectoryFiles, _lock);
    }

    private readonly object _lock = new object(); // 同期オブジェクト

    // O(1) name -> FileItem lookup (replaces per-archive linear scans that made
    // loading many archives scale ~quadratically).
    private readonly System.Collections.Generic.Dictionary<string, FileItem> _byName =
        new(System.StringComparer.OrdinalIgnoreCase);

    public void Add(IAesVfsReader reader)
    {
        if (!_hiddenArchives.IsMatch(reader.Name)) return;

        var fileItem = new FileItem(reader);

        // O(1) de-dup (the hot path when there are many archives) under the lock.
        lock (_lock)
        {
            if (!_byName.TryAdd(reader.Name, fileItem)) return;
        }

        // DirectoryFilesView is a manually-created ListCollectionView with UI-thread
        // affinity, so the bound collection itself must be modified on the UI thread.
        Application.Current.Dispatcher.Invoke(() => DirectoryFiles.Add(fileItem));
    }

    public void AddLooseFiles(int fileCount)
    {
        if (fileCount < 1)
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var looseFilesContainer = DirectoryFiles.FirstOrDefault(x => x.IsLooseFilesContainer);
            if (looseFilesContainer is not null)
            {
                looseFilesContainer.FileCount += fileCount;
            }
            else
            {
                DirectoryFiles.Add(new FileItem("Loose Files", fileCount, 0, true));
            }
        });
    }

    public void Verify(IAesVfsReader reader)
    {
        FileItem file;
        lock (_lock)
        {
            if (!_byName.TryGetValue(reader.Name, out file)) return;
        }

        file.IsEnabled = true;
        file.MountPoint = reader.MountPoint;
        file.FileCount = reader.FileCount;
    }

    public void Disable(IAesVfsReader reader)
    {
        FileItem file;
        lock (_lock)
        {
            if (!_byName.TryGetValue(reader.Name, out file)) return;
        }

        file.IsEnabled = false;
    }

    /// <summary>Thread-safe snapshot of the current archive list (for serialization, etc.).</summary>
    public FileItem[] SnapshotDirectoryFiles()
    {
        lock (_lock)
        {
            return DirectoryFiles.ToArray();
        }
    }

    [GeneratedRegex(@"^(?!global|pakchunk.+(optional|ondemand)\-).+(pak|utoc)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ArchivesRegex();
}
