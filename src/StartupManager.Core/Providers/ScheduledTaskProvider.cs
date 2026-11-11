using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Protection;

namespace StartupManager.Core.Providers;

/// <summary>
/// Scheduled Task startup source backed by the Task Scheduler COM API
/// (Schedule.Service). Only tasks with a boot or logon trigger are reported,
/// including tasks whose native Enabled flag is false.
/// </summary>
public sealed class ScheduledTaskProvider : IStartupProvider, IStartupChangeProvider
{
    private const string StateKind = "taskState";
    private const string Mechanism = "scheduledTask";

    public StartupSource Source => StartupSource.Task;

    public Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<StartupItem>>(() => Scan(cancellationToken), cancellationToken);

    public Task<StateSnapshot> CaptureStateAsync(StartupItem item, CancellationToken cancellationToken) =>
        Task.Run(() => CaptureState(item, cancellationToken), cancellationToken);

    public Task ApplyEnabledAsync(StartupItem item, bool enabled, bool isElevated, CancellationToken cancellationToken) =>
        Task.Run(() => ApplyEnabled(item, enabled, cancellationToken), cancellationToken);

    public Task<bool> VerifyEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) =>
        Task.Run(() => VerifyEnabled(item, enabled, cancellationToken), cancellationToken);

    public Task RestoreStateAsync(BackupRecord backup, bool isElevated, CancellationToken cancellationToken) =>
        Task.Run(() => RestoreState(backup, cancellationToken), cancellationToken);

    private static IReadOnlyList<StartupItem> Scan(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = new ComScope();
        dynamic service = ScheduledTaskCom.Connect(scope);
        dynamic root = ScheduledTaskCom.GetFolder(scope, service, "\\");
        var currentUser = CurrentUserIdentity.Get();
        var items = new List<StartupItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WalkFolder(scope, root, "\\", true, items, seenIds, currentUser, cancellationToken);
        return items;
    }

    private static void WalkFolder(
        ComScope scope,
        dynamic folder,
        string folderPath,
        bool isRoot,
        List<StartupItem> items,
        HashSet<string> seenIds,
        CurrentUserIdentity currentUser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        dynamic? tasks = null;
        try
        {
            tasks = scope.Track(folder.GetTasks(1));
        }
        catch (Exception exception) when (!isRoot)
        {
            // An inaccessible subfolder must not abort the scan.
            _ = exception;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("枚举计划任务", folderPath, exception);
        }

        if (tasks is not null)
        {
            var count = TaskValueConverters.ToInt(tasks.Count);
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    dynamic task = scope.Track(tasks[index])!;
                    var item = TryBuildItem(scope, task, currentUser);
                    if (item is not null && seenIds.Add(item.Id))
                    {
                        items.Add(item);
                    }
                }
                catch (Exception)
                {
                    // One unreadable task must not hide the rest of the machine.
                }
            }
        }

        dynamic? subfolders = null;
        try
        {
            subfolders = scope.Track(folder.GetFolders(1));
        }
        catch (Exception)
        {
            return;
        }

        if (subfolders is null)
        {
            return;
        }

        var subfolderCount = TaskValueConverters.ToInt(subfolders.Count);
        for (var index = 1; index <= subfolderCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                dynamic subfolder = scope.Track(subfolders[index])!;
                var subPath = ScheduledTaskClassifier.NormalizeTaskPath((string?)subfolder.Path);
                if (subPath.Length == 0)
                {
                    continue;
                }

                WalkFolder(scope, subfolder, subPath, false, items, seenIds, currentUser, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Skip inaccessible or disappearing folders.
            }
        }
    }

    private static StartupItem? TryBuildItem(ComScope scope, dynamic task, CurrentUserIdentity currentUser)
    {
        var path = ScheduledTaskClassifier.NormalizeTaskPath((string?)task.Path);
        if (path.Length == 0)
        {
            return null;
        }

        dynamic definition;
        try
        {
            definition = scope.Track(task.Definition)!;
        }
        catch (Exception)
        {
            return null;
        }

        dynamic triggers;
        try
        {
            triggers = scope.Track(definition.Triggers)!;
        }
        catch (Exception)
        {
            return null;
        }

        var triggerCount = TaskValueConverters.ToInt(triggers.Count);
        var triggerTypes = new List<int>(triggerCount);
        for (var index = 1; index <= triggerCount; index++)
        {
            try
            {
                triggerTypes.Add(TaskValueConverters.ToInt(triggers[index].Type));
            }
            catch (Exception)
            {
                // Ignore a single unreadable trigger.
            }
        }

        if (!ScheduledTaskClassifier.HasBootOrLogonTrigger(triggerTypes))
        {
            return null;
        }

        string principalUser = string.Empty;
        string principalGroup = string.Empty;
        var logonType = 0;
        var runLevel = 0;
        try
        {
            dynamic principal = scope.Track(definition.Principal)!;
            principalUser = (string?)principal.UserId ?? string.Empty;
            principalGroup = (string?)principal.GroupId ?? string.Empty;
            logonType = TaskValueConverters.ToInt(principal.LogonType);
            runLevel = TaskValueConverters.ToInt(principal.RunLevel);
        }
        catch (Exception)
        {
            // Scope falls back to AllUsers when the principal is unreadable.
        }

        string? actionPath = null;
        string? arguments = null;
        try
        {
            dynamic actions = scope.Track(definition.Actions)!;
            var actionCount = TaskValueConverters.ToInt(actions.Count);
            for (var index = 1; index <= actionCount; index++)
            {
                dynamic action = scope.Track(actions[index])!;
                // TASK_ACTION_EXEC = 0
                if (TaskValueConverters.ToInt(action.Type) != 0)
                {
                    continue;
                }

                actionPath = (string?)action.Path;
                arguments = (string?)action.Arguments;
                break;
            }
        }
        catch (Exception)
        {
            // A task with no readable exec action is still a startup task.
        }

        DateTimeOffset? lastModified = null;
        try
        {
            dynamic registrationInfo = scope.Track(definition.RegistrationInfo)!;
            lastModified = TaskValueConverters.ToDateOffset(registrationInfo.Date);
        }
        catch (Exception)
        {
            // Registration date is optional.
        }

        var name = ((string?)task.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = path.Substring(path.LastIndexOf('\\') + 1);
        }

        var executablePath = PublisherLookup.NormalizePath(actionPath);
        var publisher = PublisherLookup.TryGetPublisher(actionPath);
        var command = BuildTaskCommand(actionPath, arguments);
        var startupScope = ScheduledTaskClassifier.ClassifyScope(
            principalUser,
            principalGroup,
            currentUser.Name,
            currentUser.Sid);
        var requiresAdmin =
            ScheduledTaskClassifier.IsMicrosoftWindowsTaskPath(path) ||
            ScheduledTaskClassifier.IsHighPrivilegePrincipal(principalUser, principalGroup, runLevel);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TaskPath"] = path,
            ["TaskName"] = name,
            ["TriggerTypes"] = string.Join(",", triggerTypes.Distinct().OrderBy(type => type)),
            ["Principal"] = principalUser.Length > 0 ? principalUser : principalGroup,
            ["LogonType"] = logonType.ToString(CultureInfo.InvariantCulture),
            ["RunLevel"] = runLevel.ToString(CultureInfo.InvariantCulture)
        };

        return new StartupItem
        {
            Id = ScheduledTaskClassifier.CreateTaskId(path),
            Name = name,
            Publisher = publisher,
            Source = StartupSource.Task,
            Scope = startupScope,
            Enabled = TaskValueConverters.ToBool(task.Enabled),
            Protected = false,
            RequiresAdmin = requiresAdmin,
            Location = ScheduledTaskClassifier.BuildLocation(path),
            SubLocation = ScheduledTaskClassifier.BuildSubLocation(triggerTypes, principalUser, principalGroup),
            Command = command,
            TargetPath = executablePath.Length == 0 ? null : executablePath,
            LastModified = lastModified,
            Mechanism = Mechanism,
            Metadata = metadata
        };
    }

    private static StateSnapshot CaptureState(StartupItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTaskItem(item);
        var path = GetTaskPath(item);

        using var scope = new ComScope();
        dynamic service = ScheduledTaskCom.Connect(scope);
        dynamic task = ScheduledTaskCom.OpenTask(scope, service, path);

        var enabled = TaskValueConverters.ToBool(task.Enabled);
        string xml;
        try
        {
            xml = (string?)task.Xml ?? string.Empty;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("读取计划任务定义", path, exception);
        }

        if (xml.Length == 0)
        {
            throw new InvalidOperationException($"无法读取计划任务“{path}”的定义，已取消修改。");
        }

        var state = new ScheduledTaskBackupState
        {
            Path = path,
            Name = item.Name,
            Enabled = enabled,
            Xml = xml,
            CapturedAt = DateTimeOffset.Now
        };

        return new StateSnapshot
        {
            Kind = StateKind,
            Json = JsonSerializer.Serialize(state, JsonDefaults.Options),
            Summary = enabled ? "计划任务状态：已启用" : "计划任务状态：已禁用"
        };
    }

    private static void ApplyEnabled(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTaskItem(item);
        var path = GetTaskPath(item);

        using var scope = new ComScope();
        dynamic service = ScheduledTaskCom.Connect(scope);
        dynamic task = ScheduledTaskCom.OpenTask(scope, service, path);

        try
        {
            task.Enabled = enabled;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("修改计划任务", path, exception);
        }

        // Always re-read through a fresh COM object; a property echo is not proof.
        dynamic verifiedTask = ScheduledTaskCom.OpenTask(scope, service, path);
        var actual = TaskValueConverters.ToBool(verifiedTask.Enabled);
        if (actual != enabled)
        {
            throw new InvalidOperationException(
                $"计划任务“{path}”修改后重新读取仍未生效（当前状态：{(actual ? "已启用" : "已禁用")}）。");
        }
    }

    private static bool VerifyEnabled(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTaskItem(item);
        var path = GetTaskPath(item);

        using var scope = new ComScope();
        dynamic service = ScheduledTaskCom.Connect(scope);
        if (!ScheduledTaskCom.TryOpenTask(scope, (object)service, path, out _, out var taskObject) ||
            taskObject is null)
        {
            return false;
        }

        dynamic task = taskObject;
        return TaskValueConverters.ToBool(task.Enabled) == enabled;
    }

    private static void RestoreState(BackupRecord backup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(backup);

        if (backup.Source != StartupSource.Task)
        {
            throw new ArgumentException("备份记录不是计划任务类型，无法由计划任务提供程序恢复。", nameof(backup));
        }

        var state = DeserializeState(backup);
        var path = ScheduledTaskClassifier.NormalizeTaskPath(state.Path);
        if (path.Length == 0)
        {
            throw new InvalidOperationException("备份记录缺少计划任务路径，无法恢复。");
        }

        using var scope = new ComScope();
        dynamic service = ScheduledTaskCom.Connect(scope);

        if (ScheduledTaskCom.TryOpenTask(scope, (object)service, path, out _, out var existingObject) &&
            existingObject is not null)
        {
            dynamic existing = existingObject;
            try
            {
                existing.Enabled = state.Enabled;
            }
            catch (Exception exception)
            {
                throw TaskSchedulerErrors.Map("恢复计划任务状态", path, exception);
            }

            dynamic verified = ScheduledTaskCom.OpenTask(scope, service, path);
            var actual = TaskValueConverters.ToBool(verified.Enabled);
            if (actual != state.Enabled)
            {
                throw new InvalidOperationException(
                    $"恢复计划任务“{path}”后重新读取状态不一致（当前：{(actual ? "已启用" : "已禁用")}）。");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(state.Xml))
        {
            throw new InvalidOperationException(
                $"计划任务“{path}”已不存在，备份中没有可重建的任务定义，无法恢复。");
        }

        var (folderPath, taskName) = ScheduledTaskCom.SplitTaskPath(path);
        dynamic definition;
        try
        {
            definition = scope.Track(service.NewTask(0))!;
            definition.XmlText = state.Xml;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("重建计划任务定义", path, exception);
        }

        dynamic folder;
        try
        {
            folder = ScheduledTaskCom.GetFolder(scope, service, folderPath);
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("重建计划任务", path, exception);
        }

        dynamic registered;
        try
        {
            // TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_NONE = 0. The principal
            // (including its logon type) comes from the saved task XML.
            registered = scope.Track(folder.RegisterTaskDefinition(taskName, definition, 6, null, null, 0, null))!;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map(
                "计划任务已不存在，重建任务定义",
                path,
                exception,
                "如果该任务原属于受保护范围，请以管理员身份运行后再恢复。");
        }

        if (TaskValueConverters.ToBool(registered.Enabled) != state.Enabled)
        {
            try
            {
                registered.Enabled = state.Enabled;
            }
            catch (Exception exception)
            {
                throw TaskSchedulerErrors.Map("恢复重建计划任务的启用状态", path, exception);
            }
        }

        dynamic verifiedTask = ScheduledTaskCom.OpenTask(scope, service, path);
        var actualEnabled = TaskValueConverters.ToBool(verifiedTask.Enabled);
        if (actualEnabled != state.Enabled || ((string?)verifiedTask.Xml ?? string.Empty).Length == 0)
        {
            throw new InvalidOperationException(
                $"计划任务“{path}”已重建，但重新读取状态不一致（当前：{(actualEnabled ? "已启用" : "已禁用")}）。");
        }
    }

    private static ScheduledTaskBackupState DeserializeState(BackupRecord backup)
    {
        if (string.IsNullOrWhiteSpace(backup.StateJson))
        {
            throw new InvalidOperationException("备份记录缺少计划任务状态，无法恢复。");
        }

        try
        {
            var state = JsonSerializer.Deserialize<ScheduledTaskBackupState>(backup.StateJson, JsonDefaults.Options);
            if (state is null)
            {
                throw new InvalidOperationException("备份记录中的计划任务状态无法解析，无法恢复。");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("备份记录中的计划任务状态已损坏，无法恢复。", exception);
        }
    }

    private static void EnsureTaskItem(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Source != StartupSource.Task)
        {
            throw new ArgumentException("该启动项不是计划任务，无法由计划任务提供程序处理。", nameof(item));
        }
    }

    private static string GetTaskPath(StartupItem item)
    {
        if (item.Metadata.TryGetValue("TaskPath", out var metadataPath) &&
            !string.IsNullOrWhiteSpace(metadataPath))
        {
            return ScheduledTaskClassifier.NormalizeTaskPath(metadataPath);
        }

        var location = item.Location?.Trim() ?? string.Empty;
        const string prefix = "任务计划程序库";
        if (location.StartsWith(prefix, StringComparison.Ordinal))
        {
            location = location.Substring(prefix.Length).Trim();
        }

        if (location.StartsWith('\\'))
        {
            return ScheduledTaskClassifier.NormalizeTaskPath(location);
        }

        throw new InvalidOperationException(
            $"无法确定启动项“{item.Name}”对应的计划任务路径，已取消修改。");
    }

    private static string BuildTaskCommand(string? actionPath, string? arguments)
    {
        var rawPath = (actionPath ?? string.Empty).Trim();
        var args = (arguments ?? string.Empty).Trim();

        if (rawPath.Length == 0)
        {
            return args;
        }

        var unquoted = rawPath.Trim('"');
        var displayPath = rawPath.StartsWith('"')
            ? rawPath
            : unquoted.Contains(' ')
                ? "\"" + unquoted + "\""
                : unquoted;

        return args.Length == 0 ? displayPath : displayPath + " " + args;
    }
}

