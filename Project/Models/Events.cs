using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ToolBox.Models;

public class ScrapEventArgs : EventArgs
{
    public ScrapWindow Scrap { get; set; }
}

public class ScrapMenuEventArgs : EventArgs
{
    public ScrapWindow Scrap { get; set; }
    public System.Windows.Controls.ContextMenu Menu { get; set; }
}

public class ScrapKeyPressEventArgs : EventArgs
{
    public Key Key { get; set; }
}

public delegate void ScrapEventHandler(object sender, ScrapEventArgs e);
public delegate void ScrapMenuHandler(object sender, ScrapMenuEventArgs e);
public delegate void ScrapKeyPressHandler(object sender, ScrapKeyPressEventArgs e);

public interface IScrapStyleListener
{
    void ScrapCreated(object sender, ScrapEventArgs e);
    void ScrapActivated(object sender, ScrapEventArgs e);
    void ScrapInactived(object sender, ScrapEventArgs e);
    void ScrapInactiveMouseOver(object sender, ScrapEventArgs e);
    void ScrapInactiveMouseOut(object sender, ScrapEventArgs e);
}

public interface IScrapMenuListener
{
    void ScrapMenuOpening(object sender, ScrapMenuEventArgs e);
}

public interface IScrapKeyPressEventListener
{
    void ScrapKeyPress(object sender, ScrapKeyPressEventArgs e);
}

public interface IScrapAddedListener
{
    void ScrapAdded(object sender, ScrapEventArgs e);
}

public interface IScrapRemovedListener
{
    void ScrapRemoved(object sender, ScrapEventArgs e);
}

public interface IScrapLocationChangedListener
{
    void ScrapLocationChanged(object sender, ScrapEventArgs e);
}

public interface IScrapImageChangedListener
{
    void ScrapImageChanged(object sender, ScrapEventArgs e);
}

public interface IScrapStyleAppliedListener
{
    void ScrapStyleApplied(object sender, ScrapEventArgs e);
}

public interface IScrapStyleRemovedListener
{
    void ScrapStyleRemoved(object sender, ScrapEventArgs e);
}
