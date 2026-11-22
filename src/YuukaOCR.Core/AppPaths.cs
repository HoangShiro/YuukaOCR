namespace YuukaOCR.Core;

/// <summary>Resolves every path the app reads or writes. Works both from bin/ during development
/// and from a published folder.</summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string ConfigDir => Path.Combine(Root, "config");
    public static string UserDir => Path.Combine(Root, "user");
    public static string AppConfigFile => Path.Combine(ConfigDir, "app_configs.json");
    public static string SettingsFile => Path.Combine(UserDir, "settings.json");
    public static string LegacySettingsFile => Path.Combine(UserDir, "user_config.json");
    public static string LogFile => Path.Combine(UserDir, "log.json");
    public static string EnvFile => Path.Combine(UserDir, ".env");
    public static string UserUiImage => Path.Combine(UserDir, "ui.png");
    public static string BundledUiImage => Path.Combine(AppContext.BaseDirectory, "Assets", "ui.png");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(UserDir);
    }

    private static string ResolveRoot()
    {
        // Tools and probes point this at a throwaway folder: the layout harness saves settings and chat
        // turns, and it must never rewrite the user's own user/ directory.
        var isolated = Environment.GetEnvironmentVariable("YUUKA_ROOT");
        if (!string.IsNullOrWhiteSpace(isolated))
        {
            return Path.GetFullPath(isolated.Trim());
        }

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // Published build: config/app_configs.json sits next to the exe, so use that very folder.
        // Check BEFORE walking up, otherwise an exe in publish/ would "see" the project folder
        // above it and write user data somewhere else.
        if (File.Exists(Path.Combine(baseDir, "config", "app_configs.json")))
        {
            return baseDir;
        }

        // Dev build: the exe lives in src/YuukaOCR.App/bin/<cfg>/<tfm>/, so walk up to the folder
        // that holds both config/ and src/.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }
        return baseDir;
    }
}
