using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sims4ModDoctor.Core
{
    public class ModScannerService
    {
        public async Task<List<ModFileItem>> ScanDirectoryAsync(string rootPath)
        {
            return await Task.Run(() =>
            {
                var modItems = new List<ModFileItem>();
                if (!Directory.Exists(rootPath)) return modItems;

                var dirInfo = new DirectoryInfo(rootPath);
                var files = dirInfo.GetFiles("*.*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    // 忽略临时与隔离区文件
                    if (file.DirectoryName != null && file.DirectoryName.Contains(".Quarantine"))
                        continue;

                    string ext = file.Extension.ToLower();
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

                    // 执行智能分类与脚本深度检测
                    ClassifyMod(item);
                    CheckScriptDepth(item, relativePath);

                    modItems.Add(item);
                }

                return modItems;
            });
        }

        // 智能分类逻辑
        private void ClassifyMod(ModFileItem item)
        {
            if (item.Type == ModFileType.Script)
            {
                item.Category = ModCategory.Script;
                return;
            }

            string pathLower = item.RelativePath.ToLower();
            
            // 基于路径特征判断 CAS 或 BuildBuy
            if (pathLower.Contains("cas") || pathLower.Contains("hair") || pathLower.Contains("skin") || pathLower.Contains("clothes") || pathLower.Contains("makeup"))
            {
                item.Category = ModCategory.CAS;
            }
            else if (pathLower.Contains("build") || pathLower.Contains("buy") || pathLower.Contains("furniture") || pathLower.Contains("decor"))
            {
                item.Category = ModCategory.BuildBuy;
            }
            else
            {
                item.Category = ModCategory.General;
            }
        }

        // 校验 .ts4script 文件层级深度（不能超过 1 层子目录）
        private void CheckScriptDepth(ModFileItem item, string relativePath)
        {
            if (item.Type == ModFileType.Script)
            {
                string[] parts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length > 2)
                {
                    item.IsScriptDepthInvalid = true;
                }
            }
        }
    }
}