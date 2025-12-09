using System;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Versions;

namespace FModelTest
{
    public class IniFileReader
    {
        /// <summary>
        /// PAK/IoStoreから.iniファイルを取得・復号化して内容を取得する
        /// </summary>
        public static void ReadIniFromPak()
        {
            // 1. FileProviderを初期化
            var provider = new DefaultFileProvider(
                gamePath: @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks",
                searchPattern: "*.pak",
                isCaseSensitive: false
            );
            
            // ゲームバージョンを指定（Fortniteの場合）
            provider.Versions = new VersionContainer(EGame.GAME_UE5_3);
            
            // 2. PAKファイルを初期化（マウント）
            provider.Initialize();
            
            // 3. AESキーを登録（必要な場合）
            // var aesKey = new FAesKey("0x1234567890ABCDEF...");
            // provider.SubmitKey(guid, aesKey);
            
            // 4. マウント済みVFSの全ファイルを確認
            Console.WriteLine($"Total VFS files: {provider.Files.Count}");
            
            // .iniファイルを検索
            var iniFiles = provider.Files.Where(kvp => kvp.Key.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"\nFound {iniFiles.Count} .ini files in VFS:");
            foreach (var iniFile in iniFiles.Take(20)) // 最初の20件表示
            {
                Console.WriteLine($"  - {iniFile.Key}");
                if (iniFile.Value is CUE4Parse.UE4.VirtualFileSystem.VfsEntry vfsEntry)
                {
                    Console.WriteLine($"    VFS: {vfsEntry.Vfs.Name}, Size: {vfsEntry.Size} bytes");
                }
            }
            
            // 5. DefaultFortReleaseVersion.iniを取得（Fortniteバージョン情報）
            if (provider.TryGetGameFile("FortniteGame/Config/DefaultFortReleaseVersion.ini", out var versionFile))
            {
                Console.WriteLine($"\n=== Found DefaultFortReleaseVersion.ini ===");
                Console.WriteLine($"Path: {versionFile.Path}");
                
                if (versionFile.TryCreateReader(out var archive))
                {
                    using (archive)
                    using (var reader = new StreamReader(archive))
                    {
                        var content = reader.ReadToEnd();
                        Console.WriteLine("Content:");
                        Console.WriteLine(content);
                    }
                }
            }
            else
            {
                Console.WriteLine("\nDefaultFortReleaseVersion.ini not found in VFS");
            }
            
            // 6. DefaultGame.iniを取得（VFS内のパスで検索）
            var possiblePaths = new[]
            {
                "FortniteGame/Config/DefaultGame.ini",
                "/Game/Config/DefaultGame.ini",
                "Engine/Config/DefaultGame.ini",
                "/FortniteGame/Config/DefaultGame.ini"
            };
            
            foreach (var path in possiblePaths)
            {
                if (provider.TryGetGameFile(path, out var defaultGameFile))
                {
                    Console.WriteLine($"\nFound DefaultGame.ini at: {defaultGameFile.Path}");
                    
                    if (defaultGameFile.TryCreateReader(out var archive))
                    {
                        using (archive)
                        using (var reader = new StreamReader(archive))
                        {
                            var content = reader.ReadToEnd();
                            Console.WriteLine("=== DefaultGame.ini Content (first 500 chars) ===");
                            Console.WriteLine(content.Substring(0, Math.Min(500, content.Length)));
                        }
                    }
                    break;
                }
            }
            
            // 7. DefaultEngine.iniを取得
            var enginePaths = new[]
            {
                "FortniteGame/Config/DefaultEngine.ini",
                "/Game/Config/DefaultEngine.ini",
                "Engine/Config/DefaultEngine.ini",
                "/FortniteGame/Config/DefaultEngine.ini"
            };
            
            foreach (var path in enginePaths)
            {
                if (provider.TryGetGameFile(path, out var defaultEngineFile))
                {
                    Console.WriteLine($"\nFound DefaultEngine.ini at: {defaultEngineFile.Path}");
                    
                    if (defaultEngineFile.TryCreateReader(out var archive))
                    {
                        using (archive)
                        using (var reader = new StreamReader(archive))
                        {
                            var content = reader.ReadToEnd();
                            Console.WriteLine("=== DefaultEngine.ini Content (first 500 chars) ===");
                            Console.WriteLine(content.Substring(0, Math.Min(500, content.Length)));
                        }
                    }
                    break;
                }
            }
            // 7. LoadIniConfigs を使用する方法（推奨）
            // これは内部で DefaultGame と DefaultEngine を自動的に読み込みます
            var workingAes = ((CUE4Parse.FileProvider.AbstractFileProvider)provider).LoadIniConfigs();
            // 読み込まれた設定にアクセス
            var defaultGame = provider.DefaultGame;
            var defaultEngine = provider.DefaultEngine;
            Console.WriteLine("\n=== Using LoadIniConfigs ===");
            foreach (var section in defaultGame.Sections)
            {
                Console.WriteLine($"[{section.Name}]");
                foreach (var token in section.Tokens)
                {
                    Console.WriteLine($"  {token}");
                }
            }
            // 8. クリーンアップ
            provider.Dispose();
        }
        /// <summary>
        /// 直接バイト配列から復号化する低レベルAPI例
        /// </summary>
        public static byte[] DecryptIniBytes(byte[] encryptedData, FAesKey aesKey)
        {
            // AES復号化
            return encryptedData.Decrypt(aesKey);
        }
        /// <summary>
        /// カスタム暗号化処理を使用する例
        /// </summary>
        public static void ReadIniWithCustomEncryption()
        {
            var provider = new DefaultFileProvider(
                gamePath: @"C:\Path\To\Game",
                searchPattern: "*.pak"
            );
            // カスタム復号化デリゲートを設定
            provider.CustomEncryption = (bytes, offset, count, isIndex, reader) =>
            {
                // カスタム復号化ロジック
                Console.WriteLine($"Custom decryption called for {reader.Name}");
                // 例: XOR復号化
                var decrypted = new byte[count];
                Array.Copy(bytes, offset, decrypted, 0, count);
                for (int i = 0; i < count; i++)
                {
                    decrypted[i] ^= 0xAB; // カスタムキー
                }
                return decrypted;
            };
            provider.Initialize();
            // 以降は通常通りファイルを取得
            if (provider.TryGetGameFile("/Game/Config/DefaultGame.ini", out var file))
            {
                if (file.TryCreateReader(out var archive))
                {
                    using (archive)
                    using (var reader = new StreamReader(archive))
                    {
                        Console.WriteLine(reader.ReadToEnd());
                    }
                }
            }
            provider.Dispose();
        }
        /// <summary>
        /// 特定の暗号化されたファイルのみを読み取る例
        /// </summary>
        public static void ReadSpecificEncryptedIni(string pakPath, FAesKey aesKey)
        {
            var provider = new DefaultFileProvider(pakPath, "*.pak");
            provider.Initialize();
            // 特定のGUIDに対してキーを登録
            var guid = provider.RequiredKeys.Keys.FirstOrDefault();
            if (guid != default)
            {
                provider.SubmitKey(guid, aesKey);
            }
            // マウント（復号化が必要なファイルが読み込まれる）
            var mountCount = provider.Mount();
            Console.WriteLine($"Mounted {mountCount} archives");
            // .iniファイルを取得
            if (provider.TryGetGameFile("/Game/Config/DefaultGame.ini", out var file))
            {
                var data = file.Read(); // バイト配列として取得（復号化済み）
                var text = System.Text.Encoding.UTF8.GetString(data);
                Console.WriteLine(text);
            }
            provider.Dispose();
        }

