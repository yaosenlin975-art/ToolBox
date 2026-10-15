using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolBox;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;
using ToolBox.Views.Boards;

internal static class BoardsUiLiveProgram
{
    private const int TimeoutMilliseconds = 40000;
    private static string taskRoot = "";
    private static string workspace = "";
    private static string projectPath = "";
    private static string productOutput = "";
    private static string cliPath = "";
    private static bool commentIoDiagnostic;
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
            commentIoDiagnostic = string.Equals(Environment.GetEnvironmentVariable("TOOLBOX_BOARD_HARNESS_MODE"), "comment-io-diagnostic", StringComparison.Ordinal);
            workspace = Path.Combine(taskRoot, "workspace");
            projectPath = Path.Combine(workspace, "中文 项目 UI 验收");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(projectPath);

            VerifyIsolation();
            Console.WriteLine($"Isolated task pipe: {TaskIpc.PipeName}");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!commentIoDiagnostic)
            {
                AddResource(app, "DesignTokens.xaml");
                AddResource(app, "Light.xaml");
                AddResource(app, "Lang zh-CN.xaml");
            }
            StartTaskPipeHost();
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                int exitCode = 0;
                try
                {
                    if (commentIoDiagnostic) await RunInlineCommentIoDiagnosticAsync();
                    else await RunUiChecksAsync(app);
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    Console.Error.WriteLine($"Checks passed before failure: {checks}.");
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
        JsonElement registrationData = await InvokeCliAsync("project", "register", "--path", projectPath, "--init", "--json");
        var registration = JsonSerializer.Deserialize<ProjectBoardRegistration>(registrationData.GetRawText(), TaskProtocol.Options)
            ?? throw new InvalidOperationException("CLI registration did not return a project registration.");
        string boardId = registration.BoardId.ToString("D");
        var board = await InvokeCliAsync("board", "get", "--project", boardId, "--json");
        long revision = GetInt64(board, "revision");
        Check(GetArray(board, "columns").GetArrayLength() == 4, "CLI registration did not initialize four columns.");
        JsonElement notStartedColumn = GetArray(board, "columns").EnumerateArray().Single(column => GetString(column, "status") == "notStarted");
        string firstColumn = GetString(notStartedColumn, "id");
        var firstCard = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "CLI 首屏卡片", "--column-id", firstColumn, "--revision", revision.ToString(), "--json");
        revision = GetInt64(firstCard, "revision");
        string firstCardId = GetString(GetObject(firstCard, "card"), "id");
        var childCard = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "CLI 子任务", "--column-id", firstColumn, "--parent-id", firstCardId, "--revision", revision.ToString(), "--json");
        revision = GetInt64(childCard, "revision");

        var inProgressCard = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "进行中状态卡", "--column-id", firstColumn, "--revision", revision.ToString(), "--json");
        revision = GetInt64(inProgressCard, "revision");
        string inProgressId = GetString(GetObject(inProgressCard, "card"), "id");
        const string smokeActor = "board-ui-smoke-agent";
        const string smokeHost = "board-ui-smoke-host";
        const string smokeSession = "board-ui-smoke-session";
        var claimed = await InvokeCliAsync("board", "card", "claim", "--project", boardId, "--id", inProgressId,
            "--actor", smokeActor, "--host", smokeHost, "--session", smokeSession, "--revision", revision.ToString(), "--json");
        revision = GetInt64(claimed, "revision");

        var reviewCard = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "待验收状态卡", "--column-id", firstColumn, "--revision", revision.ToString(), "--json");
        revision = GetInt64(reviewCard, "revision");
        string reviewId = GetString(GetObject(reviewCard, "card"), "id");
        var reviewClaim = await InvokeCliAsync("board", "card", "claim", "--project", boardId, "--id", reviewId,
            "--actor", smokeActor, "--host", smokeHost, "--session", smokeSession, "--revision", revision.ToString(), "--json");
        revision = GetInt64(reviewClaim, "revision");
        var submitted = await InvokeCliAsync("board", "card", "submit", "--project", boardId, "--id", reviewId,
            "--actor", smokeActor, "--host", smokeHost, "--session", smokeSession, "--revision", revision.ToString(), "--json");
        revision = GetInt64(submitted, "revision");

        var closedCard = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "已关闭状态卡", "--column-id", firstColumn, "--revision", revision.ToString(), "--json");
        revision = GetInt64(closedCard, "revision");
        string closedId = GetString(GetObject(closedCard, "card"), "id");
        var closedClaim = await InvokeCliAsync("board", "card", "claim", "--project", boardId, "--id", closedId,
            "--actor", smokeActor, "--host", smokeHost, "--session", smokeSession, "--revision", revision.ToString(), "--json");
        revision = GetInt64(closedClaim, "revision");
        var closedSubmit = await InvokeCliAsync("board", "card", "submit", "--project", boardId, "--id", closedId,
            "--actor", smokeActor, "--host", smokeHost, "--session", smokeSession, "--revision", revision.ToString(), "--json");
        revision = GetInt64(closedSubmit, "revision");
        var closed = await InvokeCliAsync("board", "card", "close", "--project", boardId, "--id", closedId,
            "--actor", "local-user", "--host", "toolbox-qa", "--revision", revision.ToString(), "--json");
        revision = GetInt64(closed, "revision");

        var roots = await InvokeCliAsync("board", "card", "list", "--project", boardId, "--roots-only", "--json");
        JsonElement rootCards = GetArray(roots, "cards");
        Check(rootCards.GetArrayLength() == 4, "The root-only CLI list did not include exactly the four root cards.");
        Check(!rootCards.EnumerateArray().Any(card => GetString(card, "id") == GetString(GetObject(childCard, "card"), "id")), "The root-only CLI list included a child card.");

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
        Check(!FindText(view, "CLI 子任务"), "The main board rendered a child card outside the details tree.");
        Check(FindText(view, "未开始") && FindText(view, "进行中") && FindText(view, "待验收") && FindText(view, "已关闭"), "The main board did not render all four stable card states.");
        Check(GetField<string?>(view, "selectedBoardId") == boardId, "The board view did not select the current project.");

        var latestBoardJson = await InvokeCliAsync("board", "get", "--project", boardId, "--json");
        var latestBoard = JsonSerializer.Deserialize<ProjectBoard>(latestBoardJson.GetRawText(), TaskProtocol.Options)
            ?? throw new InvalidOperationException("CLI board.get did not return a board snapshot.");
        ProjectBoardCard rootModel = latestBoard.Cards.Single(card => card.Id.ToString("D") == firstCardId);
        Type detailsType = typeof(BoardsView).Assembly.GetType("ToolBox.Views.Boards.BoardCardDetailsWindow", throwOnError: true)!;
        Type snapshotType = typeof(BoardsView).Assembly.GetType("ToolBox.Views.Boards.BoardDetailsSnapshot", throwOnError: true)!;
        object detailsSnapshot = Activator.CreateInstance(snapshotType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [boardId, latestBoard.Revision, latestBoard.CommentRevision], culture: null)!;
        ConstructorInfo detailsConstructor = detailsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var details = (Window)detailsConstructor.Invoke([window, latestBoard, registration, rootModel, detailsSnapshot, null]);
        details.Opacity = 0;
        details.ShowActivated = false;
        details.ShowInTaskbar = false;
        details.Show();
        var treePanel = GetField<StackPanel>(details, "treePanel");
        await WaitUntilAsync(() => FindText(treePanel, "CLI 子任务"), "The card details window did not render the child tree.");
        Check(FindText(treePanel, "CLI 首屏卡片") && FindText(treePanel, "CLI 子任务"), "The details tree did not include the root and its child.");
        CaptureVisual(view, "light-zh-board.png");
        CaptureVisual(details, "light-zh-details.png");

        var lightBrush = view.Background as SolidColorBrush;
        Check(lightBrush?.Color == Color.FromRgb(0xF8, 0xF9, 0xFB), "The Light theme brush was not applied to the board view.");
        ReplaceTheme(app, "Dark.xaml");
        Check((view.Background as SolidColorBrush)?.Color == Color.FromRgb(0x0C, 0x0D, 0x10), "The board view did not update to the Dark theme.");
        CaptureVisual(view, "dark-zh-board.png");
        CaptureVisual(details, "dark-zh-details.png");
        ReplaceTheme(app, "Light.xaml");
        Check((view.Background as SolidColorBrush)?.Color == Color.FromRgb(0xF8, 0xF9, 0xFB), "The board view did not return to the Light theme.");

        ReplaceLanguage(app, "Lang en-US.xaml");
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(FindText(view, "Project") && FindText(view, "Not started"), "The main board did not refresh its English labels.");
        Check(FindText(details, "Subtasks") && FindText(details, "Not started"), "The details window did not refresh its English labels.");
        CaptureVisual(view, "light-en-board.png");
        CaptureVisual(details, "light-en-details.png");

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

        ReplaceLanguage(app, "Lang zh-CN.xaml");
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(FindText(view, "项目") && FindText(view, "未开始"), "The main board did not return to Chinese labels.");
        Check(FindText(details, "子事项") && FindText(details, "未开始"), "The details window did not return to Chinese labels.");
        Check(firstCardId.Length > 0, "The first CLI card ID was empty.");

        details.Close();
        window.Close();
        Console.WriteLine($"PASS: {checks} live WPF/CLI integration checks. Project: {projectPath}");
    }

    private static async Task RunInlineCommentIoDiagnosticAsync()
    {
        JsonElement registrationData = await InvokeCliAsync("project", "register", "--path", projectPath, "--init", "--json");
        var registration = JsonSerializer.Deserialize<ProjectBoardRegistration>(registrationData.GetRawText(), TaskProtocol.Options)
            ?? throw new InvalidOperationException("CLI registration did not return a project registration.");
        string boardId = registration.BoardId.ToString("D");
        var board = await InvokeCliAsync("board", "get", "--project", boardId, "--json");
        long revision = GetInt64(board, "revision");
        long commentRevision = GetInt64(board, "commentRevision");
        JsonElement column = GetArray(board, "columns").EnumerateArray().Single(item => GetString(item, "status") == "notStarted");
        var cardData = await InvokeCliAsync("board", "card", "create", "--project", boardId,
            "--title", "Inline comment I/O diagnostic", "--column-id", GetString(column, "id"), "--revision", revision.ToString(), "--json");
        revision = GetInt64(cardData, "revision");
        commentRevision = GetInt64(cardData, "commentRevision");
        string cardId = GetString(GetObject(cardData, "card"), "id");
        string body = new('界', 5400);
        string boardFile = Path.Combine(projectPath, ".toolbox", "board.json");
        for (int index = 1; index <= 22; index++)
        {
            string snapshotPath = Path.Combine(taskRoot, $"board-before-inline-write-{index}.json");
            File.Copy(boardFile, snapshotPath, overwrite: false);
            try
            {
                var commentData = await InvokeCliAsync("board", "comment", "add", "--project", boardId, "--id", cardId,
                    "--body", body, "--actor", "external-writer", "--host", "external-host", "--revision", revision.ToString(),
                    "--comment-revision", commentRevision.ToString(), "--json");
                revision = GetInt64(commentData, "revision");
                commentRevision = GetInt64(commentData, "commentRevision");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TARGETED RESULT: inline write {index} failed after {index - 1} successes. Evidence root: {taskRoot}; pre-write snapshot: {snapshotPath}; bytes: {new FileInfo(snapshotPath).Length}; commentRevision: {commentRevision}.");
                Console.Error.WriteLine(ex.Message);
                return;
            }
        }

        Console.WriteLine($"TARGETED RESULT: all 22 inline writes of {body.Length} characters succeeded; no I/O failure reproduced. Evidence root: {taskRoot}; latest commentRevision: {commentRevision}.");
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
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            string timedOutStdout = await stdoutTask;
            string timedOutStderr = await stderrTask;
            throw new TimeoutException($"CLI did not exit within {TimeoutMilliseconds} ms. Pipe: {TaskIpc.PipeName}. stdout: {timedOutStdout} stderr: {timedOutStderr}");
        }
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

    private static void ReplaceLanguage(Application app, string name)
    {
        for (int index = app.Resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            if ((app.Resources.MergedDictionaries[index].Source?.OriginalString ?? "").Contains("Lang ", StringComparison.OrdinalIgnoreCase))
                app.Resources.MergedDictionaries.RemoveAt(index);
        }
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/ToolBox;component/Themes/{name}", UriKind.Relative)
        });
        if (typeof(App).GetField("LanguageChanged", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not Action changed)
            throw new MissingFieldException(typeof(App).FullName, "LanguageChanged");
        changed.Invoke();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static void CaptureVisual(FrameworkElement visual, string fileName)
    {
        double previousOpacity = visual.Opacity;
        try
        {
            visual.Opacity = 1;
            visual.UpdateLayout();
            int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            bool hasVisiblePixels = false;
            for (int index = 3; index < pixels.Length; index += 4)
            {
                if (pixels[index] == 0) continue;
                hasVisiblePixels = true;
                break;
            }
            Check(hasVisiblePixels, $"The rendered visual is fully transparent: {fileName}.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(taskRoot, fileName);
            using var output = File.Create(path);
            encoder.Save(output);
            Check(output.Length > 0, $"The visual evidence image is empty: {fileName}.");
        }
        finally
        {
            visual.Opacity = previousOpacity;
        }
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
