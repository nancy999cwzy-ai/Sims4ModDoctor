using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sims4ModDoctor.Core;

namespace Sims4ModDoctor.UI
{
    public class CategoryStatRow
    {
        public string Label { get; set; } = string.Empty;
        public double Percent { get; set; }
        public string StatsText { get; set; } = string.Empty;
        public Brush BarBrush { get; set; } = Brushes.Gray;
    }

    public enum MainViewMode
    {
        Dashboard,
        ModList,
        Quarantine
    }

    public partial class MainWindow : Window
    {
        private string? _selectedModsPath;
        private readonly ModScannerService _scannerService = new();
        private ConflictDiagnosisEngine _diagnosisEngine = new();
        private List<ModFileItem> _allMods = new();
        private List<ModFileItem> _quarantineFiles = new();
        private MainViewMode _currentView = MainViewMode.ModList;
        private bool _hasInvalidScripts = false;
        private bool _isDiagnosisMode = false;
        private string? _highlightQuarantineRelativePath;

        // Mod 列表：文件夹树筛选（路径 + 可选分类约束）
        private FolderFilterState _folderFilter = FolderFilterState.All;
        private ModCategory? _categoryFilter;

        public MainWindow()
        {
            InitializeComponent();
            InitCategoryFilterCombo();
        }

