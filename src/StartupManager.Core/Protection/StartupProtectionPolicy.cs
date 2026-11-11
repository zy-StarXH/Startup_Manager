using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Providers;

namespace StartupManager.Core.Protection;

/// <summary>
/// Safety-first protection rules for all four startup sources.
/// The policy only classifies items; it never reads or changes the machine.
/// </summary>
public sealed class StartupProtectionPolicy : IProtectionPolicy
{
    private const string TaskReasonWindows =
        "这是 Windows 系统自带的计划任务，禁用可能影响系统功能，因此已锁定。";

    private const string TaskReasonMicrosoft =
        "这是 Microsoft 组件自带的计划任务，禁用可能影响相关组件功能，因此已锁定。";

    private const string ServiceReasonDriver =
        "这是系统驱动服务，禁用可能导致设备或系统无法正常工作，因此已锁定。";

    private const string ServiceReasonSecurity =
        "这是 Windows 安全组件服务，禁用可能降低系统安全防护，因此已锁定。";

    private const string ServiceReasonCore =
        "这是 Windows 核心系统服务，禁用可能导致系统功能异常，因此已锁定。";

    private const string ServiceReasonRealtek =
        "这是 Realtek 音频驱动服务，禁用可能导致扬声器或耳机没有声音，因此已锁定。";

    private const string ServiceReasonRealtekBluetooth =
        "这是 Realtek 蓝牙支持服务，禁用可能导致蓝牙设备无法正常使用，因此已锁定。";

    private const string ServiceReasonNvidia =
        "这是 NVIDIA 显示驱动服务，禁用可能导致分辨率切换、多屏输出等显示功能异常，因此已锁定。";

    private const string ServiceReasonAmd =
        "这是 AMD 显卡或音频驱动服务，禁用可能导致显示或声音异常，因此已锁定。";

    private const string ServiceReasonIntel =
        "这是 Intel 显卡或音频驱动服务，禁用可能导致显示或声音异常，因此已锁定。";

    private const string ServiceReasonMicrosoft =
        "这是 Microsoft 系统服务，禁用可能影响系统或相关组件的正常运行，因此已锁定。";

    private const string ServiceReasonSystemDirectory =
        "这是位于 Windows 系统目录的服务，禁用可能影响系统稳定，因此已锁定。";

    private const string ServiceReasonUnverified =
        "无法确认该服务的发布者或程序路径，出于安全考虑已锁定。";

    private const string RunEntryReasonSecurity =
        "这是 Windows 安全中心 / Microsoft Defender 的系统启动项，禁用可能降低系统安全性，因此已锁定。";

    private const string RunEntryReasonWindows =
        "这是 Windows 系统自带的启动项，禁用可能影响系统功能，因此已锁定。";

