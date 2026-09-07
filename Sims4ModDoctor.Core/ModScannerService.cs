using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Sims4ModDoctor.Core
{
    public class ModScannerService
    {
        public async Task<List<ModFileItem>> ScanDirectoryAsync(string rootPath)
        {
            return await Task.Run(() =>
            {
                var resultList = new List<ModFileItem>();

                if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                    return resultList;

                var dirInfo = new DirectoryInfo(rootPath);
                var files = dirInfo.GetFiles("*.*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    string ext = file.Extension.ToLower();
                    if (ext != ".package" && ext != ".ts4script")
                        continue;

                    var modItem = new ModFileItem
                    {
                        Name = file.Name,
                        FilePath = file.FullName,
                        RelativePath = Path.GetRelativePath(rootPath, file.FullName),
                        FileSizeBytes = file.Length,
                        LastModified = file.LastWriteTime,
                        Type = ext switch
                        {
                            ".package" => ModFileType.Package,
                            ".ts4script" => ModFileType.Script,
                            _ => ModFileType.Unknown
                        }
                    };

                    resultList.Add(modItem);
                }

                return resultList;
            });
        }
    }
}
