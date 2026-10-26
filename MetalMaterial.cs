using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProcessPanel;

// 金属材质辅助：微表面噪声（程序生成，一次性缓存，无外部图片依赖）
internal static class MetalMaterial
{
    private static ImageBrush? _noise;

    /// <summary>
    /// 64×64 稀疏灰度噪点，平铺使用。配合极低不透明度（0.03）只消除"过于完美"的塑料感，
    /// 不产生可见颗粒。生成一次后冻结复用。
    /// </summary>
    public static ImageBrush NoiseBrush
    {
        get
        {
            if (_noise != null) return _noise;

            const int size = 64;
            const int stride = size * 4;
            var pixels = new byte[stride * size];
            var rnd = new Random(20261023);

            for (int i = 0; i < size * size; i++)
            {
                byte v = (byte)rnd.Next(150, 256);
                // 约 30% 的像素带极低 alpha，形成稀疏细噪点
                byte a = rnd.Next(0, 100) < 30 ? (byte)90 : (byte)0;
                int o = i * 4;
                pixels[o + 0] = v;   // B
                pixels[o + 1] = v;   // G
                pixels[o + 2] = v;   // R
                pixels[o + 3] = a;   // A
            }

            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
            bmp.Freeze();

            var brush = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size),
                Stretch = Stretch.None,
            };
            brush.Freeze();
            _noise = brush;
            return _noise;
        }
    }
}
