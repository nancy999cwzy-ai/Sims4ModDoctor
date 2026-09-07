using System;

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
        Script,      // 脚本模组 (.ts4script)
        CAS,         // 捏人/服装/发型/皮肤
        BuildBuy,    // 建筑/家具/地皮
        General,     // 普通 package
        Unknown
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

        // 脚本深度校验：.ts4script 文件若嵌套层级 > 1 则会导致游戏内失效
        public bool IsScriptDepthInvalid { get; set; } = false;

        public string SizeDisplay => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB";
        
        // 移除了带兼容性问题的 Emoji 特殊字符，采用极简干净的文本标识
        public string CategoryDisplay => Category switch
        {
            ModCategory.Script => "[脚本模组]",
            ModCategory.CAS => "[CAS 美化]",
            ModCategory.BuildBuy => "[建筑家具]",
            ModCategory.General => "[普通 Package]",
            _ => "[未知分类]"
        };

        public string TypeDisplay => Type switch
        {
            ModFileType.Script => "脚本 (.ts4script)",
            ModFileType.Package => "资源 (.package)",
            _ => "未知"
        };
    }
}