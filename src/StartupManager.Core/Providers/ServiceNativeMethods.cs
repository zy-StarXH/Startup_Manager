using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StartupManager.Core.Providers;

internal readonly record struct ServiceConfiguration(
    string ServiceName,
    string DisplayName,
    uint ServiceType,
    uint StartType,
    bool DelayedAutoStart,
    string BinaryPath,
    string Description);

internal static class ServiceNativeMethods
{
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    public const uint SERVICE_WIN32 = 0x00000030;
    public const uint SERVICE_STATE_ALL = 0x00000003;
    public const uint SC_ENUM_PROCESS_INFO = 0;

    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;

    public const uint SERVICE_CONFIG_DESCRIPTION = 1;
    public const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    public const uint SERVICE_KERNEL_DRIVER = 0x00000001;
    public const uint SERVICE_FILE_SYSTEM_DRIVER = 0x00000002;
    public const uint SERVICE_PACKAGED_PROCESS = 0x00000200;

    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceDelayedAutoStartInfo
    {
        public int DelayedAutostart;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumServicesStatusEx(
        IntPtr scManager,
        uint infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr services,
        uint bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        ref uint resumeHandle,
        string? groupName);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfig(
        IntPtr service,
        IntPtr serviceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfig2(
        IntPtr service,
        uint infoLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2(
        IntPtr service,
        uint infoLevel,
        IntPtr info);
}

/// <summary>Read/write access to the Windows service control manager.</summary>
internal static class ServiceNative
{
    private const int InitialBufferSize = 64 * 1024;

    public static IntPtr OpenManager()
    {
        var handle = ServiceNativeMethods.OpenSCManager(
            null,
            null,
            ServiceNativeMethods.SC_MANAGER_CONNECT | ServiceNativeMethods.SC_MANAGER_ENUMERATE_SERVICE);
        if (handle == IntPtr.Zero)
        {
            throw ServiceErrors.Create(
                "打开服务控制管理器",
                Marshal.GetLastWin32Error(),
                string.Empty);
        }

        return handle;
    }

    public static void CloseManager(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            ServiceNativeMethods.CloseServiceHandle(handle);
        }
    }

    /// <summary>Enumerates only Win32 services; drivers are never returned.</summary>
    public static List<(string ServiceName, string DisplayName)> EnumerateWin32Services(
        IntPtr scManager,
        CancellationToken cancellationToken)
    {
        var bufferSize = InitialBufferSize;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint bytesNeeded;
                uint servicesReturned;
                uint resumeHandle = 0;
                var ok = ServiceNativeMethods.EnumServicesStatusEx(
                    scManager,
                    ServiceNativeMethods.SC_ENUM_PROCESS_INFO,
                    ServiceNativeMethods.SERVICE_WIN32,
                    ServiceNativeMethods.SERVICE_STATE_ALL,
                    buffer,
                    (uint)bufferSize,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);
                var error = Marshal.GetLastWin32Error();

                if (!ok)
                {
                    if (error == ServiceNativeMethods.ERROR_MORE_DATA && bytesNeeded > bufferSize)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        bufferSize = checked((int)bytesNeeded);
                        buffer = Marshal.AllocHGlobal(bufferSize);
                        continue;
                    }

                    throw ServiceErrors.Create("枚举 Windows 服务", error, string.Empty);
                }

                var result = new List<(string, string)>((int)servicesReturned);
                var offset = 0;
                var entrySize = Marshal.SizeOf<ServiceNativeMethods.EnumServiceStatusProcess>();
                for (uint index = 0; index < servicesReturned; index++)
                {
                    var entry = Marshal.PtrToStructure<ServiceNativeMethods.EnumServiceStatusProcess>(
                        buffer + offset);
                    offset += entrySize;

                    var name = Marshal.PtrToStringUni(entry.ServiceName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var displayName = Marshal.PtrToStringUni(entry.DisplayName) ?? name;
                    result.Add((name, displayName));
                }

                return result;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Reads start type, delayed flag, binary path, display name, and description.</summary>
    public static ServiceConfiguration? TryQuery(
        IntPtr scManager,
        string serviceName,
        string fallbackDisplayName = "")
    {
        var serviceHandle = ServiceNativeMethods.OpenService(
            scManager,
            serviceName,
            ServiceNativeMethods.SERVICE_QUERY_CONFIG);
        if (serviceHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ServiceNativeMethods.ERROR_SERVICE_DOES_NOT_EXIST)
            {
                return null;
            }

            throw ServiceErrors.Create("读取服务配置", error, serviceName);
        }

        try
        {
            var config = ReadConfiguration(serviceHandle, serviceName);
            var delayed = ReadDelayedAutoStart(serviceHandle);
            var description = ReadDescription(serviceHandle);
            var display = config.DisplayName.Length > 0 ? config.DisplayName : fallbackDisplayName;

            return new ServiceConfiguration(
                serviceName,
                display,
                config.ServiceType,
                config.StartType,
                delayed,
                config.BinaryPathName,
                description);
        }
        finally
        {
            ServiceNativeMethods.CloseServiceHandle(serviceHandle);
        }
    }

    /// <summary>
    /// Changes only the start type (and optionally the delayed-auto flag).
    /// The service is never started, stopped, or restarted.
    /// </summary>
    public static void ChangeStartType(
        IntPtr scManager,
        string serviceName,
        uint startType,
        bool? delayedAutoStart)
    {
        var serviceHandle = ServiceNativeMethods.OpenService(
            scManager,
            serviceName,
            ServiceNativeMethods.SERVICE_CHANGE_CONFIG);
        if (serviceHandle == IntPtr.Zero)
        {
            throw ServiceErrors.Create("修改服务启动类型", Marshal.GetLastWin32Error(), serviceName);
        }

        try
        {
            var changed = ServiceNativeMethods.ChangeServiceConfig(
                serviceHandle,
                ServiceNativeMethods.SERVICE_NO_CHANGE,
                startType,
                ServiceNativeMethods.SERVICE_NO_CHANGE,
                null,
                null,
                IntPtr.Zero,
                null,
                null,
                null,
                null);
            if (!changed)
            {
                throw ServiceErrors.Create("修改服务启动类型", Marshal.GetLastWin32Error(), serviceName);
            }

            if (delayedAutoStart.HasValue)
            {
                WriteDelayedAutoStart(serviceHandle, serviceName, delayedAutoStart.Value);
            }
        }
        finally
        {
            ServiceNativeMethods.CloseServiceHandle(serviceHandle);
        }
    }

    private static (uint ServiceType, uint StartType, string BinaryPathName, string DisplayName) ReadConfiguration(
        IntPtr serviceHandle,
        string serviceName)
    {
        uint bytesNeeded;
        ServiceNativeMethods.QueryServiceConfig(serviceHandle, IntPtr.Zero, 0, out bytesNeeded);
        if (bytesNeeded == 0)
        {
            throw ServiceErrors.Create(
                "读取服务配置",
                Marshal.GetLastWin32Error(),
                serviceName);
        }

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!ServiceNativeMethods.QueryServiceConfig(
                    serviceHandle,
                    buffer,
                    bytesNeeded,
                    out _))
            {
                throw ServiceErrors.Create(
                    "读取服务配置",
                    Marshal.GetLastWin32Error(),
                    serviceName);
            }

            var config = Marshal.PtrToStructure<ServiceNativeMethods.QueryServiceConfigData>(buffer);
            // Materialise the pointers before the native buffer is freed.
            return (
                config.ServiceType,
                config.StartType,
                GetString(config.BinaryPathName),
                GetString(config.DisplayName));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadDelayedAutoStart(IntPtr serviceHandle)
    {
        uint bytesNeeded;
        ServiceNativeMethods.QueryServiceConfig2(
            serviceHandle,
            ServiceNativeMethods.SERVICE_CONFIG_DELAYED_AUTO_START_INFO,
            IntPtr.Zero,
            0,
            out bytesNeeded);
        if (bytesNeeded < sizeof(int))
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!ServiceNativeMethods.QueryServiceConfig2(
                    serviceHandle,
                    ServiceNativeMethods.SERVICE_CONFIG_DELAYED_AUTO_START_INFO,
                    buffer,
                    bytesNeeded,
                    out _))
            {
                return false;
            }

            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadDescription(IntPtr serviceHandle)
    {
        uint bytesNeeded;
        ServiceNativeMethods.QueryServiceConfig2(
            serviceHandle,
            ServiceNativeMethods.SERVICE_CONFIG_DESCRIPTION,
            IntPtr.Zero,
            0,
            out bytesNeeded);
        if (bytesNeeded == 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!ServiceNativeMethods.QueryServiceConfig2(
                    serviceHandle,
                    ServiceNativeMethods.SERVICE_CONFIG_DESCRIPTION,
                    buffer,
                    bytesNeeded,
                    out _))
            {
                return string.Empty;
            }

            var pointer = Marshal.ReadIntPtr(buffer);
            return pointer == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(pointer)?.Trim() ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WriteDelayedAutoStart(
        IntPtr serviceHandle,
        string serviceName,
        bool delayedAutoStart)
    {
        var info = new ServiceNativeMethods.ServiceDelayedAutoStartInfo
        {
            DelayedAutostart = delayedAutoStart ? 1 : 0
        };

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceNativeMethods.ServiceDelayedAutoStartInfo>());
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!ServiceNativeMethods.ChangeServiceConfig2(
                    serviceHandle,
                    ServiceNativeMethods.SERVICE_CONFIG_DELAYED_AUTO_START_INFO,
                    buffer))
            {
                throw ServiceErrors.Create(
                    "修改服务延迟启动状态",
                    Marshal.GetLastWin32Error(),
                    serviceName);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer)?.Trim() ?? string.Empty;
}

internal static class ServiceErrors
{
    private const int AccessDenied = 5;
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceMarkedForDeletion = 1072;

    public static Exception Create(string operation, int error, string serviceName)
    {
        var name = string.IsNullOrWhiteSpace(serviceName) ? string.Empty : $"“{serviceName}”";
        var win32 = new Win32Exception(error);

        return error switch
        {
            AccessDenied => new UnauthorizedAccessException(
                $"{operation}{name}需要管理员权限。",
                win32),
            ServiceDoesNotExist => new InvalidOperationException(
                $"{operation}{name}失败：服务不存在或已被卸载。",
                win32),
            ServiceMarkedForDeletion => new InvalidOperationException(
                $"{operation}{name}失败：服务正在被删除，请重启后再试。",
                win32),
            _ => new Win32Exception(error, $"{operation}{name}失败（错误 {error}）。")
        };
    }
}
