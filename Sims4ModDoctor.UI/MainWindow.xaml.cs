using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Sims4ModDoctor.Core;

namespace Sims4ModDoctor.UI
{
    public partial class MainWindow : Window
    {
        private string? _selectedModsPath;
        private readonly ModScannerService _scannerService = new();
        private List<ModFileItem> _allMods = new();

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

            TxtStatus.Text = "⏳ 正在分析 Mods 结构与文件元数据...";

            _allMods = await _scannerService.ScanDirectoryAsync(_selectedModsPath);

            int packageCount = _allMods.Count(m => m.Type == ModFileType.Package);
            int scriptCount = _allMods.Count(m => m.Type == ModFileType.Script);
            double totalMB = _allMods.Sum(m => m.FileSizeBytes) / (1024.0 * 1024.0);

            // 渲染数据到 DataGrid
            ApplyFilter();

            TxtStatus.Text = $"扫描完成！共发现 {_allMods.Count} 个文件（.package: {packageCount} | .ts4script: {scriptCount}），总体积：{totalMB:F2} MB";
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
                GridModList.ItemsSource = _allMods;
            }
            else
            {
                var filtered = _allMods.Where(m => 
                    m.Name.ToLower().Contains(keyword) || 
                    m.RelativePath.ToLower().Contains(keyword)
                ).ToList();

                GridModList.ItemsSource = filtered;
            }
        }
    }
}