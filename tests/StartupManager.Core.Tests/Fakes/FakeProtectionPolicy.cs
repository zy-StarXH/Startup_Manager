using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

public sealed class FakeProtectionPolicy : IProtectionPolicy
{
    public Func<StartupItem, ProtectionDecision> Rule { get; set; } =
        _ => new ProtectionDecision(false, null, false);

    public ProtectionDecision Evaluate(StartupItem item) => Rule(item);
}
