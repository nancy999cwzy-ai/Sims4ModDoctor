using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sims4ModDoctor.Spike
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Sims 4 Mod Doctor - 技术验证程序 (Spike) ===");
            Console.Write("请输入你的测试 Mods 文件夹路径: ");
            string? modsPath = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
            {
                Console.WriteLine("❌ 路径不存在或为空，程序退出。");
                return;
            }

            string[] files = Directory.GetFiles(modsPath, "*.*", SearchOption.AllDirectories);
            Console.WriteLine($"\n找到 {files.Length} 个文件，开始测试读取性能...\n");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int packageCount = 0;
            int scriptCount = 0;

            foreach (var filePath in files)
            {
                string ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".package")
                {
                    TestPackageHeader(filePath);
                    packageCount++;
                }
                else if (ext == ".ts4script")
                {
                    TestScriptZip(filePath);
                    scriptCount++;
                }
            }

            watch.Stop();
            Console.WriteLine("\n==========================================");
            Console.WriteLine($"✅ 验证完成！耗时: {watch.ElapsedMilliseconds} ms");
            Console.WriteLine($"📄 .package 文件数: {packageCount}");
            Console.WriteLine($"📦 .ts4script 文件数: {scriptCount}");
            Console.WriteLine("==========================================");
        }

        // 验证 1: 读取 DBPF 格式文件头 Header
        static void TestPackageHeader(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                if (fs.Length < 4) return;

                byte[] headerBytes = reader.ReadBytes(4);
                string magic = Encoding.ASCII.GetString(headerBytes);

                if (magic == "DBPF")
                {
                    Console.WriteLine($"[Package 正常] {Path.GetFileName(filePath)} (Magic: DBPF)");
                }
                else
                {
                    Console.WriteLine($"[Package 异常] {Path.GetFileName(filePath)} (未知 Header: {magic})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Package 读取失败] {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        // 验证 2: 读取 Zip 压缩包内部结构 (.ts4script)
        static void TestScriptZip(string filePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(filePath);
                Console.WriteLine($"[Script 包识别] {Path.GetFileName(filePath)} (内部包含 {archive.Entries.Count} 个文件)");
                
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".py") || entry.FullName.EndsWith(".pyo") || entry.FullName.EndsWith(".pyc"))
                    {
                        Console.WriteLine($"   └── 包含模块: {entry.FullName}");
                        break; 
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Script 读取失败] {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }
    }
}