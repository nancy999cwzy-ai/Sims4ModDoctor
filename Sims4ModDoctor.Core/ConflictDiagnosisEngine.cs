using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sims4ModDoctor.Core
{
    // 隔离区内单个文件的原始位置映射，是精确还原的唯一依据
    public class QuarantineEntry
    {
        // 相对于 .Quarantine 目录的存放路径
        public string QuarantineRelativePath { get; set; } = string.Empty;

        // 被隔离前的完整原始路径
        public string OriginalPath { get; set; } = string.Empty;

        public DateTime IsolatedAt { get; set; }
    }

    public class QuarantineRestoreResult
    {
        public int RestoredCount { get; set; }

        // 原位置已存在同名文件，为避免覆盖而继续留在隔离区的文件
        public List<string> SkippedConflicts { get; } = new();

        // manifest 丢失或损坏，靠隔离区目录结构推断位置还原的文件
        public List<string> RestoredWithoutMapping { get; } = new();

        public List<string> Failures { get; } = new();

        public bool IsFullyRestored => SkippedConflicts.Count == 0 && Failures.Count == 0;
    }

    public class ConflictDiagnosisEngine
    {
        private const string QuarantineFolderName = ".Quarantine";
        private const string ManifestFileName = "quarantine-manifest.json";

        private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

        private List<ModFileItem> _candidates = new();
        private List<ModFileItem> _currentTestGroup = new();
        private List<ModFileItem> _currentExcludedGroup = new();

        public int CurrentStep { get; private set; } = 0;
        public bool IsCompleted { get; private set; } = false;
        public ModFileItem? CulpritMod { get; private set; }

        public void StartDiagnosis(List<ModFileItem> allMods)
        {
            _candidates = allMods.Where(m => m.Type == ModFileType.Package || m.Type == ModFileType.Script).ToList();
            CurrentStep = 0;
            IsCompleted = false;
            CulpritMod = null;
            NextStep();
        }

        public void SubmitFeedback(bool isProblemPresent)
        {
            if (IsCompleted) return;

            if (isProblemPresent)
            {
                _candidates = new List<ModFileItem>(_currentTestGroup);
            }
            else
            {
                _candidates = new List<ModFileItem>(_currentExcludedGroup);
            }

            if (_candidates.Count <= 1)
            {
                IsCompleted = true;
                CulpritMod = _candidates.FirstOrDefault();
            }
            else
            {
                NextStep();
            }
        }

        private void NextStep()
        {
            CurrentStep++;
            int half = _candidates.Count / 2;
            _currentTestGroup = _candidates.Take(half).ToList();
            _currentExcludedGroup = _candidates.Skip(half).ToList();
        }

        public List<ModFileItem> GetCurrentTestGroup() => _currentTestGroup;
        public List<ModFileItem> GetExcludedGroup() => _currentExcludedGroup;

        // 隔离区内镜像原有子目录结构：相对路径天然唯一，同名文件不会互相覆盖
        public void IsolateFiles(string rootPath, List<ModFileItem> filesToIsolate)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || filesToIsolate == null || filesToIsolate.Count == 0) return;

            string quarantinePath = Path.Combine(rootPath, QuarantineFolderName);
            Directory.CreateDirectory(quarantinePath);

            // 合并已有映射：进程异常退出后隔离区可能残留上一次的条目
            var manifest = LoadManifest(quarantinePath);

            try
            {
                foreach (var item in filesToIsolate)
                {
                    if (!File.Exists(item.FilePath)) continue;

                    string relativePath = GetSafeRelativePath(rootPath, item);
                    string targetPath = Path.Combine(quarantinePath, relativePath);

                    // 残留文件占位等极端情况下另起唯一名，绝不删除隔离区里已有的文件
                    if (File.Exists(targetPath))
                    {
                        targetPath = BuildUniquePath(targetPath);
                    }

                    string? targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Move(item.FilePath, targetPath);

                    manifest.Add(new QuarantineEntry
                    {
                        QuarantineRelativePath = Path.GetRelativePath(quarantinePath, targetPath),
                        OriginalPath = item.FilePath,
                        IsolatedAt = DateTime.Now
                    });
                }
            }
            finally
            {
                // 中途抛异常也要落盘：已移动的文件必须留下可还原的映射
                SaveManifest(quarantinePath, manifest);
            }
        }

        public QuarantineRestoreResult RestoreAllFiles(string rootPath)
        {
            var result = new QuarantineRestoreResult();
            if (string.IsNullOrWhiteSpace(rootPath)) return result;

            string quarantinePath = Path.Combine(rootPath, QuarantineFolderName);
            if (!Directory.Exists(quarantinePath)) return result;

            var manifest = LoadManifest(quarantinePath);
            var pending = new List<QuarantineEntry>();

            try
            {
                foreach (var entry in manifest)
                {
                    string sourcePath = Path.Combine(quarantinePath, entry.QuarantineRelativePath);

                    // 文件已被用户手动移走，直接丢弃这条失效映射
                    if (!File.Exists(sourcePath)) continue;

                    if (TryMoveBack(sourcePath, entry.OriginalPath, result))
                    {
                        result.RestoredCount++;
                    }
                    else
                    {
                        pending.Add(entry);
                    }
                }

                // 兜底：manifest 缺失或损坏时，按隔离区内的镜像结构推断原位置
                RestoreUnmappedFiles(rootPath, quarantinePath, pending, result);
            }
            finally
            {
                SaveManifest(quarantinePath, pending);
                CleanUpQuarantine(quarantinePath);
            }

            return result;
        }

        public bool HasPendingQuarantine(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return false;

            string quarantinePath = Path.Combine(rootPath, QuarantineFolderName);
            if (!Directory.Exists(quarantinePath)) return false;

            return Directory.EnumerateFiles(quarantinePath, "*.*", SearchOption.AllDirectories)
                .Any(f => !string.Equals(Path.GetFileName(f), ManifestFileName, StringComparison.OrdinalIgnoreCase));
        }

        // 还原单个文件；目标位置已被占用或移动失败时返回 false，文件保持在隔离区
        private static bool TryMoveBack(string sourcePath, string destination, QuarantineRestoreResult result)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                result.Failures.Add($"{sourcePath}（缺少原始路径信息）");
                return false;
            }

            // 原位置已有同名文件：保留隔离区副本并上报，绝不覆盖用户文件
            if (File.Exists(destination))
            {
                result.SkippedConflicts.Add(destination);
                return false;
            }

            try
            {
                string? destDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Move(sourcePath, destination);
                return true;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{destination}（{ex.Message}）");
                return false;
            }
        }

        private static void RestoreUnmappedFiles(string rootPath, string quarantinePath, List<QuarantineEntry> pending, QuarantineRestoreResult result)
        {
            var pendingPaths = pending
                .Select(e => Path.GetFullPath(Path.Combine(quarantinePath, e.QuarantineRelativePath)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var leftovers = Directory.EnumerateFiles(quarantinePath, "*.*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Where(f => !pendingPaths.Contains(Path.GetFullPath(f)))
                .ToList();

            foreach (var sourcePath in leftovers)
            {
                // 隔离区内的相对路径就是原始相对路径，可据此反推目标位置
                string relativePath = Path.GetRelativePath(quarantinePath, sourcePath);
                string destination = Path.Combine(rootPath, relativePath);

                if (TryMoveBack(sourcePath, destination, result))
                {
                    result.RestoredCount++;
                    result.RestoredWithoutMapping.Add(destination);
                }
                else
                {
                    pending.Add(new QuarantineEntry
                    {
                        QuarantineRelativePath = relativePath,
                        OriginalPath = destination,
                        IsolatedAt = DateTime.Now
                    });
                }
            }
        }

        private static string GetSafeRelativePath(string rootPath, ModFileItem item)
        {
            string candidate = item.RelativePath;

            if (!IsSafeRelativePath(candidate))
            {
                candidate = Path.GetRelativePath(rootPath, item.FilePath);
            }

            if (!IsSafeRelativePath(candidate))
            {
                // 拿不到安全的相对路径时退化为「文件名 + 原路径短哈希」，仍能保证唯一
                string extension = Path.GetExtension(item.Name);
                string nameWithoutExt = Path.GetFileNameWithoutExtension(item.Name);
                candidate = $"{nameWithoutExt}_{ComputeShortHash(item.FilePath)}{extension}";
            }

            return candidate;
        }

        private static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (Path.IsPathRooted(path)) return false;

            var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            return !path.Split(separators, StringSplitOptions.RemoveEmptyEntries).Contains("..");
        }

        private static string ComputeShortHash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        private static string BuildUniquePath(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);

            for (int i = 1; i <= 999; i++)
            {
                string candidate = Path.Combine(dir, $"{nameWithoutExt}_{i}{extension}");
                if (!File.Exists(candidate)) return candidate;
            }

            return Path.Combine(dir, $"{nameWithoutExt}_{Guid.NewGuid():N}{extension}");
        }

        private static List<QuarantineEntry> LoadManifest(string quarantinePath)
        {
            string manifestPath = Path.Combine(quarantinePath, ManifestFileName);
            if (!File.Exists(manifestPath)) return new List<QuarantineEntry>();

            try
            {
                string json = File.ReadAllText(manifestPath);
                var entries = JsonSerializer.Deserialize<List<QuarantineEntry>>(json);
                return entries ?? new List<QuarantineEntry>();
            }
            catch (Exception)
            {
                // manifest 损坏不影响还原：隔离区的镜像目录结构本身就是一份冗余映射
                return new List<QuarantineEntry>();
            }
        }

        private static void SaveManifest(string quarantinePath, List<QuarantineEntry> entries)
        {
            string manifestPath = Path.Combine(quarantinePath, ManifestFileName);

            try
            {
                if (entries.Count == 0)
                {
                    if (File.Exists(manifestPath))
                    {
                        File.Delete(manifestPath);
                    }
                    return;
                }

                Directory.CreateDirectory(quarantinePath);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(entries, ManifestJsonOptions));
            }
            catch (Exception)
            {
                // 映射落盘失败不应打断文件操作本身，镜像目录结构仍可支撑还原
            }
        }

        // 清掉隔离区里残留的空目录，隔离区本身为空时一并移除
        private static void CleanUpQuarantine(string quarantinePath)
        {
            try
            {
                if (!Directory.Exists(quarantinePath)) return;

                foreach (var dir in Directory.GetDirectories(quarantinePath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(quarantinePath).Any())
                {
                    Directory.Delete(quarantinePath);
                }
            }
            catch (Exception)
            {
                // 清理属于锦上添花，失败不影响还原结果
            }
        }
    }
}
