using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;

namespace FModel.Services.AssetEditing;

/// <summary>
/// packs a folder (the edited packages, laid out like the game's paths) into an unencrypted, uncompressed .pak
/// legacy index format (versions 3 to 7), which every UE4 engine since 4.16 and UE5 still mount through their legacy index loader
/// </summary>
public static class PakWriter
{
    private const uint Magic = 0x5A6F12E1;

    /// <summary>files that make up packages, everything else in the folder (obj, json...) is left out</summary>
    public static readonly string[] PackageExtensions = [".uasset", ".uexp", ".ubulk", ".uptnl", ".umap", ".m.ubulk", ".ushaderbytecode", ".bin", ".ini", ".locres", ".uplugin", ".upluginmanifest"];

    public sealed class Result
    {
        public string PakPath;
        public List<string> Files = [];
        public long Size;
        public List<string> VerificationErrors = [];
    }

    public static List<(string fullPath, string pakPath)> Collect(string sourceDirectory)
    {
        var files = new List<(string, string)>();
        if (!Directory.Exists(sourceDirectory)) return files;

        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            if (!PackageExtensions.Any(e => name.EndsWith(e))) continue;
            files.Add((path, Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/')));
        }
        return files.OrderBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="version">3 (UE4.13+, what u4pak writes by default) to 7 (UE4.20+)</param>
    /// <param name="mountPoint">"../../../" maps the first folder ("FortniteGame", "Engine"...) to the project/engine root</param>
    public static Result Write(IReadOnlyList<(string fullPath, string pakPath)> files, string pakPath, int version, string mountPoint)
    {
        if (version is < 3 or > 7) throw new ArgumentOutOfRangeException(nameof(version), "3 から 7 の pak バージョンに対応しています");
        if (files.Count == 0) throw new InvalidOperationException("pak に入れるファイルがありません");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pakPath))!);
        var result = new Result { PakPath = pakPath };
        var entries = new List<(string path, long offset, long size, byte[] hash)>();

        using (var stream = File.Create(pakPath))
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            foreach (var (fullPath, path) in files)
            {
                var data = File.ReadAllBytes(fullPath);
                var hash = SHA1.HashData(data);
                var offset = stream.Position;

                // every file is preceded by its entry, with offset 0 like UnrealPak writes it
                WriteEntry(w, 0, data.Length, hash);
                w.Write(data);

                entries.Add((path, offset, data.Length, hash));
                result.Files.Add(path);
            }

            byte[] index;
            using (var indexStream = new MemoryStream())
            using (var iw = new BinaryWriter(indexStream, Encoding.UTF8, true))
            {
                WriteString(iw, mountPoint);
                iw.Write(entries.Count);
                foreach (var (path, offset, size, hash) in entries)
                {
                    WriteString(iw, path);
                    WriteEntry(iw, offset, size, hash);
                }
                iw.Flush();
                index = indexStream.ToArray();
            }

            var indexOffset = stream.Position;
            w.Write(index);

