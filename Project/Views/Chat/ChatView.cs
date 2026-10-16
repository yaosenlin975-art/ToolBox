using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ToolBox.Core.Llm;
using ToolBox.Core.Theming;
using ToolBox.Models;

namespace ToolBox.Views.Chat;

public sealed class ChatView : UserControl
{
    private const string VirtualHost = "toolbox.assistant.local";
    private const string AssistantOrigin = "https://toolbox.assistant.local";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly Grid root = new();
    private readonly WebView2 browser = new();
    private readonly Border fallback = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock fallbackText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560, FontSize = 13 };
    private readonly StackPanel fallbackContent = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
    private AssistantRuntime? runtime;
    private CoreWebView2? core;
    private bool loaded;
    private bool hostEventsAttached;
    private bool virtualHostMapped;
    private bool initializing;

    public ChatView()
    {
        root.SetResourceReference(Panel.BackgroundProperty, "BgBrush");
        browser.Visibility = Visibility.Collapsed;
        fallback.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        fallbackText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        fallbackContent.Children.Add(fallbackText);
        fallback.Child = fallbackContent;
        root.Children.Add(browser);
        root.Children.Add(fallback);
        Content = root;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ShowFallback(GetLoadingText(), false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        App.LanguageChanged += OnLanguageChanged;

        try
        {
            runtime = AssistantRuntime.Instance;
            runtime.Changed += OnRuntimeChanged;
        }
        catch (Exception ex)
        {
            ShowFallback(GetUnavailableText(ex), true);
            return;
        }

        _ = InitializeBrowserAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!loaded) return;
        loaded = false;
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
        App.LanguageChanged -= OnLanguageChanged;
        if (runtime != null) runtime.Changed -= OnRuntimeChanged;
    }

    private async Task InitializeBrowserAsync()
    {
        if (!loaded || initializing) return;
        initializing = true;
        ShowFallback(GetLoadingText(), false);
        try
        {
            if (runtime == null) runtime = AssistantRuntime.Instance;
            var assetDirectory = Path.Combine(AppContext.BaseDirectory, "AssistantWeb", "dist");
            var entryPoint = Path.Combine(assetDirectory, "index.html");
            if (!File.Exists(entryPoint)) throw new FileNotFoundException("本地助手页面资源不存在。", entryPoint);

            await browser.EnsureCoreWebView2Async();
            core = browser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化未完成。");
            ConfigureBrowser(core, assetDirectory);
            browser.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Visible;
            fallbackText.Text = GetLoadingText();
            core.Navigate($"{AssistantOrigin}/index.html");
        }
        catch (Exception ex)
        {
            browser.Visibility = Visibility.Collapsed;
            ShowFallback(GetUnavailableText(ex), true);
        }
        finally
        {
            initializing = false;
        }
    }

    private void ConfigureBrowser(CoreWebView2 webView, string assetDirectory)
    {
        if (!virtualHostMapped)
        {
            webView.SetVirtualHostNameToFolderMapping(VirtualHost, assetDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            virtualHostMapped = true;
        }

        webView.Settings.AreDefaultScriptDialogsEnabled = false;
        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.IsStatusBarEnabled = false;
        webView.Settings.AreDefaultContextMenusEnabled = false;

        if (hostEventsAttached) return;
        webView.NavigationStarting += OnNavigationStarting;
        webView.NewWindowRequested += OnNewWindowRequested;
        webView.DownloadStarting += OnDownloadStarting;
        webView.WebMessageReceived += OnWebMessageReceived;
        webView.NavigationCompleted += OnNavigationCompleted;
        hostEventsAttached = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!AssistantWebProtocol.IsTrustedOrigin(e.Uri)) e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!loaded) return;
        if (!e.IsSuccess)
        {
            ShowFallback(GetUnavailableText($"页面加载失败：{e.WebErrorStatus}"), true);
            return;
        }

        fallback.Visibility = Visibility.Collapsed;
        PostPresentationState();
        PostFreshSnapshot();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!loaded || runtime == null || !AssistantWebProtocol.IsTrustedOrigin(e.Source)) return;
        if (!AssistantWebProtocol.TryParse(e.WebMessageAsJson, out var command, out var parseError, out var requestId))
        {
            if (requestId != null) PostResponse(requestId, false, error: parseError);
            return;
        }

        try
        {
            if (command!.Type == "ready")
            {
                var snapshot = runtime.GetSnapshot();
                PostEnvelope("snapshot", snapshot);
                PostResponse(command.Id, true, snapshot);
                return;
            }

            if (command.Type == "link.open")
            {
                OpenExternalLink(command.Payload.GetProperty("url").GetString()!);
                PostResponse(command.Id, true, new { opened = true });
                return;
            }

            var result = await runtime.HandleAsync(command.Type, command.SessionId, command.Payload);
            PostResponse(command.Id, true, result);
            PostFreshSnapshot();
        }
        catch (Exception ex)
        {
            var message = ex is ArgumentException or InvalidOperationException
                ? ex.Message
                : IsEnglish() ? "The Assistant request failed." : "助手请求失败。";
            PostResponse(command!.Id, false, error: message);
        }
    }

    private void OnRuntimeChanged(AssistantRuntimeEvent value)
    {
        if (!loaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess())
            PostEnvelope(value.Type, value.Payload, value.SessionId, value.RunId);
        else
            _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (loaded) PostEnvelope(value.Type, value.Payload, value.SessionId, value.RunId);
            }));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => DispatchHostState(PostTheme);

    private void OnLanguageChanged() => DispatchHostState(PostLocale);

    private void DispatchHostState(Action callback)
    {
        if (!loaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) callback();
        else _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, callback);
    }

    private void PostPresentationState()
    {
        PostTheme();
        PostLocale();
    }

    private void PostTheme() => PostEnvelope("theme", new { theme = ThemeManager.Instance.IsDark ? "dark" : "light" });

    private void PostLocale() => PostEnvelope("locale", new { locale = IsEnglish() ? "en-US" : "zh-CN" });

    private void PostFreshSnapshot()
    {
        try
        {
            if (runtime != null) PostEnvelope("snapshot", runtime.GetSnapshot());
        }
        catch (Exception ex)
        {
            ShowFallback(GetUnavailableText(ex), true);
        }
    }

    private void PostResponse(string id, bool ok, object? result = null, string? error = null) =>
        PostEnvelope("response", new { ok, result, error }, id);

    private void PostEnvelope(string type, object? payload, string? id = null, string? sessionId = null, string? runId = null)
    {
        if (!loaded || core == null) return;
        try
        {
            var envelope = new OutgoingEnvelope(1, id ?? Guid.NewGuid().ToString("N"), type, payload, sessionId, runId);
            core.PostWebMessageAsJson(JsonSerializer.Serialize(envelope, WebJson));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssistantWeb] Message delivery failed: {ex.Message}");
        }
    }

    private static void OpenExternalLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("链接必须是有效的 HTTP 或 HTTPS 地址。");

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowFallback(string message, bool allowRetry)
    {
        fallbackText.Text = message;
        fallbackContent.Children.Clear();
        fallbackContent.Children.Add(fallbackText);
        if (allowRetry)
        {
            var retry = new Button
            {
                Content = IsEnglish() ? "Retry" : "重试",
                MinWidth = 100,
                MinHeight = 34,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            retry.SetResourceReference(Control.StyleProperty, "BtnPrimary");
            retry.Click += async (_, _) =>
            {
                try
                {
                    runtime ??= AssistantRuntime.Instance;
                    runtime.Changed -= OnRuntimeChanged;
                    runtime.Changed += OnRuntimeChanged;
                    await InitializeBrowserAsync();
                }
                catch (Exception ex)
                {
                    ShowFallback(GetUnavailableText(ex), true);
                }
            };
            fallbackContent.Children.Add(retry);
        }
        fallback.Visibility = Visibility.Visible;
    }

    private bool IsEnglish()
    {
        try { return ToolBoxOption.Load().Language == "en-US"; }
        catch { return false; }
    }

    private string GetLoadingText() => IsEnglish() ? "Connecting to Assistant…" : "正在连接助手…";

    private string GetUnavailableText(Exception? error = null)
    {
        var message = IsEnglish()
            ? "Assistant is unavailable. Check WebView2 Runtime and local assistant resources, then retry."
            : "助手暂不可用。请检查 WebView2 Runtime 与本地助手资源后重试。";
        if (error is FileNotFoundException) return IsEnglish() ? "Local Assistant resources are missing. Rebuild or repair the application, then retry." : "本地助手页面资源缺失。请修复或重新构建应用后重试。";
        return message;
    }

    private sealed record OutgoingEnvelope(int Version, string Id, string Type, object? Payload, string? SessionId = null, string? RunId = null);
}
