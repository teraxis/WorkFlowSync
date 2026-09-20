namespace WorkFlowSync.Core.Config;

/// <summary>When the program is allowed to say something out loud (docs/product/features/tray.md).</summary>
public enum NotifyMode
{
    /// <summary>New files and problems. The default.</summary>
    All = 0,

    /// <summary>Only when something went wrong — for people who do not want to hear about routine work.</summary>
    ErrorsOnly = 1,

    /// <summary>Never. The tray icon and the «Останні зміни» window still tell the whole story.</summary>
    Off = 2,
}
