# 可重复待办（重置模型） 产品规格说明

- **优先级**：P0
- **对应设计**：`Docs/recurring-todo-design.md`
- **版本**：v1.0

---

## 一、功能概述

为 ToolBox 待办系统增加**可重复待办**能力：支持创建 Daily/Weekly/Monthly/Yearly 频率的重复待办，到提醒时间自动将已完成项重置为未完成并弹通知。核心场景包括每日签到（B站、GitHub、各平台每日任务等）、周报提醒、月度总结等周期性任务。

采用**单项目重置模型**——同一个 TodoItem 按周期重置 `IsCompleted` 状态，而非创建多个实例。轻量、直观、存储高效。

---

## 二、用户故事

| # | 用户故事 | 验收优先级 |
|---|---------|-----------|
| US1 | 作为**日常用户**，我想要创建"每日 B 站签到"这样的重复待办，设置每天提醒，到点自动重置，以免每天手动重建 | P0 |
| US2 | 作为**多平台签到用户**，我想要在迷你窗口看到"今日重复"分组排在待办列表最前面，一眼看清今天有哪些签到任务 | P0 |
| US3 | 作为**作息不固定的用户**，我关闭应用几天后打开，系统自动补齐错过的重置，让我不会漏掉签到 | P0 |
| US4 | 作为**LLM 用户**，我想要用聊天告诉 AI 创建重复待办（如"每天提醒我喝水"），AI 自动配置好重复规则 | P1 |
| US5 | 作为**精细化管理者**，我想要在 TodoView 详情面板中修改频率、提醒时间或结束日期 | P1 |
| US6 | 作为**任务审视者**，我想要在 TodoView 筛选栏点击"重复"统一查看所有配置了重复的待办 | P1 |

---

## 三、验收标准

### 创建重复待办

- [ ] **AC1.1**：通过迷你窗口的"+"按钮打开 InputWindow，新增"重复频率"下拉选项（不重复/每天/每周/每月/每年），选择频率后添加即为重复待办
- [ ] **AC1.2**：重复待办创建后，迷你窗口待办列表顶部出现"今日重复"分组，该项显示在分组中
- [ ] **AC1.3**：重复待办在 TodoView 列表中显示 🔄 图标标识（位于 checkbox 和标题之间）
- [ ] **AC1.4**：重复待办的 JSON 序列化包含 `RepeatConfig` 字段，旧数据（无此字段）反序列化后视为普通待办

### 重置与提醒

- [ ] **AC2.1**：到 Cron 提醒时间时，如果该项 `IsCompleted == true` → 自动重置为 `false`，`CompletedAt` 清空，`LastResetAt` 更新，弹出系统通知 "🔄 XXX 已重置，记得完成哦"
- [ ] **AC2.2**：到 Cron 提醒时间时，如果该项 `IsCompleted == false` → 不重置，弹出系统通知 "XXX 今天还没有完成哦"
- [ ] **AC2.3**：重置后迷你窗口实时刷新，该项显示为未完成状态（checkbox 取消勾选）

### 启动补齐

- [ ] **AC3.1**：应用启动时自动执行 `CatchUpRepeatingAsync()`，遍历所有已完成的重复待办，如果当前时间 >= 下次重置时间则重置一次
- [ ] **AC3.2**：补齐只重置一次（不会循环补多天），用户看到一个当前周期的项
- [ ] **AC3.3**：补齐数量 > 0 时输出 Debug 日志 `[ToolBox] 重置了 N 个重复待办`

### 迷你窗口分组

- [ ] **AC4.1**：迷你窗口待办区域顶部显示"今日重复"分组（11px 灰色分隔标题），仅在有重复待办时显示
- [ ] **AC4.2**：重复待办显示 🔄 标识，排列在普通待办上面
- [ ] **AC4.3**：重复待办组内按 `IsCompleted` 升序（未完成排前），再按 `DueDate` 升序

### TodoView 筛选与展示

- [ ] **AC5.1**：筛选栏新增"重复"标签（RadioButton），点击后只显示所有配置了 `RepeatConfig` 的待办
- [ ] **AC5.2**：列表中的重复项标题前显示 🔄 图标
- [ ] **AC5.3**：详情面板在优先级下方显示重复配置区（频率/提醒时间/结束日期/保存按钮）

### 编辑与删除

- [ ] **AC6.1**：在详情面板修改频率/提醒时间/结束日期后，点击"设为重复"保存，旧 Cron 注销，新 Cron 注册
- [ ] **AC6.2**：将频率改为"不重复"并保存 → `RepeatConfig` 清空，Cron 注销，项转为普通待办
- [ ] **AC6.3**：删除重复待办完全走 Trash 回收站流程（与普通项一致）
- [ ] **AC6.4**：从回收站恢复重复待办后，`RepeatConfig` 保留，Cron 重新注册

### LLM 集成

- [ ] **AC7.1**：`add_todo` 工具接受 `repeat` 参数（`daily`/`weekly`/`monthly`/`yearly`），传入时自动调用 `AddRepeatingAsync`
- [ ] **AC7.2**：`list_todos` 工具接受 `filter: "repeating"`，返回所有重复待办
- [ ] **AC7.3**：AI 创建的重复待办在 UI 中与手动创建的完全一致（显示 🔄、参与分组、到点重置）

