using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
}
