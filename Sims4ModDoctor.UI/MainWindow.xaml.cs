using System.IO;
using System.Windows;

namespace Sims4ModDoctor.UI
{
    public partial class MainWindow : Window
    {
        private string? _selectedModsPath;

        public MainWindow()
        {
            InitializeComponent();
        }

        // 选择文件夹点击逻辑
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

        // 开始扫描点击逻辑
        private void BtnStartScan_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedModsPath) || !Directory.Exists(_selectedModsPath))
            {
                MessageBox.Show("请先选择有效的 Mods 文件夹！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TxtStatus.Text = "正在后台扫描 Mods 文件...";
            
            // 简单的同级别文件计数
            string[] files = Directory.GetFiles(_selectedModsPath, "*.*", SearchOption.AllDirectories);
            int packageCount = 0;
            int scriptCount = 0;

            foreach (var file in files)
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext == ".package") packageCount++;
                else if (ext == ".ts4script") scriptCount++;
            }

            TxtStatus.Text = $"扫描完成！共发现 {files.Length} 个文件（.package: {packageCount} | .ts4script: {scriptCount}）";
            MessageBox.Show($"扫描成功！\n共扫描 {files.Length} 个文件。\n.package: {packageCount}\n.ts4script: {scriptCount}", "诊断报告", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}