### 边界情况

- [ ] **AC8.1**：`EndDate` 到期后，Cron 注销，不再重置；项保留在列表中
- [ ] **AC8.2**：重复待办的子任务不参与重复，父项重置不影响子任务状态
- [ ] **AC8.3**：旧版 `todos.json`（无 `RepeatConfig`/`LastResetAt` 字段）加载正常，不报错，不丢失数据

---

## 四、技术方案

### 4.1 数据模型

```csharp
// Core/Todo/TodoItem.cs 新增字段
public RepeatConfig? RepeatConfig { get; set; }
public DateTime? LastResetAt { get; set; }

// Core/Todo/RepeatConfig.cs 新增
public enum ERepeatFrequency { Daily, Weekly, Monthly, Yearly }

public class RepeatConfig
{
    public ERepeatFrequency Frequency { get; set; } = ERepeatFrequency.Daily;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int ReminderHour { get; set; } = 9;
    public int ReminderMinute { get; set; } = 0;
}
```

### 4.2 TodoStore 新增方法

| 方法 | 说明 |
|------|------|
| `AddRepeatingAsync(...)` | 创建重复待办 |
| `GetRepeating()` | 获取所有重复项 |
| `GetDueRepeating()` | 获取今日应显示的重复项 |
| `ResetRepeatingAsync(id)` | 重置为未完成 |
| `CatchUpRepeatingAsync()` | 启动补齐 |
| `RegisterRepeatReminders()` | 注册 Cron 提醒 |

### 4.3 Cron 集成

- 每个重复项注册一个唯一 Cron ID: `"repeat_{item.Id}"`
- Cron 表达式由 `RepeatCalculator.ToCron(frequency, hour, minute)` 生成
- 回调检查 `IsCompleted` → 已完成则重置+通知，未完成则仅通知
- ScheduleManager 在 App 启动时统一注册

### 4.4 补齐逻辑

```
遍历所有 RepeatConfig != null && !IsTrashed 的项
  跳过 IsCompleted == false 的项
  计算下次重置时间 = CalculateResetTime(LastResetAt ?? StartDate, frequency, hour, minute)
  如果 DateTime.UtcNow >= 下次重置时间
    IsCompleted = false, CompletedAt = null, LastResetAt = UtcNow
```

---

## 五、UI/UX 设计

### 迷你窗口

```
┌─────────────────────────────┐
│ [今日重复]                   │
│ ── 今日重复 ─────────────── │
│ [ ] 🔄 每日 B 站签到        │
│ [ ] 🔄 每日 GitHub 签到     │
│ ── 未分类 ──────────────── │
│ [x] 写周报                  │
│ [ ] 买菜                    │
└─────────────────────────────┘
```

### TodoView 详情面板重复配置区

频率下拉框：不重复 / 每天 / 每周 / 每月 / 每年
提醒时间：TimePicker（时:分）
结束日期：DatePicker（日期选择），默认"永不"
确认按钮："设为重复"（更改频率/时间/结束时显示）

---

## 六、边界情况

| 场景 | 处理 |
|------|------|
| 应用关闭多天后打开 | CatchUpRepeatingAsync 检查一次重置，只重置一个周期 |
| Day N 未完成到了 Day N+1 | 旧项保持未完成状态，Cron 触发时不重置（已完成 != true） |
| EndDate 到期 | Cron 注销，不再重置，项保留 |
| 删除后恢复 | RepeatConfig 保留，Cron 重新注册 |
| 子任务 | 不参与重复，跟随父项 |
| 旧数据兼容 | 全部 nullable 字段，缺失时自动为 null |
| 提醒时间与创建时间重合 | 创建时 LastResetAt = UtcNow 避免立即被补齐 |

---

## 七、文件变更清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `Core/Todo/TodoItem.cs` | 修改 | 新增 `RepeatConfig`、`LastResetAt` 字段 |
| `Core/Todo/RepeatConfig.cs` | **新增** | `ERepeatFrequency` 枚举 + `RepeatConfig` 类 |
| `Core/Todo/RepeatCalculator.cs` | **新增** | 日期计算 + Cron 表达式生成 |
| `Core/Todo/TodoStore.cs` | 修改 | 新增 6 个方法 |
| `Core/Scheduling/ScheduleManager.cs` | 修改 | 集成 `RegisterRepeatReminders` |
| `Views/Todo/TodoView.xaml` | 修改 | 详情面板 + 筛选栏 |
| `Views/Todo/TodoView.xaml.cs` | 修改 | 重复配置交互 |
| `Views/CompactToolboxWindow.xaml` | 修改 | "今日重复"分组容器 |
| `Views/CompactToolboxWindow.xaml.cs` | 修改 | 重复项加载与渲染 |
| `Views/InputWindow.xaml` | 修改 | 重复频率下拉 |
| `Views/InputWindow.xaml.cs` | 修改 | 重复频率传递 |
| `Core/Tools/TodoTools.cs` | 修改 | repeat 参数 + repeating 筛选 |
| `App.xaml.cs` | 修改 | 启动补齐 + 提醒注册 |

---

## 八、依赖

- `Cronos 0.8.4` — 项目已引用
- 无新增 NuGet 包

---

*规格说明 v1.0 — 对应 Docs/recurring-todo-design.md（重置模型）*
