using System.Windows.Media.Imaging;

namespace ToolBox.Core.PreviewCard;

/// <summary>
/// 预览卡用户操作类型
/// </summary>
public enum EPreviewCardAction
{
    Edit,           // 打开标注编辑
    PinToDesktop,   // 贴图置顶
    Copy,           // 复制到剪贴板
    Save,           // 保存到文件
    SendToAi,       // 发送到 AI 助手
    Discard         // 丢弃（不保存）
}

/// <summary>
/// 预览卡配置
/// </summary>
public class PreviewCardConfig
{
    public double DisplayDurationMs { get; set; } = 5000;
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 150;
    public bool ShowAfterCapture { get; set; } = true;
    public bool SkipInPinMode { get; set; } = false;
}

/// <summary>
/// 预览卡生命周期管理单例
/// </summary>
public class PreviewCardManager
{
    private static PreviewCardManager? _instance;
    public static PreviewCardManager Instance => _instance ??= new PreviewCardManager();

    private Views.PreviewCard.PreviewCardWindow? _currentCard;

    /// <summary>预览卡用户操作事件</summary>
    public event Action<EPreviewCardAction, BitmapSource>? ActionTriggered;

    private PreviewCardManager() { }

    /// <summary>显示预览卡</summary>
    public PreviewCardManager Show(BitmapSource screenshot, string? filePath = null)
    {
        // 如果已有预览卡，先关闭
        Hide();

        var config = GetConfig();
        if (!config.ShowAfterCapture)
            return this;

        _currentCard = new Views.PreviewCard.PreviewCardWindow();
        _currentCard.SetScreenshot(screenshot);
        _currentCard.SetFilePath(filePath);
        _currentCard.SetManager(this);
        _currentCard.Show();
        _currentCard.StartAutoDismiss(config.DisplayDurationMs);
        return this;
    }

    /// <summary>隐藏当前预览卡</summary>
    public PreviewCardManager Hide()
    {
        if (_currentCard != null)
        {
            _currentCard.ForceClose();
            _currentCard = null;
        }
        return this;
    }

    /// <summary>触发操作事件（由 PreviewCardWindow 调用）</summary>
    internal void RaiseAction(EPreviewCardAction action, BitmapSource screenshot)
    {
        ActionTriggered?.Invoke(action, screenshot);
        _currentCard = null;
    }

    /// <summary>预览卡关闭时清理引用</summary>
    internal void OnCardClosed()
    {
        _currentCard = null;
    }

    /// <summary>获取当前配置</summary>
    public PreviewCardConfig GetConfig()
    {
        var option = Models.ToolBoxOption.Load();
        return new PreviewCardConfig
        {
            ShowAfterCapture = option.Data.ShowPreviewCard,
            DisplayDurationMs = option.Data.PreviewCardDurationMs,
            SkipInPinMode = option.Data.PreviewCardSkipInPinMode
        };
    }
}
