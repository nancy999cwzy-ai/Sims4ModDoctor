using System;
using System.IO;
using System.Linq;

namespace Sims4ModDoctor.Core
{
    public enum ModFileType
    {
        Package,
        Script,
        Unknown
    }

    public enum ModCategory
    {
        CAS_Hair,
        CAS_Hat,
        CAS_Clothing,
        CAS_Shoes,
        CAS_Accessories,
        BuildBuy,
        CoreMods,
        Script,
        General
    }

    public class ModFileItem
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public DateTime LastModified { get; set; }
        public ModFileType Type { get; set; }
        public ModCategory Category { get; set; } = ModCategory.General;

        /// <summary>文件夹名前缀：Mods 根下仅 1 层，符合 EA 脚本嵌套限制。</summary>
        public const string CoreModFolderPrefix = "【功能性脚本模组】";

        /// <summary>大型功能脚本模组分组名（整理到 【功能性脚本模组】{Name}/）。</summary>
        public string? CoreModGroupName { get; set; }

        public bool IsScriptDepthInvalid { get; set; }
        public byte[]? ThumbnailData { get; set; }
        public bool HasThumbnail => ThumbnailData != null && ThumbnailData.Length > 0;

        public string SizeDisplay => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB";

        public string CategoryDisplay => Category switch
        {
            ModCategory.CAS_Hair => "[CAS · 头发]",
            ModCategory.CAS_Hat => "[CAS · 帽子]",
            ModCategory.CAS_Clothing => "[CAS · 服装]",
            ModCategory.CAS_Shoes => "[CAS · 鞋子]",
            ModCategory.CAS_Accessories => "[CAS · 饰品]",
            ModCategory.BuildBuy => "[建筑家具]",
            ModCategory.CoreMods => "[核心模组]",
            ModCategory.Script => "[脚本模组]",
            ModCategory.General => "[普通 Package]",
            _ => "[未知分类]"
        };

        public string TypeDisplay => Type switch
        {
            ModFileType.Script => "脚本 (.ts4script)",
            ModFileType.Package => "资源 (.package)",
            _ => "未知"
        };

        /// <summary>
        /// 相对 Mods_Organized 的目标子目录，与 Category 严格一一对应。
        /// CoreMods 直接落在根下第一层：【功能性脚本模组】Name/（禁止 _CoreMods 中间层）。
        /// </summary>
        public string GetOrganizeRelativeFolder()
        {
            if (Category == ModCategory.CoreMods)
                return BuildCoreModOrganizeFolder(CoreModGroupName);

            return Category switch
            {
                ModCategory.CAS_Hair => "_CAS/Hair",
                ModCategory.CAS_Hat => "_CAS/Hat",
                ModCategory.CAS_Clothing => "_CAS/Clothing",
                ModCategory.CAS_Shoes => "_CAS/Shoes",
                ModCategory.CAS_Accessories => "_CAS/Accessories",
                ModCategory.BuildBuy => "_BuildBuy",
                ModCategory.Script => "_Scripts",
                ModCategory.General => "_General",
                _ => "_General"
            };
        }

        /// <summary>相对 Mods_Organized 的完整目标路径（含文件名，正斜杠）。</summary>
        public string GetOrganizeRelativePath()
        {
            string folder = GetOrganizeRelativeFolder().Replace('\\', '/').Trim('/');
            return string.IsNullOrEmpty(folder) ? Name : folder + "/" + Name;
        }

        /// <summary>构建 CoreMod 目标文件夹名（Mods 根下第 1 层）。</summary>
        public static string BuildCoreModOrganizeFolder(string? groupName)
            => CoreModFolderPrefix + SanitizeFolderName(
                string.IsNullOrWhiteSpace(groupName) ? "Ungrouped" : groupName);

        /// <summary>从文件夹名解析 CoreMod 组名（支持新前缀与旧 _CoreMods 子目录名）。</summary>
        public static bool TryParseCoreModFolderName(string folderName, out string groupName)
        {
            groupName = string.Empty;
            if (string.IsNullOrWhiteSpace(folderName)) return false;

            string name = folderName.Trim();
            if (name.StartsWith(CoreModFolderPrefix, StringComparison.Ordinal))
            {
                groupName = SanitizeFolderName(name[CoreModFolderPrefix.Length..]);
                return !string.IsNullOrWhiteSpace(groupName);
            }

            return false;
        }

        // 兼容旧绑定名
        public string OrganizeRelativeFolder => GetOrganizeRelativeFolder();

        public static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            string cleaned = new string(chars).Trim('.', ' ');
            while (cleaned.Contains("__"))
                cleaned = cleaned.Replace("__", "_");
            return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
        }
    }
}
