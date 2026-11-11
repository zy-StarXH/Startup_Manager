using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using StartupManager.Core.Models;
using StartupManager.Core.Providers.RegistryFolder;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class RegistryApprovalTests
{
    [Fact]
    public void Enabled_bytes_have_flag_02_reserved_zeros_and_filetime()
    {
        var timestamp = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        var bytes = StartupApproved.CreateEnabledBytes(timestamp);

        Assert.Equal(12, bytes.Length);
        Assert.Equal(0x02, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[2]);
        Assert.Equal(0, bytes[3]);
        Assert.True(StartupApproved.IsEnabled(bytes));
        Assert.True(StartupApproved.TryGetTimestamp(bytes, out var parsed));
        Assert.Equal(timestamp, parsed.UtcDateTime);
    }

    [Fact]
    public void Disabled_bytes_have_flag_03_and_are_not_enabled()
    {
        var timestamp = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        var bytes = StartupApproved.CreateDisabledBytes(timestamp);

        Assert.Equal(12, bytes.Length);
        Assert.Equal(0x03, bytes[0]);
        Assert.False(StartupApproved.IsEnabled(bytes));
        Assert.True(StartupApproved.TryGetTimestamp(bytes, out var parsed));
        Assert.Equal(timestamp, parsed.UtcDateTime);
    }

    [Fact]
    public void Missing_empty_and_unknown_approval_data_is_treated_as_enabled()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled(Array.Empty<byte>()));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 0x00 }));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 0x02 }));
        Assert.False(StartupApproved.IsEnabled(new byte[] { 0x03 }));
        Assert.False(StartupApproved.IsEnabled(new byte[] { 0x03, 0, 0, 0 }));
    }

    [Fact]
    public void Zero_filetime_yields_no_timestamp()
    {
        var zeroTimestamp = StartupApproved.CreateEnabledBytes(
            new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(StartupApproved.IsEnabled(zeroTimestamp));
        Assert.False(StartupApproved.TryGetTimestamp(zeroTimestamp, out _));
        Assert.False(StartupApproved.TryGetTimestamp(new byte[] { 0x02, 0, 0, 0 }, out _));
        Assert.False(StartupApproved.TryGetTimestamp(null, out _));
    }

    [Fact]
    public void Registry_views_map_to_the_expected_approval_families_and_keys()
    {
        Assert.Equal(
            StartupApprovalFamily.Run,
            StartupApproved.GetApprovalFamily(RegistryView.Registry64));
        Assert.Equal(
            StartupApprovalFamily.Run,
            StartupApproved.GetApprovalFamily(RegistryView.Default));
        Assert.Equal(
            StartupApprovalFamily.Run32,
            StartupApproved.GetApprovalFamily(RegistryView.Registry32));

        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            StartupApproved.GetKeyPath(StartupApprovalFamily.Run));
        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            StartupApproved.GetKeyPath(StartupApprovalFamily.Run32));
        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            StartupApproved.GetKeyPath(StartupApprovalFamily.StartupFolder));

        Assert.StartsWith(
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\",
            StartupApproved.GetDisplayKeyPath(StartupApprovalFamily.Run));
    }

    [Fact]
    public void Registry_ids_are_stable_lowercase_sha256_with_prefix()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "WeChat";

        var id = StartupItemIds.CreateRegistryId("HKCU", "Registry64", keyPath, valueName);
        var sameId = StartupItemIds.CreateRegistryId("HKCU", "Registry64", keyPath, valueName);
        var expectedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    "HKCU|Registry64|Software\\Microsoft\\Windows\\CurrentVersion\\Run|WeChat")))
            .ToLowerInvariant();

        Assert.Equal(id, sameId);
        Assert.Equal("reg-" + expectedHash, id);
        Assert.Matches("^reg-[0-9a-f]{64}$", id);
        Assert.NotEqual(
            id,
            StartupItemIds.CreateRegistryId("HKLM", "Registry64", keyPath, valueName));
        Assert.NotEqual(
            id,
            StartupItemIds.CreateRegistryId("HKCU", "Registry32", keyPath, valueName));
    }

    [Fact]
    public void Folder_ids_are_stable_and_include_scope()
    {
        const string path = @"C:\Users\Test\Start Menu\Programs\Startup\Example.lnk";

        var currentUser = StartupItemIds.CreateFolderId(StartupScope.CurrentUser, path);
        var allUsers = StartupItemIds.CreateFolderId(StartupScope.AllUsers, path);

        Assert.Equal(
            currentUser,
            StartupItemIds.CreateFolderId(StartupScope.CurrentUser, path));
        Assert.StartsWith("dir-", currentUser);
        Assert.Matches("^dir-[0-9a-f]{64}$", currentUser);
        Assert.NotEqual(currentUser, allUsers);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Example\\example.exe\" --flag", @"C:\Program Files\Example\example.exe")]
    [InlineData("C:\\Program Files\\Example\\example.exe --flag", @"C:\Program Files\Example\example.exe")]
    [InlineData("\"C:\\Tools\\my tool.exe\"", @"C:\Tools\my tool.exe")]
    [InlineData("C:\\Tools\\runner.bat /x", @"C:\Tools\runner.bat")]
    [InlineData("C:\\Tools\\runner.cmd", @"C:\Tools\runner.cmd")]
    public void Command_line_parser_extracts_rooted_executables(string command, string expected)
    {
        Assert.Equal(expected, CommandLineParser.TryGetExecutablePath(command));
    }

    [Fact]
    public void Command_line_parser_expands_environment_variables()
    {
        var parsed = CommandLineParser.TryGetExecutablePath(
            "%SystemRoot%\\System32\\cmd.exe /c exit");

        Assert.NotNull(parsed);
        Assert.True(
            string.Equals(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                parsed,
                StringComparison.OrdinalIgnoreCase),
            $"Expected the system cmd.exe path but got '{parsed}'.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cmd.exe /c exit")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL")]
    [InlineData("%UNKNOWN_VARIABLE%\\app.exe")]
    public void Command_line_parser_rejects_non_rooted_commands(string? command)
    {
        Assert.Null(CommandLineParser.TryGetExecutablePath(command));
    }

    [Fact]
    public void Approval_value_roundtrips_in_current_user_hive()
    {
        var valueName = "StartupManagerTest_" + Guid.NewGuid().ToString("N");
        var original = new byte[] { 0x03, 0x01, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8 };

        try
        {
            Assert.False(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out _,
                out var initiallyExists));
            Assert.False(initiallyExists);

            StartupApproved.WriteApproval(StartupApprovalFamily.Run, valueName, original);

            Assert.True(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out var read,
                out var exists));
            Assert.True(exists);
            Assert.Equal(original, read);
            Assert.False(StartupApproved.IsEnabled(read));

            StartupApproved.DeleteApproval(StartupApprovalFamily.Run, valueName);

            Assert.False(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out _,
                out var afterDelete));
            Assert.False(afterDelete);
        }
        finally
        {
            StartupApproved.DeleteApproval(StartupApprovalFamily.Run, valueName);
        }
    }
}
