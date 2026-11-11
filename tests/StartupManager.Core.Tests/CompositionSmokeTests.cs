using StartupManager.Core;
using StartupManager.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace StartupManager.Core.Tests;

public sealed class CompositionSmokeTests
{
    private readonly ITestOutputHelper _output;

    public CompositionSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Default_composition_scans_real_sources_and_applies_protection()
    {
        var manager = CoreComposition.CreateManager();
        var snapshot = await manager.ScanAsync(CancellationToken.None);

        _output.WriteLine($"items={snapshot.Items.Count}; warnings={snapshot.Warnings.Count}; elevated={snapshot.IsElevated}");
        foreach (var group in snapshot.Items.GroupBy(item => item.Source))
        {
            _output.WriteLine($"{group.Key}: {group.Count()} (enabled {group.Count(item => item.Enabled)}, protected {group.Count(item => item.Protected)})");
        }

        foreach (var item in snapshot.Items.Take(20))
        {
            _output.WriteLine($"{item.Source,-8} | enabled={item.Enabled,-5} | protected={item.Protected,-5} | {item.Name} | {item.Scope} | admin={item.RequiresAdmin}");
        }

        foreach (var service in snapshot.Items.Where(item => item.Source == StartupSource.Service && !item.Protected))
        {
            _output.WriteLine($"manageable service: {service.Name} | {service.Location} | {service.Publisher} | admin={service.RequiresAdmin}");
        }

        Assert.NotEmpty(snapshot.Items);
        Assert.Contains(snapshot.Items, item => item.Source == StartupSource.Registry);
        Assert.Contains(snapshot.Items, item => item.Protected);
        Assert.All(snapshot.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
    }
}
