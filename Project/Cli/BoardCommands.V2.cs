using System.CommandLine;

namespace ToolBox.Cli;

internal static partial class Program
{
    private static void AddBoardV2Commands(Command board)
    {
        var cards = board.Subcommands.OfType<Command>().First(command => command.Name == "card");
        AddCardStateCommand(cards, "claim", "Claim a card or renew its lease.", "board.card.claim", ttl: true);
        AddCardStateCommand(cards, "release", "Release the current card claim.", "board.card.release");
        AddCardStateCommand(cards, "submit", "Submit a card for review.", "board.card.submit");
        AddCardStateCommand(cards, "close", "Close a reviewed card.", "board.card.close");
        AddCardStateCommand(cards, "reopen", "Reopen a reviewed or closed card.", "board.card.reopen");

        var comments = new Command("comment", "Manage card comments and routed replies.");
        comments.SetAction(_ => WriteFailure(new ToolBox.Core.Todo.TaskRequest { Operation = "board.comment" }, "INVALID_ARGUMENT", "A comment command is required."));
        AddBoardCommand(comments, "add", "Add a comment or reply.", "board.comment.add",
            required: ["id", "body"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                (command => AddStringField(command, "parentId", "--parent-id", "Parent comment ID.")),
                (command => AddStringField(command, "body", "--body", "Comment body.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField, AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "get", "Get one comment by ID.", "board.comment.get",
            required: ["commentId"], fields:
            [AddProjectField, (command => AddStringField(command, "commentId", "--comment-id", "Comment ID."))]);
        AddBoardCommand(comments, "list", "List card comments with pagination.", "board.comment.list",
            required: ["id"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                (command => AddBooleanField(command, "includeDeleted", "--include-deleted", "Include deleted comments.")),
                (command => AddIntegerField(command, "limit", "--limit", "Page size from 1 to 100.")),
                (command => AddStringField(command, "cursor", "--cursor", "Cursor returned by the previous page.")),
                AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "update", "Update a comment body.", "board.comment.update",
            required: ["commentId", "body"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "commentId", "--comment-id", "Comment ID.")),
                (command => AddStringField(command, "body", "--body", "New comment body.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField, AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "delete", "Soft-delete a comment.", "board.comment.delete",
            required: ["commentId"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "commentId", "--comment-id", "Comment ID.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField, AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "inbox", "Reserve routed replies for a host or session.", "board.comment.inbox",
            required: ["host", "actor"], fields:
            [
                AddProjectField, AddHostField, AddSessionField, AddActorField,
                (command => AddIntegerField(command, "ttlMinutes", "--ttl-minutes", "Reservation duration from 5 to 480 minutes.")),
                (command => AddIntegerField(command, "limit", "--limit", "Page size from 1 to 100.")),
                (command => AddStringField(command, "cursor", "--cursor", "Cursor returned by the previous page.")),
                AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "ack", "Acknowledge a reserved reply.", "board.comment.ack",
            required: ["commentId", "claimId", "actor", "host"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "commentId", "--comment-id", "Comment ID.")),
                (command => AddStringField(command, "claimId", "--claim-id", "Inbox reservation ID.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField, AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "bind", "Bind or clear a comment origin.", "board.comment.bind",
            required: ["commentId"], oneOf: ["targetHost", "targetSession", "clear"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "commentId", "--comment-id", "Comment ID.")),
                (command => AddStringField(command, "targetHost", "--target-host", "Host to bind as the comment origin.")),
                (command => AddStringField(command, "targetSession", "--target-session", "Session to bind as the comment origin.")),
                (command => AddBooleanField(command, "clear", "--clear", "Clear the comment origin.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField, AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "deliver", "Deliver a comment using its saved routing.", "board.comment.deliver",
            required: ["commentId"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "commentId", "--comment-id", "Comment ID.")),
                (command => AddBooleanField(command, "confirm", "--confirm", "Confirm the displayed target and message.")),
                (command => AddBooleanField(command, "verifiedUnknown", "--verified-unknown", "Confirm that an unknown prior attempt was checked before retry.")),
                (command => AddStringField(command, "confirmationToken", "--confirmation-token", "Confirmation hash from board comment delivery.")),
                AddRevisionField, AddCommentRevisionField
            ]);
        AddBoardCommand(comments, "delivery", "Get local delivery state and confirmation hash.", "board.comment.delivery",
            required: ["commentId"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "commentId", "--comment-id", "Comment ID.")),
                AddRevisionField, AddCommentRevisionField
            ]);
        board.Subcommands.Add(comments);

        var attachments = new Command("attachment", "Manage local card image attachments.");
        attachments.SetAction(_ => WriteFailure(new ToolBox.Core.Todo.TaskRequest { Operation = "board.attachment" }, "INVALID_ARGUMENT", "An attachment command is required."));
        AddBoardCommand(attachments, "add", "Attach a local image to a card.", "board.attachment.add",
            required: ["id", "file"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                (command => AddStringField(command, "file", "--file", "Local image path; relative paths use the caller's working directory.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField
            ]);
        AddBoardCommand(attachments, "list", "List card attachments.", "board.attachment.list",
            required: ["id"], fields:
            [AddProjectField, (command => AddStringField(command, "id", "--id", "Card ID."))]);
        AddBoardCommand(attachments, "remove", "Remove an attachment and its stored image.", "board.attachment.remove",
            required: ["attachmentId"], fields:
            [
                AddProjectField,
                (command => AddStringField(command, "attachmentId", "--attachment-id", "Attachment ID.")),
                AddActorField, AddHostField, AddSessionField, AddRevisionField
            ]);
        board.Subcommands.Add(attachments);
    }

    private static void AddCardStateCommand(Command cards, string name, string description, string operation, bool ttl = false)
    {
        var fields = new List<Func<Command, BoardField>>
        {
            AddProjectField,
            AddRevisionField,
            (command => AddStringField(command, "id", "--id", "Card ID.")),
            AddActorField, AddHostField, AddSessionField,
            (command => AddStringField(command, "columnId", "--column-id", "Target column for this state transition.")),
            (command => AddIntegerField(command, "order", "--order", "Target position within the column."))
        };
        if (ttl) fields.Add(command => AddIntegerField(command, "ttlMinutes", "--ttl-minutes", "Claim lease duration from 5 to 480 minutes."));
        AddBoardCommand(cards, name, description, operation, required: ["id", "actor", "host"], fields: [.. fields]);
    }

    private static BoardField AddProjectField(Command command) =>
        AddStringField(command, "project", "--project", "Project board ID or path.");
}
