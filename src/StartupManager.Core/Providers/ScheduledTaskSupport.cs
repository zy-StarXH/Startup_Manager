using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using StartupManager.Core.Models;

namespace StartupManager.Core.Providers;

/// <summary>
/// Pure task classification helpers used by the provider, the protection policy,
/// and unit tests. No COM or file access happens here.
/// </summary>
public static class ScheduledTaskClassifier
{
    public const int BootTriggerType = 8;
    public const int LogonTriggerType = 9;

    public static bool IsBootOrLogonTrigger(int triggerType) =>
        triggerType is BootTriggerType or LogonTriggerType;

    public static bool HasBootOrLogonTrigger(IEnumerable<int> triggerTypes)
    {
        ArgumentNullException.ThrowIfNull(triggerTypes);
        return triggerTypes.Any(IsBootOrLogonTrigger);
    }

    public static IReadOnlyList<int> SelectBootLogonTriggers(IEnumerable<int> triggerTypes)
    {
        ArgumentNullException.ThrowIfNull(triggerTypes);
        return triggerTypes.Where(IsBootOrLogonTrigger).Distinct().OrderBy(type => type).ToArray();
    }

    public static string DescribeTriggers(IEnumerable<int> triggerTypes)
    {
        var types = SelectBootLogonTriggers(triggerTypes);
        if (types.Count == 0)
        {
            return string.Empty;
        }

        var labels = new List<string>(types.Count);
        if (types.Contains(BootTriggerType))
        {
            labels.Add("启动时");
        }

        if (types.Contains(LogonTriggerType))
        {
            labels.Add("登录时");
        }

        return "触发器：" + string.Join("、", labels);
    }

    public static string BuildSubLocation(IEnumerable<int> triggerTypes, string? principalUserId, string? principalGroupId)
    {
        var triggerText = DescribeTriggers(triggerTypes);
        var user = GetAccountDisplayName(principalUserId);
        var group = GetAccountDisplayName(principalGroupId);

        var principalText = user.Length > 0
            ? "用户：" + user
            : group.Length > 0
                ? "用户组：" + group
                : string.Empty;

        if (triggerText.Length == 0)
        {
            return principalText;
        }

        return principalText.Length == 0 ? triggerText : triggerText + " · " + principalText;
    }

    /// <summary>
    /// currentUser for the current user's account/SID, System for SYSTEM and
    /// service accounts, AllUsers otherwise (including group principals).
    /// </summary>
    public static StartupScope ClassifyScope(
        string? principalUserId,
        string? principalGroupId = null,
        string? currentUserName = null,
        string? currentUserSid = null)
    {
        var user = (principalUserId ?? string.Empty).Trim();
        var group = (principalGroupId ?? string.Empty).Trim();

        if (user.Length > 0)
        {
            if (IsSystemPrincipal(user))
            {
                return StartupScope.System;
            }

            if (IsCurrentUser(user, currentUserName, currentUserSid))
            {
                return StartupScope.CurrentUser;
            }

            return StartupScope.AllUsers;
        }

        if (group.Length > 0)
        {
            return IsSystemPrincipal(group) ? StartupScope.System : StartupScope.AllUsers;
        }

        return StartupScope.AllUsers;
    }

