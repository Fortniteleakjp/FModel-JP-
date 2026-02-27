using System;
using System.IO;
using System.Diagnostics;
using AdonisUI.Controls;

namespace FModel.Services
{
    public static class UFontExtractor
    {
        public static string ExtractTTF(byte[] data, string outputPath)
        {
            int start = FindTTFHeader(data);
            if (start == -1)
                return "ヘッダーが見つかりませんでした。";

            int numTables = (data[start + 4] << 8) | data[start + 5];

            int maxOffset = 0;
            int tableOffset = start + 12;
            for (int i = 0; i < numTables; i++)
            {
                int entryOffset = tableOffset + i * 16;
                int offset = ReadInt32BE(data, entryOffset + 8);
                int length = ReadInt32BE(data, entryOffset + 12);
                int end = offset + length;
                if (end > maxOffset)
                    maxOffset = end;
            }

            if (maxOffset + start > data.Length)
                return "フォントが破損している可能性があります。";

            byte[] ttfData = new byte[maxOffset];
            Array.Copy(data, start, ttfData, 0, maxOffset);
            File.WriteAllBytes(outputPath, ttfData);

            OpenFolderAndSelectFile(outputPath);
            MessageBox.Show($"抽出成功: {Path.GetFileName(outputPath)}", "UFont Converter", MessageBoxButton.OK, MessageBoxImage.Information);

            return $"抽出成功: {Path.GetFileName(outputPath)}";
        }

        public static string ExtractTTF(string inputPath, string outputPath)
        {
            if (!File.Exists(inputPath))
                return $"ファイルが見つかりません: {inputPath}";

            byte[] data = File.ReadAllBytes(inputPath);
            return ExtractTTF(data, outputPath);
        }

        private static int FindTTFHeader(byte[] data)
        {
            for (int i = 0; i < data.Length - 4; i++)
            {
                // TrueType
                if (data[i] == 0x00 && data[i + 1] == 0x01 && data[i + 2] == 0x00 && data[i + 3] == 0x00)
                    return i;
                // Apple TrueType
                if (data[i] == 't' && data[i + 1] == 'r' && data[i + 2] == 'u' && data[i + 3] == 'e')
                    return i;
                // OpenType (CFF)
                if (data[i] == 'O' && data[i + 1] == 'T' && data[i + 2] == 'T' && data[i + 3] == 'O')
                    return i;
            }
            return -1;
        }

        private static int ReadInt32BE(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        public static void OpenFolderAndSelectFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
        }
    }
}