        /// <summary>
        /// ===【重要】FortnitePortingのような Web API で .ini ファイルが見つからない問題の解決方法 ===
        /// 
        /// 問題: provider.Initialize() だけでは .ini ファイルが Files にマウントされない
        /// 解決: Initialize() の後に Mount() または LoadIniConfigs() を呼ぶ必要がある
        /// </summary>
        public static void WebApiIniFileFix()
        {
            Console.WriteLine("=== Web API での .ini ファイルアクセス修正方法 ===\n");

            var provider = new DefaultFileProvider(
                @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks",
                "*.pak"
            );

            Console.WriteLine("1. Initialize() を実行");
            provider.Initialize();
            Console.WriteLine($"   Files.Count = {provider.Files.Count}"); // この時点では少ない可能性

            Console.WriteLine("\n2. Mount() を実行（重要！）");
            var mountedCount = provider.Mount();
            Console.WriteLine($"   マウントされたアーカイブ数: {mountedCount}");
            Console.WriteLine($"   Files.Count = {provider.Files.Count}"); // これで増える

            // または LoadIniConfigs() を使用（推奨）
            Console.WriteLine("\n3. LoadIniConfigs() を実行（推奨）");
            var hasConfig = ((CUE4Parse.FileProvider.AbstractFileProvider)provider).LoadIniConfigs();
            Console.WriteLine($"   設定ファイルの読み込み成功: {hasConfig}");

            // これで .ini ファイルが provider.Files に含まれる
            var iniFiles = provider.Files.Keys
                .Where(k => k.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            Console.WriteLine($"\n4. VFS内の .ini ファイル ({iniFiles.Count}件):");
            foreach (var ini in iniFiles.Take(20))
            {
                Console.WriteLine($"   - {ini}");
            }

            // DefaultFortReleaseVersion.ini を取得
            var targetPath = "FortniteGame/Config/DefaultFortReleaseVersion.ini";
            Console.WriteLine($"\n5. ターゲットファイル取得: {targetPath}");
            if (provider.TryGetGameFile(targetPath, out var versionFile))
            {
                Console.WriteLine($"   ✓ 見つかりました: {versionFile.Path}");
                
                if (versionFile.TryCreateReader(out var archive))
                {
                    using (archive)
                    using (var reader = new StreamReader(archive))
                    {
                        var content = reader.ReadToEnd();
                        Console.WriteLine($"   内容 ({content.Length} 文字):");
                        Console.WriteLine("   " + string.Join("\n   ", content.Split('\n').Take(5)));
                    }
                }
            }
            else
            {
                Console.WriteLine($"   ✗ 見つかりませんでした");
            }

            provider.Dispose();

            Console.WriteLine("\n=== 修正方法まとめ ===");
            Console.WriteLine("FortnitePorting の Startup.cs または Program.cs で:");
            Console.WriteLine("1. provider.Initialize() の後に");
            Console.WriteLine("2. provider.Mount() を呼ぶ");
            Console.WriteLine("3. または ((AbstractFileProvider)provider).LoadIniConfigs() を呼ぶ");
            Console.WriteLine("\nこれにより .ini ファイルが provider.Files にマウントされます。");
        }

        /// <summary>
        /// DIコンテナでIFileProviderを登録する際の正しいセットアップ例
        /// </summary>
        public static void CorrectDISetup()
        {
            Console.WriteLine("=== DIコンテナでの正しいセットアップ例 ===\n");
            Console.WriteLine("// Startup.cs または Program.cs で:");
            Console.WriteLine(@"
services.AddSingleton<IFileProvider>(provider =>
{
    var fileProvider = new DefaultFileProvider(
        gamePath: @""C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks"",
        searchPattern: ""*.pak"",
        isCaseSensitive: false
    );
    
    // ゲームバージョン設定
    fileProvider.Versions = new VersionContainer(EGame.GAME_UE5_3);
    
    // 重要: Initialize() を呼ぶ
    fileProvider.Initialize();
    
    // 重要: Mount() を呼んで VFS にファイルをマウント
    var mountedCount = fileProvider.Mount();
    Console.WriteLine($""Mounted {mountedCount} archives"");
    
    // または LoadIniConfigs() で .ini ファイルを自動ロード
    if (fileProvider is AbstractFileProvider abstractProvider)
    {
        abstractProvider.LoadIniConfigs();
        Console.WriteLine(""Loaded ini configs"");
    }
    
    // AES キーが必要な場合
    // fileProvider.SubmitKey(guid, new FAesKey(""0x...""));
    
    return fileProvider;
});
");

            Console.WriteLine("\nこれにより ExportController で .ini ファイルにアクセスできるようになります。");
        }

        /// <summary>
        /// FortnitePorting の ExportController で使用する修正版コード
        /// </summary>
        public static void ExportControllerFix()
        {
            Console.WriteLine("=== ExportController 修正版 ===\n");
            Console.WriteLine(@"
[HttpGet(""ini"")]
public IActionResult GetIni([FromQuery] string path)
{
    Console.WriteLine($""\nReceived request for config file: \""{path}\"""");
    
    if (string.IsNullOrEmpty(path))
    {
        return BadRequest(""ファイルパスは空にできません。"");
    }

    try
    {
        // パスの正規化
        path = Uri.UnescapeDataString(path).Trim().Replace(""\\"", ""/"");
        var fileName = Path.GetFileNameWithoutExtension(path);
        
        // 1. DefaultGame/DefaultEngine から取得を試みる（高速）
        if (fileName.Equals(""DefaultGame"", StringComparison.OrdinalIgnoreCase) && _provider.DefaultGame != null)
        {
            var iniContent = ConvertCustomConfigIniToIni(_provider.DefaultGame);
            return Content(iniContent, ""text/plain; charset=utf-8"");
        }
        
        if (fileName.Equals(""DefaultEngine"", StringComparison.OrdinalIgnoreCase) && _provider.DefaultEngine != null)
        {
            var iniContent = ConvertCustomConfigIniToIni(_provider.DefaultEngine);
            return Content(iniContent, ""text/plain; charset=utf-8"");
        }

        // 2. VFS から直接取得
        var possiblePaths = new[]
        {
            path,
            $""FortniteGame/Config/{fileName}.ini"",
            $""/Game/Config/{fileName}.ini"",
            $""Engine/Config/{fileName}.ini"",
        };

        foreach (var searchPath in possiblePaths)
        {
            if (_provider.TryGetGameFile(searchPath, out var gameFile))
            {
                if (gameFile.TryCreateReader(out var archive))
                {
                    using (archive)
                    using (var reader = new StreamReader(archive, Encoding.UTF8))
                    {
                        var content = reader.ReadToEnd();
                        return Content(content, ""text/plain; charset=utf-8"");
                    }
                }
            }
        }

        // 3. デバッグ情報を返す
        var allIniFiles = _provider.Files.Keys
            .Where(k => k.EndsWith("".ini"", StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();

        return NotFound(new
        {
            error = ""設定ファイルが見つかりませんでした"",
            requestedPath = path,
            mountedFiles = _provider.Files.Count,
            availableIniFiles = allIniFiles,
            hint = ""Startup.cs で provider.Mount() または LoadIniConfigs() を呼んでいますか?""
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { error = ex.Message });
    }
}
");
        }

        /// <summary>
        /// iniファイルの取得と読み込み方法の完全ガイド
        /// </summary>
        public static void CompleteIniReadingGuide()
        {
            var provider = new DefaultFileProvider(
                @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks",
                "*.pak"
            );
            provider.Initialize();

            Console.WriteLine("=== INIファイル取得・読み込みガイド ===\n");

            // ========================================
            // 方法1: TryGetGameFile + TryCreateReader
            // ========================================
            Console.WriteLine("【方法1】TryGetGameFile + TryCreateReader");
            if (provider.TryGetGameFile("FortniteGame/Config/DefaultFortReleaseVersion.ini", out var versionFile))
            {
                // ファイルリーダーを作成（自動的に復号化される）
                if (versionFile.TryCreateReader(out var archive))
                {
                    using (archive)
                    using (var reader = new StreamReader(archive))
                    {
                        string content = reader.ReadToEnd();
                        Console.WriteLine($"Success! Content length: {content.Length} chars\n");
                    }
                }
            }

            // ========================================
            // 方法2: Read() でバイト配列として取得
            // ========================================
            Console.WriteLine("【方法2】Read() でバイト配列取得");
            if (provider.TryGetGameFile("FortniteGame/Config/DefaultGame.ini", out var gameFile))
            {
                try
                {
                    byte[] data = gameFile.Read(); // 復号化済みバイト配列
                    string text = System.Text.Encoding.UTF8.GetString(data);
                    Console.WriteLine($"Success! Size: {data.Length} bytes\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message} (AESキーが必要な可能性)\n");
                }
            }

            // ========================================
            // 方法3: LoadIniConfigs() で自動パース
            // ========================================
            Console.WriteLine("【方法3】LoadIniConfigs() で自動読み込み");
            var hasValidConfig = ((CUE4Parse.FileProvider.AbstractFileProvider)provider).LoadIniConfigs();
            
            if (hasValidConfig)
            {
                Console.WriteLine("DefaultGame.ini が正常にロードされました");
                
                // パース済みの設定にアクセス
                var defaultGame = provider.DefaultGame;
                foreach (var section in defaultGame.Sections.Take(3))
                {
                    Console.WriteLine($"  セクション: [{section.Name}]");
                    foreach (var token in section.Tokens.Take(3))
                    {
                        if (token is UE4Config.Parsing.InstructionToken it)
                        {
                            Console.WriteLine($"    {it.Key} = {it.Value}");
                        }
                    }
                }
                Console.WriteLine();
            }

            // ========================================
            // 方法4: SafeCreateReader() で安全に読み取り
            // ========================================
            Console.WriteLine("【方法4】SafeCreateReader() で安全に取得");
            if (provider.TryGetGameFile("FortniteGame/Config/DefaultEngine.ini", out var engineFile))
            {
                var archive = engineFile.SafeCreateReader(); // 失敗時はnullを返す
                if (archive != null)
                {
                    using (archive)
                    using (var reader = new StreamReader(archive))
                    {
                        string content = reader.ReadToEnd();
                        Console.WriteLine($"Success! Content length: {content.Length} chars\n");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to create reader\n");
                }
            }

            // ========================================
            // 実践例: カスタムiniファイルを読む
            // ========================================
            Console.WriteLine("【実践例】任意のiniファイルを検索して読む");
            var customIniPath = "FortniteGame/Config/DefaultInput.ini";
            if (provider.TryGetGameFile(customIniPath, out var customIni))
            {
                Console.WriteLine($"Found: {customIni.Path}");
                Console.WriteLine($"Size: {customIni.Size} bytes");
                
                if (customIni is CUE4Parse.UE4.VirtualFileSystem.VfsEntry vfsEntry)
                {
                    Console.WriteLine($"VFS: {vfsEntry.Vfs.Name}");
                    Console.WriteLine($"Encrypted: {vfsEntry.Vfs is CUE4Parse.UE4.VirtualFileSystem.IAesVfsReader aes && aes.IsEncrypted}");
                }

                if (customIni.TryCreateReader(out var ar))
                {
                    using (ar)
                    using (var sr = new StreamReader(ar))
                    {
                        var lines = sr.ReadToEnd().Split('\n').Take(10);
                        Console.WriteLine("最初の10行:");
                        foreach (var line in lines)
                        {
                            Console.WriteLine($"  {line.TrimEnd()}");
                        }
                    }
                }
            }

            provider.Dispose();
            Console.WriteLine("\n=== 完了 ===");
        }

        /// <summary>
        /// ConfigIniを使った高度なiniパース例
        /// </summary>
        public static void AdvancedIniParsing()
        {
            var provider = new DefaultFileProvider(
                @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks",
                "*.pak"
            );
            provider.Initialize();
            
            // LoadIniConfigs を実行
            ((CUE4Parse.FileProvider.AbstractFileProvider)provider).LoadIniConfigs();

            Console.WriteLine("=== 高度なiniパース ===\n");

            // DefaultGame.ini から特定のセクションを取得
            var defaultGame = provider.DefaultGame;
            var projectSettings = defaultGame.Sections
                .FirstOrDefault(s => s.Name == "/Script/EngineSettings.GeneralProjectSettings");

            if (projectSettings != null)
            {
                Console.WriteLine("【プロジェクト設定】");
                foreach (var token in projectSettings.Tokens)
                {
                    if (token is UE4Config.Parsing.InstructionToken it)
                    {
                        Console.WriteLine($"  {it.Key} = {it.Value}");
                    }
                }
            }

            // DefaultEngine.ini から ConsoleVariables を取得
            var defaultEngine = provider.DefaultEngine;
            var consoleVars = defaultEngine.Sections
                .FirstOrDefault(s => s.Name == "ConsoleVariables");

            if (consoleVars != null)
            {
                Console.WriteLine("\n【コンソール変数】");
                foreach (var token in consoleVars.Tokens.Take(10))
                {
                    if (token is UE4Config.Parsing.InstructionToken it)
                    {
                        Console.WriteLine($"  {it.Key} = {it.Value}");
                    }
                }
            }

            provider.Dispose();
        }
    }
}
