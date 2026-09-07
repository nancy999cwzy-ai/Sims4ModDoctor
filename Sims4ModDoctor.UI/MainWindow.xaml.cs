using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Sims4ModDoctor.Core;

namespace Sims4ModDoctor.UI
{
    public partial class MainWindow : Window
    {
        private string? _selectedModsPath;
        private readonly ModScannerService _scannerService = new();
        private ConflictDiagnosisEngine _diagnosisEngine = new();
        private List<ModFileItem> _allMods = new();
        private bool _isDiagnosisMode = false;

        public MainWindow()
        {
            InitializeComponent();
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
            double totalMB = _allMods.Sum(m => m.FileSizeBytes) / (1024.0 * 1024.0);

            ApplyFilter();

            if (invalidScriptCount > 0)
            {
                BtnFixScripts.Visibility = Visibility.Visible;
                TxtStatus.Text = $"扫描完成！共发现 {_allMods.Count} 个文件。 ⚠️ 警告：检测到 {invalidScriptCount} 个脚本文件嵌套层级 > 2，游戏内将无法生效！点击右上角按钮可自动修复。";
            }
            else
            {
                BtnFixScripts.Visibility = Visibility.Collapsed;
                TxtStatus.Text = $"扫描完成！共发现 {_allMods.Count} 个文件（.package: {packageCount} | .ts4script: {scriptCount}），总体积：{totalMB:F2} MB。所有脚本层级均正常。";
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

        private void ApplyFilter()
        {
            if (_allMods == null || !_allMods.Any()) return;

            string keyword = TxtSearch.Text.Trim().ToLower();

            if (string.IsNullOrEmpty(keyword))
            {
                GridModList.ItemsSource = null;
                GridModList.ItemsSource = _allMods;
            }
            else
            {
                var filtered = _allMods.Where(m => 
                    m.Name.ToLower().Contains(keyword) || 
                    m.RelativePath.ToLower().Contains(keyword)
                ).ToList();

                GridModList.ItemsSource = null;
                GridModList.ItemsSource = filtered;
            }
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
            SetDiagnosisModeUI(true);

            ApplyCurrentRound();
        }

        private void BtnProblemYes_Click(object sender, RoutedEventArgs e) => HandleDiagnosisFeedback(true);

        private void BtnProblemNo_Click(object sender, RoutedEventArgs e) => HandleDiagnosisFeedback(false);

        private void BtnExitDiagnosis_Click(object sender, RoutedEventArgs e)
        {
            if (_isDiagnosisMode && !TryRestoreAllFiles()) return;

            _isDiagnosisMode = false;
            SetDiagnosisModeUI(false);
            TxtStatus.Text = "已退出诊断模式，隔离区中的文件均已恢复原位。";
        }

        private void HandleDiagnosisFeedback(bool isProblemPresent)
        {
            if (!_isDiagnosisMode || string.IsNullOrEmpty(_selectedModsPath)) return;

            // 必须先还原再收敛候选集：引擎按文件名在当前候选集中反查原始路径
            if (!TryRestoreAllFiles()) return;

            _diagnosisEngine.SubmitFeedback(isProblemPresent);

            if (_diagnosisEngine.IsCompleted)
            {
                var culprit = _diagnosisEngine.CulpritMod;

                _isDiagnosisMode = false;
                SetDiagnosisModeUI(false);

                if (culprit != null)
                {
                    TxtStatus.Text = $"✅ 诊断完成，已锁定问题 Mod：{culprit.RelativePath}（所有文件已恢复原位）";
                    MessageBox.Show(
                        $"🎯 已锁定问题 Mod！\n\n" +
                        $"文件名：{culprit.Name}\n" +
                        $"相对路径：{culprit.RelativePath}\n" +
                        $"智能分类：{culprit.CategoryDisplay}\n" +
                        $"文件大小：{culprit.SizeDisplay}\n\n" +
                        $"所有隔离文件已恢复原位。建议先移除或更新该 Mod，再次进入游戏确认问题是否解决。",
                        "诊断结果 - 已锁定目标",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                else
                {
                    TxtStatus.Text = "诊断结束，但候选范围已被全部排除，未能锁定问题 Mod。";
                    MessageBox.Show(
                        "本轮反馈把所有候选 Mod 都排除了，未能锁定目标。\n\n" +
                        "这通常说明问题不在 Mod 内（例如游戏本体、存档或缓存），或某一轮的反馈与实际情况不一致。\n" +
                        "可以清理游戏缓存后，重新点击【冲突/失效诊断】再排查一次。",
                        "诊断结束",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }

                return;
            }

            ApplyCurrentRound();
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
}