using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.Screenshot;

/// <summary>图像拼接器（纯 C#，无 OpenCV 依赖）</summary>
public static class ImageStitcher
{
    /// <summary>拼接多帧为一张长图</summary>
    public static BitmapSource Stitch(List<BitmapSource> frames, double overlapRatio = 0.15)
    {
        if (frames.Count == 0) throw new ArgumentException("No frames");
        if (frames.Count == 1) return frames[0];

        // 找到重叠边界
        var boundaries = new List<int>();
        for (int i = 1; i < frames.Count; i++)
        {
            var boundary = FindOverlapBoundary(frames[i - 1], frames[i], overlapRatio);
            boundaries.Add(boundary);
        }

        // 计算总高度
        var width = frames[0].PixelWidth;
        var totalHeight = frames[0].PixelHeight;

        for (int i = 0; i < boundaries.Count; i++)
        {
            var prevHeight = frames[i].PixelHeight;
            var overlap = prevHeight - boundaries[i];
            totalHeight += frames[i + 1].PixelHeight - overlap;
        }

        // 创建拼接图
        var result = new RenderTargetBitmap(width, totalHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();

        using (var ctx = visual.RenderOpen())
        {
            double yOffset = 0;
            ctx.DrawImage(frames[0], new Rect(0, 0, width, frames[0].PixelHeight));

            for (int i = 0; i < boundaries.Count; i++)
            {
                var overlap = frames[i].PixelHeight - boundaries[i];
                yOffset += frames[i].PixelHeight - overlap;

                // 使用 CroppedBitmap 裁剪掉重叠区域的顶部
                if (overlap > 0)
                {
                    var cropRect = new Int32Rect(0, overlap, frames[i + 1].PixelWidth,
                        frames[i + 1].PixelHeight - overlap);
                    var cropped = new CroppedBitmap(frames[i + 1], cropRect);
                    ctx.DrawImage(cropped, new Rect(0, yOffset, width, cropped.PixelHeight));
                }
                else
                {
                    ctx.DrawImage(frames[i + 1], new Rect(0, yOffset, width, frames[i + 1].PixelHeight));
                }
            }
        }

        result.Render(visual);
        result.Freeze();
        return result;
    }

    /// <summary>找到相邻帧的重叠位置（像素匹配）</summary>
    private static int FindOverlapBoundary(BitmapSource prev, BitmapSource next, double overlapRatio)
    {
        var searchHeight = (int)(prev.PixelHeight * 0.3); // 搜索前一帧底部 30%
        var minOverlap = (int)(prev.PixelHeight * overlapRatio * 0.5);

        var prevPixels = GetPixelRows(prev, prev.PixelHeight - searchHeight, searchHeight);
        var nextPixels = GetPixelRows(next, 0, searchHeight);

        var bestScore = double.MaxValue;
        var bestOffset = minOverlap;

        for (int offset = minOverlap; offset < searchHeight; offset += 2)
        {
            double score = 0;
            int samples = 0;

            for (int row = 0; row < Math.Min(searchHeight - offset, 50); row += 3)
            {
                var prevRow = prev.PixelHeight - searchHeight + offset + row;
                var nextRow = row;

                if (prevRow >= 0 && prevRow < prev.PixelHeight && nextRow < next.PixelHeight)
                {
                    score += CompareRows(prev, next, prevRow, nextRow);
                    samples++;
                }
            }

            if (samples > 0)
            {
                score /= samples;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                }
            }
        }

        return prev.PixelHeight - bestOffset;
    }

    private static double CompareRows(BitmapSource a, BitmapSource b, int rowA, int rowB)
    {
        var strideA = a.PixelWidth * 4;
        var strideB = b.PixelWidth * 4;
        var pixelsA = new byte[strideA];
        var pixelsB = new byte[strideB];

        a.CopyPixels(new Int32Rect(0, rowA, a.PixelWidth, 1), pixelsA, strideA, 0);
        b.CopyPixels(new Int32Rect(0, rowB, b.PixelWidth, 1), pixelsB, strideB, 0);

        double diff = 0;
        var width = Math.Min(a.PixelWidth, b.PixelWidth);
        for (int x = 0; x < width * 4; x += 4)
        {
            diff += Math.Abs(pixelsA[x] - pixelsB[x]);
            diff += Math.Abs(pixelsA[x + 1] - pixelsB[x + 1]);
            diff += Math.Abs(pixelsA[x + 2] - pixelsB[x + 2]);
        }
        return diff / width;
    }

    private static byte[] GetPixelRows(BitmapSource bitmap, int startY, int height)
    {
        var stride = bitmap.PixelWidth * 4;
        var data = new byte[stride * height];
        bitmap.CopyPixels(new Int32Rect(0, startY, bitmap.PixelWidth, height), data, stride, 0);
        return data;
    }
}
