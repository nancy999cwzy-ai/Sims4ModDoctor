using System;

namespace Sims4ModDoctor.Core
{
    public enum ModFileType
    {
        Package,
        Script,
        Unknown
    }

    public class ModFileItem
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public ModFileType Type { get; set; }
        public DateTime LastModified { get; set; }

        // 格式化输出文件大小 (例如: 2.4 MB)
        public string FormattedSize
        {
            get
            {
                if (FileSizeBytes >= 1024 * 1024)
                    return $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB";
                if (FileSizeBytes >= 1024)
                    return $"{FileSizeBytes / 1024.0:F2} KB";
                return $"{FileSizeBytes} B";
            }
        }
    }
}