namespace D2RHost;

public enum VmWatchdogVerdict
{
    /// <summary>The agent is connected. Nothing is wrong, and any offline streak is cleared.</summary>
    Healthy,

    /// <summary>Not enough evidence yet, or the VM is not this watchdog's to touch.</summary>
    KeepWaiting,

    /// <summary>
    /// Restart the guest in place with a single <c>Restart-VM</c>. The VM never passes through Off,
    /// so nothing has to succeed at starting it again afterwards.
    /// </summary>
    Restart,

    /// <summary>Hand the guest to the full VM recovery: stop, confirm Off, start, wait.</summary>
    PowerCycle,

    /// <summary>Out of attempts. Recovering again would only loop on a guest that is not coming back.</summary>
    GiveUp
}

public sealed record VmWatchdogAssessment(VmWatchdogVerdict Verdict, string Reason);

/// <summary>
/// Decides when a VM that is powered on but has stopped running its agent should be power-cycled,
/// without waiting for follow-auto to notice.
///
/// The reason this is a separate policy from <see cref="VmHangRecoveryPolicy"/>: that one runs
/// *during* a recovery already in progress and answers "may I cut this guest's power". This one
/// runs on a timer and answers the question nothing was asking - "is anything wrong at all".
///
/// The signal it deliberately does not use is Hyper-V's power state. "Running" means virtual power
/// is applied and nothing more: a hung guest, a bluescreened guest, one frozen forever on the boot
/// logo, and a perfectly healthy one all report it identically. The only states that ever differ
/// are transitions the host itself commanded. So power state appears here exactly once, as a guard
/// that some *other* transition is not already in flight, and contributes nothing to the verdict.
///
/// What does contribute is the one signal that requires the guest to actually be doing work: its
/// agent's connection. The registry only counts an agent connected while its socket is open and it
/// has been heard from inside the negotiated heartbeat window, so a guest that freezes stops
/// counting within roughly a minute even though its socket may still look open from the outside.
///
/// The heartbeat integration service then decides how much corroboration that offline streak has:
///
/// <list type="bullet">
/// <item><description>
/// <b>OK</b> - the hypervisor is in contact with the guest OS. Paired with an offline agent this is
/// the <i>strongest</i> hang evidence available, not the weakest: it rules out "still booting" as
/// the explanation, because Windows is demonstrably up and our software still is not running on it.
/// It is also the case <see cref="VmHangRecoveryPolicy"/> explicitly refuses to act on, which is
/// correct there - a power cut is the wrong tool for a live guest. Because the guest is answering
/// its integration services, the cheapest thing that can work is a plain in-place restart, so that
/// is what this verdict asks for.
/// </description></item>
/// <item><description>
/// <b>No Contact / Lost Communication</b> - the guest never reached a working Windows, or reached
/// one and stopped answering. An in-place restart is routed through the same integration services
/// that are already not answering, so this goes straight to the full power cycle, which escalates
/// to a hard cut on its own evidence.
/// </description></item>
/// <item><description>
/// <b>Unknown</b> - the integration service is disabled or absent. No corroboration, so the offline
/// streak alone has to carry the decision and waits out a longer window before it does.
/// </description></item>
/// </list>
/// </summary>
public sealed class StuckVmWatchdogPolicy
{
    private const string RunningState = "Running";

    private readonly StuckVmWatchdogOptions _options;

