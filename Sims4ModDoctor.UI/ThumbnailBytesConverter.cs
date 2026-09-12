using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sims4ModDoctor.UI
{
    /// <summary>
    /// 将 ModFileItem.ThumbnailData (byte[]) 转为 WPF ImageSource；无图时返回灰色占位。
    /// </summary>
    public class ThumbnailBytesConverter : IValueConverter
    {
        private static readonly ImageSource Placeholder = CreatePlaceholder();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is byte[] bytes && bytes.Length > 8)
            {
                try
                {
                    // DDS 等非 WPF 原生格式暂用占位图
                    if (bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S')
                        return Placeholder;

                    var image = new BitmapImage();
                    using var stream = new MemoryStream(bytes);
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 64;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch
                {
                    return Placeholder;
                }
            }

            return Placeholder;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static ImageSource CreatePlaceholder()
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF4)), null, new System.Windows.Rect(0, 0, 48, 48));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xDE)), null, new System.Windows.Rect(12, 12, 24, 24));
            }

            var bmp = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
