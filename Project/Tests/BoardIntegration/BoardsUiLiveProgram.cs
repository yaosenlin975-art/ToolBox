using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;
using ToolBox.Views.Boards;

internal static class BoardsUiLiveProgram
{
    private const int TimeoutMilliseconds = 15000;
    private static string taskRoot = "";
    private static string workspace = "";
    private static string projectPath = "";
    private static string productOutput = "";
    private static string cliPath = "";
    private static object? taskPipeHost;
    private static Type? taskPipeHostType;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            taskRoot = RequiredEnvironment("TOOLBOX_TASK_TEST_ROOT");
            productOutput = RequiredEnvironment("TOOLBOX_BOARD_PRODUCT_OUTPUT");
            cliPath = RequiredEnvironment("TOOLBOX_BOARD_CLI_PATH");
            workspace = Path.Combine(taskRoot, "workspace");
            projectPath = Path.Combine(workspace, "中文 项目 UI 验收");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(projectPath);

            VerifyIsolation();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AddResource(app, "DesignTokens.xaml");
            AddResource(app, "Light.xaml");
            AddResource(app, "Lang zh-CN.xaml");
            StartTaskPipeHost();
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                int exitCode = 0;
                try { await RunUiChecksAsync(app); }
                catch (Exception ex)
                {
                    exitCode = 1;
                    Console.Error.WriteLine($"FAIL: {ex}");
                }
                finally
                {
                    try { DisposeTaskPipeHost(); }
                    catch (Exception ex) { exitCode = 1; Console.Error.WriteLine($"Host cleanup failed: {ex.Message}"); }
                    app.Shutdown(exitCode);
                }
            }));
            return app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            try { DisposeTaskPipeHost(); } catch { }
            return 1;
        }
    }

    private static async Task RunUiChecksAsync(Application app)
    {
        var registration = await InvokeCliAsync("project", "register", "--path", projectPath, "--init", "--json");
        string boardId = GetString(registration, "boardId");
        var board = await InvokeCliAsync("board", "get", "--project", boardId, "--json");
        long revision = GetInt64(board, "revision");
        Check(GetArray(board, "columns").GetArrayLength() == 4, "CLI registration did not initialize four columns.");
        string firstColumn = GetString(GetArray(board, "columns")[0], "id");
        var firstCard = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "CLI 首屏卡片", "--column-id", firstColumn, "--revision", revision.ToString(), "--json");
        revision = GetInt64(firstCard, "revision");
        string firstCardId = GetString(GetObject(firstCard, "card"), "id");

        var view = new BoardsView();
        var window = new Window
        {
            Title = "Boards integration test",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 1024,
            Height = 720,
            Content = view
        };
        window.Show();

        var columnsPanel = GetField<StackPanel>(view, "columnsPanel");
        var projectSelector = GetField<ComboBox>(view, "projectSelector");
        await WaitUntilAsync(() => columnsPanel.Children.Count == 4 && projectSelector.SelectedItem is ProjectBoardRegistration selected && selected.BoardId.ToString("D") == boardId,
            "The board view did not load the registered project and four columns.");
        Check(FindText(view, "CLI 首屏卡片"), "The board view did not render the card created before opening it.");
        Check(GetField<string?>(view, "selectedBoardId") == boardId, "The board view did not select the current project.");

        var created = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "CLI 实时刷新卡片", "--column-id", firstColumn, "--revision", revision.ToString(), "--json");
        revision = GetInt64(created, "revision");
        string createdCardId = GetString(GetObject(created, "card"), "id");
        await WaitUntilAsync(() => FindText(view, "CLI 实时刷新卡片"), "A CLI card write did not refresh the live WPF board.");
        Check(true, "The live view refreshed after a CLI write.");

        var deleted = await InvokeCliAsync("board", "card", "delete", "--project", boardId,
            "--id", createdCardId, "--revision", revision.ToString(), "--json");
        revision = GetInt64(deleted, "revision");
        await WaitUntilAsync(() => !FindText(view, "CLI 实时刷新卡片"), "The board view did not remove a soft-deleted card from the active columns.");
        var trashButton = GetField<Button>(view, "trashButton");
        trashButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitUntilAsync(() => GetField<bool>(view, "showRecycleBin") && FindText(view, "CLI 实时刷新卡片"),
            "The recycle-bin control did not display a CLI-deleted card.");
        Check(true, "Recycle-bin control displayed the soft-deleted card.");
        trashButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitUntilAsync(() => !GetField<bool>(view, "showRecycleBin") && columnsPanel.Children.Count == 4,
            "The board view did not return from the recycle bin.");

        var lightBrush = view.Background as SolidColorBrush;
        Check(lightBrush?.Color == Color.FromRgb(0xF8, 0xF9, 0xFB), "The Light theme brush was not applied to the board view.");
        ReplaceTheme(app, "Dark.xaml");
        Check((view.Background as SolidColorBrush)?.Color == Color.FromRgb(0x0C, 0x0D, 0x10), "The board view did not update to the Dark theme.");
        ReplaceTheme(app, "Light.xaml");
        Check((view.Background as SolidColorBrush)?.Color == Color.FromRgb(0xF8, 0xF9, 0xFB), "The board view did not return to the Light theme.");
        Check(firstCardId.Length > 0, "The first CLI card ID was empty.");

        window.Close();
        Console.WriteLine($"PASS: {checks} live WPF/CLI integration checks. Project: {projectPath}");
    }

    private static void VerifyIsolation()
    {
        string expectedRoot = Path.GetFullPath(taskRoot);
        string? suffix = TaskIpc.TestSuffix;
        if (string.IsNullOrWhiteSpace(suffix)) throw new InvalidOperationException("Debug TaskIpc test suffix is missing.");
        var store = ProjectBoardStore.Instance;
        FieldInfo registryField = typeof(ProjectBoardStore).GetField("registryPath", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ProjectBoardStore).FullName, "registryPath");
        string registryPath = (string)registryField.GetValue(store)!;
        string expected = Path.Combine(expectedRoot, "project-boards.json");
        if (!string.Equals(Path.GetFullPath(registryPath), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Board registry is outside the isolated root: {registryPath}");
        string normalizedProductOutput = Path.GetFullPath(productOutput).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(cliPath).StartsWith(normalizedProductOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The CLI must be loaded from the isolated Debug product output directory.");
    }

    private static void StartTaskPipeHost()
    {
        taskPipeHostType = typeof(ProjectBoardStore).Assembly.GetType("ToolBox.Core.Todo.TaskPipeHost", throwOnError: true)!;
        ConstructorInfo constructor = taskPipeHostType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var handler = new Func<TaskRequest, CancellationToken, Task<TaskResponse>>(DispatchAsync);
        taskPipeHost = constructor.Invoke([handler, new Action(() => { })]);
        taskPipeHostType.GetMethod("Start", BindingFlags.Instance | BindingFlags.Public)!.Invoke(taskPipeHost, null);
    }

    private static async Task<TaskResponse> DispatchAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Operation == "host.stop") return TaskResponse.Success(request);
        if (TaskProtocol.IsBoardOperation(request.Operation)) return await ProjectBoardStore.Instance.ExecuteAsync(request);
        return await TodoStore.Instance.ExecuteTaskAsync(request);
    }

    private static void DisposeTaskPipeHost()
    {
        if (taskPipeHost is IDisposable disposable) disposable.Dispose();
        taskPipeHost = null;
    }

    private static async Task<JsonElement> InvokeCliAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.Environment["TOOLBOX_TASK_TEST_ROOT"] = taskRoot;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated Debug CLI.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
        await process.WaitForExitAsync(timeout.Token);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"CLI exited {process.ExitCode}: {stdout} {stderr}");
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"CLI failed: {root.GetProperty("error").GetRawText()}");
        Check(true, $"CLI {string.Join(' ', arguments)} returned success.");
        return root.GetProperty("data").Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < TimeoutMilliseconds)
        {
            if (predicate()) return;
            await Task.Delay(40);
        }
        throw new TimeoutException(failureMessage);
    }

    private static bool FindText(DependencyObject root, string expected)
    {
        if (root is TextBlock text && string.Equals(text.Text, expected, StringComparison.Ordinal)) return true;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindText(VisualTreeHelper.GetChild(root, index), expected)) return true;
        return false;
    }

    private static void AddResource(Application app, string name) =>
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/ToolBox;component/Themes/{name}", UriKind.Relative)
        });

    private static void ReplaceTheme(Application app, string name)
    {
        ResourceDictionary? current = app.Resources.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            dictionary.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (current == null) throw new InvalidOperationException("Theme dictionary was not found.");
        int index = app.Resources.MergedDictionaries.IndexOf(current);
        app.Resources.MergedDictionaries[index] = new ResourceDictionary
        {
            Source = new Uri($"/ToolBox;component/Themes/{name}", UriKind.Relative)
        };
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        return (T)field.GetValue(instance)!;
    }

    private static JsonElement GetObject(JsonElement value, string property) => value.GetProperty(property);
    private static JsonElement GetArray(JsonElement value, string property) => value.GetProperty(property);
    private static string GetString(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static long GetInt64(JsonElement value, string property) => value.GetProperty(property).GetInt64();
    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
}
