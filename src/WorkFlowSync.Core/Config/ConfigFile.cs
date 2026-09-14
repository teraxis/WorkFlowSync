namespace WorkFlowSync.Core.Config;

/// <summary>Locates, loads and saves config.json next to the executable (portable layout).</summary>
public static class ConfigFile
{
    public const string DefaultFileName = "config.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, DefaultFileName);

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
