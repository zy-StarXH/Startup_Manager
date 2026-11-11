using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using StartupManager.Core.Models;
using StartupManager.Core.Providers;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ScheduledTaskProviderTests
{
    [Theory]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(7, false)]
    [InlineData(11, false)]
    public void Boot_and_logon_triggers_are_classified(int triggerType, bool expected)
    {
        Assert.Equal(expected, ScheduledTaskClassifier.IsBootOrLogonTrigger(triggerType));
    }

    [Fact]
    public void Mixed_trigger_list_is_recognised_and_described()
    {
        var types = new[] { 2, 8, 9, 11 };

        Assert.True(ScheduledTaskClassifier.HasBootOrLogonTrigger(types));
        var description = ScheduledTaskClassifier.DescribeTriggers(types);
        Assert.Contains("启动时", description);
        Assert.Contains("登录时", description);
        Assert.DoesNotContain("其他", description);
    }

    [Theory]
    [InlineData("SYSTEM", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.System)]
    [InlineData("S-1-5-18", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.System)]
    [InlineData(@"NT SERVICE\SomeService", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.System)]
    [InlineData("LOCAL SERVICE", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.System)]
    [InlineData(@"LAPTOP\Zy", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.CurrentUser)]
    [InlineData("Zy", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.CurrentUser)]
    [InlineData("S-1-5-21-1", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.CurrentUser)]
    [InlineData(null, "Users", @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.AllUsers)]
    [InlineData(null, "INTERACTIVE", @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.AllUsers)]
    [InlineData(null, "S-1-5-32-545", @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.AllUsers)]
    [InlineData(null, null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.AllUsers)]
    [InlineData("OtherUser", null, @"LAPTOP\Zy", "S-1-5-21-1", StartupScope.AllUsers)]
    public void Principal_scope_is_classified(
        string? principalUser,
        string? principalGroup,
        string currentName,
        string currentSid,
        StartupScope expected)
    {
        Assert.Equal(
            expected,
            ScheduledTaskClassifier.ClassifyScope(principalUser, principalGroup, currentName, currentSid));
    }

    [Theory]
    [InlineData("SYSTEM", true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-19", true)]
    [InlineData("S-1-5-20", true)]
    [InlineData(@"NT SERVICE\Foo", true)]
    [InlineData("S-1-5-80-1234-5678", true)]
    [InlineData("S-1-5-21-1001", false)]
    [InlineData("Users", false)]
    public void System_accounts_are_recognised(string identity, bool expected)
    {
        Assert.Equal(expected, ScheduledTaskClassifier.IsSystemPrincipal(identity));
    }

    [Fact]
    public void Highest_privilege_principal_requires_admin()
    {
        Assert.True(ScheduledTaskClassifier.IsHighPrivilegePrincipal("SYSTEM", null, 0));
        Assert.True(ScheduledTaskClassifier.IsHighPrivilegePrincipal(@"LAPTOP\Zy", null, 1));
        Assert.True(ScheduledTaskClassifier.IsHighPrivilegePrincipal(null, "S-1-5-32-544", 0));
        Assert.False(ScheduledTaskClassifier.IsHighPrivilegePrincipal(@"LAPTOP\Zy", null, 0));
    }

    [Fact]
    public void Sub_location_contains_trigger_and_principal()
    {
        var text = ScheduledTaskClassifier.BuildSubLocation(
            new[] { 9, 8 },
            @"LAPTOP\Zy",
            string.Empty);

        Assert.Contains("触发器：", text);
        Assert.Contains("启动时", text);
        Assert.Contains("登录时", text);
        Assert.Contains("用户：Zy", text);

        var groupText = ScheduledTaskClassifier.BuildSubLocation(new[] { 9 }, string.Empty, "Users");
        Assert.Contains("用户组：Users", groupText);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true, true)]
    [InlineData("Microsoft\\Windows\\Defrag\\ScheduledDefrag", true, true)]
    [InlineData(@"\Microsoft\Office\Office Actions Server", false, true)]
    [InlineData(@"\Adobe Acrobat Update Task", false, false)]
    public void Microsoft_task_paths_are_recognised(
        string path,
        bool isWindowsPath,
        bool isMicrosoftPath)
    {
        Assert.Equal(isWindowsPath, ScheduledTaskClassifier.IsMicrosoftWindowsTaskPath(path));
        Assert.Equal(isMicrosoftPath, ScheduledTaskClassifier.IsMicrosoftTaskPath(path));
    }

    [Fact]
    public void Task_location_and_id_are_stable()
    {
        var location = ScheduledTaskClassifier.BuildLocation("Vendor\\Task");
        Assert.Equal(@"任务计划程序库 \Vendor\Task", location);

        var id = ScheduledTaskClassifier.CreateTaskId(@"\Vendor\Task");
        Assert.Equal(id, ScheduledTaskClassifier.CreateTaskId("Vendor\\Task"));
        Assert.Matches("^tsk-[0-9a-f]{64}$", id);
        Assert.NotEqual(id, ScheduledTaskClassifier.CreateTaskId(@"\Vendor\OtherTask"));
        Assert.Throws<ArgumentException>(() => ScheduledTaskClassifier.CreateTaskId("  "));
    }

    [Fact]
    public async Task Scan_is_read_only_and_reports_well_formed_boot_or_logon_tasks()
    {
        var provider = new ScheduledTaskProvider();

        var items = await provider.ScanAsync(CancellationToken.None);

        Assert.NotEmpty(items);
        Assert.Equal(StartupSource.Task, provider.Source);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            Assert.Equal(StartupSource.Task, item.Source);
            Assert.False(item.Protected);
            Assert.Equal("scheduledTask", item.Mechanism);
            Assert.StartsWith("tsk-", item.Id);
            Assert.Matches("^tsk-[0-9a-f]{64}$", item.Id);
            Assert.True(ids.Add(item.Id));
            Assert.StartsWith("任务计划程序库 ", item.Location);
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.SubLocation));
            Assert.Contains("触发器：", item.SubLocation);

            Assert.True(item.Metadata.TryGetValue("TaskPath", out var taskPath));
            Assert.StartsWith(@"\", taskPath);

            Assert.True(item.Metadata.TryGetValue("TriggerTypes", out var rawTypes));
            var triggerTypes = rawTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();
            Assert.True(ScheduledTaskClassifier.HasBootOrLogonTrigger(triggerTypes));
        }
    }

    [Fact]
    public async Task Capture_and_verify_read_the_task_without_changing_it()
    {
        var provider = new ScheduledTaskProvider();
        var items = await provider.ScanAsync(CancellationToken.None);
        var item = items.First();

        var state = await provider.CaptureStateAsync(item, CancellationToken.None);

        Assert.Equal("taskState", state.Kind);
        Assert.Contains("计划任务状态", state.Summary);
        using var document = JsonDocument.Parse(state.Json);
        Assert.Equal(
            item.Metadata["TaskPath"],
            document.RootElement.GetProperty("path").GetString());
        Assert.Equal(item.Enabled, document.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("xml").GetString()));

        Assert.True(await provider.VerifyEnabledAsync(item, item.Enabled, CancellationToken.None));
        Assert.False(await provider.VerifyEnabledAsync(item, !item.Enabled, CancellationToken.None));
    }

    [Fact]
    public async Task Non_task_items_are_rejected_without_touching_the_machine()
    {
        var provider = new ScheduledTaskProvider();
        var item = new StartupItem
        {
            Id = "svc-test",
            Name = "Not a task",
            Source = StartupSource.Service,
            Scope = StartupScope.System,
            Enabled = true
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CaptureStateAsync(item, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.ApplyEnabledAsync(item, false, false, CancellationToken.None));
    }

    [ScheduledTaskIntegrationFact]
    public async Task Temporary_logon_task_can_be_disabled_restored_and_deleted()
    {
        var name = "StartupManager_Test_" + Guid.NewGuid().ToString("N");
        var taskPath = "\\" + name;
        dynamic? service = null;
        dynamic? root = null;

        try
        {
            dynamic connected = ScheduledTaskTestSupport.ConnectService();
            service = connected;
            dynamic folder = connected.GetFolder("\\");
            root = folder;

            dynamic definition = ScheduledTaskTestSupport.NewLogonTaskDefinition(connected);
            folder.RegisterTaskDefinition(name, definition, 6, null, null, 3, null);

            var provider = new ScheduledTaskProvider();
            var items = await provider.ScanAsync(CancellationToken.None);
            var item = Assert.Single(
                items,
                candidate => candidate.Metadata.TryGetValue("TaskPath", out var path) &&
                    string.Equals(path, taskPath, StringComparison.OrdinalIgnoreCase));

            Assert.True(item.Enabled);
            Assert.Equal(StartupScope.CurrentUser, item.Scope);
            Assert.False(item.Protected);
            Assert.False(item.RequiresAdmin);

            var state = await provider.CaptureStateAsync(item, CancellationToken.None);
            Assert.Equal("taskState", state.Kind);
            Assert.Contains("已启用", state.Summary);
            Assert.Contains("<Task", state.Json);

            await provider.ApplyEnabledAsync(item, false, false, CancellationToken.None);
            Assert.False(await provider.VerifyEnabledAsync(item, false, CancellationToken.None));

            var scanAfterDisable = await provider.ScanAsync(CancellationToken.None);
            var disabledItem = Assert.Single(
                scanAfterDisable,
                candidate => candidate.Metadata.TryGetValue("TaskPath", out var path) &&
                    string.Equals(path, taskPath, StringComparison.OrdinalIgnoreCase));
            Assert.False(disabledItem.Enabled);

            var backup = new BackupRecord
            {
                Id = "test-backup",
                ItemId = item.Id,
                Source = StartupSource.Task,
                Scope = item.Scope,
                RequiresAdmin = item.RequiresAdmin,
                DisplayName = item.Name,
                Location = item.Location,
                Command = item.Command,
                CreatedAt = DateTimeOffset.Now,
                Summary = state.Summary,
                StateKind = state.Kind,
                StateJson = state.Json
            };

            await provider.RestoreStateAsync(backup, false, CancellationToken.None);
            Assert.True(await provider.VerifyEnabledAsync(item, true, CancellationToken.None));

            await provider.ApplyEnabledAsync(item, false, false, CancellationToken.None);
            Assert.False(await provider.VerifyEnabledAsync(item, false, CancellationToken.None));

            await provider.ApplyEnabledAsync(item, true, false, CancellationToken.None);
            Assert.True(await provider.VerifyEnabledAsync(item, true, CancellationToken.None));
        }
        finally
        {
            if (root is not null)
            {
                try
                {
                    root.DeleteTask(name, 0);
                }
                catch
                {
                    // Best-effort cleanup; the probe already proved deletion works.
                }
            }

            ScheduledTaskTestSupport.ReleaseCom(root);
            ScheduledTaskTestSupport.ReleaseCom(service);
        }
    }
}

