using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Services;

public abstract class ToolCommand
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public ToolCommand()
    {
    }

    public ToolCommand(string name, string displayName)
    {
        Name = name;
        DisplayName = displayName;
    }

    public abstract void Execute(ScrapWindow scrap);
}

public class ToolCommand<T> : ToolCommand where T : class
{
    public T Parameter { get; set; }

    public ToolCommand()
    {
    }

    public ToolCommand(string name, string displayName, T parameter)
        : base(name, displayName)
    {
        Parameter = parameter;
    }

    public override void Execute(ScrapWindow scrap)
    {
    }
}
