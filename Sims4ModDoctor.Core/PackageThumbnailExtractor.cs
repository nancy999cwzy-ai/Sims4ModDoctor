using System.Collections.Generic;
using System.IO;

namespace Sims4ModDoctor.Core
{
    /// <summary>
    /// 从 Sims 4 .package (DBPF 2.1) 中提取缩略图资源。
    /// </summary>
    public static class PackageThumbnailExtractor
    {
        private static readonly HashSet<uint> ThumbnailTypeIds = new()
        {
            0x3C1D8799,
            0x0D64A2F0,
            0x3C1AF1F2,
            0x3C2A8647,
            0x5B282D45,
            0x9C925813,
            0xCD9DE247,
            0x2F7D0004
        };

        private const int MaxThumbnailBytes = 2 * 1024 * 1024;

        public static byte[]? ExtractThumbnail(string packagePath)
        {
            try
            {
                using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var index = DbpfReader.ReadIndex(fs);
                if (index == null) return null;

                byte[]? bestThumb = null;
                int bestSize = -1;

                foreach (var entry in index)
                {
                    if (!ThumbnailTypeIds.Contains(entry.Type)) continue;
                    if (entry.UncompressedSize == 0 || entry.UncompressedSize > MaxThumbnailBytes) continue;
                    if ((int)entry.UncompressedSize <= bestSize) continue;

                    long resume = fs.Position;
                    try
                    {
                        byte[]? data = DbpfReader.ReadResource(fs, entry, MaxThumbnailBytes);
                        if (data != null && IsLikelyImage(data))
                        {
                            bestThumb = data;
                            bestSize = data.Length;
                        }
                    }
                    catch
                    {
                        // ignore single resource failure
                    }
                    finally
                    {
                        fs.Position = resume;
                    }
                }

                return bestThumb;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLikelyImage(byte[] data)
        {
            if (data == null || data.Length < 8) return false;
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
            if (data[0] == 'B' && data[1] == 'M') return true;
            if (data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ') return true;
            return false;
        }
    }
}
