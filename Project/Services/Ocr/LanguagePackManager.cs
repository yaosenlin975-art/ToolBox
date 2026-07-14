// File: LanguagePackManager.cs
// Tesseract 语言包(traineddata)下载与校验管理。
// 语言包存放于 %LocalAppData%/Setuna/ocr/tessdata/，与 CachePath 同根(Setuna 目录)。
// 数据源: tesseract-ocr/tessdata_fast (体积更小，适合桌面工具按需下载)。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ToolBox.Services.Ocr;

public class LanguagePackManager
{
    private const string DownloadBaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";

    /// <summary>tessdata 目录(包含 *.traineddata)。与截图缓存同根，便于统一清理。</summary>
    public static readonly string TessDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Setuna", "ocr", "tessdata");

    /// <summary>Engine 构造所需的 dataPath(tessdata 的父目录)。</summary>
    public static readonly string EngineDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Setuna", "ocr");

    public static LanguagePackManager Instance { get; } = new();

    private readonly HttpClient httpClient;

    private LanguagePackManager()
    {
        httpClient = new HttpClient(new HttpClientHandler
        {
            // GitHub raw 走 HTTPS，超时放宽以适应大语言包(chi_sim ~5MB)
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>指定语言包是否已安装。"chi_sim+eng" 拆分后逐一检查。</summary>
    public bool IsInstalled(string languageSpec)
    {
        if (string.IsNullOrWhiteSpace(languageSpec)) return false;
        foreach (var lang in ParseLanguages(languageSpec))
        {
            if (!File.Exists(Path.Combine(TessDataPath, lang + ".traineddata")))
                return false;
        }
        return true;
    }

    /// <summary>列出已安装的语言代码。</summary>
    public List<string> ListInstalled()
    {
        var list = new List<string>();
        if (!Directory.Exists(TessDataPath)) return list;
        foreach (var f in Directory.EnumerateFiles(TessDataPath, "*.traineddata"))
            list.Add(Path.GetFileNameWithoutExtension(f));
        return list;
    }

    /// <summary>
    /// 确保指定语言包全部就绪。缺失的逐一下载，通过 progress 回调报告百分比。
    /// 返回是否最终全部就绪(失败时返回 false 但不抛出，调用方可回退)。
    /// </summary>
    public async Task<bool> EnsureAsync(string languageSpec, IProgress<(int percent, string status)>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TessDataPath);
        var langs = ParseLanguages(languageSpec);
        for (int i = 0; i < langs.Count; i++)
        {
            var lang = langs[i];
            var target = Path.Combine(TessDataPath, lang + ".traineddata");
            if (File.Exists(target) && new FileInfo(target).Length > 1024)
            {
                Report(progress, OverallPercent(i, langs.Count, 100), "已安装: " + lang);
                continue;
            }

            Report(progress, OverallPercent(i, langs.Count, 0), "下载中: " + lang);
            var ok = await DownloadAsync(lang, target, p =>
                Report(progress, OverallPercent(i, langs.Count, p), "下载中: " + lang + " " + p + "%"), ct);
            if (!ok) return false;
            Report(progress, OverallPercent(i + 1, langs.Count, 100), "已安装: " + lang);
        }
        return true;
    }

    /// <summary>下载单个语言包。带进度回调与基本完整性校验(非空且 >1KB)。</summary>
    private async Task<bool> DownloadAsync(string lang, string target, Action<int>? progress, CancellationToken ct)
    {
        try
        {
            var url = DownloadBaseUrl + lang + ".traineddata";
            using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var total = resp.Content.Headers.ContentLength ?? -1L;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = File.Create(target + ".tmp");
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && progress != null)
                    progress((int)(read * 100 / total));
            }
            await dst.FlushAsync(ct);
            dst.Close();

            // 完整性校验: 文件须明显非空(traineddata 至少数十 KB)。完整 SHA256 校验需已知基准值，此处以体积兜底。
            var info = new FileInfo(target + ".tmp");
            if (info.Length < 1024)
            {
                TryDelete(target + ".tmp");
                return false;
            }
            File.Move(target + ".tmp", target, overwrite: true);
            progress?.Invoke(100);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ToolBox] OCR 语言包下载失败 " + lang + ": " + ex.Message);
            TryDelete(target + ".tmp");
            return false;
        }
    }

    /// <summary>"chi_sim+eng" → ["chi_sim","eng"]。兼容 "+" 与空格分隔。</summary>
    private static List<string> ParseLanguages(string spec)
    {
        var parts = spec.Split(new[] { '+', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>(parts.Length);
        foreach (var p in parts) if (p.Length > 0) list.Add(p);
        return list;
    }

    private static int OverallPercent(int index, int count, int current)
    {
        if (count <= 0) return current;
        return (index * 100 + current) / count;
    }

    private static void Report(IProgress<(int, string)>? p, int percent, string status)
        => p?.Report((percent, status));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
