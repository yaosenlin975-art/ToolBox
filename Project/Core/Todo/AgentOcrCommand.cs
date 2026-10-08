using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ToolBox.Services.Ocr;

namespace ToolBox.Core.Todo;

internal static class AgentOcrCommand
{
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxImageEdge = 16384;
    private const long MaxImagePixels = 32_000_000;

    internal static async Task<TaskResponse> ExecuteAsync(TaskRequest request, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (request.Operation != "ocr.file")
                throw new TaskCommandException("PROTOCOL_ERROR", "Unsupported OCR operation.", "operation");
            TaskProtocol.Validate(request);
            var payload = request.Payload!.Value;
            var path = payload.GetProperty("path").GetString()!;
            var language = payload.TryGetProperty("language", out var value) ? value.GetString()! : OcrService.DefaultLanguage;
            var fullPath = ValidateLocalPath(path);
            var bytes = await ReadImageAsync(fullPath, ct);
            ValidateImage(bytes);
            ct.ThrowIfCancellationRequested();

            var result = await OcrService.Instance.RecognizeBytesAsync(bytes, language, autoDownload: false, progress: null, ct: ct);
            ct.ThrowIfCancellationRequested();
            if (result.EngineUsed == "None")
                return TaskResponse.Failure(request, "ENGINE_UNAVAILABLE", "OCR is unavailable for this image.");

            var lines = result.Lines.Select(line => new
            {
                text = line.Text,
                confidence = line.Confidence,
                rect = line.Rect
            }).ToArray();
            return TaskResponse.Success(request, new { engine = result.EngineUsed, text = result.FullText, lines });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCommandException ex)
        {
            return TaskResponse.Failure(request, ex.Code, ex.Message, ex.Field);
        }
        catch (FileNotFoundException)
        {
            return TaskResponse.Failure(request, "NOT_FOUND", "The image file was not found.", "path");
        }
        catch (DirectoryNotFoundException)
        {
            return TaskResponse.Failure(request, "NOT_FOUND", "The image file was not found.", "path");
        }
        catch (DriveNotFoundException)
        {
            return TaskResponse.Failure(request, "NOT_FOUND", "The image file was not found.", "path");
        }
        catch (UnauthorizedAccessException)
        {
            return TaskResponse.Failure(request, "ACCESS_DENIED", "Access to the image file was denied.", "path");
        }
        catch (SecurityException)
        {
            return TaskResponse.Failure(request, "ACCESS_DENIED", "Access to the image file was denied.", "path");
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return TaskResponse.Failure(request, "ACCESS_DENIED", "Access to the image file was denied.", "path");
        }
        catch (IOException)
        {
            return TaskResponse.Failure(request, "IO_ERROR", "The image file could not be read.", "path");
        }
        catch (Exception)
        {
            return TaskResponse.Failure(request, "IO_ERROR", "The image could not be processed.");
        }
    }

    private static string ValidateLocalPath(string path)
    {
        if (path.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c)) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\??\\", StringComparison.Ordinal) ||
            path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] is not ('\\' or '/') || path.IndexOf(':', 2) >= 0)
            throw Invalid("An absolute local file path is required.", "path");

        if (path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw Invalid("The image path cannot contain relative segments.", "path");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid("The image path is invalid.", "path");
        }

        var root = Path.GetPathRoot(fullPath);
        if (root is null || new DriveInfo(root).DriveType == DriveType.Network)
            throw Invalid("Network paths are not accepted.", "path");

        RejectReparseComponents(fullPath, root);
        return fullPath;
    }

    private static void RejectReparseComponents(string fullPath, string root)
    {
        var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw Invalid("The path must identify an image file.", "path");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw Invalid("Reparse points are not accepted in the image path.", "path");
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Invalid("Reparse points are not accepted in the image path.", "path");
            if (i < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                throw Invalid("The image path contains a non-directory parent.", "path");
            if (i == parts.Length - 1 && (attributes & FileAttributes.Directory) != 0)
                throw Invalid("The path must identify an image file.", "path");
        }
    }

    private static async Task<byte[]> ReadImageAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RejectReparseComponents(path, Path.GetPathRoot(path)!);
        if (stream.Length > MaxFileBytes) throw Invalid("The image file exceeds 16 MiB.", "path");

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        if (stream.Length != bytes.Length) throw new IOException("The image changed while it was read.");
        return bytes;
    }

    private static void ValidateImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count == 0) throw Invalid("The file is not a supported image.", "path");
            var frame = decoder.Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || width > MaxImageEdge || height > MaxImageEdge ||
                (long)width * height > MaxImagePixels)
                throw Invalid("The image dimensions exceed the supported limit.", "path");
        }
        catch (TaskCommandException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or ArgumentException or OverflowException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw Invalid("The file is not a supported image.", "path");
        }
    }

    private static TaskCommandException Invalid(string message, string field) => new("INVALID_ARGUMENT", message, field);
}
