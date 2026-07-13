using ToolBox.Core.Todo;

namespace ToolBox.Core.Tools;

public static class TodoTools
{
    [Tool("add_todo", "创建新的 Todo 任务")]
    public static string AddTodo(
        [ToolParam("任务标题")] string title,
        [ToolParam("任务描述")] string description = "",
        [ToolParam("优先级 0=普通 1=重要 2=紧急")] int priority = 0,
        [ToolParam("标签（逗号分隔）")] string tags = "",
        [ToolParam("截止日期（yyyy-MM-dd）")] string dueDate = "")
    {
        var tagList = string.IsNullOrWhiteSpace(tags) ? new List<string>() : tags.Split(',').Select(t => t.Trim()).ToList();
        DateTime? due = DateTime.TryParse(dueDate, out var d) ? d : null;
        var item = TodoStore.Instance.Add(title, description, priority, tags: tagList, dueDate: due);
        return "已添加 Todo [" + item.Id + "]: " + item.Title;
    }

    [Tool("list_todos", "查询 Todo 列表")]
    public static string ListTodos(
        [ToolParam("筛选: all/pending/completed")] string filter = "all",
        [ToolParam("按标签筛选")] string tag = "")
    {
        List<TodoItem> items = filter switch
        {
            "pending" => TodoStore.Instance.GetPending(),
            "completed" => TodoStore.Instance.GetCompleted(),
            _ => TodoStore.Instance.GetAll()
        };
        if (!string.IsNullOrWhiteSpace(tag))
            items = items.Where(t => t.Tags.Contains(tag)).ToList();
        if (items.Count == 0) return "没有找到 Todo";
        return string.Join("\n", items.Select(t =>
            "[" + (t.IsCompleted ? "完成" : "待办") + "] " + t.Id + " | " + t.Title + " | P" + t.Priority));
    }

    [Tool("complete_todo", "标记 Todo 为已完成")]
    public static string CompleteTodo(
        [ToolParam("Todo ID")] string todoId)
    {
        return TodoStore.Instance.Complete(todoId)
            ? "已标记完成: " + todoId
            : "未找到 Todo: " + todoId;
    }

    [Tool("delete_todo", "删除 Todo")]
    public static string DeleteTodo(
        [ToolParam("Todo ID")] string todoId)
    {
        return TodoStore.Instance.Delete(todoId)
            ? "已删除: " + todoId
            : "未找到 Todo: " + todoId;
    }

    [Tool("update_todo", "更新 Todo 信息")]
    public static string UpdateTodo(
        [ToolParam("Todo ID")] string todoId,
        [ToolParam("新标题")] string title = "",
        [ToolParam("新描述")] string description = "",
        [ToolParam("新优先级")] string priority = "")
    {
        int? p = int.TryParse(priority, out var pv) ? pv : null;
        var success = TodoStore.Instance.Update(todoId,
            string.IsNullOrEmpty(title) ? null : title,
            string.IsNullOrEmpty(description) ? null : description,
            p);
        return success ? "已更新: " + todoId : "未找到 Todo: " + todoId;
    }
}
