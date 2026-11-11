using StartupManager.Core.Models;
using StartupManager.Core.Providers;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ServiceStartupProviderTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe\"", @"C:\Program Files\TeamViewer\TeamViewer_Service.exe")]
    [InlineData("\"C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe\" --service", @"C:\Program Files\TeamViewer\TeamViewer_Service.exe")]
    [InlineData("C:\\Windows\\system32\\svchost.exe -k netsvcs -p", @"C:\Windows\system32\svchost.exe")]
    [InlineData("C:\\Program Files (x86)\\Vendor\\svc.exe -arg", @"C:\Program Files (x86)\Vendor\svc.exe")]
    [InlineData("C:\\Windows\\System32\\foo.exe", @"C:\Windows\System32\foo.exe")]
    [InlineData("svchost.exe", "svchost.exe")]
    [InlineData("  C:\\Windows\\System32\\foo.exe   ", @"C:\Windows\System32\foo.exe")]
    public void Executable_path_is_extracted_from_service_binary_path(string input, string expected)
    {
        Assert.Equal(expected, ServiceCommandLine.ExtractExecutablePath(input), ignoreCase: true);
    }

    [Fact]
    public void Executable_path_handles_system_root_and_volume_prefixes()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var systemRoot = ServiceCommandLine.ExtractExecutablePath(@"\SystemRoot\System32\foo.exe -x");
        Assert.Equal(Path.Combine(windows, "System32", "foo.exe"), systemRoot, ignoreCase: true);

        var volumeInput = @"\??\" + Path.Combine(windows, "System32", "foo.exe");
        var volume = ServiceCommandLine.ExtractExecutablePath(volumeInput);
        Assert.Equal(Path.Combine(windows, "System32", "foo.exe"), volume, ignoreCase: true);

        var programs = ServiceCommandLine.ExtractExecutablePath("%ProgramFiles%\\Vendor\\app.exe");
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Vendor",
                "app.exe"),
            programs,
            ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Executable_path_is_null_for_empty_binary_path(string? input)
    {
        Assert.Null(ServiceCommandLine.ExtractExecutablePath(input));
    }

    [Fact]
    public void Display_command_quotes_unquoted_paths_with_spaces_only()
    {
        Assert.Equal(
            "\"C:\\Program Files\\App\\a.exe\" -x",
            ServiceCommandLine.BuildDisplayCommand(@"C:\Program Files\App\a.exe -x"));

        const string quoted = "\"C:\\Program Files\\App\\a.exe\" -x";
        Assert.Equal(quoted, ServiceCommandLine.BuildDisplayCommand(quoted));

        Assert.Equal(
            @"C:\Windows\System32\svchost.exe -k netsvcs",
            ServiceCommandLine.BuildDisplayCommand(@"C:\Windows\System32\svchost.exe -k netsvcs"));
    }

    [Fact]
    public void Service_id_is_stable_lowercase_hash_of_the_name()
    {
        var upper = ServiceCommandLine.CreateServiceId("TeamViewer");
        var lower = ServiceCommandLine.CreateServiceId("teamviewer");

        Assert.Equal(upper, lower);
        Assert.Matches("^svc-[0-9a-f]{64}$", upper);
        Assert.NotEqual(upper, ServiceCommandLine.CreateServiceId("TeamViewer_Service"));
        Assert.Throws<ArgumentException>(() => ServiceCommandLine.CreateServiceId("  "));
    }

    [Theory]
    [InlineData(2u, false, "自动")]
    [InlineData(2u, true, "自动（延迟）")]
    [InlineData(3u, false, "手动")]
    [InlineData(4u, false, "已禁用")]
    public void Start_type_labels_are_user_facing(uint startType, bool delayed, string expected)
    {
        Assert.Equal(expected, ServiceStartTypes.ToChineseLabel(startType, delayed));
        Assert.Equal("启动类型：" + expected, ServiceStartTypes.BuildSummary(startType, delayed));
        Assert.Contains(
            "服务名称：TeamViewer",
            ServiceStartTypes.BuildSubLocation(startType, delayed, "TeamViewer"));
    }

    [Fact]
    public async Task Scan_reports_only_automatic_win32_services_without_changing_them()
    {
        var provider = new ServiceStartupProvider();

        var items = await provider.ScanAsync(CancellationToken.None);

        Assert.NotEmpty(items);
        Assert.Equal(StartupSource.Service, provider.Source);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            Assert.Equal(StartupSource.Service, item.Source);
            Assert.Equal(StartupScope.System, item.Scope);
            Assert.True(item.Enabled);
            Assert.False(item.Protected);
            Assert.True(item.RequiresAdmin);
            Assert.Equal("serviceStartType", item.Mechanism);
            Assert.Matches("^svc-[0-9a-f]{64}$", item.Id);
            Assert.True(ids.Add(item.Id));
            Assert.StartsWith("服务：", item.Location);
            Assert.Contains("启动类型：", item.SubLocation);
            Assert.Contains("服务名称：", item.SubLocation);
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Command));

            Assert.True(item.Metadata.TryGetValue("ServiceName", out var serviceName));
            Assert.False(string.IsNullOrWhiteSpace(serviceName));
            Assert.Equal("2", item.Metadata["StartType"]);
            Assert.Equal("2", item.Metadata["OriginalStartType"]);
            Assert.True(bool.TryParse(item.Metadata["DelayedAutoStart"], out _));
        }
    }

    [Fact]
    public async Task Non_service_items_are_rejected_without_touching_the_machine()
    {
        var provider = new ServiceStartupProvider();
        var item = new StartupItem
        {
            Id = "tsk-test",
            Name = "Not a service",
            Source = StartupSource.Task,
            Scope = StartupScope.System,
            Enabled = true
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CaptureStateAsync(item, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.ApplyEnabledAsync(item, false, false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.VerifyEnabledAsync(item, false, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_reads_current_start_type_without_changing_it()
    {
        var provider = new ServiceStartupProvider();
        var items = await provider.ScanAsync(CancellationToken.None);
        var item = items.First();

        Assert.True(await provider.VerifyEnabledAsync(item, true, CancellationToken.None));
        Assert.False(await provider.VerifyEnabledAsync(item, false, CancellationToken.None));
    }

    [Fact]
    public async Task Capture_state_reads_start_type_without_changing_it()
    {
        var provider = new ServiceStartupProvider();
        var items = await provider.ScanAsync(CancellationToken.None);
        var item = items.First();

        var state = await provider.CaptureStateAsync(item, CancellationToken.None);

        Assert.Equal("serviceStartType", state.Kind);
        Assert.StartsWith("启动类型：", state.Summary);
        Assert.Contains(item.Metadata["ServiceName"], state.Json);
    }
}
