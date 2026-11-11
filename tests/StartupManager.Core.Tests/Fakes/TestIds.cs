using System.Security.Cryptography;
using System.Text;
using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

public static class TestIds
{
    public static string For(StartupSource source, string seed)
    {
        var prefix = source switch
        {
            StartupSource.Registry => "reg",
            StartupSource.Folder => "dir",
            StartupSource.Task => "tsk",
            StartupSource.Service => "svc",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant();

        return prefix + "-" + hash;
    }

    public static string NewId(StartupSource source) => For(source, Guid.NewGuid().ToString("N"));
}
