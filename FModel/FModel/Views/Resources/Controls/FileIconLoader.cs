using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Textures;
using FModel.Services;
using FModel.Settings;
using CUE4Parse.Utils;
using SkiaSharp;

namespace FModel.Views.Resources.Controls
{
    public static class FileIconLoader
    {
        private static readonly SemaphoreSlim _semaphore = new(10);

        public static readonly DependencyProperty GameFileProperty =
            DependencyProperty.RegisterAttached("GameFile", typeof(GameFile), typeof(FileIconLoader), new PropertyMetadata(null, OnGameFileChanged));

        public static GameFile GetGameFile(DependencyObject obj) => (GameFile)obj.GetValue(GameFileProperty);
        public static void SetGameFile(DependencyObject obj, GameFile value) => obj.SetValue(GameFileProperty, value);

        private static async void OnGameFileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image) return;

            var file = e.NewValue as GameFile;
            if (file == null)
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                // Reset state
                image.Source = null;
                image.Visibility = Visibility.Collapsed;

                await _semaphore.WaitAsync();
                try
                {
                    if (GetGameFile(image) != file) return;

                    var bitmap = await Task.Run(async () =>
                    {
                    var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
                    if (provider == null) return null;
                    if (!file.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase)) return null;

                    try
                    {
                        if (!provider.TryLoadPackage(file.Path, out var package)) return null;
                        var obj = package.GetExports().FirstOrDefault();
                        if (obj == null) return null;

                        // Case 1: Type is Texture2D
                        if (obj is UTexture2D texture)
                        {
                            return DecodeTexture(texture);
                        }

                        // Case 2: DataList contains LargeIcon
                        string assetPath = null;

                        if (obj.TryGetValue(out FInstancedStruct[] dataList, "DataList"))
                        {
                            foreach (var data in dataList)
                            {
                                if (data.NonConstStruct != null && data.NonConstStruct.TryGetValue(out FSoftObjectPath largeIcon, "LargeIcon"))
                                {
                                    assetPath = largeIcon.AssetPathName.Text;
                                    break;
                                }
                            }

                            if (string.IsNullOrEmpty(assetPath))
                            {
                                foreach (var data in dataList)
                                {
                                    if (data.NonConstStruct != null && data.NonConstStruct.TryGetValue(out FSoftObjectPath icon, "Icon"))
                                    {
                                        assetPath = icon.AssetPathName.Text;
                                        break;
                                    }
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(assetPath) && obj.TryGetValue(out FStructFallback[] contextualPresentations, "ContextualPresentations"))
                        {
                            foreach (var presentation in contextualPresentations)
                            {
                                if (presentation.TryGetValue(out FSoftObjectPath renderImage, "RenderImage"))
                                {
                                    assetPath = renderImage.AssetPathName.Text;
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(assetPath))
                        {
                            if (obj.TryGetValue(out FSoftObjectPath softPath, "LargeIcon", "Icon"))
                            {
                                assetPath = softPath.AssetPathName.Text;
                            }
                            else if (obj.TryGetValue(out FStructFallback structFallback, "LargeIcon", "Icon"))
                            {
                                if (structFallback.TryGetValue(out FSoftObjectPath innerPath, "AssetPathName") || 
                                    structFallback.TryGetValue(out innerPath, "ResourceObject"))
                                {
                                    assetPath = innerPath.AssetPathName.Text;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            var packagePath = assetPath.Contains('.') ? assetPath.SubstringBeforeLast('.') : assetPath;
                            if (provider.TryLoadPackage(packagePath, out var linkedPackage) &&
                                linkedPackage.GetExports().FirstOrDefault() is UTexture2D linkedTexture)
                            {
                                return DecodeTexture(linkedTexture);
                            }
                        }
                    }
                    catch
                    {
                        // Suppress errors
                    }

                    return null;
                    });

                    if (bitmap != null && GetGameFile(image) == file)
                    {
                        image.Source = bitmap;
                        image.Visibility = Visibility.Visible;
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch
            {
                // UI thread safety
            }
        }

        private static BitmapSource DecodeTexture(UTexture2D texture)
        {
            try
            {
                var skImage = texture.Decode(UserSettings.Default.CurrentDir.TexturePlatform)?.ToSkBitmap();
                if (skImage == null) return null;

                using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = data.AsStream();

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
            catch
            {
                return null;
            }
        }
    }
}
