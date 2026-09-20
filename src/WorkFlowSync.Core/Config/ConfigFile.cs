namespace WorkFlowSync.Core.Config;

/// <summary>Locates, loads and saves config.json next to the executable (portable layout).</summary>
public static class ConfigFile
{
    public const string DefaultFileName = "config.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, DefaultFileName);

    /// <summary>
    /// The config a command line points at: <c>--config &lt;path&gt;</c>, otherwise the one next to the
    /// executable. Shared so the entry point and the application itself can never disagree about which
    /// config this process belongs to — that would split the single-instance lock in two.
    /// </summary>
    public static string FromArgs(IReadOnlyList<string>? args)
    {
        if (args is null) return DefaultPath;
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == "--config") return args[i + 1];
        return DefaultPath;
    }

    /// <summary>Loads the config or returns an empty default when the file does not exist yet.</summary>
    public static SyncConfig LoadOrDefault(string path) =>
        File.Exists(path) ? SyncConfig.Load(path) : new SyncConfig();

    /// <summary>Atomic save: write to a temp file, then replace, so a crash never leaves a half-written config.</summary>
    public static void Save(SyncConfig config, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, config.ToJson() + Environment.NewLine, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