internal readonly record struct CurrentUserIdentity(string Name, string Sid)
{
    public static CurrentUserIdentity Get()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new CurrentUserIdentity(identity.Name, identity.User?.Value ?? string.Empty);
        }
        catch
        {
            return new CurrentUserIdentity(Environment.UserName, string.Empty);
        }
    }
}

/// <summary>Tracks and releases the COM objects created during one operation.</summary>
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _objects = new();

    public object? Track(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _objects.Add(value);
        }

        return value;
    }

    public void Dispose()
    {
        for (var index = _objects.Count - 1; index >= 0; index--)
        {
            try
            {
                if (Marshal.IsComObject(_objects[index]))
                {
                    Marshal.FinalReleaseComObject(_objects[index]);
                }
            }
            catch
            {
                // Releasing is best effort; never mask the real result.
            }
        }

        _objects.Clear();
    }
}

internal static class ScheduledTaskCom
{
    public static dynamic Connect(ComScope scope)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
        {
            throw new PlatformNotSupportedException("当前系统不支持任务计划程序（Schedule.Service）。");
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("无法启动任务计划程序 COM 服务。", exception);
        }

        dynamic service = scope.Track(instance)!;
        try
        {
            service.Connect();
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("连接任务计划程序", string.Empty, exception);
        }

