using System.Windows;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.Screenshot;

/// <summary>滚动停止检测器</summary>
public class ScrollDetector
{
    private const double SimilarityThreshold = 0.95; // 相似度阈值

    /// <summary>检测是否已滚动到底部（连续帧无变化）</summary>
    public bool IsAtBottom(BitmapSource previousFrame, BitmapSource currentFrame,
        EScrollDirection direction = EScrollDirection.Vertical)
    {
        if (previousFrame.PixelWidth != currentFrame.PixelWidth ||
            previousFrame.PixelHeight != currentFrame.PixelHeight)
            return false;

        var similarity = CalculateSimilarity(previousFrame, currentFrame);
        return similarity >= SimilarityThreshold;
    }

    private double CalculateSimilarity(BitmapSource a, BitmapSource b)
    {
        var stride = a.PixelWidth * 4;
        var pixelsA = new byte[stride * a.PixelHeight];
        var pixelsB = new byte[stride * b.PixelHeight];

        a.CopyPixels(pixelsA, stride, 0);
        b.CopyPixels(pixelsB, stride, 0);

        long totalDiff = 0;
        int sampleCount = 0;

        // 采样比较（每 8 像素取 1 个）
        for (int i = 0; i < pixelsA.Length; i += 32)
        {
            totalDiff += Math.Abs(pixelsA[i] - pixelsB[i]);
            totalDiff += Math.Abs(pixelsA[i + 1] - pixelsB[i + 1]);
            totalDiff += Math.Abs(pixelsA[i + 2] - pixelsB[i + 2]);
            sampleCount++;
        }

        if (sampleCount == 0) return 1.0;

        var avgDiff = (double)totalDiff / sampleCount / 3.0;
        return 1.0 - avgDiff / 255.0;
    }
}
