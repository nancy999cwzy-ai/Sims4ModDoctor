using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Sims4ModDoctor.Core
{
    public class OrganizeResult
    {
        public int CopiedCount { get; set; }
        public int SkippedAlreadyOrganized { get; set; }
        public int FailedCount { get; set; }
        public int OrphanDeletedCount { get; set; }
        public int EmptyFoldersRemoved { get; set; }
        public int PreviewImagesDeleted { get; set; }
        public string OutputFolder { get; set; } = string.Empty;
        public Dictionary<string, int> CategoryCopiedCounts { get; } = new();
        public List<string> Failures { get; } = new();
    }

    public class ModScannerService
    {
        private const string QuarantineFolderName = ".Quarantine";
        private const string ManifestFileName = "quarantine-manifest.json";
        public const string OrganizedSiblingFolderName = "Mods_Organized";
        private const string OrganizeManifestFileName = "organize-manifest.json";

        // 二级降级词库：仅在 DBPF 未识别且非 CoreMod 同组锁定时使用（顺序即优先级）
        private static readonly (ModCategory Category, string[] Keywords)[] NamePathRules =
        {
            // Poses / 姿势动画 → 归入饰品大类（便于与 CAS 资源同区管理）
            (ModCategory.CAS_Accessories, new[]
            {
                "pose", "poses", "poser", "apose", "anim", "animation", "blend", "teleport",
                "姿势", "动作", "动画"
            }),
            // Skin / Makeup
            (ModCategory.CAS_Accessories, new[]
            {
                "skin", "skintone", "overlay", "tattoo", "mole", "blush", "lip", "lipstick",
                "liner", "lashes", "eyes", "eyeshadow", "makeup", "preset", "freckle", "scar",
                "nail", "nails", "皮肤", "妆", "纹身", "眼影", "睫毛"
            }),
            (ModCategory.CAS_Hair, new[]
            {
                "hair", "hairstyle", "bang", "bangs", "fringe", "ponytail", "braid", "wig", "发型", "头发"
            }),
            (ModCategory.CAS_Hat, new[]
            {
                "hat", "hats", "cap", "beanie", "helmet", "帽子", "头饰"
            }),
            (ModCategory.CAS_Shoes, new[]
            {
                "shoe", "shoes", "boot", "boots", "sandal", "heel", "heels", "sneaker", "sneakers", "鞋子", "靴"
            }),
            (ModCategory.CAS_Clothing, new[]
            {
                "dress", "outfit", "clothes", "clothing", "shirt", "pants", "skirt",
                "jacket", "coat", "sweater", "hoodie", "bodysuit", "swimsuit",
                "top", "bottom", "fullbody", "服装", "衣服", "裙", "裤"
            }),
            (ModCategory.CAS_Accessories, new[]
            {
                "accessory", "accessories", "earring", "earrings", "necklace", "bracelet",
                "glasses", "ring", "acc", "配饰", "耳环", "项链", "眼镜", "戒指"
            }),
            (ModCategory.BuildBuy, new[]
            {
                "decor", "decoration", "plant", "bed", "chair", "sofa", "table", "desk", "furniture",
                "cabinet", "lamp", "rug", "curtain", "clutter", "build", "buy", "wall", "floor",
                "地板", "墙", "家具", "装饰", "床", "椅", "桌"
            }),
            (ModCategory.CoreMods, new[]
            {
                "mccc", "mccommand", "mc_cmd", "wickedwhims", "wicked", "basemental",
                "uicheats", "ui_cheats", "xmlinjector", "sliceoflife", "tweaks"
            }),
        };

        private static readonly (string GroupName, string[] Markers)[] KnownCoreModProfiles =
        {
            ("MC_Command_Center", new[] { "mccc", "mc_cmd", "mccommand", "mc command center", "mc_command" }),
            ("WickedWhims", new[] { "wickedwhims", "wicked_whims", "ww_", "_ww" }),
            ("UI_Cheats_Extension", new[] { "uicheats", "ui_cheats", "ui cheats", "ui_cheat" }),
            ("Basemental", new[] { "basemental" }),
            ("Slice_of_Life", new[] { "sliceoflife", "slice_of_life", "slice of life" }),
            ("XML_Injector", new[] { "xmlinjector", "xml_injector", "xml injector" }),
        };

        /// <summary>文件名噪声后缀（提取核心标识时剥离）。</summary>
        private static readonly string[] CoreIdentityNoiseTokens =
        {
            "script", "scripts", "ts4script", "tuning", "tunings", "settings", "setting",
            "module", "modules", "main", "core", "lib", "library", "addon", "add_on",
            "resource", "resources", "data", "config", "cfg", "package", "pkg"
        };

        private const int MinNameAffinityScore = 50;

        public async Task<List<ModFileItem>> ScanDirectoryAsync(string rootPath, bool extractThumbnails = true)
        {
            return await Task.Run(() =>
            {
                var modItems = new List<ModFileItem>();
                if (!Directory.Exists(rootPath)) return modItems;

                CleanupStrayPreviewImages(rootPath);

                foreach (var file in new DirectoryInfo(rootPath).GetFiles("*.*", SearchOption.AllDirectories))
                {
                    if (file.DirectoryName != null && file.DirectoryName.Contains(".Quarantine"))
                        continue;

                    string ext = file.Extension.ToLowerInvariant();
                    if (ext != ".package" && ext != ".ts4script")
                        continue;

                    string relativePath = Path.GetRelativePath(rootPath, file.FullName);

                    var item = new ModFileItem
                    {
                        Name = file.Name,
                        FilePath = file.FullName,
                        RelativePath = relativePath,
                        FileSizeBytes = file.Length,
                        LastModified = file.LastWriteTime,
                        Type = ext == ".ts4script" ? ModFileType.Script : ModFileType.Package
                    };

                    ClassifyMod(item);
                    CheckScriptDepth(item, relativePath);

                    if (extractThumbnails && item.Type == ModFileType.Package)
                        item.ThumbnailData = PackageThumbnailExtractor.ExtractThumbnail(file.FullName);

                    modItems.Add(item);
                }

                // 将大型功能脚本模组及其配套 package 收拢为 CoreMod 分组
                AssignCoreModGroups(modItems);

                return modItems;
            });
        }

        public List<ModFileItem> GetQuarantineFiles(string rootPath)
        {
            var quarantineItems = new List<ModFileItem>();
            if (string.IsNullOrWhiteSpace(rootPath)) return quarantineItems;

            string quarantinePath = Path.Combine(rootPath, QuarantineFolderName);
            if (!Directory.Exists(quarantinePath)) return quarantineItems;

            foreach (var file in new DirectoryInfo(quarantinePath).GetFiles("*.*", SearchOption.AllDirectories))
            {
                if (string.Equals(file.Name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string ext = file.Extension.ToLowerInvariant();
                string relativePath = Path.GetRelativePath(quarantinePath, file.FullName);

                var item = new ModFileItem
                {
                    Name = file.Name,
                    FilePath = file.FullName,
                    RelativePath = relativePath,
                    FileSizeBytes = file.Length,
                    LastModified = file.LastWriteTime,
                    Type = ext switch
                    {
                        ".ts4script" => ModFileType.Script,
                        ".package" => ModFileType.Package,
                        _ => ModFileType.Unknown
                    }
                };

                ClassifyMod(item);
                CheckScriptDepth(item, relativePath);

                if (item.Type == ModFileType.Package)
                    item.ThumbnailData = PackageThumbnailExtractor.ExtractThumbnail(file.FullName);

                quarantineItems.Add(item);
            }

            AssignCoreModGroups(quarantineItems);
            return quarantineItems;
        }

        public void ClearQuarantine(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return;
            string quarantinePath = Path.Combine(rootPath, QuarantineFolderName);
            if (Directory.Exists(quarantinePath))
                Directory.Delete(quarantinePath, true);
        }

        public OrganizeResult AutoOrganizeByCategory(string rootPath, List<ModFileItem> mods)
        {
            var result = new OrganizeResult();
            if (string.IsNullOrWhiteSpace(rootPath) || mods == null || mods.Count == 0)
                return result;

            // 整理前再跑一遍分组，确保 Category / CoreModGroupName 最新
            AssignCoreModGroups(mods);

            string outputRoot = ResolveOrganizedSiblingPath(rootPath);
            result.OutputFolder = outputRoot;
            Directory.CreateDirectory(outputRoot);
            EnsureOrganizeFolders(outputRoot);

            // 预期文件映射表：Mods_Organized 内相对路径（正斜杠）→ 源文件
            var expectedFilesMap = new Dictionary<string, ModFileItem>(StringComparer.OrdinalIgnoreCase);
            var usedTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var copyLog = new List<OrganizeManifestEntry>();

            foreach (var item in mods)
            {
                if (!File.Exists(item.FilePath)) continue;
                if (item.FilePath.Contains(QuarantineFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string relativeFolder = item.GetOrganizeRelativeFolder().Replace('/', Path.DirectorySeparatorChar);
                string relativeKey = NormalizePathKey(item.GetOrganizeRelativePath());

                // 同名冲突：为第二个及以后的源文件生成唯一相对路径
                if (!usedTargetKeys.Add(relativeKey))
                {
                    relativeKey = AllocateUniqueRelativeKey(relativeFolder, item.Name, usedTargetKeys);
                }

                expectedFilesMap[relativeKey] = item;

                string targetPath = Path.Combine(outputRoot,
                    relativeKey.Replace('/', Path.DirectorySeparatorChar));
                string targetFolder = Path.GetDirectoryName(targetPath) ?? outputRoot;

                string categoryKey = item.Category == ModCategory.CoreMods && !string.IsNullOrEmpty(item.CoreModGroupName)
                    ? $"CoreMods/{item.CoreModGroupName}"
                    : item.Category.ToString();

                try
                {
                    if (File.Exists(targetPath))
                    {
                        var srcInfo = new FileInfo(item.FilePath);
                        var dstInfo = new FileInfo(targetPath);
                        if (srcInfo.Length == dstInfo.Length)
                        {
                            // Smart Copy：大小一致则跳过 IO
                            result.SkippedAlreadyOrganized++;
                            result.CategoryCopiedCounts[categoryKey] =
                                result.CategoryCopiedCounts.GetValueOrDefault(categoryKey) + 1;
                            copyLog.Add(new OrganizeManifestEntry
                            {
                                OriginalPath = item.FilePath,
                                NewPath = targetPath,
                                Category = categoryKey,
                                MovedAt = DateTime.Now
                            });
                            continue;
                        }

                        // 大小变更：覆盖写入（保持预期路径稳定，便于孤儿清理）
                        File.Copy(item.FilePath, targetPath, overwrite: true);
                    }
                    else
                    {
                        Directory.CreateDirectory(targetFolder);
                        File.Copy(item.FilePath, targetPath, overwrite: false);
                    }

                    result.CopiedCount++;
                    result.CategoryCopiedCounts[categoryKey] =
                        result.CategoryCopiedCounts.GetValueOrDefault(categoryKey) + 1;
                    copyLog.Add(new OrganizeManifestEntry
                    {
                        OriginalPath = item.FilePath,
                        NewPath = targetPath,
                        Category = categoryKey,
                        MovedAt = DateTime.Now
                    });
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    result.Failures.Add($"{item.RelativePath}（{ex.Message}）");
                    expectedFilesMap.Remove(relativeKey);
                }
            }

            // 孤儿清理：不在 ExpectedFilesMap 中的旧文件（含分错遗留）一律删除
            result.OrphanDeletedCount = CleanupOrphanFiles(outputRoot, expectedFilesMap.Keys);
            result.PreviewImagesDeleted = CleanupStrayPreviewImages(outputRoot);
            result.EmptyFoldersRemoved = CleanupEmptyDirectories(outputRoot);

            // 确保分类骨架目录仍在（空目录清理后重建）
            EnsureOrganizeFolders(outputRoot);

            SaveOrganizeManifest(outputRoot, copyLog);
            return result;
        }

        private static string AllocateUniqueRelativeKey(
            string relativeFolder, string fileName, HashSet<string> usedKeys)
        {
            string dir = NormalizePathKey(relativeFolder);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);

            for (int i = 1; i <= 999; i++)
            {
                string key = string.IsNullOrEmpty(dir)
                    ? $"{stem}_{i}{ext}"
                    : $"{dir}/{stem}_{i}{ext}";
                if (usedKeys.Add(key)) return key;
            }

            string fallback = string.IsNullOrEmpty(dir)
                ? $"{stem}_{Guid.NewGuid():N}{ext}"
                : $"{dir}/{stem}_{Guid.NewGuid():N}{ext}";
            usedKeys.Add(fallback);
            return fallback;
        }

        /// <summary>删除 Mods_Organized 中不在预期映射表内的文件（保留整理清单）。</summary>
        private static int CleanupOrphanFiles(string outputRoot, IEnumerable<string> expectedRelativePaths)
        {
            if (!Directory.Exists(outputRoot)) return 0;

            var expected = new HashSet<string>(
                expectedRelativePaths.Select(NormalizePathKey),
                StringComparer.OrdinalIgnoreCase);

            int deleted = 0;
            foreach (var file in Directory.EnumerateFiles(outputRoot, "*.*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, OrganizeManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = NormalizePathKey(Path.GetRelativePath(outputRoot, file));
                if (expected.Contains(rel)) continue;

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch { }
            }

            return deleted;
        }

        /// <summary>自底向上删除空子目录（保留 outputRoot 本身）。</summary>
        private static int CleanupEmptyDirectories(string outputRoot)
        {
            if (!Directory.Exists(outputRoot)) return 0;

            int removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(outputRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, false);
                        removed++;
                    }
                }
                catch { }
            }

            return removed;
        }

        private static string NormalizePathKey(string path)
            => path.Replace('\\', '/').Trim('/');

        public static string ResolveOrganizedSiblingPath(string modsRootPath)
        {
            var parent = Directory.GetParent(modsRootPath)
                         ?? throw new InvalidOperationException("无法定位 Mods 的父目录。");
            return Path.Combine(parent.FullName, OrganizedSiblingFolderName);
        }

        /// <summary>
        /// 将含脚本的模组目录（及名字强相关配套 package）收拢为 CoreMods 分组。
        /// 策略：① 同源目录锁定 ② 跨目录文件名相似度绑定。
        /// </summary>
        public static void AssignCoreModGroups(List<ModFileItem> mods)
        {
            if (mods == null || mods.Count == 0) return;

            // 已在 【功能性脚本模组】Name/ 或旧版 _CoreMods/Name/ 下的文件：维持分组
            foreach (var item in mods)
            {
                if (TryExtractCoreModGroupFromRelativePath(item.RelativePath, out var existingGroup))
                {
                    item.Category = ModCategory.CoreMods;
                    item.CoreModGroupName = existingGroup;
                }
            }

            var byDir = mods
                .GroupBy(m => GetRelativeDirectory(m.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dirGroup in byDir)
            {
                var files = dirGroup.ToList();
                bool hasScript = files.Any(f => f.Type == ModFileType.Script);
                bool hasPackage = files.Any(f => f.Type == ModFileType.Package);
                if (!hasScript) continue;

                string dirKey = dirGroup.Key ?? string.Empty;
                string leaf = dirKey.Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() ?? string.Empty;

                // 规范分类目录内：绝不吞掉 CAS/家具，只提升散落脚本（跨目录相似度稍后再拉配套）
                if (TryGetCategoryConstraintFromFolderPath(dirKey, out var folderCat) &&
                    folderCat != ModCategory.CoreMods &&
                    folderCat != ModCategory.Script)
                {
                    foreach (var script in files.Where(f => f.Type == ModFileType.Script))
                    {
                        script.Category = ModCategory.CoreMods;
                        script.CoreModGroupName = ResolveCoreModGroupNameFromFileName(script.Name);
                    }
                    continue;
                }

                // 通用脚本容器：按脚本前缀分别建组（同源 package 优先绑入）
                if (IsGenericScriptContainer(leaf))
                {
                    GroupScriptsInGenericContainer(files);
                    continue;
                }

                // ★ 第一优先级：同源文件夹内 .ts4script + .package → 整夹绑定，继承文件夹名
                if (hasScript && hasPackage)
                {
                    string groupName = ResolveInheritedFolderGroupName(dirKey, files);
                    foreach (var f in files)
                    {
                        f.Category = ModCategory.CoreMods;
                        f.CoreModGroupName = groupName;
                    }
                    continue;
                }

                // 仅有脚本的作者目录：仍按目录名建 CoreMod 组
                string folderGroupName = ResolveInheritedFolderGroupName(dirKey, files);
                foreach (var f in files)
                {
                    f.Category = ModCategory.CoreMods;
                    f.CoreModGroupName = folderGroupName;
                }
            }

            // 确保所有脚本都有 CoreMod 组名（根目录散落脚本等）
            foreach (var script in mods.Where(m => m.Type == ModFileType.Script))
            {
                if (string.IsNullOrEmpty(script.CoreModGroupName))
                    script.CoreModGroupName = ResolveCoreModGroupNameFromFileName(script.Name);
                script.Category = ModCategory.CoreMods;
            }

            // ★ 第二优先级：跨目录文件名强相关 → 把散落/异地 package 拉进对应脚本组
            BindPackagesByNameSimilarity(mods);

            // 已知大型模组：把散落文件按名称拉入对应 CoreMod 组
            foreach (var profile in KnownCoreModProfiles)
            {
                bool groupExists = mods.Any(m =>
                    string.Equals(m.CoreModGroupName, profile.GroupName, StringComparison.OrdinalIgnoreCase));

                var namedHits = mods.Where(m =>
                    profile.Markers.Any(marker => ContainsToken(m.Name + " " + m.RelativePath, marker))).ToList();

                bool hasScriptHit = namedHits.Any(m => m.Type == ModFileType.Script);
                if (!groupExists && !hasScriptHit) continue;

                foreach (var m in namedHits)
                {
                    m.Category = ModCategory.CoreMods;
                    m.CoreModGroupName = profile.GroupName;
                }
            }
        }

        /// <summary>
        /// 跨目录：按核心标识/作者+模组前缀相似度，将未分组 package 绑定到脚本模组组。
        /// 绑定后 Category=CoreMods，整理时不会再落入 CAS/General。
        /// </summary>
        private static void BindPackagesByNameSimilarity(List<ModFileItem> mods)
        {
            var scriptAnchors = mods
                .Where(m => m.Type == ModFileType.Script && !string.IsNullOrEmpty(m.CoreModGroupName))
                .Select(s => (
                    Script: s,
                    Identity: ExtractCoreModIdentity(s.Name),
                    Group: s.CoreModGroupName!))
                .Where(x => x.Identity.Length >= 4)
                .ToList();

            if (scriptAnchors.Count == 0) return;

            foreach (var pkg in mods.Where(m => m.Type == ModFileType.Package))
            {
                // 已在其它 CoreMod 组内的 package 不抢绑（同源目录锁定优先）
                if (pkg.Category == ModCategory.CoreMods && !string.IsNullOrEmpty(pkg.CoreModGroupName))
                    continue;

                string pkgIdentity = ExtractCoreModIdentity(pkg.Name);
                string? bestGroup = null;
                int bestScore = 0;

                foreach (var anchor in scriptAnchors)
                {
                    int score = ScoreNameAffinity(
                        anchor.Identity, pkgIdentity, anchor.Script.Name, pkg.Name, anchor.Group);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestGroup = anchor.Group;
                    }
                }

                if (bestGroup != null && bestScore >= MinNameAffinityScore)
                {
                    pkg.Category = ModCategory.CoreMods;
                    pkg.CoreModGroupName = bestGroup;
                }
            }
        }

        private static void GroupScriptsInGenericContainer(List<ModFileItem> files)
        {
            foreach (var script in files.Where(f => f.Type == ModFileType.Script))
            {
                string groupName = ResolveCoreModGroupNameFromFileName(script.Name);
                script.Category = ModCategory.CoreMods;
                script.CoreModGroupName = groupName;

                // 第一优先级：同源目录内名字相关 package
                foreach (var pkg in files.Where(f => f.Type == ModFileType.Package &&
                                                     string.IsNullOrEmpty(f.CoreModGroupName)))
                {
                    if (ScoreNameAffinity(
                            ExtractCoreModIdentity(script.Name),
                            ExtractCoreModIdentity(pkg.Name),
                            script.Name, pkg.Name, groupName) >= MinNameAffinityScore)
                    {
                        pkg.Category = ModCategory.CoreMods;
                        pkg.CoreModGroupName = groupName;
                    }
                }
            }
        }

        /// <summary>
        /// 从文件名提取核心标识：去掉扩展名，剥离 _script / _tuning / _v1.0 等修饰后缀。
        /// 例：TURBODRIVER_WickedWhims_Script_v1.0 → TURBODRIVER_WickedWhims
        /// </summary>
        public static string ExtractCoreModIdentity(string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(stem)) return string.Empty;

            // 反复剥离尾部噪声，直到稳定
            for (int guard = 0; guard < 12; guard++)
            {
                string before = stem;

                // 版本号：_v1 / _v1.0 / _1.0 / _rev2
                stem = Regex.Replace(stem, @"[_\-\s]+v?\d+(\.\d+)*$", "", RegexOptions.IgnoreCase);
                stem = Regex.Replace(stem, @"[_\-\s]+rev\d+$", "", RegexOptions.IgnoreCase);

                // 噪声词：_script / _ts4script / _tuning ...
                foreach (var noise in CoreIdentityNoiseTokens)
                {
                    stem = Regex.Replace(
                        stem,
                        @"[_\-\s]+" + Regex.Escape(noise) + @"$",
                        "",
                        RegexOptions.IgnoreCase);
                }

                // 尾部纯 ts4script 粘连（无分隔符）
                if (stem.EndsWith("ts4script", StringComparison.OrdinalIgnoreCase) && stem.Length > 9)
                    stem = stem[..^9];

                stem = stem.Trim('_', '-', '.', ' ');
                if (string.Equals(before, stem, StringComparison.Ordinal))
                    break;
            }

            return stem;
        }

        /// <summary>
        /// 文件名强相关评分。作者+模组双段前缀、长公共前缀、已知档案命中得高分。
        /// </summary>
        private static int ScoreNameAffinity(
            string scriptIdentity, string packageIdentity,
            string scriptFileName, string packageFileName, string groupName)
        {
            if (MatchesKnownProfile(packageFileName, groupName) ||
                MatchesKnownProfile(packageIdentity, groupName) ||
                MatchesKnownProfile(ExtractCoreModIdentity(packageFileName), groupName))
                return 100;

            string a = (scriptIdentity ?? string.Empty).ToLowerInvariant();
            string b = (packageIdentity ?? string.Empty).ToLowerInvariant();
            if (a.Length < 4 || b.Length < 4) return 0;

            if (string.Equals(a, b, StringComparison.Ordinal))
                return 95;

            // 一方是另一方的前缀（如 TURBODRIVER_WickedWhims vs TURBODRIVER_WickedWhims_Animations）
            if (b.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(b, StringComparison.Ordinal))
            {
                int shorter = Math.Min(a.Length, b.Length);
                if (shorter >= 10) return 88;
                if (shorter >= 6) return 70;
            }

            var ta = SplitNameTokens(a);
            var tb = SplitNameTokens(b);

            // 作者名 + 模组名 双段一致（如 turbodriver + wickedwhims）
            if (ta.Count >= 2 && tb.Count >= 2
                && ta[0] == tb[0] && ta[1] == tb[1]
                && ta[0].Length + ta[1].Length >= 6)
                return 90;

            // 连续 token 序列包含（脚本核心段出现在 package 中）
            if (ta.Count >= 2)
            {
                string joined = string.Join("_", ta.Take(Math.Min(3, ta.Count)));
                if (joined.Length >= 8 && b.Contains(joined, StringComparison.Ordinal))
                    return 82;
            }

            if (ta.Count >= 1 && tb.Count >= 1 && ta[0].Length >= 6 && ta[0] == tb[0])
                return 55;

            // 显著公共前缀
            int lcp = 0;
            int lim = Math.Min(a.Length, b.Length);
            while (lcp < lim && a[lcp] == b[lcp]) lcp++;

            if (lcp >= 14) return 78;
            if (lcp >= 10) return 65;
            if (lcp >= 8 && lcp >= (int)(Math.Min(a.Length, b.Length) * 0.55))
                return 58;

            // 兼容旧 SharesCoreModPrefix 行为
            if (SharesCoreModPrefix(a, b))
                return 52;

            return 0;
        }

        private static List<string> SplitNameTokens(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return new List<string>();
            return Regex.Split(identity.ToLowerInvariant(), @"[_\-\s\.]+")
                .Where(t => t.Length > 0 && !CoreIdentityNoiseTokens.Contains(t))
                .ToList();
        }

        /// <summary>继承原始文件夹名称作为 CoreMod 组名（已知大型模组可规范化命名）。</summary>
        private static string ResolveInheritedFolderGroupName(string relativeDir, List<ModFileItem> files)
        {
            if (string.IsNullOrEmpty(relativeDir))
                return ResolveCoreModGroupNameFromFileName(files.First(f => f.Type == ModFileType.Script).Name);

            string leaf = relativeDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            if (ModFileItem.TryParseCoreModFolderName(leaf, out var fromPrefix))
                return fromPrefix;

            foreach (var profile in KnownCoreModProfiles)
            {
                if (profile.Markers.Any(m => ContainsToken(leaf, m) || files.Any(f => ContainsToken(f.Name, m))))
                    return profile.GroupName;
            }

            // 关键：继承原始文件夹名
            return ModFileItem.SanitizeFolderName(leaf);
        }

        private static bool IsGenericScriptContainer(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return true;
            return leaf.Equals("_Scripts", StringComparison.OrdinalIgnoreCase)
                   || leaf.Equals("Scripts", StringComparison.OrdinalIgnoreCase)
                   || leaf.Equals("FixedScripts", StringComparison.OrdinalIgnoreCase)
                   || leaf.Equals("_CoreMods", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExtractCoreModGroupFromRelativePath(string relativePath, out string groupName)
        {
            groupName = string.Empty;
            string norm = relativePath.Replace('\\', '/');

            var modern = Regex.Match(norm, @"(?:^|/)(【功能性脚本模组】[^/]+)/");
            if (modern.Success &&
                ModFileItem.TryParseCoreModFolderName(modern.Groups[1].Value, out groupName))
                return true;

            var legacy = Regex.Match(norm, @"(?:^|/)_CoreMods/([^/]+)/", RegexOptions.IgnoreCase);
            if (legacy.Success)
            {
                groupName = ModFileItem.SanitizeFolderName(legacy.Groups[1].Value);
                return !string.IsNullOrWhiteSpace(groupName);
            }

            return false;
        }

        private static string ResolveCoreModGroupNameFromFileName(string fileName)
        {
            string identity = ExtractCoreModIdentity(fileName);
            string haystack = string.IsNullOrEmpty(identity) ? Path.GetFileNameWithoutExtension(fileName) : identity;

            foreach (var profile in KnownCoreModProfiles)
            {
                if (profile.Markers.Any(m => ContainsToken(haystack, m) || ContainsToken(fileName, m)))
                    return profile.GroupName;
            }

            // 优先用「作者_模组」双段作为组名，否则用完整核心标识
            var tokens = SplitNameTokens(haystack);
            if (tokens.Count >= 2 && tokens[0].Length >= 2 && tokens[1].Length >= 3)
                return ModFileItem.SanitizeFolderName(tokens[0] + "_" + tokens[1]);

            if (!string.IsNullOrEmpty(identity))
                return ModFileItem.SanitizeFolderName(identity);

            var parts = Regex.Split(haystack, @"[_\-\s]+");
            string prefix = parts.Length > 0 && parts[0].Length >= 3 ? parts[0] : haystack;
            return ModFileItem.SanitizeFolderName(prefix);
        }

        private static bool MatchesKnownProfile(string text, string groupName)
        {
            var profile = KnownCoreModProfiles.FirstOrDefault(p =>
                string.Equals(p.GroupName, groupName, StringComparison.OrdinalIgnoreCase));
            if (profile.GroupName == null) return false;
            return profile.Markers.Any(m => ContainsToken(text, m));
        }

        private static bool SharesCoreModPrefix(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string aa = ExtractCoreModIdentity(a).ToLowerInvariant();
            string bb = ExtractCoreModIdentity(b).ToLowerInvariant();
            if (string.IsNullOrEmpty(aa)) aa = a.ToLowerInvariant();
            if (string.IsNullOrEmpty(bb)) bb = b.ToLowerInvariant();

            if (aa.StartsWith(bb, StringComparison.Ordinal) || bb.StartsWith(aa, StringComparison.Ordinal))
                return Math.Min(aa.Length, bb.Length) >= 4;

            var pa = SplitNameTokens(aa);
            var pb = SplitNameTokens(bb);
            if (pa.Count > 0 && pb.Count > 0 && pa[0].Length >= 3 && pa[0] == pb[0])
                return true;

            return false;
        }

        private static string GetRelativeDirectory(string relativePath)
        {
            string norm = relativePath.Replace('\\', '/');
            int idx = norm.LastIndexOf('/');
            return idx <= 0 ? string.Empty : norm[..idx];
        }

        public static int CleanupStrayPreviewImages(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return 0;

            int deleted = 0;
            var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".bmp", ".webp"
            };

            try
            {
                var packagesByDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var pkg in Directory.EnumerateFiles(rootPath, "*.package", SearchOption.AllDirectories))
                {
                    if (pkg.Contains(".Quarantine", StringComparison.OrdinalIgnoreCase)) continue;
                    string? dir = Path.GetDirectoryName(pkg);
                    if (string.IsNullOrEmpty(dir)) continue;

                    if (!packagesByDir.TryGetValue(dir, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        packagesByDir[dir] = set;
                    }

                    set.Add(Path.GetFileNameWithoutExtension(pkg));
                }

                foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
                {
                    if (file.Contains(".Quarantine", StringComparison.OrdinalIgnoreCase)) continue;

                    string ext = Path.GetExtension(file);
                    if (!imageExts.Contains(ext)) continue;

                    string name = Path.GetFileName(file);
                    string nameNoExt = Path.GetFileNameWithoutExtension(file);
                    string? dir = Path.GetDirectoryName(file);

                    bool looksLikePackagePng =
                        name.EndsWith(".package.png", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".package.jpg", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".package.jpeg", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".package.bmp", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".package.webp", StringComparison.OrdinalIgnoreCase)
                        || name.Contains(".package.", StringComparison.OrdinalIgnoreCase)
                        || nameNoExt.EndsWith(".package", StringComparison.OrdinalIgnoreCase);

                    bool companionOfPackage = false;
                    if (!string.IsNullOrEmpty(dir) && packagesByDir.TryGetValue(dir, out var pkgNames))
                    {
                        string baseName = nameNoExt;
                        if (baseName.EndsWith("_preview", StringComparison.OrdinalIgnoreCase))
                            baseName = baseName[..^"_preview".Length];
                        companionOfPackage = pkgNames.Contains(baseName);
                    }

                    if (!looksLikePackagePng && !companionOfPackage)
                        continue;

                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch { }
                }
            }
            catch { }

            return deleted;
        }

        private static void EnsureOrganizeFolders(string outputRoot)
        {
            string[] folders =
            {
                Path.Combine(outputRoot, "_CAS", "Hair"),
                Path.Combine(outputRoot, "_CAS", "Hat"),
                Path.Combine(outputRoot, "_CAS", "Clothing"),
                Path.Combine(outputRoot, "_CAS", "Shoes"),
                Path.Combine(outputRoot, "_CAS", "Accessories"),
                Path.Combine(outputRoot, "_BuildBuy"),
                Path.Combine(outputRoot, "_Scripts"),
                Path.Combine(outputRoot, "_General")
            };
            // CoreMods 子文件夹按模组名在复制时动态创建：【功能性脚本模组】Name/

            foreach (var folder in folders)
                Directory.CreateDirectory(folder);
        }

        private class OrganizeManifestEntry
        {
            public string OriginalPath { get; set; } = string.Empty;
            public string NewPath { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public DateTime MovedAt { get; set; }
        }

        private static void SaveOrganizeManifest(string organizedRoot, List<OrganizeManifestEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(organizedRoot);
                File.WriteAllText(
                    Path.Combine(organizedRoot, OrganizeManifestFileName),
                    JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void ClassifyMod(ModFileItem item)
        {
            if (item.Type == ModFileType.Script)
            {
                item.Category = ModCategory.Script;
                return;
            }

            if (item.Type != ModFileType.Package)
            {
                item.Category = ModCategory.General;
                return;
            }

            // 零线：已落在整理目录中的文件，以路径为准（避免扫回后徽章与文件夹不一致）
            if (TryClassifyFromOrganizedPath(item.RelativePath, out var pathCat, out var coreGroup))
            {
                item.Category = pathCat;
                if (pathCat == ModCategory.CoreMods)
                    item.CoreModGroupName = coreGroup;
                return;
            }

            if (DbpfContentClassifier.TryClassifyFromDbpf(item.FilePath, out var dbpfCategory))
            {
                item.Category = dbpfCategory;
                return;
            }

            if (TryClassifyFromNameAndPath(item, out var nameCategory))
            {
                item.Category = nameCategory;
                return;
            }

            item.Category = ModCategory.General;
        }

        /// <summary>
        /// 从整理后的规范路径反推分类（_CAS/Hat → CAS_Hat，_General → General，等）。
        /// </summary>
        public static bool TryClassifyFromOrganizedPath(string relativePath, out ModCategory category, out string? coreModGroup)
        {
            category = ModCategory.General;
            coreModGroup = null;
            string norm = relativePath.Replace('\\', '/').ToLowerInvariant();

            // 新规范：【功能性脚本模组】Name/（大小写不敏感匹配前缀需保留原文组名，故在原路径上解析）
            string originalNorm = relativePath.Replace('\\', '/');
            var modernCore = Regex.Match(originalNorm, @"(?:^|/)(【功能性脚本模组】[^/]+)/");
            if (modernCore.Success &&
                ModFileItem.TryParseCoreModFolderName(modernCore.Groups[1].Value, out var modernGroup))
            {
                category = ModCategory.CoreMods;
                coreModGroup = modernGroup;
                return true;
            }

            // 旧版 _CoreMods/Name/（2 层，仅兼容识别）
            var legacyCore = Regex.Match(norm, @"(?:^|/)_coremods/([^/]+)/");
            if (legacyCore.Success)
            {
                category = ModCategory.CoreMods;
                coreModGroup = ModFileItem.SanitizeFolderName(legacyCore.Groups[1].Value);
                return true;
            }

            if (norm.Contains("/_cas/hair/") || norm.StartsWith("_cas/hair/") || norm.Contains("/_cas/hair\\"))
            { category = ModCategory.CAS_Hair; return true; }
            if (norm.Contains("/_cas/hat/") || norm.StartsWith("_cas/hat/"))
            { category = ModCategory.CAS_Hat; return true; }
            if (norm.Contains("/_cas/clothing/") || norm.StartsWith("_cas/clothing/"))
            { category = ModCategory.CAS_Clothing; return true; }
            if (norm.Contains("/_cas/shoes/") || norm.StartsWith("_cas/shoes/"))
            { category = ModCategory.CAS_Shoes; return true; }
            if (norm.Contains("/_cas/accessories/") || norm.StartsWith("_cas/accessories/"))
            { category = ModCategory.CAS_Accessories; return true; }
            if (norm.Contains("/_buildbuy/") || norm.StartsWith("_buildbuy/"))
            { category = ModCategory.BuildBuy; return true; }
            if (norm.Contains("/_scripts/") || norm.StartsWith("_scripts/"))
            { category = ModCategory.Script; return true; }
            if (norm.Contains("/_general/") || norm.StartsWith("_general/"))
            { category = ModCategory.General; return true; }

            // 宽松：路径段精确匹配
            if (Regex.IsMatch(norm, @"(^|/)_cas/hair(/|$)")) { category = ModCategory.CAS_Hair; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_cas/hat(/|$)")) { category = ModCategory.CAS_Hat; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_cas/clothing(/|$)")) { category = ModCategory.CAS_Clothing; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_cas/shoes(/|$)")) { category = ModCategory.CAS_Shoes; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_cas/accessories(/|$)")) { category = ModCategory.CAS_Accessories; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_buildbuy(/|$)")) { category = ModCategory.BuildBuy; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_scripts(/|$)")) { category = ModCategory.Script; return true; }
            if (Regex.IsMatch(norm, @"(^|/)_general(/|$)")) { category = ModCategory.General; return true; }

            return false;
        }

        /// <summary>树节点路径 → 应对齐的分类约束（若可识别）。</summary>
        public static bool TryGetCategoryConstraintFromFolderPath(string folderPrefix, out ModCategory category)
        {
            category = ModCategory.General;
            if (string.IsNullOrWhiteSpace(folderPrefix)) return false;

            // 伪造一个相对路径以复用解析
            string fake = folderPrefix.Replace('\\', '/').TrimEnd('/') + "/dummy.package";
            return TryClassifyFromOrganizedPath(fake, out category, out _);
        }

        private static bool TryClassifyFromNameAndPath(ModFileItem item, out ModCategory category)
        {
            category = ModCategory.General;
            string haystack = (item.Name + " " + item.RelativePath).ToLowerInvariant();

            foreach (var (cat, keywords) in NamePathRules)
            {
                foreach (var keyword in keywords)
                {
                    if (ContainsToken(haystack, keyword))
                    {
                        category = cat;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>词边界匹配，避免 hat∈that、top∈desktop 等误伤。</summary>
        private static bool ContainsToken(string haystack, string keyword)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(keyword)) return false;
            string h = haystack.ToLowerInvariant();
            string k = keyword.ToLowerInvariant();

            int idx = 0;
            while ((idx = h.IndexOf(k, idx, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(h[idx - 1]);
                int end = idx + k.Length;
                bool rightOk = end >= h.Length || !char.IsLetterOrDigit(h[end]);
                if (leftOk && rightOk) return true;
                idx = end;
            }

            return false;
        }

        private void CheckScriptDepth(ModFileItem item, string relativePath)
        {
            if (item.Type != ModFileType.Script) return;

            string[] parts = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            // EA：.ts4script 最多 1 层子目录（Mods/Folder/file.ts4script → parts.Length == 2）
            // 【功能性脚本模组】Name/file 正好 1 层；_CoreMods/Name/file 为 2 层，判定为失效
            if (parts.Length <= 2)
                return;

            item.IsScriptDepthInvalid = true;
        }
    }
}
