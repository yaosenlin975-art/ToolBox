using System.IO;
using System.Text.Json;

namespace ToolBox.Core.ActionChain;

/// <summary>动作链持久化存储单例</summary>
public class ActionChainStore
{
    private static ActionChainStore? _instance;
    public static ActionChainStore Instance => _instance ??= new ActionChainStore();

    private readonly string _filePath;
    private List<ActionChainDefinition> _chains = new();
    private string? _defaultChainId;

    public event Action? ChainsChanged;

    private ActionChainStore()
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "action_chains.json");
        Load();
    }

    public IReadOnlyList<ActionChainDefinition> Chains => _chains;
    public string? DefaultChainId => _defaultChainId;

    public ActionChainDefinition? GetDefaultChain()
    {
        if (_defaultChainId != null)
            return _chains.FirstOrDefault(c => c.Id == _defaultChainId);
        return _chains.FirstOrDefault(c => c.IsDefault) ?? _chains.FirstOrDefault();
    }

    public ActionChainDefinition? GetChain(string id) => _chains.FirstOrDefault(c => c.Id == id);

    public ActionChainStore Add(ActionChainDefinition chain)
    {
        _chains.Add(chain);
        Save();
        ChainsChanged?.Invoke();
        return this;
    }

    public ActionChainStore Update(ActionChainDefinition chain)
    {
        var idx = _chains.FindIndex(c => c.Id == chain.Id);
        if (idx >= 0)
        {
            _chains[idx] = chain;
            Save();
            ChainsChanged?.Invoke();
        }
        return this;
    }

    public ActionChainStore Delete(string chainId)
    {
        var chain = _chains.FirstOrDefault(c => c.Id == chainId);
        if (chain != null && !chain.IsBuiltIn)
        {
            _chains.Remove(chain);
            if (_defaultChainId == chainId)
                _defaultChainId = _chains.FirstOrDefault()?.Id;
            Save();
            ChainsChanged?.Invoke();
        }
        return this;
    }

    public ActionChainStore SetDefault(string chainId)
    {
        _defaultChainId = chainId;
        foreach (var c in _chains) c.IsDefault = (c.Id == chainId);
        Save();
        ChainsChanged?.Invoke();
        return this;
    }

    public ActionChainStore ResetBuiltIn()
    {
        _chains.RemoveAll(c => c.IsBuiltIn);
        _chains.InsertRange(0, GetBuiltInChains());
        Save();
        ChainsChanged?.Invoke();
        return this;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<ActionChainStoreData>(json);
                if (data != null)
                {
                    _chains = data.Chains ?? new();
                    _defaultChainId = data.DefaultChainId;
                }
            }
        }
        catch { }

        if (_chains.Count == 0)
        {
            _chains = GetBuiltInChains().ToList();
            _defaultChainId = _chains[0].Id;
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var data = new ActionChainStoreData { Chains = _chains, DefaultChainId = _defaultChainId };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    private static List<ActionChainDefinition> GetBuiltInChains() => new()
    {
        new() { Id = "builtin_copy", Name = "仅复制到剪贴板", Description = "截图后直接复制到剪贴板", IsBuiltIn = true, IsDefault = true,
            Nodes = new() { new() { NodeType = "CopyToClipboard" } } },
        new() { Id = "builtin_ocr_copy", Name = "OCR → 复制文字", Description = "OCR 提取文字后复制到剪贴板", IsBuiltIn = true,
            Nodes = new() { new() { NodeType = "OcrExtract" }, new() { NodeType = "CopyTextToClipboard" } } },
        new() { Id = "builtin_save_copy", Name = "保存 + 复制", Description = "保存到文件并复制到剪贴板", IsBuiltIn = true,
            Nodes = new() { new() { NodeType = "SaveToFile" }, new() { NodeType = "CopyToClipboard" } } }
    };
}

internal class ActionChainStoreData
{
    public List<ActionChainDefinition> Chains { get; set; } = new();
    public string? DefaultChainId { get; set; }
}
