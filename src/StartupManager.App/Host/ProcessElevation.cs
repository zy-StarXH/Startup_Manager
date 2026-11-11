using System.Security.Principal;

namespace StartupManager.App.Host;

internal static class ProcessElevation
{
    /// <summary>
    /// True only for a real elevated token. With UAC enabled this is false for the
    /// normal, filtered user token even if the account is an administrator.
    /// </summary>
    public static bool IsElevated { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not determine process elevation.", ex);
            return false;
        }
    }
}
