using System.IO;
using System.Linq;
using System.Windows;
using Sims4ModDoctor.Core;

namespace Sims4ModDoctor.UI
{
    public partial class MainWindow : Window
    {
        private string? _selectedModsPath;
        private readonly ModScannerService _scannerService = new();

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

            TxtStatus.Text = "⏳ 正在分析 Mods 结构与文件元数据，请稍候...";

            // 调用 Core 层的异步扫描服务
            var modList = await _scannerService.ScanDirectoryAsync(_selectedModsPath);

            int packageCount = modList.Count(m => m.Type == ModFileType.Package);
            int scriptCount = modList.Count(m => m.Type == ModFileType.Script);
            double totalMB = modList.Sum(m => m.FileSizeBytes) / (1024.0 * 1024.0);

            TxtStatus.Text = $"扫描完成！分析文件：{modList.Count} 个 | 总体积：{totalMB:F2} MB";

            MessageBox.Show(
                $"诊断完毕！\n" +
                $"• 扫描文件总量: {modList.Count} 个\n" +
                $"• .package 架构文件: {packageCount} 个\n" +
                $"• .ts4script 脚本文件: {scriptCount} 个\n" +
                $"• Mods 占用体积: {totalMB:F2} MB", 
                "Mod Doctor 诊断报告", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
        }

        private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // 预留搜索事件
        }
    }
}