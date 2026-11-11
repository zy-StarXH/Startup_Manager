using System.Reflection;
using System.Runtime.InteropServices;

namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// Resolves .lnk targets through the WScript.Shell COM object. COM is created
/// with late binding so a missing/broken registration only degrades the display
/// (command falls back to the .lnk path) instead of failing the whole scan.
/// </summary>
public static class ShellShortcutResolver
{
    public static bool TryResolve(
        string shortcutPath,
        out string targetPath,
        out string arguments)
    {
        targetPath = string.Empty;
        arguments = string.Empty;

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });

            if (shortcut is null)
            {
                return false;
            }

            var shortcutType = shortcut.GetType();
            targetPath = ReadComString(shortcutType, shortcut, "TargetPath");
            arguments = ReadComString(shortcutType, shortcut, "Arguments");
            return targetPath.Length > 0;
        }
        catch (Exception)
        {
            // COMException, TargetInvocationException, missing COM support, and
            // malformed shortcut files all result in a graceful fallback.
            targetPath = string.Empty;
            arguments = string.Empty;
            return false;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static string ReadComString(Type comType, object comObject, string propertyName)
    {
        try
        {
            var value = comType.InvokeMember(
                propertyName,
                BindingFlags.GetProperty,
                binder: null,
                target: comObject,
                args: null);
            return (value as string)?.Trim() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch (Exception)
        {
            // Some runtimes disable FinalReleaseComObject; the RCW is still collected.
        }
    }
}
