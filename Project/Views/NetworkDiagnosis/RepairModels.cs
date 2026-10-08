using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ToolBox.Views.NetworkDiagnosis;

/// <summary>修复步骤行级状态（修复区列表 + 日志区共用）。</summary>
public enum RepairStepStatus
{
    Pending,
    Running,
    Pass,
    Fail,
    Skipped
}

/// <summary>一条待执行命令。</summary>
public sealed record RepairCommand(string File, string Args);

/// <summary>
/// 单个修复步骤（T2 修复器）。
/// Title 为渲染时实时本地化（FindResource 线程亲和），仅允许在 UI 线程访问。
/// </summary>
public sealed class RepairStep
{
    /// <summary>步骤标识（bat 标记前缀 / 逻辑去重用）。</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>步骤名称的语言包 key。</summary>
    public string TitleKey { get; init; } = string.Empty;
    /// <summary>步骤名称格式化参数（如适配器名）。</summary>
    public object[] TitleArgs { get; init; } = [];
    /// <summary>触发本步骤的错误码集合（NET-xxx）。</summary>
    public string[] ErrorCodes { get; set; } = [];
    /// <summary>是否需要在提权（UAC）进程内执行。</summary>
    public bool RequiresElevation { get; init; }
    /// <summary>按序执行的命令。</summary>
    public IReadOnlyList<RepairCommand> Commands { get; init; } = [];

    public RepairStepStatus Status { get; set; } = RepairStepStatus.Pending;

    /// <summary>步骤名称（渲染时取当前语言包）。</summary>
    public string Title
    {
        get
        {
            var text = (System.Windows.Application.Current?.FindResource(TitleKey) as string) ?? TitleKey;
            return TitleArgs.Length > 0 ? string.Format(text, TitleArgs) : text;
        }
    }
}

/// <summary>一次修复执行的汇总结果。</summary>
public sealed record RepairSummary(int Succeeded, int Failed, int Skipped, bool ElevationCancelled, bool ElevationFailed);

/// <summary>
/// 网络修复服务（PRD 5.5 / T2 修复器）。
/// 根据诊断失败项的 NET 错误码匹配修复动作（BuildPlan），
/// 提权方案：权限预检 + 单次批处理提权（所有需提权动作打包进一个 runas 子进程，全程仅一次 UAC）；
/// UAC 取消 / 提权失败不静默——相关步骤标记 Skipped 由 UI 给出明确提示。
/// 本类不访问 UI 资源（线程安全）。
/// </summary>
public static class NetworkRepairService
{
    private const int ErrorCanceled = 1223; // ERROR_CANCELLED：用户取消 UAC

    /// <summary>单条命令执行超时（网络栈异常时 ipconfig/netsh 可能长时间挂起，超时后终止进程，避免修复区无限"执行中"）。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>提权 bat 子进程整体执行超时。</summary>
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromSeconds(90);

    /// <summary>bat 脚本编码：系统 OEM 代码页（cmd 原生解析中文适配器名）。</summary>
    private static readonly Encoding BatEncoding = CreateBatEncoding();

