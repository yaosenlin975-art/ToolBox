using System;

namespace ToolBox.Models;

public enum EApplicationType
{
    ApplicationMode = 1,
    ResidentMode = 16
}

public enum EOpeningType
{
    Normal = 0,
    Capture = 1
}

public enum EHotKeyID
{
    Capture1 = 0,
    Capture2 = 1,
    __Count__ = 2
}

public enum ELocationType
{
    LeftTop,
    RightBottom
}
