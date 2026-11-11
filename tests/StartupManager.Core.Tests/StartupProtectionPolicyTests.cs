using StartupManager.Core.Models;
using StartupManager.Core.Protection;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class StartupProtectionPolicyTests
{
    private readonly StartupProtectionPolicy _policy = new();

    [Fact]
    public void TeamViewer_service_is_manageable_but_requires_admin()
    {
        var item = ServiceItem(
            "TeamViewer",
            "TeamViewer 服务",
            "TeamViewer Germany GmbH",
            @"C:\Program Files\TeamViewer\TeamViewer_Service.exe");

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
        Assert.Null(decision.Reason);
        Assert.True(decision.RequiresAdmin);
    }

    [Fact]
    public void Tailscale_service_is_manageable()
    {
        var item = ServiceItem(
            "Tailscale",
            "Tailscale",
            "Tailscale Inc.",
            @"D:\Tailscale\tailscaled.exe");

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
        Assert.True(decision.RequiresAdmin);
    }

    [Theory]
    [InlineData("NVDisplay.ContainerLocalSystem", "NVIDIA Display Container LS", "NVIDIA Corporation", @"C:\Program Files\NVIDIA Corporation\Display.NvContainer\NVDisplay.Container.exe")]
    [InlineData("RtkAudioUniversalService", "Realtek 高清晰音频管理器", "Realtek Semiconductor Corp.", @"C:\Windows\System32\DriverStore\FileRepository\realtekservice.inf_amd64\RtkAudUService64.exe")]
    [InlineData("RtkBtManServ", "Realtek Bluetooth Radio", "Realtek Semiconductor Corp.", @"C:\Program Files\Realtek\BtManServ.exe")]
    [InlineData("AMD External Events Utility", "AMD External Events Utility", "Advanced Micro Devices, Inc.", @"C:\Windows\System32\atiesrxx.exe")]
    [InlineData("IntelAudioService", "Intel(R) Audio Service", "Intel Corporation", @"C:\Windows\System32\DriverStore\FileRepository\intcaudiobus.inf_amd64\IntelAudioService.exe")]
    public void Known_hardware_support_services_are_protected(
        string serviceName,
        string displayName,
        string publisher,
        string path)
    {
        var item = ServiceItem(serviceName, displayName, publisher, path);

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.True(decision.RequiresAdmin);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        Assert.Contains("已锁定", decision.Reason);
    }

    [Fact]
    public void Realtek_service_is_protected_even_when_publisher_is_missing()
    {
        var item = ServiceItem(
            "RtkAudioUniversalService",
            "Realtek 高清晰音频管理器",
            publisher: string.Empty,
            @"C:\Windows\System32\DriverStore\FileRepository\realtekservice.inf_amd64\RtkAudUService64.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("Realtek", decision.Reason);
        Assert.Contains("音频", decision.Reason);
    }

    [Fact]
    public void Realtek_bluetooth_service_reason_is_specific()
    {
        var item = ServiceItem(
            "RtkBtManServ",
            "Realtek Bluetooth Radio",
            "Realtek Semiconductor Corp.",
            @"C:\Program Files\Realtek\BtManServ.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("蓝牙", decision.Reason);
    }

    [Fact]
    public void Nvidia_display_service_reason_is_specific()
    {
        var item = ServiceItem(
            "NVDisplay.ContainerLocalSystem",
            "NVIDIA Display Container LS",
            "NVIDIA Corporation",
            @"C:\Program Files\NVIDIA Corporation\Display.NvContainer\NVDisplay.Container.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("NVIDIA", decision.Reason);
        Assert.Contains("显示", decision.Reason);
    }

    [Theory]
    [InlineData("RpcSs", "DCOM Server Process Launcher")]
    [InlineData("EventLog", "Windows Event Log")]
    [InlineData("Power", "Power")]
    [InlineData("Schedule", "Task Scheduler")]
    [InlineData("Themes", "Themes")]
    [InlineData("Dhcp", "DHCP Client")]
    [InlineData("Dnscache", "DNS Client")]
    [InlineData("WlanSvc", "WLAN AutoConfig")]
    public void Core_windows_services_are_protected(string serviceName, string displayName)
    {
        var item = ServiceItem(serviceName, displayName, "Microsoft Corporation", @"C:\Windows\System32\svchost.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.True(decision.RequiresAdmin);
        Assert.Contains("已锁定", decision.Reason);
    }

    [Theory]
    [InlineData("WinDefend", "Microsoft Defender Antivirus Service")]
    [InlineData("mpssvc", "Windows Defender Firewall")]
    [InlineData("BFE", "Base Filtering Engine")]
    [InlineData("SecurityHealthService", "Windows Security Service")]
    [InlineData("Sense", "Windows Defender Advanced Threat Protection Service")]
    public void Security_services_are_protected(string serviceName, string displayName)
    {
        var item = ServiceItem(serviceName, displayName, "Microsoft Corporation", @"C:\Windows\System32\svchost.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("安全", decision.Reason);
    }

    [Fact]
    public void Microsoft_published_automatic_service_is_protected()
    {
        var item = ServiceItem(
            "DiagTrack",
            "Connected User Experiences and Telemetry",
            "Microsoft Corporation",
            @"C:\Windows\System32\svchost.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("Microsoft", decision.Reason);
    }

    [Fact]
    public void Driver_service_is_protected_by_service_type()
    {
        var item = ServiceItem(
            "SomeDriver",
            "Some Kernel Driver",
            "Third Party Vendor",
            @"C:\Windows\System32\drivers\somedriver.sys",
            serviceType: "1");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("驱动", decision.Reason);
    }

    [Fact]
    public void Service_with_unknown_publisher_is_protected()
    {
        var item = ServiceItem(
            "clash_verge_service",
            "Clash Verge Service",
            publisher: string.Empty,
            @"D:\ClashVerge\resources\clash-verge-service.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.True(decision.CanOverride);
        Assert.Contains("无法确认", decision.Reason);
    }

    [Fact]
    public void Microsoft_service_protection_cannot_be_overridden()
    {
        var item = ServiceItem(
            "DiagTrack",
            "Connected User Experiences and Telemetry",
            "Microsoft Corporation",
            @"C:\Windows\System32\svchost.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.False(decision.CanOverride);
    }

    [Fact]
    public void Core_service_protection_cannot_be_overridden()
    {
        var item = ServiceItem(
            "RpcSs",
            "Remote Procedure Call (RPC)",
            "Microsoft Corporation",
            @"C:\Windows\System32\svchost.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.False(decision.CanOverride);
    }

    [Fact]
    public void Service_located_in_windows_directory_is_protected()
    {
        var item = ServiceItem(
            "ASUSOptimization",
            "ASUS Optimization",
            "ASUSTeK COMPUTER INC.",
            @"C:\Windows\System32\DriverStore\FileRepository\asussci2.inf_amd64\ASUSOptimization\AsusOptimization.exe");

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("系统目录", decision.Reason);
    }

    [Fact]
    public void Windows_owned_scheduled_task_is_protected()
    {
        var item = TaskItem(@"\Microsoft\Windows\Defrag\ScheduledDefrag", requiresAdmin: true);

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("Windows", decision.Reason);
        Assert.True(decision.RequiresAdmin);
    }

    [Fact]
    public void Microsoft_component_scheduled_task_is_protected()
    {
        var item = TaskItem(@"\Microsoft\Office\Office Actions Server", requiresAdmin: false);

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("Microsoft", decision.Reason);
    }

    [Fact]
    public void Third_party_scheduled_task_is_manageable()
    {
        var item = TaskItem(@"\Adobe Acrobat Update Task", requiresAdmin: false);

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
        Assert.Null(decision.Reason);
        Assert.False(decision.RequiresAdmin);
    }

    [Fact]
    public void System_third_party_task_is_manageable_but_requires_admin()
    {
        var item = TaskItem(@"\VendorUpdater\Vendor Update Task", requiresAdmin: true);

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
        Assert.True(decision.RequiresAdmin);
    }

    [Fact]
    public void Windows_security_run_entry_is_protected()
    {
        var item = RunItem(
            "SecurityHealth",
            @"%windir%\system32\SecurityHealthSystray.exe",
            requiresAdmin: false);

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("安全", decision.Reason);
        Assert.False(decision.RequiresAdmin);
    }

    [Fact]
    public void Windows_defender_run_entry_is_protected()
    {
        var item = RunItem(
            "Windows Defender",
            @"""C:\Program Files\Windows Defender\MSASCuiL.exe""",
            requiresAdmin: true);

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("安全", decision.Reason);
    }

    [Fact]
    public void Windows_directory_run_entry_is_protected()
    {
        var item = RunItem(
            "系统组件",
            @"C:\Windows\System32\SystemSettings.exe",
            requiresAdmin: false);

        var decision = _policy.Evaluate(item);

        Assert.True(decision.IsProtected);
        Assert.Contains("Windows", decision.Reason);
    }

    [Fact]
    public void WeChat_run_entry_is_manageable()
    {
        var item = RunItem(
            "微信",
            @"""C:\Program Files\Tencent\WeChat\WeChat.exe"" --auto-start-offline",
            requiresAdmin: false);

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
        Assert.Null(decision.Reason);
        Assert.False(decision.RequiresAdmin);
    }

    [Fact]
    public void Steam_startup_folder_entry_is_manageable()
    {
        var item = RunItem(
            "Steam",
            @"""C:\Program Files (x86)\Steam\steam.exe"" -silent",
            requiresAdmin: false,
            source: StartupSource.Folder);

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
    }

    [Fact]
    public void Microsoft_publisher_alone_does_not_protect_a_user_run_entry()
    {
        var item = RunItem(
            "OneDrive",
            @"""C:\Users\Zy\AppData\Local\Microsoft\OneDrive\OneDrive.exe"" /background",
            requiresAdmin: false);

        var decision = _policy.Evaluate(item);

        Assert.False(decision.IsProtected);
    }

    private static StartupItem ServiceItem(
        string serviceName,
        string displayName,
        string publisher,
        string path,
        string? serviceType = null)
    {
        var item = new StartupItem
        {
            Id = Providers.ServiceCommandLine.CreateServiceId(serviceName),
            Name = displayName,
            Publisher = publisher,
            Source = StartupSource.Service,
            Scope = StartupScope.System,
            Enabled = true,
            RequiresAdmin = true,
            Location = "服务：" + serviceName,
            SubLocation = "启动类型：自动 · 服务名称：" + serviceName,
            Command = path,
            TargetPath = path,
            Mechanism = "serviceStartType"
        };

        item.Metadata["ServiceName"] = serviceName;
        if (serviceType is not null)
        {
            item.Metadata["ServiceType"] = serviceType;
        }

        return item;
    }

    private static StartupItem TaskItem(string taskPath, bool requiresAdmin)
    {
        var item = new StartupItem
        {
            Id = Providers.ScheduledTaskClassifier.CreateTaskId(taskPath),
            Name = taskPath.Substring(taskPath.LastIndexOf('\\') + 1),
            Source = StartupSource.Task,
            Scope = StartupScope.System,
            Enabled = true,
            RequiresAdmin = requiresAdmin,
            Location = Providers.ScheduledTaskClassifier.BuildLocation(taskPath),
            SubLocation = "触发器：登录时 · 用户：SYSTEM",
            Command = string.Empty,
            Mechanism = "scheduledTask"
        };

        item.Metadata["TaskPath"] = taskPath;
        return item;
    }

    private static StartupItem RunItem(
        string name,
        string command,
        bool requiresAdmin,
        StartupSource source = StartupSource.Registry)
    {
        return new StartupItem
        {
            Id = "reg-test",
            Name = name,
            Source = source,
            Scope = StartupScope.CurrentUser,
            Enabled = true,
            RequiresAdmin = requiresAdmin,
            Location = source == StartupSource.Registry
                ? @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
                : @"C:\Users\Zy\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
            SubLocation = name,
            Command = command,
            Mechanism = "startupApproved"
        };
    }
}