    public StuckVmWatchdogPolicy(StuckVmWatchdogOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// How long an agent must be missing before the sweep spends a Hyper-V round-trip on it. The
    /// sweep runs over every configured VM on a short interval, so probing one that only just went
    /// quiet would cost a PowerShell process per VM per tick to learn nothing.
    /// </summary>
    public TimeSpan ProbeAfter => _options.AgentOfflineGrace;

    public VmWatchdogAssessment Assess(
        bool agentOnline,
        string? powerState,
        VmHeartbeatStatus heartbeat,
        TimeSpan? vmUptime,
        TimeSpan agentOfflineFor,
        int recoveriesUsed)
    {
        if (agentOnline)
        {
            return new VmWatchdogAssessment(
                VmWatchdogVerdict.Healthy,
                "the agent is connected and inside its heartbeat window");
        }

        if (!_options.Enabled)
        {
            return new VmWatchdogAssessment(
                VmWatchdogVerdict.KeepWaiting,
                "the stuck-VM watchdog is disabled in host config (stuckVmWatchdog.enabled)");
        }

        // The one use of power state, and it is a guard rather than a symptom. A VM that is Off was
        // almost certainly turned off on purpose; anything else is a transition someone already
        // started, and starting a second one into it would race them.
        if (!IsState(powerState, RunningState))
        {
            return new VmWatchdogAssessment(
                VmWatchdogVerdict.KeepWaiting,
                $"the VM is {DescribeState(powerState)}, not Running; it is either off deliberately or already "
                    + "mid-transition, and neither is the watchdog's to interrupt");
        }

        // A guest that genuinely only just powered on has not had time to fail yet. Hyper-V's
        // uptime is measured by the hypervisor, so it stays true even when the guest inside is
        // wedged and cannot report anything itself.
        if (vmUptime is { } uptime && uptime < _options.MinimumVmUptime)
        {
            return new VmWatchdogAssessment(
                VmWatchdogVerdict.KeepWaiting,
                $"the VM has only been powered on {Describe(uptime)}; a guest is given "
                    + $"{Describe(_options.MinimumVmUptime)} to boot and connect before it counts as stuck");
        }

        // An agent cannot have been missing for longer than its VM has been powered on: whatever
        // silence predates this boot belongs to the guest that was running before it. Without this
        // clamp a guest rebooted by anything other than this watchdog - a follow-auto node restart,
        // the host power lifecycle's resume, an operator's Start-VM - comes back carrying a streak
        // that is already past the grace window, and the only thing left between a booting guest
        // and a power cycle is MinimumVmUptime. The configured grace is the value documented as the
        // one that clears a cold boot plus the agent's connect, so it has to be the one that runs.
        if (vmUptime is { } poweredOnFor && agentOfflineFor > poweredOnFor)
        {
            agentOfflineFor = poweredOnFor;
        }

        var grace = heartbeat == VmHeartbeatStatus.Unknown
            ? _options.NoHeartbeatEvidenceGrace
            : _options.AgentOfflineGrace;
        if (agentOfflineFor < grace)
        {
            return new VmWatchdogAssessment(
                VmWatchdogVerdict.KeepWaiting,
                heartbeat == VmHeartbeatStatus.Unknown
                    ? $"the agent has been gone {Describe(agentOfflineFor)}, and with no heartbeat reported for this VM "
                        + $"there is nothing to corroborate it; waiting the full {Describe(grace)}"
                    : $"the agent has been gone {Describe(agentOfflineFor)} of the {Describe(grace)} "
                        + "an offline agent is given before its guest counts as stuck");
        }

        if (recoveriesUsed >= _options.MaxRecoveriesPerVm)
        {
            return new VmWatchdogAssessment(
                VmWatchdogVerdict.GiveUp,
                $"already spent all {_options.MaxRecoveriesPerVm} watchdog power cycle(s) on this guest without its "
                    + "agent coming back; the problem is below the guest and looping would only hide that");
        }

        // The guest still answers the hypervisor, so the cooperative path is available and is both
        // faster and safer than a power cycle: Restart-VM keeps the VM powered on throughout, which
        // means there is no Off state in which a failed Start-VM could strand it. Callers escalate
        // to the full cycle on their own if the restart does not bring the agent back.
        var verdict = heartbeat == VmHeartbeatStatus.Ok
            ? VmWatchdogVerdict.Restart
            : VmWatchdogVerdict.PowerCycle;
        return new VmWatchdogAssessment(verdict, DescribeEvidence(heartbeat, agentOfflineFor));
    }

    private string DescribeEvidence(VmHeartbeatStatus heartbeat, TimeSpan agentOfflineFor)
    {
        var gone = Describe(agentOfflineFor);
        return heartbeat switch
        {
            // Deliberately worded as the strong case it is. Windows is up, so "it is still booting"
            // is off the table - the guest simply is not running our software any more.
            VmHeartbeatStatus.Ok =>
                $"the VM is powered on and Hyper-V is in contact with the guest OS, yet its agent has been gone {gone}; "
                    + "Windows is up and is not running the agent, so this is not a slow boot",
            VmHeartbeatStatus.NoContact =>
                $"the VM is powered on but Hyper-V has no contact with the guest OS and its agent has been gone {gone}; "
                    + "the guest is not answering the hypervisor either",
            _ =>
                $"the VM is powered on and its agent has been gone {gone}; heartbeat is not reported for this VM, so "
                    + "the full no-evidence window was waited out first"
        };
    }

    private static bool IsState(string? actual, string expected)
    {
        return string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeState(string? state)
    {
        return string.IsNullOrWhiteSpace(state) ? "in an unreported state" : state.Trim();
    }

    private static string Describe(TimeSpan span)
    {
        return span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:N0}m"
            : $"{span.TotalSeconds:N0}s";
    }
}

public sealed record StuckVmWatchdogOptions(
    bool Enabled,
    TimeSpan AgentOfflineGrace,
    TimeSpan NoHeartbeatEvidenceGrace,
    TimeSpan MinimumVmUptime,
    int MaxRecoveriesPerVm);
