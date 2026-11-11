using System.Diagnostics;
using StartupManager.Core.Elevation;

namespace StartupManager.Core.Tests.Fakes;

public sealed class FakeElevatedLauncher : IElevatedProcessLauncher
{
    public ProcessStartInfo? SeenStartInfo { get; private set; }

    public int RunCalls { get; private set; }

    public Func<ProcessStartInfo, Task<int>> Handler { get; set; } =
        _ => Task.FromResult(0);

    public Task<int> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        RunCalls++;
        SeenStartInfo = startInfo;
        return Handler(startInfo);
    }

    /// <summary>Arguments are --request "&lt;path&gt;".</summary>
    public static string RequestPathFrom(ProcessStartInfo startInfo)
    {
        var parts = startInfo.Arguments.Split('"');
        if (parts.Length < 3)
        {
            throw new InvalidOperationException("Unexpected helper arguments: " + startInfo.Arguments);
        }

        return parts[1];
    }

    public static string ResponsePathFor(string requestPath) =>
        ElevationProtocol.GetResponsePath(requestPath);
}