    private static readonly Regex MarkerRegex = new(@"^([A-Za-z0-9_]+)_(\d+)=(-?\d+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// 按诊断结果中的失败/警告项错误码匹配修复动作（PRD 5.5 修复项清单 + CSH-10 场景）：
    /// NET-201/NET-202 → 刷新 DNS 缓存（免提权，无需管理员权限）；NET-401 → 不自动修改代理，只给引导（PRD R3/AC-05）；
    /// NET-001 → 重置网络接口配置；NET-101 → 重启对应网络适配器；NET-301 → 重置 Winsock。
    /// </summary>
    public static List<RepairStep> BuildPlan(IEnumerable<DiagnosticItem> items)
    {
        var codes = items
            .Where(i => i.Status is DiagnosisItemStatus.Fail or DiagnosisItemStatus.Warning)
            .Select(i => i.ErrorCode)
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>()
            .ToHashSet();
        if (codes.Count == 0) return [];

        var plan = new List<RepairStep>();

        if (codes.Contains("NET-201") || codes.Contains("NET-202"))
        {
            plan.Add(new RepairStep
            {
                Id = "dns",
                TitleKey = "Lang_Net_RepairDns",
                ErrorCodes = ["NET-201", "NET-202"],
                Commands = [new RepairCommand("ipconfig", "/flushdns")]
            });
        }

        // NET-401(系统代理) 不生成修复步骤：PRD AC-05 明确「不自动修改代理设置」，
        // 公司网/VPN 依赖代理，静默关闭等于把用户的网络切断。
        if (codes.Contains("NET-001"))
            AddInterfaceResetStep(plan, "NET-001");

        if (codes.Contains("NET-101"))
        {
            var gateway = items.FirstOrDefault(i => i.ErrorCode == "NET-101")?.DetailInfo ?? string.Empty;
            var adapterName = FindAdapterNameByGateway(gateway);
            if (adapterName != null && CanEncodeOem(adapterName))
            {
                plan.Add(new RepairStep
                {
                    Id = "adapter",
                    TitleKey = "Lang_Net_RepairAdapter",
                    TitleArgs = [adapterName],
                    ErrorCodes = ["NET-101"],
                    RequiresElevation = true,
                    Commands =
                    [
                        new RepairCommand("netsh", $"interface set interface \"{adapterName}\" disable"),
                        new RepairCommand("netsh", $"interface set interface \"{adapterName}\" enable")
                    ]
                });
            }
            else
            {
                AddInterfaceResetStep(plan, "NET-101");
            }
        }

        if (codes.Contains("NET-301"))
        {
            plan.Add(new RepairStep
            {
                Id = "winsock",
                TitleKey = "Lang_Net_RepairWinsock",
                ErrorCodes = ["NET-301"],
                RequiresElevation = true,
                Commands = [new RepairCommand("netsh", "winsock reset")]
            });
        }

        return plan;
    }

    private static void AddInterfaceResetStep(List<RepairStep> plan, string code)
    {
        var existing = plan.FirstOrDefault(s => s.Id == "interface");
        if (existing != null)
        {
            existing.ErrorCodes = existing.ErrorCodes.Append(code).ToArray();
            return;
        }
        plan.Add(new RepairStep
        {
            Id = "interface",
            TitleKey = "Lang_Net_RepairInterface",
            ErrorCodes = [code],
            RequiresElevation = true,
            Commands = [new RepairCommand("netsh", "interface ip reset")]
        });
    }

    /// <summary>
    /// 执行修复计划：无需提权的步骤直接运行并逐项上报；
    /// 需提权的步骤收集后进入单次批处理提权（已提权进程则直接运行）。
    /// </summary>
    public static async Task<RepairSummary> ExecuteAsync(
        IReadOnlyList<RepairStep> steps, IProgress<RepairStep> progress, CancellationToken ct)
    {
        var elevated = new List<RepairStep>();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var step in steps)
        {
            if (step.RequiresElevation)
            {
                elevated.Add(step);
                continue;
            }
            step.Status = RepairStepStatus.Running;
            progress.Report(step);
            try
            {
                var ok = await RunShellAsync(step, ct);
                if (ok)
                {
                    step.Status = RepairStepStatus.Pass;
                    succeeded++;
                }
                else
                {
                    step.Status = RepairStepStatus.Fail;
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                step.Status = RepairStepStatus.Fail;
                failed++;
            }
            progress.Report(step);
        }

        if (elevated.Count > 0)
        {
            foreach (var step in elevated)
            {
                step.Status = RepairStepStatus.Running;
                progress.Report(step);
            }
            var (cancelled, elevationFailed) = await RunElevatedAsync(elevated, ct);
            foreach (var step in elevated)
            {
                switch (step.Status)
                {
                    case RepairStepStatus.Pass: succeeded++; break;
                    case RepairStepStatus.Skipped: skipped++; break;
                    default: failed++; break;
                }
                progress.Report(step);
            }
            return new RepairSummary(succeeded, failed, skipped, cancelled, elevationFailed);
        }

        return new RepairSummary(succeeded, failed, skipped, false, false);
    }

    private static async Task<bool> RunShellAsync(RepairStep step, CancellationToken ct)
    {
        foreach (var command in step.Commands)
        {
            var exit = await RunProcessAsync(command.File, command.Args, ct);
            if (exit != 0) return false;
        }
        return true;
    }

    /// <summary>普通进程执行（无提权），捕获输出避免死锁，返回退出码；超时（CommandTimeout）则终止进程并返回 -1。</summary>
    private static async Task<int> RunProcessAsync(string file, string args, CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var exitTask = p.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(CommandTimeout, ct));
        if (!ReferenceEquals(completed, exitTask))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try { await exitTask; } catch { /* 进程已终止 */ }
            _ = stdout.ContinueWith(_ => { });
            _ = stderr.ContinueWith(_ => { });
            return -1;
        }
        await Task.WhenAll(stdout, stderr);
        return p.ExitCode;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 执行需提权的步骤（PRD R6 单次批处理提权）：
    /// - 进程已提权：直接运行，不弹 UAC；
    /// - 未提权：全部命令写入临时 bat，通过一次 runas cmd.exe 按序执行，全程仅一次 UAC；
    /// - UAC 取消（ERROR_CANCELLED）：所有提权步骤置 Skipped，返回 ElevationCancelled；
    /// - 提权失败（无管理员账户等）：所有提权步骤置 Skipped，返回 ElevationFailed。
    /// </summary>
    private static async Task<(bool cancelled, bool elevationFailed)> RunElevatedAsync(
        IReadOnlyList<RepairStep> steps, CancellationToken ct)
    {
        if (IsAdministrator())
        {
            foreach (var step in steps)
            {
                step.Status = RepairStepStatus.Running;
                var ok = true;
                for (var i = 0; i < step.Commands.Count; i++)
                {
                    var exit = await RunProcessAsync(step.Commands[i].File, step.Commands[i].Args, ct);
                    if (exit != 0)
                    {
                        ok = false;
                        break;
                    }
                    if (step.Id == "adapter" && i < step.Commands.Count - 1)
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                step.Status = ok ? RepairStepStatus.Pass : RepairStepStatus.Fail;
            }
            return (false, false);
        }

        var dir = Path.Combine(Path.GetTempPath(), "ToolBoxRepair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var batPath = Path.Combine(dir, "repair.bat");
        var outPath = Path.Combine(dir, "out.txt");
        try
        {
            File.WriteAllText(batPath, BuildBatchScript(steps, outPath), BatEncoding);

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{batPath}\" \"{outPath}\"\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) throw new Win32Exception();
                var exitTask = p.WaitForExitAsync(ct);
                var completed = await Task.WhenAny(exitTask, Task.Delay(ElevationTimeout, ct));
                if (!ReferenceEquals(completed, exitTask))
                {
                    // 提权子进程长时间无响应：终止并整体标记失败，避免修复区无限"执行中"
                    try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    foreach (var s in steps) s.Status = RepairStepStatus.Fail;
                    return (false, false);
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCanceled)
            {
                foreach (var s in steps) s.Status = RepairStepStatus.Skipped;
                return (true, false);
            }
            catch (Win32Exception)
            {
                foreach (var s in steps) s.Status = RepairStepStatus.Skipped;
                return (false, true);
            }

            var markers = ParseMarkers(outPath);
            foreach (var step in steps)
            {
                var ok = true;
                for (var i = 0; i < step.Commands.Count; i++)
                {
                    if (!markers.TryGetValue($"{step.Id}_{i}", out var code) || code != 0)
                    {
                        ok = false;
                        break;
                    }
                }
                step.Status = ok ? RepairStepStatus.Pass : RepairStepStatus.Fail;
            }
            return (false, false);
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>生成 bat：每条命令后写标记行 `id_i=退出码` 到输出文件。</summary>
    private static string BuildBatchScript(IReadOnlyList<RepairStep> steps, string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("set OUT=%~1");
        foreach (var step in steps)
        {
            for (var i = 0; i < step.Commands.Count; i++)
            {
                var command = step.Commands[i];
                sb.AppendLine($"{command.File} {EscapeBatArgument(command.Args)} <nul >nul 2>&1");
                sb.AppendLine($"echo {step.Id}_{i}=%ERRORLEVEL% >> \"%OUT%\"");
                if (step.Id == "adapter" && i < step.Commands.Count - 1)
                    sb.AppendLine("timeout /t 2 /nobreak >nul");
            }
        }
        return sb.ToString();
    }

    private static string EscapeBatArgument(string text) =>
        text.Replace("%", "%%").Replace("^", "^^").Replace("&", "^&")
            .Replace("|", "^|").Replace("<", "^<").Replace(">", "^>");

    private static Dictionary<string, int> ParseMarkers(string outPath)
    {
        var result = new Dictionary<string, int>();
        try
        {
            if (!File.Exists(outPath)) return result;
            foreach (var line in File.ReadAllLines(outPath, BatEncoding))
            {
                var m = MarkerRegex.Match(line);
                if (m.Success)
                    result[$"{m.Groups[1].Value}_{m.Groups[2].Value}"] = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            /* best-effort */
        }
        return result;
    }

    private static Encoding CreateBatEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }

    /// <summary>适配器名是否可无损写入 bat（OEM 代码页可编码）。</summary>
    private static bool CanEncodeOem(string text)
    {
        try
        {
            return BatEncoding.GetString(BatEncoding.GetBytes(text)) == text;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindAdapterNameByGateway(string gatewayIp)
    {
        if (!IPAddress.TryParse(gatewayIp, out var gateway)) return null;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.Equals(gateway)))?.Name;
        }
        catch
        {
            return null;
        }
    }
}
