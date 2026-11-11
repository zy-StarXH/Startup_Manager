using Microsoft.Win32;

namespace StartupManager.App.Host;

/// <summary>
/// Writes/deletes the per-user HKCU Run value for start-with-Windows.
///
/// INTEGRATION NOTE: today the host applies this after a successful
/// IStartupManager.SaveSettingsAsync. If Core later takes ownership of the Run
/// value, delete the call in BridgeHost.SaveSettingsCommandAsync; the settings
/// JSON/contract does not change either way.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StartupManager";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("无法打开当前用户的启动注册表项。");
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("无法确定当前程序路径。");
        }

        key.SetValue(ValueName, "\"" + executable + "\"", RegistryValueKind.String);
    }
}
