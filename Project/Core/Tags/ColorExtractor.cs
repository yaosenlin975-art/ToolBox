using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.Tags;

/// <summary>简化的 K-Means 颜色提取器（纯 C#，无 OpenCV 依赖）</summary>
public static class ColorExtractor
{
    /// <summary>提取图片主色调（返回 ARGB 值列表，最多 5 个）</summary>
    public static List<int> ExtractDominantColors(BitmapSource image, int colorCount = 3)
    {
        if (image == null) return new();

        try
        {
            // 缩小图片以加速处理
            var scaled = new TransformedBitmap(image, new ScaleTransform(
                Math.Min(1.0, 100.0 / image.PixelWidth),
                Math.Min(1.0, 100.0 / image.PixelHeight)));
            scaled.Freeze();

            var pixels = GetPixels(scaled);
            if (pixels.Count == 0) return new();

            return KMeansClustering(pixels, colorCount);
        }
        catch { return new(); }
    }

    private static List<(int r, int g, int b)> GetPixels(BitmapSource bitmap)
    {
        var pixels = new List<(int r, int g, int b)>();
        var stride = bitmap.PixelWidth * 4;
        var data = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(data, stride, 0);

        // 采样（每 4 像素取 1 个）
        for (int i = 0; i < data.Length; i += 16)
        {
            var b = data[i]; var g = data[i + 1]; var r = data[i + 2];
            // 跳过纯白/纯黑/半透明
            if (r + g + b > 700 || r + g + b < 30) continue;
            pixels.Add((r, g, b));
        }
        return pixels;
    }

    private static List<int> KMeansClustering(List<(int r, int g, int b)> pixels, int k)
    {
        if (pixels.Count < k) return pixels.Select(p => (0xFF << 24) | (p.r << 16) | (p.g << 8) | p.b).ToList();

        var random = new Random(42);
        var centroids = new List<(int r, int g, int b)>();
        var used = new HashSet<int>();

        // K-Means++ 初始化
        var idx = random.Next(pixels.Count);
        centroids.Add(pixels[idx]);
        used.Add(idx);

        for (int c = 1; c < k; c++)
        {
            var distances = pixels.Select((p, i) =>
            {
                if (used.Contains(i)) return (double.MaxValue, i);
                var minDist = centroids.Min(ct => Distance(p, ct));
                return (minDist * minDist, i);
            }).ToList();

            var totalDist = distances.Sum(d => d.Item1 == double.MaxValue ? 0 : d.Item1);
            if (totalDist == 0) break;

            var target = random.NextDouble() * totalDist;
            var cumSum = 0.0;
            foreach (var (dist, i) in distances)
            {
                cumSum += dist;
                if (cumSum >= target && !used.Contains(i))
                {
                    centroids.Add(pixels[i]);
                    used.Add(i);
                    break;
                }
            }
        }

        // 迭代
        for (int iter = 0; iter < 10; iter++)
        {
            var clusters = new List<List<(int r, int g, int b)>>();
            for (int c = 0; c < centroids.Count; c++) clusters.Add(new());

            foreach (var pixel in pixels)
            {
                var minDist = double.MaxValue;
                var minIdx = 0;
                for (int c = 0; c < centroids.Count; c++)
                {
                    var d = Distance(pixel, centroids[c]);
                    if (d < minDist) { minDist = d; minIdx = c; }
                }
                clusters[minIdx].Add(pixel);
            }

            bool changed = false;
            for (int c = 0; c < centroids.Count; c++)
            {
                if (clusters[c].Count == 0) continue;
                var newC = (
                    (int)clusters[c].Average(p => p.r),
                    (int)clusters[c].Average(p => p.g),
                    (int)clusters[c].Average(p => p.b));
                if (newC != centroids[c]) { centroids[c] = newC; changed = true; }
            }
            if (!changed) break;
        }

        return centroids.Select(c => (0xFF << 24) | (c.r << 16) | (c.g << 8) | c.b).ToList();
    }

    private static double Distance((int r, int g, int b) a, (int r, int g, int b) b)
    {
        var dr = a.r - b.r; var dg = a.g - b.g; var db = a.b - b.b;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
