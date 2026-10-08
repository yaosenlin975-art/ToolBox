using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ToolBox.Views.NetworkDiagnosis;

/// <summary>诊断会话整体状态（PRD 5.3 状态机：Idle → Diagnosing → DiagnosisDone）。</summary>
public enum DiagnosisSessionStatus
{
    Idle,
    Diagnosing,
    Healthy,
    IssuesFound
}

/// <summary>检测项行级状态（PRD 5.4）。</summary>
public enum DiagnosisItemStatus
{
    Pending,
    Testing,
    Pass,
    Fail,
    Warning,
    Skipped
}

/// <summary>
/// 单个检测项的行数据。
/// Name/Summary/Details 为渲染时实时本地化（语言包切换后无需缓存刷新），
/// 因此仅允许在 UI 线程访问（FindResource 线程亲和）。
/// </summary>
public class DiagnosticItem
{
    /// <summary>检测项名称的语言包 key（PRD 8.4 错误码同理）。</summary>
    public string NameKey { get; set; } = string.Empty;
    public DiagnosisItemStatus Status { get; set; } = DiagnosisItemStatus.Pending;
    /// <summary>检测原始信息（适配器名/网关 IP/DNS 服务器等），用于拼装三段式详情。</summary>
    public string DetailInfo { get; set; } = string.Empty;
    /// <summary>错误码（如 NET-101），无则为 null。</summary>
    public string? ErrorCode { get; set; }
    /// <summary>是否可自动修复（T2 修复器注册表驱动）。</summary>
    public bool CanRepair { get; set; }
    public bool IsExpanded { get; set; }

    /// <summary>检测项名称（渲染时取当前语言包）。</summary>
    public string Name => Localize(NameKey);

    /// <summary>行右侧状态说明（辅助 11 号）。</summary>
    public string Summary => StatusText(Status);

    /// <summary>展开面板详情行（自然语言，禁止命令原文，PRD 5.6）。</summary>
    public List<string> Details => DetailLines(Status, DetailInfo);

    private static string Localize(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return (Application.Current?.FindResource(key) as string) ?? key;
    }

    private static string StatusText(DiagnosisItemStatus status)
    {
        return status switch
        {
            DiagnosisItemStatus.Pass => Localize("Lang_Net_StatusPass"),
            DiagnosisItemStatus.Fail => Localize("Lang_Net_StatusFail"),
            DiagnosisItemStatus.Warning => Localize("Lang_Net_StatusWarning"),
            DiagnosisItemStatus.Skipped => Localize("Lang_Net_StatusSkipped"),
            DiagnosisItemStatus.Testing => Localize("Lang_Net_Testing"),
            _ => Localize("Lang_Net_StatusPending")
        };
    }

    private static List<string> DetailLines(DiagnosisItemStatus status, string detail)
    {
        return status switch
        {
            DiagnosisItemStatus.Pass => new List<string> { Localize("Lang_Net_DetailPass") },
            DiagnosisItemStatus.Warning => new List<string>
            {
                string.IsNullOrEmpty(detail)
                    ? Localize("Lang_Net_DetailNoInfo")
                    : string.Format(Localize("Lang_Net_DetailWarnReason"), detail),
                Localize("Lang_Net_DetailWarnImpact"),
                Localize("Lang_Net_DetailWarnAdvice")
            },
            DiagnosisItemStatus.Fail => new List<string>
            {
                string.IsNullOrEmpty(detail)
                    ? Localize("Lang_Net_DetailNoInfo")
                    : string.Format(Localize("Lang_Net_DetailFailReason"), detail),
                Localize("Lang_Net_DetailFailImpact"),
                Localize("Lang_Net_DetailFailAdvice")
            },
            _ => new List<string> { Localize("Lang_Net_StatusPending") }
        };
    }
}

/// <summary>
/// 外壳演示诊断运行器（T3 占位）。
/// 使用系统级 .NET API 做轻量真实探测，验证界面状态机/进度渲染/双主题/语言包；
/// T1 落地后由 Checker 插件式注册表整体替换（PRD 8.5）。
/// 本类不访问 UI 资源（线程安全），文案本地化由 DiagnosticItem 在 UI 线程渲染时完成。
/// </summary>
public static class DemoDiagnosisRunner
{
    /// <summary>外网探测目标（PRD 8.3 默认 baidu.com；可配置/关闭由 T4 落地）。</summary>
    private const string InternetProbeHost = "baidu.com";

    /// <summary>检测项顺序与 L1/L2 层级（PRD 5.6 严重度排序：适配器 &gt; IP &gt; DNS &gt; 代理 &gt; 外网）。</summary>
    private enum CheckSlot
    {
        Adapters = 0,
        Gateway = 1,
        Dns = 2,
        Internet = 3,
        DnsConfig = 4,
        Proxy = 5
    }

