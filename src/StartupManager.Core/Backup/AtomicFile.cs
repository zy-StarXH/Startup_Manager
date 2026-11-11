using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace StartupManager.Core.Backup;

/// <summary>
/// Small file helpers shared by persistence and IPC code.
/// Writes go to a sibling temp file and are moved into place, so a reader never
/// sees a half-written file. ACL hardening is best-effort: it must never make
/// the feature fail on a machine where the security APIs are unavailable.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        bool restrictToCurrentUser,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("文件路径缺少目录。", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (restrictToCurrentUser)
            {
                TryRestrictFile(tempPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath, overwrite: true);

            if (restrictToCurrentUser)
            {
                TryRestrictFile(fullPath);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Removes inherited ACEs and grants FullControl to the current user only.
    /// Returns false when the platform or ACL operation is unavailable.
    /// </summary>
    public static bool TryRestrictFile(string path)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null)
            {
                return false;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            info.SetAccessControl(security);
            return true;
        }
        catch (Exception)
        {
            // Best effort only: never fail a backup save because ACL hardening was rejected.
            return false;
        }
    }

    /// <summary>
    /// Removes inherited ACEs from a directory and grants FullControl to the
    /// current user. New files inherit the same user-only rule.
    /// </summary>
    public static bool TryRestrictDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null)
            {
                return false;
            }

            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Leftover temp files are ignored by readers and cleaned by the OS eventually.
        }
    }
}