        private void InitCategoryFilterCombo()
        {
            CmbCategoryFilter.Items.Clear();
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("全部", null));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("CAS 头发", ModCategory.CAS_Hair));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("CAS 帽子", ModCategory.CAS_Hat));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("服装", ModCategory.CAS_Clothing));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("鞋子", ModCategory.CAS_Shoes));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("饰品", ModCategory.CAS_Accessories));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("建筑家具", ModCategory.BuildBuy));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("核心模组", ModCategory.CoreMods));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("脚本", ModCategory.Script));
            CmbCategoryFilter.Items.Add(new CategoryFilterOption("普通 Package", ModCategory.General));
            CmbCategoryFilter.DisplayMemberPath = nameof(CategoryFilterOption.Label);
            CmbCategoryFilter.SelectedIndex = 0;
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "请选择你的 Sims 4 Mods 文件夹"
            };

            if (dialog.ShowDialog() == true)
            {
                _selectedModsPath = dialog.FolderName;
                TxtModsPath.Text = _selectedModsPath;
                TxtModsPath.Foreground = System.Windows.Media.Brushes.Black;
                TxtStatus.Text = $"已选择目录：{_selectedModsPath}，请点击“开始全面扫描”。";
            }
        }

        private async void BtnStartScan_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath) || !Directory.Exists(_selectedModsPath))
            {
                MessageBox.Show("请先选择有效的 Mods 文件夹！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TxtStatus.Text = "⏳ 正在分析 Mods 结构与分类元数据...";

            _allMods = await _scannerService.ScanDirectoryAsync(_selectedModsPath);
            _diagnosisEngine = new ConflictDiagnosisEngine();

            int packageCount = _allMods.Count(m => m.Type == ModFileType.Package);
            int scriptCount = _allMods.Count(m => m.Type == ModFileType.Script);
            int invalidScriptCount = _allMods.Count(m => m.IsScriptDepthInvalid);
            int thumbCount = _allMods.Count(m => m.HasThumbnail);
            double totalMB = _allMods.Sum(m => m.FileSizeBytes) / (1024.0 * 1024.0);

            // 扫描结果默认回到 Mod 列表；大盘数据同步刷新
            _currentView = MainViewMode.ModList;
            _hasInvalidScripts = invalidScriptCount > 0;
            _folderFilter = FolderFilterState.All;
            if (CmbCategoryFilter.SelectedIndex != 0)
                CmbCategoryFilter.SelectedIndex = 0;
            else
                _categoryFilter = null;

            RebuildFolderTree();
            SwitchToCurrentView();
            RefreshDashboard();

            if (invalidScriptCount > 0)
            {
                TxtStatus.Text = $"扫描完成！共 {_allMods.Count} 个文件，缩略图 {thumbCount} 张。⚠️ 检测到 {invalidScriptCount} 个脚本嵌套过深！";
            }
            else
            {
                TxtStatus.Text = $"扫描完成！共 {_allMods.Count} 个文件（.package: {packageCount} | .ts4script: {scriptCount}），总体积 {totalMB:F2} MB，缩略图 {thumbCount} 张。";
            }
        }

        // 自动平铺修复嵌套过深的 .ts4script 脚本
        private void BtnFixScripts_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath)) return;

            var invalidScripts = _allMods.Where(m => m.IsScriptDepthInvalid).ToList();
            if (!invalidScripts.Any()) return;

            var confirm = MessageBox.Show(
                $"共找到 {invalidScripts.Count} 个嵌套过深的脚本文件 (.ts4script)。\n\n" +
                $"系统会将它们自动移动到 Mods 的一级子目录中（例如: Mods/FixedScripts/），以恢复游戏内生效。\n\n是否立即修复？",
                "确认一键平铺",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirm == MessageBoxResult.Yes)
            {
                string targetDir = Path.Combine(_selectedModsPath, "FixedScripts");
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                int movedCount = 0;
                foreach (var script in invalidScripts)
                {
                    if (File.Exists(script.FilePath))
                    {
                        string destPath = Path.Combine(targetDir, script.Name);
                        if (File.Exists(destPath))
                        {
                            destPath = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(script.Name)}_{Guid.NewGuid().ToString().Substring(0, 4)}{Path.GetExtension(script.Name)}");
                        }
                        File.Move(script.FilePath, destPath);
                        movedCount++;
                    }
                }

                MessageBox.Show($"成功修复 {movedCount} 个脚本文件！已统一放置在 'Mods/FixedScripts' 目录下。\n系统将自动重新扫描。", "修复完成", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // 重新扫描刷新界面
                BtnStartScan_Click(sender, e);
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void CmbCategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCategoryFilter.SelectedItem is CategoryFilterOption opt)
                _categoryFilter = opt.Category;
            else
                _categoryFilter = null;

            ApplyFilter();
        }

        private void TreeFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is FolderFilterState state)
            {
                _folderFilter = state;
                ApplyFilter();
            }
            else if (e.NewValue is TreeViewItem { Tag: string prefix })
            {
                // 兼容旧 Tag
                _folderFilter = FolderFilterState.FromPath(prefix);
                ApplyFilter();
            }
        }

        private void SliderCardZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CardScaleTransform == null || TxtZoomPercent == null) return;
            CardScaleTransform.ScaleX = e.NewValue;
            CardScaleTransform.ScaleY = e.NewValue;
            TxtZoomPercent.Text = $"{e.NewValue * 100:0}%";
        }

        private void ApplyFilter()
        {
            if (ModCardGrid == null || QuarantineCardGrid == null) return;

            if (_currentView == MainViewMode.Quarantine)
            {
                ApplyQuarantineFilter();
                return;
            }

            if (_currentView != MainViewMode.ModList) return;

            IEnumerable<ModFileItem> query = _allMods;

            // 树节点：路径严格过滤；若节点对应规范分类目录，再叠加 Category 约束
            // （例如选中 _General 时绝不显示 Category=CAS_Hat 的卡片）
            if (!_folderFilter.IsAll)
            {
                string prefix = _folderFilter.PathPrefix.Replace('\\', '/').TrimEnd('/');
                query = query.Where(m => IsUnderFolder(m.RelativePath, prefix));

                if (_folderFilter.CategoryConstraint.HasValue)
                {
                    var required = _folderFilter.CategoryConstraint.Value;
                    query = query.Where(m => m.Category == required);
                }
            }

            if (_categoryFilter.HasValue)
                query = query.Where(m => m.Category == _categoryFilter.Value);

            string keyword = TxtSearch.Text.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(m =>
                    m.Name.ToLowerInvariant().Contains(keyword) ||
                    m.RelativePath.ToLowerInvariant().Contains(keyword));
            }

            var displayItems = query.ToList();
            ModCardGrid.ItemsSource = null;
            ModCardGrid.ItemsSource = displayItems;
        }

        private static bool IsUnderFolder(string relativePath, string folderPrefix)
        {
            if (string.IsNullOrEmpty(folderPrefix)) return true;

            string rel = relativePath.Replace('\\', '/');
            string prefix = folderPrefix.Replace('\\', '/').TrimEnd('/');

            if (rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            string? dir = GetRelativeDir(rel);
            return string.Equals(dir, prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetRelativeDir(string relativePathWithSlashes)
        {
            int idx = relativePathWithSlashes.LastIndexOf('/');
            return idx <= 0 ? null : relativePathWithSlashes[..idx];
        }

        private void ApplyQuarantineFilter()
        {
            IEnumerable<ModFileItem> query = _quarantineFiles;
            string keyword = TxtSearch.Text.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(m =>
                    m.Name.ToLowerInvariant().Contains(keyword) ||
                    m.RelativePath.ToLowerInvariant().Contains(keyword));
            }

            if (!string.IsNullOrEmpty(_highlightQuarantineRelativePath))
            {
                string highlight = _highlightQuarantineRelativePath;
                query = query.OrderByDescending(m =>
                    string.Equals(m.RelativePath, highlight, StringComparison.OrdinalIgnoreCase));
            }

            QuarantineCardGrid.ItemsSource = null;
            QuarantineCardGrid.ItemsSource = query.ToList();
        }

        private void RebuildFolderTree()
        {
            if (TreeFolders == null) return;
            TreeFolders.Items.Clear();

            var root = new TreeViewItem
            {
                Header = $"全部 Mods（{_allMods.Count}）",
                Tag = FolderFilterState.All,
                IsExpanded = true,
                IsSelected = true
            };
            TreeFolders.Items.Add(root);

            var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in _allMods)
            {
                string? dir = GetRelativeDir(mod.RelativePath.Replace('\\', '/'));
                if (string.IsNullOrEmpty(dir)) continue;

                string[] parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string accum = string.Empty;
                TreeViewItem parent = root;

                foreach (var part in parts)
                {
                    accum = string.IsNullOrEmpty(accum) ? part : accum + "/" + part;
                    if (!nodes.TryGetValue(accum, out var node))
                    {
                        node = new TreeViewItem
                        {
                            Header = part,
                            Tag = FolderFilterState.FromPath(accum),
                            IsExpanded = parts.Length <= 2
                        };
                        parent.Items.Add(node);
                        nodes[accum] = node;
                    }
                    parent = node;
                }
            }

            foreach (var kv in nodes)
            {
                string prefix = kv.Key;
                int count = _allMods.Count(m => IsUnderFolder(m.RelativePath, prefix));

                // 若节点带分类约束，计数也只统计该分类（与过滤逻辑一致）
                if (kv.Value.Tag is FolderFilterState { CategoryConstraint: { } cat })
                {
                    count = _allMods.Count(m =>
                        IsUnderFolder(m.RelativePath, prefix) && m.Category == cat);
                }

                string leaf = prefix.Contains('/') ? prefix[(prefix.LastIndexOf('/') + 1)..] : prefix;
                kv.Value.Header = $"{leaf}（{count}）";
            }
        }

        private void UpdateToolbarState()
        {
            bool isDashboard = _currentView == MainViewMode.Dashboard;
            bool isModList = _currentView == MainViewMode.ModList;
            bool isQuarantineView = _currentView == MainViewMode.Quarantine;

            PanelListToolbar.Visibility = isDashboard ? Visibility.Collapsed : Visibility.Visible;
            PanelDashboard.Visibility = isDashboard ? Visibility.Visible : Visibility.Collapsed;
            PanelModBrowser.Visibility = isModList ? Visibility.Visible : Visibility.Collapsed;
            PanelQuarantineBrowser.Visibility = isQuarantineView ? Visibility.Visible : Visibility.Collapsed;

            bool allowQuarantineActions = isQuarantineView && !_isDiagnosisMode;

            BtnOrganize.Visibility = (isModList && !_isDiagnosisMode && _allMods.Count > 0)
                ? Visibility.Visible : Visibility.Collapsed;
            BtnOrganize.IsEnabled = !_isDiagnosisMode && _allMods.Count > 0;

            BtnFixScripts.Visibility = (isModList && _hasInvalidScripts) ? Visibility.Visible : Visibility.Collapsed;
            BtnRestoreQuarantine.Visibility = allowQuarantineActions ? Visibility.Visible : Visibility.Collapsed;
            BtnClearQuarantine.Visibility = allowQuarantineActions ? Visibility.Visible : Visibility.Collapsed;

            bool hasQuarantineFiles = _quarantineFiles.Count > 0 && allowQuarantineActions;
            BtnRestoreQuarantine.IsEnabled = hasQuarantineFiles;
            BtnClearQuarantine.IsEnabled = hasQuarantineFiles;
        }

        private void SwitchToCurrentView()
        {
            UpdateToolbarState();

            if (_currentView == MainViewMode.Dashboard)
            {
                RefreshDashboard();
            }
            else
            {
                ApplyFilter();
            }
        }

        // ===================== 首页大盘 =====================

        private void BtnDashboard_Click(object sender, RoutedEventArgs e)
        {
            _currentView = MainViewMode.Dashboard;
            SwitchToCurrentView();

            TxtStatus.Text = _allMods.Count == 0
                ? "首页大盘：请先完成全面扫描以查看统计数据。"
                : $"首页大盘：基于 {_allMods.Count} 个已扫描 Mod 的实时统计。";
        }

        private void RefreshDashboard()
        {
            if (TxtDashTotalMods == null) return;

            int total = _allMods.Count;
            long totalBytes = _allMods.Sum(m => m.FileSizeBytes);
            double totalMB = totalBytes / (1024.0 * 1024.0);
            int scriptCount = _allMods.Count(m => m.Type == ModFileType.Script || m.Category == ModCategory.Script);
            int anomalyCount = _allMods.Count(m => m.IsScriptDepthInvalid);
            int thumbCount = _allMods.Count(m => m.HasThumbnail);

            TxtDashTotalMods.Text = total.ToString();
            TxtDashTotalSize.Text = $"{totalMB:F2} MB";
            TxtDashScriptCount.Text = scriptCount.ToString();
            TxtDashAnomalyCount.Text = anomalyCount.ToString();

            TxtDashboardHint.Text = total == 0
                ? "请先选择 Mods 目录并完成全面扫描，即可查看可视化统计。"
                : $"基于 DBPF 内容分类 · 共 {total} 个文件 · {totalMB:F2} MB · 缩略图 {thumbCount} 张";

            var palette = new[]
            {
                "#3699FF", "#F1416C", "#50CD89", "#FFA800", "#7239EA",
                "#0BB783", "#E14CCA", "#181C32", "#009EF7", "#FFE2E5", "#A1A5B7"
            };

            var order = new[]
            {
                ModCategory.CAS_Hair, ModCategory.CAS_Hat, ModCategory.CAS_Clothing,
                ModCategory.CAS_Shoes, ModCategory.CAS_Accessories,
                ModCategory.BuildBuy, ModCategory.CoreMods, ModCategory.Script, ModCategory.General
            };

            var rows = new List<CategoryStatRow>();
            int colorIndex = 0;
            foreach (var category in order)
            {
                var items = _allMods.Where(m => m.Category == category).ToList();
                // 即使为 0 也展示，方便用户看到完整细分维度
                long bytes = items.Sum(m => m.FileSizeBytes);
                double mb = bytes / (1024.0 * 1024.0);
                double percent = totalBytes > 0 ? bytes * 100.0 / totalBytes : 0;
                string color = palette[colorIndex % palette.Length];
                colorIndex++;

                // 取一个样本的 CategoryDisplay 作为标签
                string label = items.FirstOrDefault()?.CategoryDisplay
                               ?? new ModFileItem { Category = category }.CategoryDisplay;

                rows.Add(new CategoryStatRow
                {
                    Label = label,
                    Percent = percent,
                    StatsText = $"{items.Count} 个 / {mb:F2} MB ({percent:F0}%)",
                    BarBrush = (Brush)new BrushConverter().ConvertFromString(color)!
                });
            }

            CategoryStatsList.ItemsSource = rows;
        }

        // ===================== 一键智能整理 =====================

        private async void BtnOrganize_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath) || _allMods.Count == 0)
            {
                MessageBox.Show("请先完成全面扫描后再执行智能整理。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isDiagnosisMode)
            {
                MessageBox.Show("诊断进行中，请先退出诊断后再整理文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                "即将执行智能分类并【复制】到 Mods 同级的【" + ModScannerService.OrganizedSiblingFolderName + "】：\n\n" +
                "· DBPF 二进制识别（CAS Part / Object Definition）\n" +
                "· 同源文件夹脚本+资源同组锁定 → 【功能性脚本模组】名称/\n" +
                "· 词库降级匹配（姿势/皮肤妆容/CAS/家具）\n" +
                "· 增量复制（大小一致跳过）+ 清理孤儿文件与空目录\n\n" +
                "原 Mods 目录不会被修改。缩略图仅内存渲染，不落盘图片。\n\n" +
                "目录示例：\n" +
                $"· {ModScannerService.OrganizedSiblingFolderName}/【功能性脚本模组】WickedWhims/\n" +
                $"· {ModScannerService.OrganizedSiblingFolderName}/_CAS/Hair|Hat|Clothing|Shoes|Accessories\n" +
                $"· {ModScannerService.OrganizedSiblingFolderName}/_BuildBuy / _Scripts / _General\n\n" +
                "是否继续？",
                "确认一键智能整理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirm != MessageBoxResult.Yes) return;

            TxtStatus.Text = "⏳ 正在按 DBPF 内容精确分类并复制到 Mods_Organized...";
            BtnOrganize.IsEnabled = false;

            OrganizeResult result;
            try
            {
                result = await Task.Run(() => _scannerService.AutoOrganizeByCategory(_selectedModsPath!, _allMods));
            }
            catch (Exception ex)
            {
                BtnOrganize.IsEnabled = true;
                MessageBox.Show($"整理失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BtnOrganize.IsEnabled = true;
            RefreshDashboard();

            var summary = new StringBuilder();
            summary.AppendLine($"已在 Mods 文件夹旁边生成全新的 [{ModScannerService.OrganizedSiblingFolderName}] 文件夹！");
            summary.AppendLine("请先进入游戏测试效果，满意后再手动删除旧 Mods 文件夹。");
            summary.AppendLine();
            summary.AppendLine($"输出目录：{result.OutputFolder}");
            summary.AppendLine($"成功复制：{result.CopiedCount} 个");
            summary.AppendLine($"已存在（跳过）：{result.SkippedAlreadyOrganized} 个");
            summary.AppendLine($"孤儿清理：{result.OrphanDeletedCount} 个");
            summary.AppendLine($"空目录清理：{result.EmptyFoldersRemoved} 个");
            if (result.PreviewImagesDeleted > 0)
                summary.AppendLine($"预览图垃圾清理：{result.PreviewImagesDeleted} 个");
            if (result.FailedCount > 0)
                summary.AppendLine($"失败：{result.FailedCount} 个");

            if (result.CategoryCopiedCounts.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("分类统计：");
                foreach (var kv in result.CategoryCopiedCounts.OrderBy(k => k.Key))
                    summary.AppendLine($"  · {kv.Key}：{kv.Value} 个");
            }

            if (result.Failures.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("失败明细（最多 5 条）：");
                foreach (var fail in result.Failures.Take(5))
                    summary.AppendLine($"  - {fail}");
            }

            MessageBox.Show(summary.ToString(), "整理完成", MessageBoxButton.OK,
                result.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            TxtStatus.Text = $"✅ 已复制 {result.CopiedCount}、跳过 {result.SkippedAlreadyOrganized}、清理孤儿 {result.OrphanDeletedCount} → {result.OutputFolder}";
        }

        // ===================== 安全隔离区管理 =====================

        private void BtnModList_Click(object sender, RoutedEventArgs e)
        {
            _currentView = MainViewMode.ModList;
            SwitchToCurrentView();

            if (_isDiagnosisMode)
            {
                TxtStatus.Text = $"诊断模式 - 第 {_diagnosisEngine.CurrentStep} 轮进行中。可随时切换到【安全隔离区】查看本轮临时隔离列表。";
                return;
            }

            TxtStatus.Text = _allMods.Count == 0
                ? "尚未扫描到任何 Mod，请选择 Mods 目录后点击【开始全面扫描】。"
                : $"Mod 列表：共 {_allMods.Count} 个文件。";
        }

        private void BtnQuarantine_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath) || !Directory.Exists(_selectedModsPath))
            {
                MessageBox.Show("请先选择有效的 Mods 文件夹，再查看安全隔离区。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 诊断进行中也允许查看本轮临时隔离列表（还原/清空仍被禁用，避免打断排查）
            _currentView = MainViewMode.Quarantine;
            SwitchToCurrentView();
            RefreshQuarantineList(notifyWhenEmpty: !_isDiagnosisMode);
        }

        private void RefreshQuarantineList(bool notifyWhenEmpty)
        {
            if (string.IsNullOrEmpty(_selectedModsPath)) return;

            try
            {
                _quarantineFiles = _scannerService.GetQuarantineFiles(_selectedModsPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取隔离区内容失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateToolbarState();
            ApplyFilter();

            if (_isDiagnosisMode)
            {
                TxtStatus.Text = _quarantineFiles.Count == 0
                    ? $"诊断模式：当前为第 {_diagnosisEngine.CurrentStep} 轮临时隔离列表（暂无文件）。"
                    : $"诊断模式：当前为第 {_diagnosisEngine.CurrentStep} 轮临时隔离列表，共 {_quarantineFiles.Count} 个文件。还原/清空已暂时禁用。";
                return;
            }

            if (_quarantineFiles.Count == 0)
            {
                TxtStatus.Text = "安全隔离区：当前隔离区暂无文件。";

                if (notifyWhenEmpty)
                {
                    MessageBox.Show("当前隔离区暂无文件。", "安全隔离区", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                double totalMB = _quarantineFiles.Sum(f => f.FileSizeBytes) / (1024.0 * 1024.0);
                TxtStatus.Text = $"安全隔离区：共 {_quarantineFiles.Count} 个文件，{totalMB:F2} MB。可【一键还原】放回原路径，或【彻底清空】永久删除。";
            }
        }

        private void BtnRestoreQuarantine_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath)) return;

            if (_quarantineFiles.Count == 0)
            {
                MessageBox.Show("当前隔离区暂无文件。", "安全隔离区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"将把隔离区中的 {_quarantineFiles.Count} 个文件放回各自的原始路径（依据隔离区映射记录）。\n\n是否继续？",
                "确认还原",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirm != MessageBoxResult.Yes) return;

            int countBefore = _quarantineFiles.Count;
            _highlightQuarantineRelativePath = null;

            // 未能完全还原时，TryRestoreAllFiles 已负责弹出冲突明细
            bool fullyRestored = TryRestoreAllFiles();

            RefreshQuarantineList(notifyWhenEmpty: false);

            if (fullyRestored)
            {
                MessageBox.Show(
                    $"已成功还原 {countBefore} 个文件到原始路径。\n\n建议点击【开始全面扫描】刷新 Mod 列表。",
                    "还原完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                TxtStatus.Text = $"✅ 已还原 {countBefore} 个文件到原始路径，隔离区已清空。建议重新扫描以刷新列表。";
            }
        }

        private void BtnClearQuarantine_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath)) return;

            if (_quarantineFiles.Count == 0)
            {
                MessageBox.Show("当前隔离区暂无文件。", "安全隔离区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var firstConfirm = MessageBox.Show(
                "此操作将永久删除隔离区中的所有 Mod，是否继续？",
                "危险操作确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            );

            if (firstConfirm != MessageBoxResult.Yes) return;

            int fileCount = _quarantineFiles.Count;
            double totalMB = _quarantineFiles.Sum(f => f.FileSizeBytes) / (1024.0 * 1024.0);

            var secondConfirm = MessageBox.Show(
                $"最后确认：即将永久删除 {fileCount} 个文件（共 {totalMB:F2} MB），删除后无法通过本工具找回。\n\n" +
                $"如果你只是想让这些 Mod 回到游戏里，请改用【一键还原隔离区】。\n\n确定要永久删除吗？",
                "最后确认 - 不可撤销",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            );

            if (secondConfirm != MessageBoxResult.Yes) return;

            _highlightQuarantineRelativePath = null;

            try
            {
                _scannerService.ClearQuarantine(_selectedModsPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"清空隔离区时出错：{ex.Message}\n\n请先关闭游戏或占用这些文件的程序后重试。",
                    "清理失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                RefreshQuarantineList(notifyWhenEmpty: false);
                return;
            }

            RefreshQuarantineList(notifyWhenEmpty: false);

            MessageBox.Show($"已彻底清空隔离区，共删除 {fileCount} 个文件。", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"隔离区已彻底清空，共永久删除 {fileCount} 个文件。";
        }

        // ===================== 二分排查诊断流程 =====================

        // 侧边栏【冲突/失效诊断】：开启或重置一轮全新的二分排查
        private void BtnDiagnosis_Click(object sender, RoutedEventArgs e)
        {
            if (_allMods == null || !_allMods.Any() || string.IsNullOrEmpty(_selectedModsPath))
            {
                MessageBox.Show("请先选择文件夹并点击【开始全面扫描】，获取 Mod 列表后再开启诊断！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_allMods.Count < 2)
            {
                MessageBox.Show("当前目录只有 1 个 Mod 文件，无需二分排查，可直接移除该文件测试。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isDiagnosisMode)
            {
                var confirmRestart = MessageBox.Show(
                    "当前已处于诊断模式中。是否放弃已有进度，从全部 Mod 重新开始排查？",
                    "重新开始诊断",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (confirmRestart != MessageBoxResult.Yes) return;

                // 重新开始前必须先把上一轮隔离的文件放回原位
                if (!TryRestoreAllFiles()) return;
            }

            _diagnosisEngine.StartDiagnosis(_allMods);
            _isDiagnosisMode = true;

            // 诊断开始时切到 Mod 列表，便于对照诊断面板
            _currentView = MainViewMode.ModList;
            SetDiagnosisModeUI(true);
            SwitchToCurrentView();

            ApplyCurrentRound();
        }

        private void BtnProblemYes_Click(object sender, RoutedEventArgs e) => HandleDiagnosisFeedback(true);

        private void BtnProblemNo_Click(object sender, RoutedEventArgs e) => HandleDiagnosisFeedback(false);

        private void BtnExitDiagnosis_Click(object sender, RoutedEventArgs e)
        {
            // 中途取消：还原包括潜在 Culprit 在内的全部隔离文件
            if (_isDiagnosisMode && !TryRestoreAllFiles()) return;

            _isDiagnosisMode = false;
            _highlightQuarantineRelativePath = null;
            SetDiagnosisModeUI(false);

            if (_currentView == MainViewMode.Quarantine)
            {
                RefreshQuarantineList(notifyWhenEmpty: false);
            }

            TxtStatus.Text = "已退出诊断模式，隔离区中的文件均已恢复原位。";
        }

        private void HandleDiagnosisFeedback(bool isProblemPresent)
        {
            if (!_isDiagnosisMode || string.IsNullOrEmpty(_selectedModsPath)) return;

            // SubmitFeedback 仅收敛内存候选集，不触碰磁盘；据此再决定全量还原还是保留 Culprit
            _diagnosisEngine.SubmitFeedback(isProblemPresent);

            if (_diagnosisEngine.IsCompleted)
            {
                CompleteDiagnosisWithCulprit();
                return;
            }

            // 未完成：必须先全量还原，再隔离下一轮排除组
            if (!TryRestoreAllFiles()) return;

            ApplyCurrentRound();

            // 若用户正停留在隔离区视图，刷新为本轮新的临时隔离列表
            if (_currentView == MainViewMode.Quarantine)
            {
                RefreshQuarantineList(notifyWhenEmpty: false);
            }
        }

        // 诊断完成：无辜文件全部还原，Culprit 留在隔离区，并跳转到隔离区页面突出显示
        private void CompleteDiagnosisWithCulprit()
        {
            var culprit = _diagnosisEngine.CulpritMod;

            _isDiagnosisMode = false;
            SetDiagnosisModeUI(false);

            if (culprit == null)
            {
                if (!TryRestoreAllFiles()) return;

                _highlightQuarantineRelativePath = null;
                TxtStatus.Text = "诊断结束，但候选范围已被全部排除，未能锁定问题 Mod。";
                MessageBox.Show(
                    "本轮反馈把所有候选 Mod 都排除了，未能锁定目标。\n\n" +
                    "这通常说明问题不在 Mod 内（例如游戏本体、存档或缓存），或某一轮的反馈与实际情况不一致。\n" +
                    "可以清理游戏缓存后，重新点击【冲突/失效诊断】再排查一次。",
                    "诊断结束",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            try
            {
                var finalizeResult = _diagnosisEngine.FinalizeCulpritIsolation(_selectedModsPath!, culprit);

                if (!finalizeResult.IsFullyRestored)
                {
                    ReportIncompleteRestore(finalizeResult);
                    // Culprit 可能已入隔离区，仍继续跳转展示；无辜文件冲突需用户手动处理
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"锁定坏 Mod 并整理隔离区时出错：{ex.Message}\n\n请检查 Mods\\.Quarantine 目录后重试。",
                    "隔离失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }

            _highlightQuarantineRelativePath = culprit.RelativePath;
            _currentView = MainViewMode.Quarantine;
            SwitchToCurrentView();
            RefreshQuarantineList(notifyWhenEmpty: false);

            TxtStatus.Text = $"✅ 诊断完成：坏 Mod [{culprit.Name}] 已留在安全隔离区，其他文件已全部还原。";

            MessageBox.Show(
                $"诊断完成！已将坏 Mod [{culprit.Name}] 移入安全隔离区，其他文件已全部安全还原。\n\n" +
                $"相对路径：{culprit.RelativePath}\n" +
                $"智能分类：{culprit.CategoryDisplay}\n" +
                $"文件大小：{culprit.SizeDisplay}\n\n" +
                $"可在安全隔离区中选择【一键还原】放回，或【彻底清空】永久删除。",
                "诊断结果 - 已锁定目标",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        // 隔离当前轮次的排除组，并把轮次与数量同步到诊断提示栏
        private void ApplyCurrentRound()
        {
            var testGroup = _diagnosisEngine.GetCurrentTestGroup();
            var excludedGroup = _diagnosisEngine.GetExcludedGroup();

            try
            {
                _diagnosisEngine.IsolateFiles(_selectedModsPath!, excludedGroup);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"隔离文件时出错：{ex.Message}\n\n诊断已中止，请检查 Mods\\.Quarantine 目录后重试。",
                    "隔离失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                TryRestoreAllFiles();
                _isDiagnosisMode = false;
                SetDiagnosisModeUI(false);
                return;
            }

            TxtDiagRound.Text = $"🩺 第 {_diagnosisEngine.CurrentStep} 轮排查中";
            TxtDiagCounts.Text = $"保留测试组：{testGroup.Count} 个文件  |  暂时隔离组：{excludedGroup.Count} 个文件（已移入 .Quarantine）";
            TxtDiagGuide.Text = $"请启动游戏测试问题是否存在：若问题依然存在，说明元凶在保留的 {testGroup.Count} 个文件中；若问题消失，则在被隔离的 {excludedGroup.Count} 个文件中。";
            TxtStatus.Text = $"诊断模式 - 第 {_diagnosisEngine.CurrentStep} 轮：已隔离 {excludedGroup.Count} 个文件，等待你的游戏内测试反馈。";

            // 用户若正在查看隔离区，同步刷新为本轮临时隔离列表
            if (_currentView == MainViewMode.Quarantine)
            {
                RefreshQuarantineList(notifyWhenEmpty: false);
            }
        }

        private bool TryRestoreAllFiles()
        {
            if (string.IsNullOrEmpty(_selectedModsPath)) return true;

            try
            {
                var restoreResult = _diagnosisEngine.RestoreAllFiles(_selectedModsPath);

                if (!restoreResult.IsFullyRestored)
                {
                    ReportIncompleteRestore(restoreResult);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"恢复隔离文件时出错：{ex.Message}\n\n" +
                    $"请先关闭游戏或占用该文件的程序，然后手动检查 Mods\\.Quarantine 目录，确认文件已全部放回。",
                    "恢复失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return false;
            }
        }

        // 部分文件未能归位时必须让用户知情：这些文件仍安全地留在隔离区，没有被覆盖或删除
        private void ReportIncompleteRestore(QuarantineRestoreResult result)
        {
            var message = new StringBuilder();
            message.AppendLine($"已成功还原 {result.RestoredCount} 个文件，但有部分文件未能归位（它们仍完整保留在 Mods\\.Quarantine 中，未被覆盖或删除）：");

            if (result.SkippedConflicts.Count > 0)
            {
                message.AppendLine();
                message.AppendLine($"◆ 原位置已存在同名文件，为避免覆盖而跳过（{result.SkippedConflicts.Count} 个）：");
                foreach (var path in result.SkippedConflicts.Take(5))
                {
                    message.AppendLine($"   - {path}");
                }
                if (result.SkippedConflicts.Count > 5)
                {
                    message.AppendLine($"   ...另有 {result.SkippedConflicts.Count - 5} 个");
                }
            }

            if (result.Failures.Count > 0)
            {
                message.AppendLine();
                message.AppendLine($"◆ 移动失败（{result.Failures.Count} 个）：");
                foreach (var failure in result.Failures.Take(5))
                {
                    message.AppendLine($"   - {failure}");
                }
                if (result.Failures.Count > 5)
                {
                    message.AppendLine($"   ...另有 {result.Failures.Count - 5} 个");
                }
            }

            message.AppendLine();
            message.AppendLine("请先关闭游戏，处理掉上述冲突文件后，再次点击【退出诊断】即可完成还原。");

            TxtStatus.Text = $"⚠️ 还原未完成：{result.RestoredCount} 个已归位，{result.SkippedConflicts.Count + result.Failures.Count} 个仍在隔离区。";

            MessageBox.Show(message.ToString(), "还原未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 诊断进行中禁用扫描与修复入口，避免文件处于隔离状态时刷新列表导致状态错乱
        private void SetDiagnosisModeUI(bool isActive)
        {
            PanelDiagnosis.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            BtnSelectFolder.IsEnabled = !isActive;
            BtnStartScan.IsEnabled = !isActive;
            BtnFixScripts.IsEnabled = !isActive;
            BtnOrganize.IsEnabled = !isActive && _allMods.Count > 0;

            UpdateToolbarState();
        }

        // 关窗时若仍在诊断中，先把隔离区文件放回，避免用户 Mods 残留在 .Quarantine
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isDiagnosisMode && !string.IsNullOrEmpty(_selectedModsPath))
            {
                try
                {
                    var restoreResult = _diagnosisEngine.RestoreAllFiles(_selectedModsPath);

                    if (!restoreResult.IsFullyRestored)
                    {
                        MessageBox.Show(
                            $"退出前有 {restoreResult.SkippedConflicts.Count + restoreResult.Failures.Count} 个文件未能还原，" +
                            $"它们仍完整保留在 Mods\\.Quarantine 目录中（未被覆盖或删除）。\n\n" +
                            $"下次打开本工具并选择同一 Mods 目录后进入诊断，即可继续自动还原。",
                            "部分文件仍在隔离区",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"退出前恢复隔离文件失败：{ex.Message}\n\n请手动检查 Mods\\.Quarantine 目录，把其中的文件放回 Mods 目录。",
                        "恢复失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
            }

            base.OnClosing(e);
        }
    }

    public sealed class CategoryFilterOption
    {
        public CategoryFilterOption(string label, ModCategory? category)
        {
            Label = label;
            Category = category;
        }

        public string Label { get; }
        public ModCategory? Category { get; }
    }

    /// <summary>
    /// 左侧树筛选状态：路径前缀 + 可选 Category 约束
    /// （选中 _General 时必须同时要求 Category==General，避免错显 [帽子]/[服装]）。
    /// </summary>
    public sealed class FolderFilterState
    {
        public static FolderFilterState All { get; } = new(string.Empty, null);

        public FolderFilterState(string pathPrefix, ModCategory? categoryConstraint)
        {
            PathPrefix = pathPrefix ?? string.Empty;
            CategoryConstraint = categoryConstraint;
        }

        public string PathPrefix { get; }
        public ModCategory? CategoryConstraint { get; }
        public bool IsAll => string.IsNullOrEmpty(PathPrefix) && CategoryConstraint == null;

        public static FolderFilterState FromPath(string pathPrefix)
        {
            string prefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(prefix))
                return All;

            ModCategory? constraint = null;
            if (ModScannerService.TryGetCategoryConstraintFromFolderPath(prefix, out var cat))
                constraint = cat;

            return new FolderFilterState(prefix, constraint);
        }
    }

    /// <summary>卡片大图：从 ModFileItem 提取缩略图（高分辨率解码）。</summary>
    public class CardThumbnailConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ModFileItem item || !item.HasThumbnail)
                return null;

            byte[] bytes = item.ThumbnailData!;
            if (bytes.Length < 8) return null;
            if (bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S') return null;

            try
            {
                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 420;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public class CategoryBadgeBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ModCategory cat)
                return new SolidColorBrush(Color.FromRgb(0xA1, 0xA5, 0xB7));

            string hex = cat switch
            {
                ModCategory.CAS_Hair => "#3699FF",
                ModCategory.CAS_Hat => "#7239EA",
                ModCategory.CAS_Clothing => "#F1416C",
                ModCategory.CAS_Shoes => "#FFA800",
                ModCategory.CAS_Accessories => "#E14CCA",
                ModCategory.BuildBuy => "#0BB783",
                ModCategory.CoreMods => "#181C32",
                ModCategory.Script => "#50CD89",
                ModCategory.General => "#A1A5B7",
                _ => "#A1A5B7"
            };

            return (Brush)new BrushConverter().ConvertFromString(hex)!;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public class CategoryShortBadgeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ModCategory cat) return "[未知]";
            return cat switch
            {
                ModCategory.CAS_Hair => "[头发]",
                ModCategory.CAS_Hat => "[帽子]",
                ModCategory.CAS_Clothing => "[服装]",
                ModCategory.CAS_Shoes => "[鞋子]",
                ModCategory.CAS_Accessories => "[饰品]",
                ModCategory.BuildBuy => "[家具]",
                ModCategory.CoreMods => "[核心]",
                ModCategory.Script => "[脚本]",
                ModCategory.General => "[普通]",
                _ => "[未知]"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}