    private static readonly HashSet<string> SecurityServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend",
        "WdNisSvc",
        "Sense",
        "SecurityHealthService",
        "wscsvc",
        "BFE",
        "mpssvc",
        "webthreatdefsvc",
        "webthreatdefusersvc",
        "MDCoreSvc",
        "SgrmBroker"
    };

    private static readonly HashSet<string> CoreWindowsServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs",
        "RpcEptMapper",
        "DcomLaunch",
        "Power",
        "EventLog",
        "Schedule",
        "Winmgmt",
        "Audiosrv",
        "AudioEndpointBuilder",
        "Themes",
        "WlanSvc",
        "Dhcp",
        "Dnscache",
        "ProfSvc",
        "StateRepository",
        "SystemEventsBroker",
        "BrokerInfrastructure",
        "UserManager",
        "gpsvc",
        "CryptSvc",
        "LanmanWorkstation",
        "LanmanServer",
        "Netman",
        "NlaSvc",
        "netprofm",
        "nsi",
        "WinHttpAutoProxySvc",
        "iphlpsvc",
        "Wcmsvc",
        "WpnService",
        "TokenBroker",
        "ShellHWDetection",
        "Spooler",
        "CDPSvc",
        "DeviceAssociationService",
        "CoreMessagingRegistrar",
        "TextInputManagementService"
    };

    private static readonly string[] SecurityRunKeywords =
    {
        "windows security",
        "windows 安全",
        "安全中心",
        "windows defender",
        "microsoft defender",
        "defender",
        "securityhealth",
        "msmpeng",
        "mpcmdrun",
        "sechealthui",
        "smartscreen"
    };

    public ProtectionDecision Evaluate(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Source switch
        {
            StartupSource.Task => EvaluateTask(item),
            StartupSource.Service => EvaluateService(item),
            _ => EvaluateRunEntry(item)
        };
    }

    private static ProtectionDecision EvaluateTask(StartupItem item)
    {
        var path = GetTaskPath(item);
        if (!string.IsNullOrWhiteSpace(path) && ScheduledTaskClassifier.IsMicrosoftTaskPath(path))
        {
            var reason = ScheduledTaskClassifier.IsMicrosoftWindowsTaskPath(path)
                ? TaskReasonWindows
                : TaskReasonMicrosoft;
            return new ProtectionDecision(true, reason, item.RequiresAdmin);
        }

        // Third-party logon/boot tasks stay manageable; the item already carries
        // RequiresAdmin when its principal is SYSTEM or runs with highest privileges.
        return new ProtectionDecision(false, null, item.RequiresAdmin);
    }

    private static ProtectionDecision EvaluateService(StartupItem item)
    {
        var serviceName = GetServiceName(item);
        var displayName = item.Name?.Trim() ?? string.Empty;
        var publisher = item.Publisher?.Trim() ?? string.Empty;
        var path = !string.IsNullOrWhiteSpace(item.TargetPath)
            ? item.TargetPath!.Trim()
            : WindowsPathHeuristics.ExtractExecutableFromCommand(item.Command) ?? string.Empty;

        if (publisher.Length == 0 && path.Length > 0)
        {
            // Backup-only rows carry no publisher field; read it from the binary
            // path instead of treating the service as unverifiable.
            publisher = PublisherLookup.TryGetPublisher(path);
        }

        if (IsDriverService(serviceName, path, item))
        {
            return new ProtectionDecision(true, ServiceReasonDriver, true);
        }

        if (SecurityServices.Contains(serviceName))
        {
            return new ProtectionDecision(true, ServiceReasonSecurity, true);
        }

        if (CoreWindowsServices.Contains(serviceName))
        {
            return new ProtectionDecision(true, ServiceReasonCore, true);
        }

        if (IsRealtekService(serviceName, displayName, publisher))
        {
            var reason = IsRealtekBluetoothService(serviceName, displayName)
                ? ServiceReasonRealtekBluetooth
                : ServiceReasonRealtek;
            return new ProtectionDecision(true, reason, true);
        }

        if (IsNvidiaService(serviceName, displayName, publisher))
        {
            return new ProtectionDecision(true, ServiceReasonNvidia, true);
        }

        if (IsAmdService(serviceName, displayName, publisher))
        {
            return new ProtectionDecision(true, ServiceReasonAmd, true);
        }

        if (IsIntelService(serviceName, displayName, publisher))
        {
            return new ProtectionDecision(true, ServiceReasonIntel, true);
        }

        if (WindowsPathHeuristics.IsMicrosoftPublisherName(publisher))
        {
            return new ProtectionDecision(true, ServiceReasonMicrosoft, true);
        }

        if (WindowsPathHeuristics.IsUnderWindowsDirectory(path))
        {
            return new ProtectionDecision(true, ServiceReasonSystemDirectory, true);
        }

        if (publisher.Length == 0 || path.Length == 0)
        {
            // Identity could not be established. Lock by default, but allow an
            // explicit user override. Hard protections above stay locked.
            return new ProtectionDecision(true, ServiceReasonUnverified, true, CanOverride: true);
        }

        // A clearly identified third-party Win32 auto service (for example
        // TeamViewer) is the only service class this app may change.
        return new ProtectionDecision(false, null, true);
    }

    private static ProtectionDecision EvaluateRunEntry(StartupItem item)
    {
        var targetPath = !string.IsNullOrWhiteSpace(item.TargetPath)
            ? item.TargetPath!.Trim()
            : WindowsPathHeuristics.ExtractExecutableFromCommand(item.Command) ?? string.Empty;

        var haystack = string.Join(
            ' ',
            item.Name,
            item.Command,
            targetPath);

        if (SecurityRunKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return new ProtectionDecision(true, RunEntryReasonSecurity, item.RequiresAdmin);
        }

        if (WindowsPathHeuristics.IsUnderWindowsDirectory(targetPath) ||
            ContainsWindowsDirectoryToken(item.Command))
        {
            return new ProtectionDecision(true, RunEntryReasonWindows, item.RequiresAdmin);
        }

        return new ProtectionDecision(false, null, item.RequiresAdmin);
    }

    private static bool ContainsWindowsDirectoryToken(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return command.Contains("%windir%", StringComparison.OrdinalIgnoreCase)
            || command.Contains("%systemroot%", StringComparison.OrdinalIgnoreCase)
            || command.Contains(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriverService(string serviceName, string path, StartupItem item)
    {
        if (item.Metadata.TryGetValue("ServiceType", out var rawType) &&
            int.TryParse(rawType, out var serviceType) &&
            (serviceType & 0x0F) != 0 &&
            (serviceType & 0x30) == 0)
        {
            // SERVICE_KERNEL_DRIVER / FILE_SYSTEM_DRIVER / ADAPTER / RECOGNIZER
            return true;
        }

        return WindowsPathHeuristics.IsDriverFilePath(path)
            || WindowsPathHeuristics.IsDriverStorePath(path) && path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRealtekService(string serviceName, string displayName, string publisher)
    {
        return publisher.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith("Rtk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRealtekBluetoothService(string serviceName, string displayName)
    {
        return serviceName.Contains("Bt", StringComparison.OrdinalIgnoreCase)
            || serviceName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("蓝牙", StringComparison.Ordinal);
    }

    private static bool IsNvidiaService(string serviceName, string displayName, string publisher)
    {
        return publisher.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || serviceName.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase)
            || serviceName.Contains("NvContainer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAmdService(string serviceName, string displayName, string publisher)
    {
        return publisher.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
            || publisher.StartsWith("AMD", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("AMD", StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith("AMD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIntelService(string serviceName, string displayName, string publisher)
    {
        return publisher.Contains("Intel Corporation", StringComparison.OrdinalIgnoreCase)
            || publisher.StartsWith("Intel", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("Intel", StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith("igfx", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTaskPath(StartupItem item)
    {
        if (item.Metadata.TryGetValue("TaskPath", out var taskPath) &&
            !string.IsNullOrWhiteSpace(taskPath))
        {
            return ScheduledTaskClassifier.NormalizeTaskPath(taskPath);
        }

        var location = item.Location?.Trim() ?? string.Empty;
        const string prefix = "任务计划程序库";
        if (location.StartsWith(prefix, StringComparison.Ordinal))
        {
            location = location.Substring(prefix.Length).Trim();
        }

        return location.StartsWith('\\')
            ? ScheduledTaskClassifier.NormalizeTaskPath(location)
            : string.Empty;
    }

    private static string GetServiceName(StartupItem item)
    {
        if (item.Metadata.TryGetValue("ServiceName", out var serviceName) &&
            !string.IsNullOrWhiteSpace(serviceName))
        {
            return serviceName.Trim();
        }

        var location = item.Location?.Trim() ?? string.Empty;
        const string prefix = "服务：";
        if (location.StartsWith(prefix, StringComparison.Ordinal))
        {
            return location.Substring(prefix.Length).Trim();
        }

        return item.Name?.Trim() ?? string.Empty;
    }
}
