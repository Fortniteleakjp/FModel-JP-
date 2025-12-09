using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FModel.Framework;

namespace FModel.ViewModels.FolderBrowser
{
    public class FileSystemItem
    {
        public string Name { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get
            {
                if (_icon == null)
                {
                    _icon = GetIcon(FullPath, IsDirectory);
                }
                return _icon;
            }
        }

        public FileSystemItem(string path, bool isDirectory)
        {
            FullPath = path;
            IsDirectory = isDirectory;
            Name = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(Name)) // ドライブ文字の場合 (e.g., "C:\")
            {
                Name = path;
            }
        }

        // アイコン取得ロジック
        private static ImageSource GetIcon(string path, bool isDirectory)
        {
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;
            if (isDirectory)
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var shfi = new SHFILEINFO();
            var res = SHGetFileInfo(path, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (res == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using (var icon = System.Drawing.Icon.FromHandle(shfi.hIcon))
                {
                    return Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        [DllImport("shell32.dll")]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    }
}