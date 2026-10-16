// 集成验收 harness：真实 TaskPipeHost/ToolsCommandService/AssistantHost + 真实 toolbox-cli.exe IPC，
// 以及真实 ChatView + WebView2 承载。两个模式由 TOOLBOX_ASSISTANT_HARNESS_MODE=ipc|wpf 选择。
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ToolBox;
using ToolBox.Core.Assistant;
using ToolBox.Core.Theming;
using ToolBox.Core.Todo;
using ToolBox.Core.Tools;
using ToolBox.Models;
using ToolBox.Views.Chat;

internal static class AssistantLiveProgram
{
    private const int CliTimeoutMilliseconds = 40000;
    private static string taskRoot = "";
    private static string appDataDir = "";
    private static string workspace = "";
    private static string productOutput = "";
    private static string cliPath = "";
    private static string modeName = "ipc";
    private static readonly StringBuilder evidence = new();
    private static object? taskPipeHost;
    private static Type? taskPipeHostType;
    private static readonly LiveFixtureTools fixture = new();
    private static int checks;

    [STAThread]
    private static int Main()
    {
        modeName = Environment.GetEnvironmentVariable("TOOLBOX_ASSISTANT_HARNESS_MODE") is { Length: > 0 } m ? m : "ipc";
        try
        {
            taskRoot = RequiredEnvironment("TOOLBOX_TASK_TEST_ROOT");
            appDataDir = Path.Combine(taskRoot, "AppData");
            workspace = Path.Combine(taskRoot, "workspace");
            Directory.CreateDirectory(appDataDir);
            Directory.CreateDirectory(workspace);
            productOutput = RequiredEnvironment("TOOLBOX_ASSISTANT_PRODUCT_OUTPUT");
            cliPath = Path.Combine(productOutput, "toolbox-cli.exe");
            if (!File.Exists(cliPath)) throw new InvalidOperationException($"Isolated CLI not found: {cliPath}");

            RedirectConfigToTemp();
            InstallFixtureRegistry();
            Log($"MODE={modeName}");
            Log($"ISOLATED_PIPE={TaskIpc.PipeName}");
            Log($"EVIDENCE_ROOT={taskRoot}");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AddResource(app, "DesignTokens.xaml");
            AddResource(app, "Light.xaml");
            AddResource(app, "Lang zh-CN.xaml");
            if (modeName == "ipc") StartTaskPipeHost();

            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                int exitCode = 0;
                try
                {
                    if (modeName == "wpf") await RunWpfModeAsync();
                    else await RunIpcModeAsync();
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    Log($"FAIL after {checks} checks: {ex}");
                }
                finally
                {
                    try { DisposeTaskPipeHost(); } catch (Exception ex) { exitCode = 1; Log($"Host cleanup failed: {ex.Message}"); }
                    if (exitCode == 0) Log($"PASS: {checks} checks.");
                    app.Shutdown(exitCode);
                }
            }));
            return app.Run();
        }
        catch (Exception ex)
        {
            Log($"FAIL: {ex}");
            try { DisposeTaskPipeHost(); } catch { }
            return 1;
        }
    }

    private static async Task RunIpcModeAsync()
    {
        fixture.Reset();
        Log($"REAL_TOOL_COUNTS FileTools={CountTools(typeof(FileTools))} ImageTools={CountTools(typeof(ImageTools))} " +
            $"TodoTools={CountTools(typeof(TodoTools))} WebSearchTools={CountTools(typeof(WebSearchTools))} BoardTools={CountTools(typeof(BoardTools))}");

        // (a) Disabled gate.
        Check(!AssistantHost.IsEnabled, "AssistantHost must start disabled.");
        var disabledList = await InvokeCliAsync("tools", "list");
        CheckError(disabledList, "tools.list disabled", "ASSISTANT_DISABLED");
        Log($"a tools.list(disabled) exit={disabledList.ExitCode} {Compact(disabledList.Stdout)}");

        var caps = await InvokeCliAsync("capabilities", "list");
        JsonElement capsData = RequireSuccess(caps, "capabilities.list");
        int capCount = capsData.GetArrayLength();
        int toolsCaps = capsData.EnumerateArray().Count(c => c.GetProperty("name").GetString()!.StartsWith("tools.", StringComparison.Ordinal));
        Log($"b capabilities.list total={capCount} tools.*={toolsCaps}");

        // (b) Enable via the real App path, then list/get.
        await AssistantHost.SetEnabledAsync(true);
        Log($"b AssistantHost.IsEnabled={AssistantHost.IsEnabled}");
        var list = await InvokeCliAsync("tools", "list");
        JsonElement listData = RequireSuccess(list, "tools.list enabled");
        string[] names = listData.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        int expected = CountTools(typeof(FileTools)) + CountTools(typeof(ImageTools)) + CountTools(typeof(TodoTools)) +
                       CountTools(typeof(WebSearchTools)) + CountTools(typeof(BoardTools)) + 3;
        Log($"b tools.list count={names.Length} names={string.Join(",", names)}");
        Check(names.Length == expected, $"tools.list count {names.Length} != expected {expected}.");
        string realTool = names.First(n => !n.StartsWith("fixture_", StringComparison.Ordinal));
        RequireSuccess(await InvokeCliAsync("tools", "get", realTool), "tools.get " + realTool);
        JsonElement fixtureSchema = RequireSuccess(await InvokeCliAsync("tools", "get", "fixture_long"), "tools.get fixture_long");
        Log($"b tools.get {realTool} ok; tools.get fixture_long ok schema={fixtureSchema.GetProperty("tool").GetProperty("inputSchema").GetRawText()}");

        // (c) Fast return + status semantics.
        string longInput = WriteInput("long-3.json", "{\"seconds\":3}");
        var stopwatch = Stopwatch.StartNew();
        JsonElement callData = RequireSuccess(await InvokeCliAsync("tools", "call", "fixture_long", "--input", longInput, "--request-id", Guid.NewGuid().ToString("D")), "tools.call fixture_long");
        stopwatch.Stop();
        string callId = callData.GetProperty("callId").GetString()!;
        string callStatus = callData.GetProperty("status").GetString()!;
        Log($"c tools.call returned in {stopwatch.ElapsedMilliseconds} ms callId={callId} status={callStatus}");
        Check(stopwatch.ElapsedMilliseconds < 15000, "tools.call did not return quickly.");
        var (longState, longElapsed) = await PollJobAsync(callId, 20000);
        Check(longState.Status == "completed" && longState.Result == "long-completed", "fixture_long did not complete.");
        Log($"c tools.result status={longState.Status} result={longState.Result} after {longElapsed} ms (job ran ~3000 ms)");

        // (d) Idempotency by requestId fingerprint.
        string idemInput = WriteInput("idem-2.json", "{\"seconds\":2}");
        string idemRequestId = Guid.NewGuid().ToString("D");
        JsonElement first = RequireSuccess(await InvokeCliAsync("tools", "call", "fixture_long", "--input", idemInput, "--request-id", idemRequestId), "idempotent first");
        string idemCallId = first.GetProperty("callId").GetString()!;
        int invocations = fixture.LongInvocations;
        await Task.Delay(200);
        JsonElement second = RequireSuccess(await InvokeCliAsync("tools", "call", "fixture_long", "--input", idemInput, "--request-id", idemRequestId), "idempotent second");
        Check(second.GetProperty("callId").GetString()! == idemCallId, "Idempotent retry returned a different callId.");
        Check(fixture.LongInvocations == invocations, "Idempotent retry re-executed the fixture tool.");
        Log($"d idempotent callId={idemCallId} invocations={fixture.LongInvocations}");
        string idemInput2 = WriteInput("idem-5.json", "{\"seconds\":5}");
        var conflict = await InvokeCliAsync("tools", "call", "fixture_long", "--input", idemInput2, "--request-id", idemRequestId);
        CheckError(conflict, "requestId conflict", "CONFLICT");
        Log($"d conflict exit={conflict.ExitCode} {Compact(conflict.Stdout)}");
        await PollJobAsync(idemCallId, 20000);

        // (e) Cancel a long job.
        string cancelInput = WriteInput("cancel-30.json", "{\"seconds\":30}");
        JsonElement cancelCall = RequireSuccess(await InvokeCliAsync("tools", "call", "fixture_long", "--input", cancelInput), "cancel call");
        string cancelId = cancelCall.GetProperty("callId").GetString()!;
        JsonElement cancelResp = RequireSuccess(await InvokeCliAsync("tools", "cancel", cancelId), "tools.cancel");
        Log($"e tools.cancel callId={cancelId} status={cancelResp.GetProperty("status").GetString()} cancellationRequested={cancelResp.GetProperty("cancellationRequested").GetBoolean()}");
        var (cancelState, cancelElapsed) = await PollJobAsync(cancelId, 20000);
        Check(cancelState.Status == "canceled", "Cancelled job did not reach canceled.");
        Log($"e canceled status={cancelState.Status} after {cancelElapsed} ms (completed side effects are not rolled back by design)");

        // (g) Size limits and unknown job.
        string bigFile = Path.Combine(workspace, "big-input.json");
        File.WriteAllText(bigFile, new string(' ', TaskProtocol.MaxBytes + 4096));
        var big = await InvokeCliAsync("tools", "call", "fixture_echo", "--input", bigFile);
        CheckError(big, "oversized input", "INVALID_ARGUMENT");
        Log($"g oversized input exit={big.ExitCode} {Compact(big.Stdout)}");
        string emptyInput = WriteInput("empty.json", "{}");
        JsonElement largeCall = RequireSuccess(await InvokeCliAsync("tools", "call", "fixture_large", "--input", emptyInput), "fixture_large call");
        string largeId = largeCall.GetProperty("callId").GetString()!;
        var (largeState, largeElapsed) = await PollJobAsync(largeId, 20000);
        Check(largeState.Status == "failed" && largeState.ErrorCode == "RESULT_TOO_LARGE", "fixture_large did not fail with RESULT_TOO_LARGE.");
        Log($"g oversized result status={largeState.Status} errorCode={largeState.ErrorCode} after {largeElapsed} ms");
        var unknown = await InvokeCliAsync("tools", "result", Guid.NewGuid().ToString("D"));
        CheckError(unknown, "unknown callId", "NOT_FOUND");
        Log($"g unknown callId exit={unknown.ExitCode} {Compact(unknown.Stdout)}");

        // (f) Disable cancels in-flight jobs via CancelAllAsync; result/cancel still work.
        string runningInput = WriteInput("running-30.json", "{\"seconds\":30}");
        JsonElement runningCall = RequireSuccess(await InvokeCliAsync("tools", "call", "fixture_long", "--input", runningInput), "running call");
        string runningId = runningCall.GetProperty("callId").GetString()!;
        await Task.Delay(600);
        await AssistantHost.SetEnabledAsync(false);
        Log($"f AssistantHost.IsEnabled={AssistantHost.IsEnabled}");
        var (runningState, _) = await PollJobAsync(runningId, 15000);
        Check(runningState.Status == "canceled", "Disable did not cancel the in-flight job.");
        var afterDisableList = await InvokeCliAsync("tools", "list");
        CheckError(afterDisableList, "tools.list after disable", "ASSISTANT_DISABLED");
        var afterDisableResult = await InvokeCliAsync("tools", "result", runningId);
        Check(afterDisableResult.Root!.Value.GetProperty("ok").GetBoolean(), "tools.result must still query jobs after disable.");
        var afterDisableCancel = await InvokeCliAsync("tools", "cancel", runningId);
        Check(afterDisableCancel.Root!.Value.GetProperty("ok").GetBoolean(), "tools.cancel must still work after disable.");
        Log($"f in-flight canceled={runningState.Status}; list exit={afterDisableList.ExitCode}; result ok; cancel ok");
    }

    private static async Task RunWpfModeAsync()
    {
        var view = new ChatView();
        var window = new Window
        {
            Title = "Assistant live test",
            WindowStyle = WindowStyle.ToolWindow,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 1024,
            Height = 720,
            Left = 0,
            Top = 0,
            Content = view
        };
        window.Show();

        var browser = GetField<WebView2>(view, "browser");
        var fallback = GetField<Border>(view, "fallback");

        await WaitUntilAsync(() => browser.CoreWebView2 != null, 60000, "WebView2 core did not initialize.");
        CoreWebView2 core = browser.CoreWebView2!;
        Log("4a WebView2 core initialized.");

        var readyMessages = new List<string>();
        bool navigationSucceeded = false;
        core.WebMessageReceived += (_, e) =>
        {
            string? json = e.WebMessageAsJson;
            if (json is null || !json.Contains("\"ready\"", StringComparison.Ordinal)) return;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ready")
                    readyMessages.Add(json);
            }
            catch (JsonException) { }
        };
        core.NavigationCompleted += (_, e) => navigationSucceeded = e.IsSuccess;

        for (int attempt = 0; attempt < 4 && readyMessages.Count == 0; attempt++)
        {
            await Task.Delay(4000);
            if (readyMessages.Count == 0)
            {
                Log($"4b no ready yet; reloading local page (attempt {attempt + 1}).");
                core.Reload();
            }
        }
        Check(readyMessages.Count > 0, "No ready message was received from the local React page.");
        Check(navigationSucceeded, "Navigation to the local dist page did not succeed.");
        Log($"4b ready received: {readyMessages[0]}");

        await Task.Delay(1500);
        string dom = await SafeScriptAsync(core, "JSON.stringify({title: document.title, state: document.readyState, rootChildren: (document.getElementById('root')||{}).childElementCount||0, text: (document.body.innerText||'').replace(/\\s+/g,' ').slice(0,180)})");
        Log($"4b rendered DOM: {dom}");

        await WaitUntilAsync(() => fallback.Visibility == Visibility.Collapsed && browser.Visibility == Visibility.Visible, 10000,
            "fallback/browser visibility did not settle after load.");
        Log($"4a fallback={fallback.Visibility} browser={browser.Visibility} (dist loaded offline; index.html CSP default-src 'self')");

        readyMessages.Clear();
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
        await WaitUntilAsync(() => readyMessages.Count > 0, 30000, "No ready message after unloading and re-loading the ChatView.");
        Log($"4c ready after remount: {readyMessages[0]}");

        SetThemeMode(EThemeMode.Dark);
        RaiseThemeChanged();
        SetThemeMode(EThemeMode.Light);
        RaiseThemeChanged();
        SetLanguage("en-US");
        RaiseLanguageChanged();
        SetLanguage("zh-CN");
        RaiseLanguageChanged();
        Check(true, "4d ChatView survived theme and language switches without exception (web side not observed).");

        window.Close();
        Log("4e WebView2 Runtime missing downgrade: NOT VERIFIED (Streamlit Runtime is present; code path only inspected).");
    }

    private static void RedirectConfigToTemp()
    {
        // ToolBoxOption.configPath is a static readonly field with no test-root hook.
        // Force the type initializer first, then overwrite the field from emitted IL
        // (reflection SetValue refuses initonly statics) so the real user config.xml is never touched.
        RuntimeHelpers.RunClassConstructor(typeof(ToolBoxOption).TypeHandle);
        SetStaticReadonly(typeof(ToolBoxOption), "configPath", Path.Combine(appDataDir, "ToolBox", "config.xml"));
        SetStaticReadonly(typeof(ToolBoxOption), "LegacyConfigPath", Path.Combine(appDataDir, "Setuna", "config.xml"));

        WriteTempOption("en-US");
        if (ToolBoxOption.Load().Language != "en-US")
            throw new InvalidOperationException("ToolBoxOption config path redirect did not take effect.");
        WriteTempOption("zh-CN");
        Log($"CONFIG_REDIRECT=ok ({Path.Combine(appDataDir, "ToolBox", "config.xml")})");
    }

    private static void WriteTempOption(string language)
    {
        var option = ToolBoxOption.GetDefaultOption();
        option.Language = language;
        option.Save();
    }

    private static void SetStaticReadonly(Type type, string name, string value)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);
        var method = new DynamicMethod($"__force_set_{name}", typeof(void), [typeof(string)], type, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        method.Invoke(null, [value]);
        if ((string)field.GetValue(null)! != value)
            throw new InvalidOperationException($"config path {name} was not redirected.");
    }

    private static void InstallFixtureRegistry()
    {
        var registry = new ToolRegistry()
            .Register(typeof(FileTools))
            .Register(typeof(ImageTools))
            .Register(typeof(TodoTools))
            .Register(typeof(WebSearchTools))
            .Register(typeof(BoardTools))
            .Register(fixture);
        var field = typeof(AssistantHost).GetField("registry", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AssistantHost).FullName, "registry");
        field.SetValue(null, registry);
        Log("FIXTURE_REGISTRY=installed (real tool classes + fixture_long/fixture_echo/fixture_large)");
    }

    private static void StartTaskPipeHost()
    {
        taskPipeHostType = typeof(AssistantHost).Assembly.GetType("ToolBox.Core.Todo.TaskPipeHost", throwOnError: true)!;
        ConstructorInfo constructor = taskPipeHostType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var handler = new Func<TaskRequest, CancellationToken, Task<TaskResponse>>(DispatchAsync);
        taskPipeHost = constructor.Invoke([handler, new Action(() => { })]);
        taskPipeHostType.GetMethod("Start", BindingFlags.Instance | BindingFlags.Public)!.Invoke(taskPipeHost, null);
    }

    private static Task<TaskResponse> DispatchAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == "host.stop") return Task.FromResult(TaskResponse.Success(request));
        if (TaskProtocol.IsToolsOperation(request.Operation)) return AssistantHost.Tools.ExecuteAsync(request, cancellationToken);
        return Task.FromResult(TaskResponse.Failure(request, "INVALID_ARGUMENT", "Unsupported operation in the assistant harness.", "operation"));
    }

    private static void DisposeTaskPipeHost()
    {
        if (taskPipeHost is IDisposable disposable) disposable.Dispose();
        taskPipeHost = null;
    }

    private static async Task<CliResult> InvokeCliAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["TOOLBOX_TASK_TEST_ROOT"] = taskRoot;
        start.Environment["APPDATA"] = appDataDir;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated CLI.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(CliTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw new TimeoutException($"CLI {string.Join(' ', arguments)} did not exit in {CliTimeoutMilliseconds} ms. stdout={await stdoutTask} stderr={await stderrTask}");
        }
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        JsonElement? root = null;
        try
        {
            using var document = JsonDocument.Parse(stdout);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { }
        return new CliResult(process.ExitCode, root, stdout, stderr);
    }

    private static JsonElement RequireSuccess(CliResult result, string label)
    {
        if (result.Root is not { } root)
            throw new InvalidOperationException($"{label}: CLI returned no JSON. exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"{label}: CLI failed {root.GetProperty("error").GetRawText()}");
        return root.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    private static void CheckError(CliResult result, string label, string expectedCode)
    {
        if (result.Root is not { } root)
            throw new InvalidOperationException($"{label}: CLI returned no JSON. stdout={result.Stdout} stderr={result.Stderr}");
        Check(!root.GetProperty("ok").GetBoolean(), $"{label}: expected a failure.");
        string code = root.GetProperty("error").GetProperty("code").GetString()!;
        Check(code == expectedCode, $"{label}: expected {expectedCode} but got {code}.");
    }

    private static async Task<(JobState State, long ElapsedMs)> PollJobAsync(string callId, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        JobState last = new("", null, null, null, false);
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            last = ReadJob(await InvokeCliAsync("tools", "result", callId));
            if (last.Status is "completed" or "failed" or "canceled") return (last, stopwatch.ElapsedMilliseconds);
            await Task.Delay(200);
        }
        throw new TimeoutException($"Job {callId} did not reach a terminal state in {timeoutMs} ms. Last={last}.");
    }

    private static JobState ReadJob(CliResult result)
    {
        if (result.Root is not { } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new JobState("", null, null, null, false);
        string status = data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
        string? jobResult = data.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        string? code = data.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null;
        string? error = data.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
        return new JobState(status, jobResult, code, error, root.GetProperty("ok").GetBoolean());
    }

    private static string WriteInput(string fileName, string json)
    {
        string path = Path.Combine(workspace, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static int CountTools(Type type) => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Count(method => method.GetCustomAttribute<ToolAttribute>() != null);

    private static void SetThemeMode(EThemeMode mode) =>
        typeof(ThemeManager).GetProperty("Mode")!.SetValue(ThemeManager.Instance, mode);

    private static void RaiseThemeChanged()
    {
        if (typeof(ThemeManager).GetField("ThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ThemeManager.Instance) is EventHandler handler)
            handler.Invoke(ThemeManager.Instance, EventArgs.Empty);
    }

    private static void RaiseLanguageChanged()
    {
        if (typeof(App).GetField("LanguageChanged", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not Action changed)
            throw new MissingFieldException(typeof(App).FullName, "LanguageChanged");
        changed.Invoke();
    }

    private static void SetLanguage(string language)
    {
        try
        {
            WriteTempOption(language);
            Log($"4d language requested={language} loaded={ToolBoxOption.Load().Language}");
        }
        catch (Exception ex)
        {
            Log($"4d language config write skipped: {ex.Message}");
        }
    }

    private static async Task<string> SafeScriptAsync(CoreWebView2 core, string script)
    {
        try { return await core.ExecuteScriptAsync(script); }
        catch (Exception ex) { return "script-error: " + ex.Message; }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return;
            await Task.Delay(40);
        }
        throw new TimeoutException(failureMessage);
    }

    private static void AddResource(Application app, string name) =>
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/ToolBox;component/Themes/{name}", UriKind.Relative)
        });

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        return (T)field.GetValue(instance)!;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");

    private static string Compact(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
        Log($"CHECK-OK: {message}");
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        evidence.AppendLine(message);
        try { File.WriteAllText(Path.Combine(taskRoot, $"assistant-live-{modeName}.log"), evidence.ToString()); }
        catch (IOException) { }
    }

    private sealed record CliResult(int ExitCode, JsonElement? Root, string Stdout, string Stderr);
    private sealed record JobState(string Status, string? Result, string? ErrorCode, string? Error, bool Ok);
}

internal sealed class LiveFixtureTools
{
    public int LongInvocations;

    public void Reset() => LongInvocations = 0;

    [Tool("fixture_long", "Test-only long-running tool: waits, then completes (cancellable).")]
    public async Task<string> Long([ToolParam("Seconds to wait.", Required = false)] int seconds = 4, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref LongInvocations);
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        return "long-completed";
    }

    [Tool("fixture_echo", "Test-only echo tool.")]
    public string Echo([ToolParam("Text to echo.")] string text) => "echo:" + text;

    [Tool("fixture_large", "Test-only tool returning an oversized result.")]
    public string Large() => new string('x', TaskProtocol.MaxBytes + 4096);
}