        return service;
    }

    public static dynamic GetFolder(ComScope scope, dynamic service, string folderPath)
    {
        var normalized = string.IsNullOrWhiteSpace(folderPath) ? "\\" : folderPath;
        try
        {
            return scope.Track(service.GetFolder(normalized))!;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("打开计划任务文件夹", normalized, exception);
        }
    }

    public static dynamic OpenTask(ComScope scope, dynamic service, string taskPath)
    {
        var (folderPath, taskName) = SplitTaskPath(taskPath);
        dynamic folder = GetFolder(scope, service, folderPath);
        try
        {
            return scope.Track(folder.GetTask(taskName))!;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("打开计划任务", taskPath, exception);
        }
    }

    /// <summary>Returns false only when the task genuinely does not exist.</summary>
    public static bool TryOpenTask(
        ComScope scope,
        object service,
        string taskPath,
        out object? folder,
        out object? task)
    {
        task = null;
        folder = null;
        var (folderPath, taskName) = SplitTaskPath(taskPath);
        dynamic dynamicService = service;

        try
        {
            folder = scope.Track(dynamicService.GetFolder(folderPath));
        }
        catch (Exception exception) when (TaskSchedulerErrors.IsNotFound(exception))
        {
            return false;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("打开计划任务文件夹", folderPath, exception);
        }

        if (folder is null)
        {
            return false;
        }

        dynamic dynamicFolder = folder;
        try
        {
            task = scope.Track(dynamicFolder.GetTask(taskName));
            return task is not null;
        }
        catch (Exception exception) when (TaskSchedulerErrors.IsNotFound(exception))
        {
            return false;
        }
        catch (Exception exception)
        {
            throw TaskSchedulerErrors.Map("打开计划任务", taskPath, exception);
        }
    }

    public static (string FolderPath, string TaskName) SplitTaskPath(string taskPath)
    {
        var path = ScheduledTaskClassifier.NormalizeTaskPath(taskPath);
        var slash = path.LastIndexOf('\\');
        if (path.Length == 0 || slash < 0 || slash == path.Length - 1)
        {
            throw new ArgumentException($"计划任务路径无效：{taskPath}", nameof(taskPath));
        }

        var name = path.Substring(slash + 1);
        var folder = slash == 0 ? "\\" : path.Substring(0, slash);
        return (folder, name);
    }
}