    public static async Task<List<DiagnosticItem>> RunAsync(IProgress<DiagnosticItem> progress, CancellationToken ct)
    {
        var items = BuildItems();
        void Report(int index) => progress?.Report(items[index]);

        // L1: 连通性检测
        var adapters = GetActiveAdapters();
        Complete(items[(int)CheckSlot.Adapters],
            adapters.Count > 0 ? DiagnosisItemStatus.Pass : DiagnosisItemStatus.Fail,
            adapters.Count > 0 ? adapters[0].Name : string.Empty,
            netCode: "NET-001");
        Report((int)CheckSlot.Adapters);
        await DelayAsync(ct);

        var adapter = adapters.FirstOrDefault();
        var gateway = adapter?.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address;
        Complete(items[(int)CheckSlot.Gateway],
            gateway != null ? DiagnosisItemStatus.Pass : DiagnosisItemStatus.Fail,
            gateway?.ToString() ?? string.Empty,
            netCode: "NET-101");
        Report((int)CheckSlot.Gateway);
        await DelayAsync(ct);

        var dns = await TryResolveAsync(InternetProbeHost, ct);
        Complete(items[(int)CheckSlot.Dns],
            dns.ok ? DiagnosisItemStatus.Pass : DiagnosisItemStatus.Fail,
            dns.info,
            netCode: "NET-201");
        Report((int)CheckSlot.Dns);
        await DelayAsync(ct);

        var internet = dns.ok && await TryConnectAsync(InternetProbeHost, 80, ct);
        Complete(items[(int)CheckSlot.Internet],
            internet ? DiagnosisItemStatus.Pass : DiagnosisItemStatus.Warning,
            internet ? InternetProbeHost : string.Empty,
            netCode: "NET-301");
        Report((int)CheckSlot.Internet);
        await DelayAsync(ct);

        // L2: 配置检测
        var dnsServers = adapter?.GetIPProperties().DnsAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList() ?? new List<IPAddress>();
        Complete(items[(int)CheckSlot.DnsConfig],
            dnsServers.Count > 0 ? DiagnosisItemStatus.Pass : DiagnosisItemStatus.Fail,
            dnsServers.Count > 0 ? string.Join(" ", dnsServers) : string.Empty,
            netCode: "NET-202");
        Report((int)CheckSlot.DnsConfig);
        await DelayAsync(ct);

        // 代理开启不是故障：未探测可达性就报 Warning，会连带生成「关闭系统代理」修复项，
        // 公司网/VPN 用户点一次就断网（PRD AC-05 也禁止自动修改代理）。这里只作信息展示。
        var proxy = CheckProxyEnabled();
        Complete(items[(int)CheckSlot.Proxy],
            DiagnosisItemStatus.Pass,
            proxy.enabled ? proxy.info : string.Empty,
            netCode: "NET-401");
        Report((int)CheckSlot.Proxy);

        return items;
    }

    private static List<DiagnosticItem> BuildItems()
    {
        // CanRepair：T2 修复器按错误码匹配修复动作（NET-001/101/201/202/301/401 均有对应修复步骤）
        return new List<DiagnosticItem>
        {
            new() { NameKey = "Lang_Net_Check_Adapters", CanRepair = true },
            new() { NameKey = "Lang_Net_Check_Gateway", CanRepair = true },
            new() { NameKey = "Lang_Net_Check_Dns", CanRepair = true },
            new() { NameKey = "Lang_Net_Check_Internet", CanRepair = true },
            new() { NameKey = "Lang_Net_Check_DnsConfig", CanRepair = true },
            new() { NameKey = "Lang_Net_Check_Proxy", CanRepair = true }
        };
    }

    private static async Task DelayAsync(CancellationToken ct)
    {
        // 单探测 ≤ 3s，展示增量渲染节奏
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(250, 650)), ct);
    }

    private static void Complete(DiagnosticItem item, DiagnosisItemStatus status, string detail, string? netCode = null)
    {
        item.Status = status;
        item.DetailInfo = detail;
        item.ErrorCode = status is DiagnosisItemStatus.Fail or DiagnosisItemStatus.Warning ? netCode : null;
    }

    private static List<NetworkInterface> GetActiveAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
            .ToList();
    }

    private static async Task<(bool ok, string info)> TryResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
            return (addresses.Length > 0, addresses.Length > 0 ? addresses[0].ToString() : string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static async Task<bool> TryConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (bool enabled, string info) CheckProxyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable") is int v && v == 1;
            return (enabled, enabled ? "HTTP" : string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}