            // FPakInfo, newer fields come before the magic
            if (version >= 7) w.Write(new byte[16]); // EncryptionKeyGuid, none
            w.Write((byte) 0); // bEncryptedIndex
            w.Write(Magic);
            w.Write(version);
            w.Write(indexOffset);
            w.Write((long) index.Length);
            w.Write(SHA1.HashData(index));
            result.Size = stream.Length;
        }

        return result;
    }

    // FPakEntry for versions before FNameBasedCompressionMethod, uncompressed and unencrypted
    private static void WriteEntry(BinaryWriter w, long offset, long size, byte[] hash)
    {
        w.Write(offset);
        w.Write(size); // compressed size
        w.Write(size); // uncompressed size
        w.Write(0); // COMPRESS_None
        w.Write(hash);
        w.Write((byte) 0); // flags
        w.Write(0u); // compression block size
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        var ascii = value.All(c => c < 128);
        if (ascii)
        {
            w.Write(value.Length + 1);
            w.Write(Encoding.ASCII.GetBytes(value));
            w.Write((byte) 0);
        }
        else
        {
            w.Write(-(value.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(value));
            w.Write((short) 0);
        }
    }

    private const uint SignatureMagic = 0x73832DAA;
    private const int SignatureChunkSize = 64 * 1024;

    /// <summary>
    /// writes the .sig next to the pak: FPakSignatureFile, the CRC32 of every 64 KiB of the pak and the RSA-signed hash of that list
    /// nobody but the game's developer can sign, so the signed part is copied from one of the game's own .sig, which is what the
    /// usual modding setups (a pak that is checked against its .sig only if the game is told to) expect next to every pak
    /// </summary>
    /// <param name="templateSigPath">a .sig of the game to take the signed hash from, null for an empty one</param>
    public static string WriteSignature(string pakPath, string templateSigPath)
    {
        var encryptedHash = templateSigPath != null ? ReadEncryptedHash(templateSigPath) : new byte[512];
        var hashes = ComputeChunkHashes(pakPath);

        var sigPath = Path.ChangeExtension(pakPath, ".sig");
        using var stream = File.Create(sigPath);
        using var w = new BinaryWriter(stream);
        w.Write(SignatureMagic);
        w.Write(1); // EPakSignatureFileVersion::Initial
        w.Write(encryptedHash.Length);
        w.Write(encryptedHash);
        w.Write(hashes.Length);
        foreach (var hash in hashes) w.Write(hash);
        return sigPath;
    }

    public static byte[] ReadEncryptedHash(string sigPath)
    {
        using var r = new BinaryReader(File.OpenRead(sigPath));
        if (r.BaseStream.Length < 16 || r.ReadUInt32() != SignatureMagic)
            throw new InvalidDataException($"{Path.GetFileName(sigPath)} は pak の署名ファイル（.sig）ではありません");
        r.ReadInt32(); // version
        var length = r.ReadInt32();
        if (length <= 0 || length > r.BaseStream.Length - 12) throw new InvalidDataException($"{Path.GetFileName(sigPath)} の署名部分が壊れています");
        return r.ReadBytes(length);
    }

    private static uint[] ComputeChunkHashes(string pakPath)
    {
        using var stream = File.OpenRead(pakPath);
        var hashes = new uint[(stream.Length + SignatureChunkSize - 1) / SignatureChunkSize];
        var buffer = new byte[SignatureChunkSize];
        for (var i = 0; i < hashes.Length; i++)
        {
            var read = stream.ReadAtLeast(buffer, buffer.Length, false);
            hashes[i] = System.IO.Hashing.Crc32.HashToUInt32(buffer.AsSpan(0, read));
        }
        return hashes;
    }

    /// <summary>a .sig of the game to borrow the signed hash from, the main pak's one first</summary>
    public static string FindSignatureTemplate(string gameDirectory, string excludePak = null)
    {
        try
        {
            if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory)) return null;
            var exclude = excludePak != null ? Path.GetFullPath(Path.ChangeExtension(excludePak, ".sig")) : null;
            var sigs = Directory.EnumerateFiles(gameDirectory, "*.sig", SearchOption.AllDirectories)
                .Where(p => !string.Equals(Path.GetFullPath(p), exclude, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return sigs.FirstOrDefault(p => Path.GetFileName(p).StartsWith("pakchunk0-", StringComparison.OrdinalIgnoreCase))
                   ?? sigs.OrderBy(p => p.Length).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>reads the .sig back and checks every chunk hash against the pak</summary>
    public static void VerifySignature(Result result, string sigPath)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(sigPath));
            if (r.ReadUInt32() != SignatureMagic) throw new InvalidDataException("先頭が .sig の形式ではありません");
            r.ReadInt32();
            var hashLength = r.ReadInt32();
            r.BaseStream.Position += hashLength;
            var count = r.ReadInt32();
            var expected = ComputeChunkHashes(result.PakPath);
            if (count != expected.Length)
            {
                result.VerificationErrors.Add($".sig のチャンク数（{count}）が pak（{expected.Length}）と合いません");
                return;
            }
            for (var i = 0; i < count; i++)
            {
                if (r.ReadUInt32() == expected[i]) continue;
                result.VerificationErrors.Add($".sig のチャンク {i} のハッシュが pak と合いません");
                return;
            }
            if (r.BaseStream.Position != r.BaseStream.Length) result.VerificationErrors.Add(".sig の末尾に余分なデータがあります");
        }
        catch (Exception e)
        {
            result.VerificationErrors.Add($"作成した .sig を読み込めませんでした: {e.Message}");
        }
    }

    /// <summary>
    /// opens the written pak like the game files are opened and compares every file with its source
    /// </summary>
    public static void Verify(Result result, IReadOnlyList<(string fullPath, string pakPath)> files, VersionContainer versions)
    {
        try
        {
            using var reader = new PakFileReader(result.PakPath, versions);
            reader.Mount(StringComparer.OrdinalIgnoreCase);
            var byPath = reader.Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (fullPath, pakPath) in files)
            {
                var key = byPath.Keys.FirstOrDefault(k => k.EndsWith(pakPath, StringComparison.OrdinalIgnoreCase));
                if (key == null)
                {
                    result.VerificationErrors.Add($"{pakPath}: pak の中に見つかりません");
                    continue;
                }
                if (!byPath[key].Read().AsSpan().SequenceEqual(File.ReadAllBytes(fullPath)))
                    result.VerificationErrors.Add($"{pakPath}: 中身が一致しません");
            }
        }
        catch (Exception e)
        {
            result.VerificationErrors.Add($"作成した pak を読み込めませんでした: {e.Message}");
        }
    }
}