/// <summary>
/// Skips the task integration test when the current non-elevated environment
/// cannot create a task. It never requests elevation.
/// </summary>
public sealed class ScheduledTaskIntegrationFactAttribute : FactAttribute
{
    public ScheduledTaskIntegrationFactAttribute()
    {
        var probe = ScheduledTaskIntegrationProbe.Result;
        if (!probe.Allowed)
        {
            Skip = probe.Reason;
        }
    }
}

internal static class ScheduledTaskIntegrationProbe
{
    private static readonly Lazy<(bool Allowed, string Reason)> LazyResult =
        new(ProbeCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static (bool Allowed, string Reason) Result => LazyResult.Value;

    private static (bool Allowed, string Reason) ProbeCore()
    {
        var name = "StartupManager_Probe_" + Guid.NewGuid().ToString("N");
        object? serviceObject = null;
        object? rootObject = null;

        try
        {
            dynamic service = ScheduledTaskTestSupport.ConnectService();
            serviceObject = service;
            dynamic root = service.GetFolder("\\");
            rootObject = root;

            dynamic definition = ScheduledTaskTestSupport.NewLogonTaskDefinition(service);
            root.RegisterTaskDefinition(name, definition, 6, null, null, 3, null);
            dynamic registered = root.GetTask(name);
            _ = registered.Enabled;
            root.DeleteTask(name, 0);

            return (true, string.Empty);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException ||
            exception.HResult == unchecked((int)0x80070005))
        {
            return (
                false,
                "当前环境不允许创建计划任务（需要管理员权限），已跳过集成测试，未触发 UAC。详情：" +
                exception.Message);
        }
        catch (Exception exception)
        {
            return (false, "任务计划程序 COM 集成测试在当前环境不可用，已跳过：" + exception.Message);
        }
        finally
        {
            if (rootObject is not null)
            {
                try
                {
                    dynamic root = rootObject;
                    root.DeleteTask(name, 0);
                }
                catch
                {
                    // Nothing else can be done without elevation.
                }
            }

            ScheduledTaskTestSupport.ReleaseCom(rootObject);
            ScheduledTaskTestSupport.ReleaseCom(serviceObject);
        }
    }
}

internal static class ScheduledTaskTestSupport
{
    public static dynamic ConnectService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
        {
            throw new PlatformNotSupportedException("当前系统不支持任务计划程序（Schedule.Service）。");
        }

        dynamic service = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
        service.Connect();
        return service;
    }

    public static dynamic NewLogonTaskDefinition(dynamic service)
    {
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "StartupManager temporary integration test task";
        definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        definition.Settings.Enabled = true;

        dynamic trigger = definition.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.Enabled = true;

        dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "notepad.exe");
        action.Arguments = "--startupmanager-test-never-executed";
        return definition;
    }

    public static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Best effort.
        }
    }
}
