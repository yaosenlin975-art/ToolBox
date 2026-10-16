using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Core.Llm;
using ToolBox.Core.Providers;
using ToolBox.Core.Security;
using ToolBox.Core.Tools;

internal static class Program
{
    private static int assertions;

    private static async Task Main()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ToolBox.AssistantTools." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "allowed");
        var outside = Path.Combine(temp, "allowed-sibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            CheckWhitelist(temp, root, outside);
            CheckFileTools(temp, root, outside);
            await CheckImageTools(temp, root, outside);
            await CheckProviderImages(temp, root);
            await CheckOpenAiToolCalls();
            await CheckRegistry();
            Console.WriteLine($"AssistantTools checks passed ({assertions} assertions).");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void CheckWhitelist(string temp, string root, string outside)
    {
        var configPath = Path.Combine(temp, "isolated-whitelist.json");
        var whitelist = new FileAccessWhitelist(configPath);
        whitelist.Add(root);
        Check(whitelist.IsAllowed(root), "root is allowed");
        Check(whitelist.IsAllowed(Path.Combine(root, "missing", "file.txt")), "missing descendant is allowed");
        Check(!whitelist.IsAllowed(outside), "sibling prefix is denied");
        Check(!whitelist.IsAllowed(Path.Combine(temp, "outside", "file.txt")), "outside sibling is denied");
        Check(whitelist.IsAllowed(Path.GetRelativePath(Environment.CurrentDirectory, root)), "relative path inside root is allowed");
        Check(!whitelist.IsAllowed(Path.GetRelativePath(Environment.CurrentDirectory, outside)), "relative path outside root is denied");
        Check(!whitelist.IsAllowed(@"\\server\share\file.txt"), "UNC path is denied");
        Check(!whitelist.IsAllowed(@"\\?\C:\file.txt"), "extended device path is denied");
        Check(!whitelist.IsAllowed(@"\\.\NUL"), "device path is denied");
        Check(!whitelist.IsAllowed(@"C:\NUL.txt"), "reserved device name is denied");
        Check(!whitelist.IsAllowed(@"C:\COM¹.txt"), "superscript device name is denied");
        Check(!whitelist.IsAllowed("https://example.test/file.txt"), "URI is denied");

        var volumeRoot = Path.GetPathRoot(root)!;
        var volumeWhitelist = new FileAccessWhitelist(Path.Combine(temp, "volume-whitelist.json"));
        volumeWhitelist.Add(volumeRoot);
        Check(volumeWhitelist.AllowedPaths.Single() == volumeRoot, "drive root keeps its trailing separator");
        Check(volumeWhitelist.IsAllowed(Path.Combine(volumeRoot, "missing.txt")), "drive root descendants are allowed");
        volumeWhitelist.Remove(volumeRoot);
        Check(volumeWhitelist.AllowedPaths.Count == 0, "drive root can be removed");

        var restored = new FileAccessWhitelist(configPath);
        Check(restored.IsAllowed(root), "persisted roots reload");

        try
        {
            var link = Path.Combine(root, "outside-link");
            Directory.CreateSymbolicLink(link, outside);
            Check(!whitelist.IsAllowed(Path.Combine(link, "missing.txt")), "reparse point to outside is denied for missing target");

            var insideTarget = Path.Combine(root, "inside-target");
            Directory.CreateDirectory(insideTarget);
            var insideLink = Path.Combine(root, "inside-link");
            Directory.CreateSymbolicLink(insideLink, insideTarget);
            Check(whitelist.IsAllowed(Path.Combine(insideLink, "missing.txt")), "reparse point inside root remains allowed");

            var projectRoot = Path.Combine(root, "project");
            var projectEscapeTarget = Path.Combine(root, "project-escape-target");
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(projectEscapeTarget);
            var projectEscapeLink = Path.Combine(projectRoot, "escape-link");
            Directory.CreateSymbolicLink(projectEscapeLink, projectEscapeTarget);
            var projectEscapePath = Path.Combine(projectEscapeLink, "missing.txt");
            Check(whitelist.IsAllowed(projectEscapePath), "project escape link remains inside whitelist");
            Check(!whitelist.IsAllowedWithin(projectEscapePath, projectRoot), "project root intersection rejects a link into another allowed directory");

            var explicitLink = Path.Combine(temp, "explicit-root-link");
            Directory.CreateSymbolicLink(explicitLink, outside);
            var explicitWhitelist = new FileAccessWhitelist(Path.Combine(temp, "explicit-root-whitelist.json"));
            explicitWhitelist.Add(explicitLink);
            Check(explicitWhitelist.IsAllowed(Path.Combine(explicitLink, "missing.txt")), "explicitly selected linked root is allowed");
            Check(new FileAccessWhitelist(Path.Combine(temp, "explicit-root-whitelist.json")).IsAllowed(Path.Combine(explicitLink, "missing.txt")), "explicit linked root persists safely");

            Directory.Delete(explicitLink);
            Directory.CreateSymbolicLink(explicitLink, root);
            Check(!explicitWhitelist.IsAllowed(Path.Combine(explicitLink, "missing.txt")), "retargeted explicit root link is denied");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine($"SKIP reparse point checks: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckFileTools(string temp, string root, string outside)
    {
        var whitelist = new FileAccessWhitelist(Path.Combine(temp, "file-tools-whitelist.json"));
        whitelist.Add(root);
        typeof(FileAccessWhitelist).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, whitelist);

        var allowedFile = Path.Combine(root, "sample.txt");
        File.WriteAllText(allowedFile, "before");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            Throws<OperationCanceledException>(() => FileTools.DeleteFile(allowedFile, canceled.Token));
            Throws<OperationCanceledException>(() => FileTools.MoveFile(allowedFile, Path.Combine(root, "moved.txt"), canceled.Token));
        }
        Check(FileTools.ReadFile(allowedFile) == "before", "read_file reads allowed path");
        Check(FileTools.FileExists(allowedFile) == "true", "file_exists checks allowed path");
        Check(FileTools.FileExists(Path.Combine(root, "missing.txt")) == "false", "file_exists checks allowed missing path");
        Check(FileTools.ListDirectory(root).Contains("sample.txt", StringComparison.Ordinal), "list_directory lists allowed path");

        var writtenFile = Path.Combine(root, "written.txt");
        Check(FileTools.WriteFile(writtenFile, "after").StartsWith("已写入:", StringComparison.Ordinal), "write_file writes allowed path");
        Check(File.ReadAllText(writtenFile) == "after", "write_file content is correct");
        var newDirectory = Path.Combine(root, "new-directory");
        Check(FileTools.CreateDirectory(newDirectory).StartsWith("已创建:", StringComparison.Ordinal), "create_directory creates allowed path");

        var denied = outside + "-escape";
        Check(FileTools.ReadFile(denied).StartsWith("安全限制:", StringComparison.Ordinal), "read_file validates path");
        Check(FileTools.WriteFile(denied, "x").StartsWith("安全限制:", StringComparison.Ordinal), "write_file validates path");
        Check(FileTools.FileExists(denied).StartsWith("安全限制:", StringComparison.Ordinal), "file_exists validates path");
        Check(FileTools.ListDirectory(denied).StartsWith("安全限制:", StringComparison.Ordinal), "list_directory validates path");
        Check(FileTools.CreateDirectory(denied).StartsWith("安全限制:", StringComparison.Ordinal), "create_directory validates path");
        Check(FileTools.DeleteFile(denied).StartsWith("安全限制:", StringComparison.Ordinal), "delete_file validates before confirmation");
        Check(FileTools.CopyFile(allowedFile, denied).StartsWith("安全限制:", StringComparison.Ordinal), "copy_file validates destination");
        Check(FileTools.MoveFile(denied, allowedFile).StartsWith("安全限制:", StringComparison.Ordinal), "move_file validates source before confirmation");
    }

    private static async Task CheckImageTools(string temp, string root, string outside)
    {
        var whitelist = new FileAccessWhitelist(Path.Combine(temp, "image-whitelist.json"));
        whitelist.Add(root);
        SetWhitelist(whitelist);

        var pngPath = Path.Combine(root, "real.png");
        WriteEncodedImage(pngPath, new PngBitmapEncoder());
        Check(ImageTools.GetMimeType(ImageTools.ReadImageBytes(pngPath)) == "image/png", "image: decoded PNG reports image/png");

        var mislabeledPath = Path.Combine(root, "mislabeled.png");
        WriteEncodedImage(mislabeledPath, new JpegBitmapEncoder());
        Check(ImageTools.GetMimeType(ImageTools.ReadImageBytes(mislabeledPath)) == "image/jpeg", "image: MIME follows real content, not extension");

        var gifPath = Path.Combine(root, "real.gif");
        WriteEncodedImage(gifPath, new GifBitmapEncoder());
        Check(ImageTools.GetMimeType(ImageTools.ReadImageBytes(gifPath)) == "image/gif", "image: decoded GIF reports image/gif");

        var bmpPath = Path.Combine(root, "real.bmp");
        WriteEncodedImage(bmpPath, new BmpBitmapEncoder());
        Check(ImageTools.GetMimeType(ImageTools.ReadImageBytes(bmpPath)) == "image/bmp", "image: decoded BMP reports image/bmp");

        Check(ImageTools.GetMimeType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) == "image/png", "image: PNG magic detected");
        Check(ImageTools.GetMimeType([0xFF, 0xD8, 0xFF, 0xE0]) == "image/jpeg", "image: JPEG magic detected");
        Check(ImageTools.GetMimeType("GIF87a"u8) == "image/gif", "image: GIF87a magic detected");
        Check(ImageTools.GetMimeType("GIF89a"u8) == "image/gif", "image: GIF89a magic detected");
        Check(ImageTools.GetMimeType([0x42, 0x4D, 0x00, 0x00]) == "image/bmp", "image: BMP magic detected");
        Check(ImageTools.GetMimeType([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]) == "image/webp", "image: WebP magic detected");
        Throws<InvalidDataException>(() => ImageTools.GetMimeType("NOTIMAGE"u8));

        var deniedPath = Path.Combine(outside, "denied.png");
        File.Copy(pngPath, deniedPath);
        Throws<UnauthorizedAccessException>(() => ImageTools.ReadImageBytes(deniedPath));
        Throws<UnauthorizedAccessException>(() => ImageTools.ReadImageBytes("https://example.test/a.png"));

        SetWhitelist(new FileAccessWhitelist(Path.Combine(temp, "image-empty-whitelist.json")));
        Throws<UnauthorizedAccessException>(() => ImageTools.ReadImageBytes(pngPath));
        SetWhitelist(whitelist);

        Throws<FileNotFoundException>(() => ImageTools.ReadImageBytes(Path.Combine(root, "missing.png")));

        var emptyFile = Path.Combine(root, "empty.png");
        File.WriteAllBytes(emptyFile, []);
        Throws<InvalidDataException>(() => ImageTools.ReadImageBytes(emptyFile));

        var oversized = Path.Combine(root, "oversized.png");
        using (var stream = File.Create(oversized)) { stream.SetLength(10L * 1024 * 1024 + 1); }
        Throws<InvalidDataException>(() => ImageTools.ReadImageBytes(oversized));

        var junk = Path.Combine(root, "junk.png");
        File.WriteAllBytes(junk, "NOTIMAGE"u8.ToArray());
        Throws<InvalidDataException>(() => ImageTools.ReadImageBytes(junk));

        var truncatedPng = Path.Combine(root, "truncated.png");
        File.WriteAllBytes(truncatedPng, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        Throws<InvalidDataException>(() => ImageTools.ReadImageBytes(truncatedPng));

        var webp = Path.Combine(root, "fake.webp");
        File.WriteAllBytes(webp, [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0, 0, 0, 0]);
        Throws<NotSupportedException>(() => ImageTools.ReadImageBytes(webp));

        var hugeBmp = Path.Combine(root, "huge.bmp");
        WriteOversizedBmp(hugeBmp, 9000, 5000);
        Throws<InvalidDataException>(() => ImageTools.ReadImageBytes(hugeBmp));

        var provider = new FakeImageProvider();
        var multimodal = new ModelInfo { ModelId = "gpt-4o", SupportsMultimodal = true };
        Check(await ImageTools.DescribeImageAsync(pngPath, "描述", () => (provider, multimodal)) == "fake-ok", "image: describe_image returns provider answer");
        Check(provider.LastImagePath == pngPath, "image: image path forwarded to provider");
        await ThrowsAsync<NotSupportedException>(async () => await ImageTools.DescribeImageAsync(pngPath, "描述", () => (provider, new ModelInfo { ModelId = "text-only", SupportsMultimodal = false })));
        await ThrowsAsync<UnauthorizedAccessException>(async () => await ImageTools.DescribeImageAsync(deniedPath, "描述", () => (provider, multimodal)));
        await ThrowsAsync<ArgumentException>(async () => await ImageTools.DescribeImageAsync(pngPath, "  ", () => (provider, multimodal)));

        provider.ReturnError = true;
        await ThrowsAsync<InvalidOperationException>(async () => await ImageTools.DescribeImageAsync(pngPath, "描述", () => (provider, multimodal)));

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => await ImageTools.DescribeImageAsync(pngPath, "描述", () => (provider, multimodal), canceled.Token));
        }
    }