    public static bool IsSystemPrincipal(string? identity)
    {
        var value = (identity ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase))
        {
            return value.StartsWith("S-1-5-18", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("S-1-5-19", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("S-1-5-20", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("S-1-5-80-", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("S-1-5-83-", StringComparison.OrdinalIgnoreCase);
        }

        var shortName = GetShortName(value);
        return shortName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("LOCAL SYSTEM", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHighPrivilegePrincipal(string? principalUserId, string? principalGroupId, int runLevel)
    {
        // TASK_RUNLEVEL_HIGHEST = 1
        if (runLevel == 1)
        {
            return true;
        }

        if (IsSystemPrincipal(principalUserId) || IsSystemPrincipal(principalGroupId))
        {
            return true;
        }

        var group = (principalGroupId ?? string.Empty).Trim();
        return group.Equals("S-1-5-32-544", StringComparison.OrdinalIgnoreCase)
            || GetShortName(group).Equals("Administrators", StringComparison.OrdinalIgnoreCase)
            || GetShortName(group).Equals("管理员", StringComparison.Ordinal);
    }

    public static bool IsMicrosoftWindowsTaskPath(string? taskPath)
    {
        var path = NormalizeTaskPath(taskPath);
        return path.Equals(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMicrosoftTaskPath(string? taskPath)
    {
        var path = NormalizeTaskPath(taskPath);
        return path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeTaskPath(string? taskPath)
    {
        if (string.IsNullOrWhiteSpace(taskPath))
        {
            return string.Empty;
        }

        var path = taskPath.Trim().Replace('/', '\\');
        if (!path.StartsWith('\\'))
        {
            path = "\\" + path;
        }

        if (path.Length > 1)
        {
            path = path.TrimEnd('\\');
        }

        return path;
    }

    public static string BuildLocation(string? taskPath) =>
        string.IsNullOrWhiteSpace(taskPath) ? string.Empty : "任务计划程序库 " + NormalizeTaskPath(taskPath);

    public static string CreateTaskId(string? taskPath)
    {
        var path = NormalizeTaskPath(taskPath);
        if (path.Length == 0)
        {
            throw new ArgumentException("计划任务路径不能为空。", nameof(taskPath));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return "tsk-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Turns DOMAIN\User or a SID into a short display name.</summary>
    public static string GetAccountDisplayName(string? account)
    {
        var value = (account ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var sid = new SecurityIdentifier(value);
                var translated = sid.Translate(typeof(NTAccount))?.Value;
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return GetShortName(translated);
                }
            }
            catch
            {
                // Unresolvable SIDs are displayed as-is.
            }

            return value;
        }

        return GetShortName(value);
    }

    internal static string GetShortName(string? identity)
    {
        var value = (identity ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var slash = value.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < value.Length ? value.Substring(slash + 1).Trim() : value;
    }

    private static bool IsCurrentUser(string principal, string? currentUserName, string? currentUserSid)
    {
        string? currentName = currentUserName;
        string? currentSid = currentUserSid;

        if (string.IsNullOrWhiteSpace(currentName) && string.IsNullOrWhiteSpace(currentSid))
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                currentName = identity.Name;
                currentSid = identity.User?.Value;
            }
            catch
            {
                currentName = Environment.UserName;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentSid) &&
            string.Equals(principal, currentSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(currentName) &&
            string.Equals(principal, currentName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var principalName = GetAccountDisplayName(principal);
        if (principalName.Length == 0)
        {
            return false;
        }

        var currentShortName = GetShortName(currentName);
        return currentShortName.Length > 0 &&
            (principalName.Equals(currentShortName, StringComparison.OrdinalIgnoreCase) ||
             GetShortName(principal).Equals(currentShortName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Provider-private task state persisted in a backup record.</summary>
public sealed class ScheduledTaskBackupState
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Xml { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
}

internal static class TaskValueConverters
{
    public static int ToInt(object? value, int fallback = 0)
    {
        try
        {
            return value switch
            {
                null => fallback,
                int number => number,
                short number => number,
                long number => checked((int)number),
                uint number => checked((int)number),
                bool flag => flag ? 1 : 0,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return fallback;
        }
    }

    public static bool ToBool(object? value)
    {
        try
        {
            return value switch
            {
                null => false,
                bool flag => flag,
                string text when bool.TryParse(text, out var parsed) => parsed,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed != 0,
                _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return false;
        }
    }

    public static DateTimeOffset? ToDateOffset(object? value)
    {
        try
        {
            switch (value)
            {
                case null:
                    return null;
                case DateTime dateTime:
                    if (dateTime.Year < 1980)
                    {
                        return null;
                    }

                    return dateTime.Kind == DateTimeKind.Unspecified
                        ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local))
                        : new DateTimeOffset(dateTime);
                case double oaDate when oaDate > 0:
                    return new DateTimeOffset(
                        DateTime.SpecifyKind(DateTime.FromOADate(oaDate), DateTimeKind.Local));
                case string text when DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed):
                    return parsed;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
