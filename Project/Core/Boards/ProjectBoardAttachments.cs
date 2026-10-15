// 看板图片附件：元数据内嵌 board.json，图片本体存于项目内 .toolbox/board-assets。
// 写入先按流限量读取并真实解码，再按 SHA256 去重并以临时文件原子改名；删除先写 tombstone journal 再删元数据。
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ToolBox.Core.Todo;

namespace ToolBox.Core.Boards;

public sealed partial class ProjectBoardStore
{
    private const int AttachmentJournalSchemaVersion = 1;
    private const int AttachmentMaxBytes = 10 * 1024 * 1024;
    private const string AttachmentAssetsDirectoryName = "board-assets";
    private const string AttachmentTombstoneSuffix = ".tombstone";
    private const string AttachmentJournalFileName = "attachments.journal.json";

    private static readonly HashSet<string> AttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp"
    };

    private TaskResponse AttachmentAdd(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid cardId = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, cardId);
        EnsureCardActive(card, "id");
        BoardIdentity? identity = ReadIdentity(payload);
        DateTime now = DateTime.UtcNow;
        EnsureWritable(card, identity, now);

        string sourcePath = AttachmentSourcePath(payload);
        string extension = AttachmentExtension(sourcePath);
        byte[] bytes = DecodeValidatedImage(ReadLimitedFileBytes(sourcePath));
        string sha256 = Hash(bytes);
        ProjectBoardAttachment? existing = (card.Attachments ?? [])
            .FirstOrDefault(item => string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return TaskResponse.Success(_currentRequest!, new
            {
                attachment = existing,
                revision = snapshot.Board.Revision,
                boardRevision = snapshot.Board.Revision,
                commentRevision = snapshot.Board.CommentRevision
            });

        Guid attachmentId = Guid.NewGuid();
        string relativePath = AttachmentRelativePath(cardId, attachmentId, extension);
        string storedPath = ResolveToolboxPath(registration.Path, relativePath);
        EnsureAttachmentDirectory(registration.Path, cardId);
        WriteNewAtomic(storedPath, bytes);

        var attachment = new ProjectBoardAttachment(attachmentId, Path.GetFileName(sourcePath), relativePath,
            bytes.Length, sha256, now, identity?.ActorId ?? "local-user");
        ProjectBoard updated = SetCardAttachments(snapshot.Board, cardId, (card.Attachments ?? []).Append(attachment).ToArray()) with
        {
            Revision = NextRevision(snapshot.Board)
        };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new
        {
            attachment,
            revision = updated.Revision,
            boardRevision = updated.Revision,
            commentRevision = updated.CommentRevision
        });
        try
        {
            SaveBoard(registration.Path, snapshot, updated, result);
        }
        catch
        {
            // 元数据未写入时清理刚复制进项目的图片，保证失败不留半成品。
            TryDeleteFile(storedPath);
            throw;
        }
        NotifyChanged();
        return result;
    }

    private TaskResponse AttachmentList(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        Guid cardId = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, cardId);
        ProjectBoardAttachment[] attachments = (card.Attachments ?? []).ToArray();
        var items = new List<object>(attachments.Length);
        foreach (ProjectBoardAttachment attachment in attachments)
        {
            (bool exists, bool hashMatch) = AttachmentAvailability(registration.Path, attachment);
            items.Add(new
            {
                attachment.Id,
                attachment.FileName,
                attachment.RelativePath,
                attachment.Size,
                attachment.Sha256,
                attachment.AddedAt,
                attachment.AddedBy,
                exists,
                hashMatch
            });
        }
        return TaskResponse.Success(_currentRequest!, new
        {
            attachments = items,
            revision = snapshot.Board.Revision,
            boardRevision = snapshot.Board.Revision,
            commentRevision = snapshot.Board.CommentRevision
        });
    }

    private TaskResponse AttachmentRemove(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid attachmentId = RequiredGuid(payload, "attachmentId");
        (ProjectBoardCard card, ProjectBoardAttachment attachment) = FindAttachment(snapshot.Board, attachmentId);
        EnsureCardActive(card, "attachmentId");
        DateTime now = DateTime.UtcNow;
        EnsureWritable(card, ReadIdentity(payload), now);

        string storedPath = ResolveToolboxPath(registration.Path, attachment.RelativePath);
        AttachmentTombstoneEntry? tombstone = BeginAttachmentRemoval(registration.Path, card.Id, attachment, storedPath);
        ProjectBoard updated = SetCardAttachments(snapshot.Board, card.Id,
            (card.Attachments ?? []).Where(item => item.Id != attachmentId).ToArray()) with
        {
            Revision = NextRevision(snapshot.Board)
        };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new
        {
            attachment,
            revision = updated.Revision,
            boardRevision = updated.Revision,
            commentRevision = updated.CommentRevision
        });
        try
        {
            SaveBoard(registration.Path, snapshot, updated, result);
        }
        catch
        {
            RestoreAttachmentFile(registration.Path, tombstone);
            throw;
        }
        FinishAttachmentRemoval(registration.Path, tombstone);
        NotifyChanged();
        return result;
    }

    private static ProjectBoard SetCardAttachments(ProjectBoard board, Guid cardId, IReadOnlyList<ProjectBoardAttachment> attachments) => board with
    {
        Cards = board.Cards.Select(card => card.Id == cardId ? card with { Attachments = attachments } : card).ToArray()
    };

    private static (ProjectBoardCard Card, ProjectBoardAttachment Attachment) FindAttachment(ProjectBoard board, Guid attachmentId)
    {
        foreach (ProjectBoardCard card in board.Cards)
            foreach (ProjectBoardAttachment attachment in card.Attachments ?? [])
                if (attachment.Id == attachmentId) return (card, attachment);
        throw new TaskCommandException("NOT_FOUND", "Board attachment not found.", "attachmentId");
    }

    private static string AttachmentSourcePath(JsonElement payload)
    {
        string value = RequiredString(payload, "file");
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
            throw Invalid("An absolute local image path is required.", "file");
        string path;
        try { path = Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid("The image path is invalid.", "file");
        }
        RejectReparsePoint(path);
        if (!File.Exists(path)) throw new TaskCommandException("NOT_FOUND", "The image file was not found.", "file");
        return path;
    }

    private static string AttachmentExtension(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension) || !AttachmentExtensions.Contains(extension))
            throw Invalid("Only png, jpg, jpeg, gif, and bmp images can be attached.", "file");
        return extension.ToLowerInvariant();
    }

    private static byte[] ReadLimitedFileBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > AttachmentMaxBytes)
            throw new TaskCommandException("ATTACHMENT_TOO_LARGE", "An attachment must be at most 10 MiB.", "file");
        // 只按流读取受限字节数，避免把超大文件整体读入内存；多读一字节用于发现读取期间的增长。
        byte[] buffer = new byte[length + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > AttachmentMaxBytes)
            throw new TaskCommandException("ATTACHMENT_TOO_LARGE", "An attachment must be at most 10 MiB.", "file");
        return buffer[..total];
    }

    private static byte[] DecodeValidatedImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using System.Drawing.Image image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return bytes;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            throw Invalid("The file is not a decodable image.", "file");
        }
    }

    private static (bool Exists, bool HashMatch) AttachmentAvailability(string root, ProjectBoardAttachment attachment)
    {
        string path = ResolveToolboxPath(root, attachment.RelativePath);
        if (!File.Exists(path)) return (false, false);
        return (true, string.Equals(HashFile(path), attachment.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static string AttachmentRelativePath(Guid cardId, Guid attachmentId, string extension) =>
        $".toolbox/{AttachmentAssetsDirectoryName}/{cardId:D}/{attachmentId:D}{extension}";

    private static string EnsureAttachmentDirectory(string root, Guid cardId)
    {
        string assetsDirectory = Path.Combine(root, ".toolbox", AttachmentAssetsDirectoryName);
        Directory.CreateDirectory(assetsDirectory);
        RejectReparsePoint(assetsDirectory);
        string cardDirectory = Path.Combine(assetsDirectory, cardId.ToString("D"));
        Directory.CreateDirectory(cardDirectory);
        RejectReparsePoint(cardDirectory);
        return cardDirectory;
    }

    private static string ResolveToolboxPath(string root, string relativePath)
    {
        string toolbox = Path.GetFullPath(Path.Combine(root, ".toolbox"));
        string path;
        try { path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Corrupt("A stored attachment path is invalid.");
        }
        if (path == toolbox || !IsWithinRoot(toolbox, path))
            throw Corrupt("A stored attachment path is outside the project .toolbox directory.");
        return path;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using SHA256 sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetAttachmentJournalPath(string root) => Path.Combine(root, ".toolbox", AttachmentJournalFileName);

    private static AttachmentTombstoneEntry? BeginAttachmentRemoval(string root, Guid cardId, ProjectBoardAttachment attachment, string storedPath)
    {
        if (!File.Exists(storedPath)) return null;
        RejectReparsePoint(storedPath);
        string tombstoneRelativePath = attachment.RelativePath + AttachmentTombstoneSuffix;
        string tombstonePath = ResolveToolboxPath(root, tombstoneRelativePath);
        // 先落盘删除意图（含待删文件当前内容的哈希），再改名，保证崩溃后可按 journal 判定还原或清理。
        var entry = new AttachmentTombstoneEntry(attachment.Id, cardId, attachment.RelativePath, tombstoneRelativePath, HashFile(storedPath));
        AppendTombstoneEntry(root, entry);
        File.Move(storedPath, tombstonePath);
        return entry;
    }

    private static void AppendTombstoneEntry(string root, AttachmentTombstoneEntry entry)
    {
        string path = GetAttachmentJournalPath(root);
        AttachmentJournal journal = new(AttachmentJournalSchemaVersion, []);
        string? existingHash = null;
        if (File.Exists(path))
        {
            RejectReparsePoint(path);
            (journal, existingHash) = ParseAttachmentJournal(path);
        }
        WriteAttachmentJournal(path, journal.Entries.Append(entry).ToArray(), existingHash);
    }

    private static AttachmentJournal WriteAttachmentJournal(string path, IReadOnlyList<AttachmentTombstoneEntry> entries, string? expectedExistingHash)
    {
        var journal = new AttachmentJournal(AttachmentJournalSchemaVersion, entries);
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions), expectedExistingHash, "storage");
        return journal;
    }

    private static (AttachmentJournal Journal, string Hash) ParseAttachmentJournal(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument document = JsonDocument.Parse(bytes);
            RequireProperties(document.RootElement, "schemaVersion", "entries");
            JsonElement entries = document.RootElement.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array) throw Corrupt("The attachment journal is invalid.");
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                RequireProperties(entry, "attachmentId", "cardId", "storedPath", "tombstonePath", "sha256");
                if (entry.GetProperty("sha256").ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    throw Corrupt("The attachment journal is invalid.");
            }
            AttachmentJournal journal = JsonSerializer.Deserialize<AttachmentJournal>(bytes, JsonOptions)
                ?? throw Corrupt("The attachment journal is empty.");
            if (journal.SchemaVersion != AttachmentJournalSchemaVersion || journal.Entries == null)
                throw Corrupt("The attachment journal header is invalid.");
            foreach (AttachmentTombstoneEntry entry in journal.Entries)
            {
                if (entry == null || entry.AttachmentId == Guid.Empty || entry.CardId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(entry.StoredPath) || string.IsNullOrWhiteSpace(entry.TombstonePath) ||
                    (entry.Sha256 != null && !IsSha256(entry.Sha256)))
                    throw Corrupt("The attachment journal is invalid.");
            }
            return (journal, Hash(bytes));
        }
        catch (TaskCommandException) { throw; }
        catch (JsonException) { throw Corrupt("The attachment journal is not valid JSON or contains unknown fields."); }
        catch (KeyNotFoundException) { throw Corrupt("The attachment journal is missing a required field."); }
        catch (InvalidOperationException) { throw Corrupt("The attachment journal has an invalid shape."); }
    }

    private static bool RemoveAttachmentJournal(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool FinishAttachmentRemoval(string root, AttachmentTombstoneEntry? tombstone)
    {
        if (tombstone == null) return true;
        try
        {
            if (!TryDeleteFile(ResolveToolboxPath(root, tombstone.TombstonePath))) return false;
            string path = GetAttachmentJournalPath(root);
            if (!File.Exists(path)) return true;
            (AttachmentJournal journal, string hash) = ParseAttachmentJournal(path);
            AttachmentTombstoneEntry[] remaining = journal.Entries.Where(item => item.AttachmentId != tombstone.AttachmentId).ToArray();
            if (remaining.Length == 0) return RemoveAttachmentJournal(path);
            WriteAttachmentJournal(path, remaining, hash);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TaskCommandException)
        {
            // 清理失败时保留 journal，交给下次读取时的恢复流程继续，不使已完成的删除回滚。
            return false;
        }
    }

    private static bool RestoreAttachmentFile(string root, AttachmentTombstoneEntry? tombstone)
    {
        if (tombstone == null) return true;
        try
        {
            string storedPath = ResolveToolboxPath(root, tombstone.StoredPath);
            string tombstonePath = ResolveToolboxPath(root, tombstone.TombstonePath);
            if (File.Exists(tombstonePath) && !File.Exists(storedPath)) File.Move(tombstonePath, storedPath);
            string path = GetAttachmentJournalPath(root);
            if (!File.Exists(path)) return true;
            (AttachmentJournal journal, string hash) = ParseAttachmentJournal(path);
            AttachmentTombstoneEntry[] remaining = journal.Entries.Where(item => item.AttachmentId != tombstone.AttachmentId).ToArray();
            if (remaining.Length == 0) return RemoveAttachmentJournal(path);
            WriteAttachmentJournal(path, remaining, hash);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TaskCommandException)
        {
            return false;
        }
    }

    private int RecoverAttachmentTombstones(string root, ProjectBoard board)
    {
        // 一个辅助 journal 永远不能让看板变成不可读：解析/校验/路径问题只跳过本次恢复，保留 journal 与文件。
        try
        {
            string directory = Path.Combine(root, ".toolbox");
            if (!Directory.Exists(directory)) return 0;
            RejectReparsePoint(directory);
            string path = Path.Combine(directory, AttachmentJournalFileName);
            if (!File.Exists(path)) return 0;
            RejectReparsePoint(path);
            (AttachmentJournal journal, _) = ParseAttachmentJournal(path);
            int recovered = 0;
            bool complete = true;
            foreach (AttachmentTombstoneEntry entry in journal.Entries)
            {
                (bool processed, bool restored) = TryRecoverAttachmentEntry(root, board, entry);
                if (!processed) complete = false;
                if (restored) recovered++;
            }
            // 只有全部条目都被安全处理（还原或清理）时才删除 journal；有任一条目被跳过就保留证据。
            if (complete) RemoveAttachmentJournal(path);
            return recovered;
        }
        catch (TaskCommandException)
        {
            return 0;
        }
    }

    private static (bool Processed, bool Restored) TryRecoverAttachmentEntry(string root, ProjectBoard board, AttachmentTombstoneEntry entry)
    {
        try
        {
            string storedPath = ResolveToolboxPath(root, entry.StoredPath);
            string tombstonePath = ResolveToolboxPath(root, entry.TombstonePath);
            RejectReparsePoint(storedPath);
            RejectReparsePoint(tombstonePath);
            if (!File.Exists(tombstonePath)) return (true, false);
            // 未知或不符哈希的 tombstone 不自动清除：保留 tombstone 与 journal 供人工核对，跳过该条目。
            if (entry.Sha256 == null || !string.Equals(HashFile(tombstonePath), entry.Sha256, StringComparison.Ordinal))
                return (false, false);
            bool metadataExists = board.Cards.Any(card => card.Id == entry.CardId &&
                (card.Attachments ?? []).Any(item => item.Id == entry.AttachmentId));
            if (!metadataExists)
            {
                File.Delete(tombstonePath);
                return (true, true);
            }
            // 原文件与 tombstone 同时存在（例如用户从备份或 git 恢复了文件）时保留双方文件，跳过该条目。
            if (File.Exists(storedPath)) return (false, false);
            File.Move(tombstonePath, storedPath);
            return (true, true);
        }
        catch (Exception ex) when (ex is TaskCommandException or IOException or UnauthorizedAccessException)
        {
            return (false, false);
        }
    }

    private sealed record AttachmentJournal(int SchemaVersion, IReadOnlyList<AttachmentTombstoneEntry> Entries);

    private sealed record AttachmentTombstoneEntry(Guid AttachmentId, Guid CardId, string StoredPath, string TombstonePath, string? Sha256);
}