internal static class TaskSchedulerErrors
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    public static bool IsNotFound(Exception exception)
    {
        // The COM binder surfaces HRESULT 0x80070002 as FileNotFoundException.
        if (exception is FileNotFoundException || exception is DirectoryNotFoundException)
        {
            return true;
        }

        return exception is COMException com &&
            (com.HResult == FileNotFound || com.HResult == PathNotFound);
    }

    public static Exception Map(
        string operation,
        string taskPath,
        Exception exception,
        string? hint = null)
    {
        var pathText = string.IsNullOrWhiteSpace(taskPath) ? string.Empty : $"“{taskPath}”";
        var hintText = string.IsNullOrWhiteSpace(hint) ? string.Empty : hint;

        if (exception is UnauthorizedAccessException || exception.HResult == AccessDenied)
        {
            return new UnauthorizedAccessException(
                $"{operation}{pathText}需要管理员权限。{(hintText.Length > 0 ? " " + hintText : string.Empty)}",
                exception);
        }

        if (IsNotFound(exception))
        {
            return new InvalidOperationException($"{operation}{pathText}失败：计划任务不存在。", exception);
        }

        var message = exception.Message.Trim();
        return new InvalidOperationException(
            $"{operation}{pathText}失败：{message}{(hintText.Length > 0 ? " " + hintText : string.Empty)}",
            exception);
    }
}