    private static void SetWhitelist(FileAccessWhitelist whitelist)
    {
        typeof(FileAccessWhitelist).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, whitelist);
    }

    private static void WriteEncodedImage(string path, BitmapEncoder encoder)
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[2 * 2 * 4], 2 * 4);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteOversizedBmp(string path, int width, int height)
    {
        int rowBytes = (width + 31) / 32 * 4;
        int dataSize = rowBytes * height;
        int offset = 14 + 40 + 2 * 4;
        var bytes = new byte[offset + dataSize];
        bytes[0] = 0x42;
        bytes[1] = 0x4D;
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 4), offset + dataSize);
        BitConverter.TryWriteBytes(bytes.AsSpan(10, 4), offset);
        BitConverter.TryWriteBytes(bytes.AsSpan(14, 4), 40);
        BitConverter.TryWriteBytes(bytes.AsSpan(18, 4), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(22, 4), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(26, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(34, 4), dataSize);
        using var stream = File.Create(path);
        stream.Write(bytes);
    }

    private static async Task CheckProviderImages(string temp, string root)
    {
        var whitelist = new FileAccessWhitelist(Path.Combine(temp, "provider-whitelist.json"));
        whitelist.Add(root);
        SetWhitelist(whitelist);

        var pngPath = Path.Combine(root, "real.png");
        var base64 = Convert.ToBase64String(ImageTools.ReadImageBytes(pngPath));
        var model = new ModelInfo { ModelId = "gpt-4o", ProviderName = "OpenAI Compatible", SupportsMultimodal = true };
        var messages = new ChatMessage[] { new() { Role = "user", Content = "看图", ImagePath = pngPath } };

        var openAi = new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n");
        await Drain(new OpenAiProvider("k", "https://example.test/v1", model, openAi).ChatAsync(messages, null));
        Check(openAi.LastBody?.Contains($"data:image/png;base64,{base64}", StringComparison.Ordinal) == true, "provider: OpenAI sends image as data URI with real MIME");

        var anthropic = new CapturingHandler("data: {\"type\":\"message_stop\"}\n\n");
        await Drain(new AnthropicProvider("k", "https://example.test", model, anthropic).ChatAsync(messages, null));
        Check(anthropic.LastBody?.Contains("\"media_type\":\"image/png\"", StringComparison.Ordinal) == true &&
            anthropic.LastBody.Contains(base64, StringComparison.Ordinal), "provider: Anthropic sends base64 with media_type");

        var ollama = new CapturingHandler("{\"message\":{\"content\":\"ok\"},\"done\":true}\n");
        await Drain(new OllamaProvider("http://localhost:11434", model, ollama).ChatAsync(messages, null));
        Check(ollama.LastBody?.Contains($"\"images\":[\"{base64}\"]", StringComparison.Ordinal) == true, "provider: Ollama sends images array with base64");
    }

    private static async Task CheckOpenAiToolCalls()
    {
        var model = new ModelInfo { ModelId = "gpt-4o", SupportsMultimodal = true };
        const string multiSse = """
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a\"}"}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"list_directory","arguments":"{}"}}]}}]}

data: [DONE]

""";
        var multi = new OpenAiProvider("k", "https://example.test/v1", model, new CapturingHandler(multiSse));
        var calls = new List<ToolCallInfo>();
        await foreach (var chunk in multi.ChatAsync(Array.Empty<ChatMessage>(), null))
            if (chunk.ToolCalls != null) calls.AddRange(chunk.ToolCalls);
        Check(calls.Count == 2, "openai: two streamed tool calls parsed");
        Check(calls[0].Id == "call_a" && calls[0].Name == "read_file" && calls[0].Arguments == "{\"path\":\"a\"}", "openai: index 0 accumulates id/name/arguments");
        Check(calls[1].Id == "call_b" && calls[1].Name == "list_directory" && calls[1].Arguments == "{}", "openai: index 1 accumulates id/name/arguments");

        const string singleSse = """
data: {"choices":[{"delta":{"tool_calls":[{"function":{"name":"read_file","arguments":"{}"},"id":"call_x"}]}}]}

data: [DONE]

""";
        var single = new OpenAiProvider("k", "https://example.test/v1", model, new CapturingHandler(singleSse));
        var singleCalls = new List<ToolCallInfo>();
        await foreach (var chunk in single.ChatAsync(Array.Empty<ChatMessage>(), null))
            if (chunk.ToolCalls != null) singleCalls.AddRange(chunk.ToolCalls);
        Check(singleCalls.Count == 1 && singleCalls[0].Id == "call_x" && singleCalls[0].Name == "read_file" && singleCalls[0].Arguments == "{}", "openai: single index-less tool call still parsed");
    }

    private static async Task Drain(IAsyncEnumerable<ChatChunk> stream)
    {
        await foreach (var _ in stream) { }
    }

    private static async Task CheckRegistry()
    {
        var fixture = new RegistryFixture();
        var registry = new ToolRegistry().Register(fixture);
        using var document = JsonDocument.Parse("""{"enabled":true,"text":"hello","count":7}""");
        var args = new Dictionary<string, object>
        {
            ["enabled"] = document.RootElement.GetProperty("enabled").Clone(),
            ["text"] = document.RootElement.GetProperty("text").Clone(),
            ["count"] = document.RootElement.GetProperty("count").Clone()
        };
        Check(await registry.ExecuteAsync("instance_echo", args) == "fixture:hello:True:7", "JsonElement values convert to declared types and call instance target");

        using var optionalSchema = JsonDocument.Parse(registry.GetAllTools().Single(t => t.Name == "optional").ToJsonSchema());
        Check(optionalSchema.RootElement.GetProperty("required").GetArrayLength() == 0, "defaulted parameters are optional in the public schema");
        Check(await registry.ExecuteAsync("optional", new Dictionary<string, object>()) == "5", "defaulted parameter executes without an argument");
        using var schema = JsonDocument.Parse(registry.GetAllTools().Single(t => t.Name == "wait").ToJsonSchema());
        Check(!schema.RootElement.GetProperty("properties").TryGetProperty("cancellationToken", out _), "cancellation token is absent from schema");
        Check(schema.RootElement.GetProperty("additionalProperties").GetBoolean() == false, "schema rejects unknown parameters");

        Throws<MissingParameterException>(() => registry.Execute("instance_echo", new Dictionary<string, object>()));
        Throws<UnknownParameterException>(() => registry.Execute("instance_echo", new Dictionary<string, object> { ["enabled"] = true, ["text"] = "x", ["count"] = 1, ["extra"] = true }));
        Throws<InvalidToolParameterException>(() => registry.Execute("instance_echo", new Dictionary<string, object> { ["enabled"] = "true", ["text"] = "x", ["count"] = 1 }));
        Throws<InvalidOperationException>(() => registry.Execute("fail", new Dictionary<string, object>()));

        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(registry.Execute("number", new Dictionary<string, object> { ["value"] = 1.5m }) == "1.5", "numeric conversion is culture invariant");
            Throws<InvalidToolParameterException>(() => registry.Execute("number", new Dictionary<string, object> { ["value"] = "1,5" }));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }

        using var stringNumber = JsonDocument.Parse("\"12\"");
        Throws<InvalidToolParameterException>(() => registry.Execute("number", new Dictionary<string, object> { ["value"] = stringNumber.RootElement.Clone() }));

        using var cancellation = new CancellationTokenSource();
        var pending = registry.ExecuteAsync("wait", new Dictionary<string, object>(), cancellation.Token);
        Check(fixture.ReceivedCancelableToken, "CancellationToken is injected");
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    private static void Check(bool condition, string name)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("Failed: " + name);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class RegistryFixture
    {
        [Tool("optional", "")]
        public string Optional([ToolParam("count")] int count = 5) => count.ToString(CultureInfo.InvariantCulture);

        public bool ReceivedCancelableToken { get; private set; }

        [Tool("instance_echo", "")]
        public string Echo([ToolParam("enabled")] bool enabled, [ToolParam("text")] string text, [ToolParam("count")] int count) =>
            $"fixture:{text}:{enabled}:{count}";

        [Tool("number", "")]
        public string Number([ToolParam("value")] double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        [Tool("wait", "")]
        public async Task<string> Wait(CancellationToken cancellationToken)
        {
            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "not canceled";
        }

        [Tool("fail", "")]
        public string Fail() => throw new InvalidOperationException("business failure");
    }

    private sealed class FakeImageProvider : ILlmProvider
    {
        public string Name => "fake";

        public string? LastImagePath { get; private set; }

        public bool ReturnError { get; set; }

        public async IAsyncEnumerable<ChatChunk> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolInfo>? tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var message in messages)
                if (!string.IsNullOrEmpty(message.ImagePath)) LastImagePath = message.ImagePath;
            await Task.Yield();
            if (ReturnError)
            {
                yield return new ChatChunk { Error = "fake-boom" };
                yield break;
            }
            yield return new ChatChunk { Text = "fake-ok" };
            yield return new ChatChunk { IsDone = true };
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string response;

        public CapturingHandler(string response) => this.response = response;

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
