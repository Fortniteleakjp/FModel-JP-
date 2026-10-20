using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using FModel.ViewModels;
using K4os.Compression.LZ4.Streams;

namespace FModel.Services;

/// <summary>.fbkp の 1 エントリ（バックアップ作成時のゲーム内パス・展開後サイズ・暗号化の有無）</summary>
public readonly record struct BackupEntry(string Path, long Size, bool IsEncrypted);

/// <summary>
/// バックアップファイル (.fbkp) の読み取り。LZ4 圧縮の有無、FBKP_MAGIC の無い旧形式、
/// PerfectPath 以前の先頭スラッシュ付きパスのどれにも対応する。
/// 読み込みモード「すべて（新規）/（変更）」と MCP の差分レポートが共用する。
/// </summary>
public static class BackupFileReader
{
    private const uint IsLz4 = 0x184D2204u;

    public static List<BackupEntry> Read(string path, CancellationToken cancellationToken = default)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var memoryStream = new MemoryStream();

        var magicBytes = new byte[4];
        fileStream.ReadExactly(magicBytes);
        fileStream.Position = 0;
        if (BitConverter.ToUInt32(magicBytes) == IsLz4)
        {
            using var compressionStream = LZ4Stream.Decode(fileStream);
            compressionStream.CopyTo(memoryStream);
        }
        else fileStream.CopyTo(memoryStream);

        // FStreamArchive で 1 件ずつ読むと Fortnite (約 1000 万件) で十数秒かかるので、展開済みのバッファを直接読む
        var data = new ReadOnlySpan<byte>(memoryStream.GetBuffer(), 0, (int) memoryStream.Length);
        var position = 0;
        var entries = new List<BackupEntry>();

        if (data.Length < sizeof(uint) || BinaryPrimitives.ReadUInt32LittleEndian(data) != BackupManagerViewModel.FBKP_MAGIC)
        {
            while (position < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                position += 16;
                var uncompressedSize = ReadInt64(data, ref position);
                var isEncrypted = data[position++] != 0;
                position += 4;
                var fullPath = ReadString(data, ref position)[1..];
                position += 4;

                entries.Add(new BackupEntry(fullPath, uncompressedSize, isEncrypted));
            }
        }
        else
        {
            position += sizeof(uint);
            var version = (EBackupVersion) data[position++];
            var count = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
            position += sizeof(int);
            entries.Capacity = count;
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();

                var uncompressedSize = ReadInt64(data, ref position);
                var isEncrypted = data[position++] != 0;
                var fullPath = ReadString(data, ref position);
                if (version < EBackupVersion.PerfectPath) fullPath = fullPath[1..];

                entries.Add(new BackupEntry(fullPath, uncompressedSize, isEncrypted));
            }
        }

        return entries;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
        position += sizeof(long);
        return value;
    }

    /// <summary>FArchive.ReadString と同じ形式 (7 ビット可変長の長さ + 1 バイト文字)</summary>
    private static string ReadString(ReadOnlySpan<byte> data, ref int position)
    {
        int length = 0, shift = 0;
        byte b;
        do
        {
            if (shift >= 35) throw new InvalidDataException("Corrupted backup: bad string length.");
            b = data[position++];
            length |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (length <= 0) return string.Empty;

        var value = Encoding.UTF8.GetString(data.Slice(position, length));
        position += length;
        return value;
    }
}
