using Microsoft.Win32;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Interop;

/// <summary>Start-with-Windows via the per-user Run key (no scheduled task, no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YuukaOCR";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "YuukaOCR.exe");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Enables or disables autostart. Returns an error message, or null on success.</summary>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return L.T("autostart.registry");

            if (enabled)
                key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
