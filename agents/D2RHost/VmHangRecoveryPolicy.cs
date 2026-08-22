namespace D2RHost;

/// <summary>
/// How Hyper-V's Heartbeat integration service is reporting the guest OS. This is the one signal
/// that distinguishes "Windows is up but something in it is broken" from "the guest never reached
/// a working Windows at all", which is what separates a normal recovery from a wedged boot.
/// </summary>
public enum VmHeartbeatStatus
{
    /// <summary>
    /// Not reported. The Heartbeat integration service is disabled for this VM, the guest has no
    /// integration components, or the reply came from a worker node old enough not to send it.
    /// Absence of evidence only - never treated as evidence of a hang on its own.
    /// </summary>
    Unknown,

    /// <summary>Hyper-V is in contact with the guest OS: it is alive and scheduling.</summary>
    Ok,

    /// <summary>
    /// "No Contact" (never came up) or "Lost Communication" (came up, then stopped answering).
    /// Both mean the guest OS is not responding to the hypervisor.
    /// </summary>
    NoContact
}

public enum VmHangVerdict
{
    /// <summary>Nothing is proven yet; let the existing wait run.</summary>
    KeepWaiting,

    /// <summary>Cut power without asking the guest, settle, and start it again.</summary>
    HardPowerCut,

    /// <summary>Out of attempts, or the evidence says a power cut cannot be the fix.</summary>
    GiveUp
}

public sealed record VmHangAssessment(VmHangVerdict Verdict, string Reason);

/// <summary>
/// Decides when a VM that will not come back is wedged badly enough to need its power cut rather
/// than more waiting.
///
/// The symptom this exists for: a VM told to restart because its client was glitched can freeze
/// partway through, sitting on the Windows boot logo (sometimes with the spinner under it) and
/// never reaching a desktop. Its agent never reconnects, so nothing inside the guest can report
/// the problem, and <c>Stop-VM -Force</c> cannot fix it either - that is a *guest-cooperative*
/// shutdown routed through the integration services, and a guest frozen this early is not running
/// them. Only <c>Stop-VM -TurnOff</c>, the hypervisor equivalent of holding the power button,
/// gets the machine back.
///
/// The user's constraint on this was explicit: never assume a VM is hung. Three things enforce it.
///
/// 1. Placement. Every hard cut this policy authorizes happens at a point where the caller has
///    ALREADY exhausted its patience and would otherwise escalate to restarting the whole physical
///    node - which takes every healthy sibling VM on that node down with it. A power cut scoped to
///    the one wedged guest is strictly less disruptive than the action it replaces, so this is not
///    a new risk being introduced; it is a narrower response to an existing dead end.
/// 2. Evidence. A live guest is never cut. If Hyper-V's heartbeat says it is in contact with the
///    guest OS, Windows is up and scheduling, so whatever is wrong is not a frozen boot and a
///    power cut is not the right tool - this policy refuses. Only a guest the hypervisor cannot
///    reach is a candidate, and even then only after a grace window long enough to cover an
///    ordinary cold boot.
/// 3. Bounds. Attempts are capped. A VM that will not come back after its allowance is a hardware
///    or host problem, and looping power cuts on it would only delay the node-level escalation
///    that can actually help.
/// </summary>
public sealed class VmHangRecoveryPolicy
{
    public const string RunningState = "Running";
    public const string OffState = "Off";

    private readonly VmHangRecoveryOptions _options;

    public VmHangRecoveryPolicy(VmHangRecoveryOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// A graceful stop was issued and the VM still is not Off. <c>Stop-VM -Force</c> asks the guest
    /// through its integration services; a guest that is wedged never answers, and Hyper-V leaves
    /// the VM sitting in Stopping. Unlike the boot case there is no ambiguity to guard against
    /// here - the hypervisor itself is reporting that a forced shutdown went unanswered for the
    /// full timeout, which is exactly what a hard cut is for.
    /// </summary>
    public VmHangAssessment AssessStuckShutdown(
        string? powerState,
        TimeSpan sinceStopRequested,
        int hardPowerCutsUsed)
    {
        if (IsState(powerState, OffState))
        {
            return new VmHangAssessment(
                VmHangVerdict.KeepWaiting,
                "the VM is already Off; no power cut is needed");
        }

        if (!_options.Enabled)
        {
            return new VmHangAssessment(
                VmHangVerdict.GiveUp,
                "hard power-cut recovery is disabled in host config (vmHangRecovery.enabled)");
        }

        if (hardPowerCutsUsed >= _options.MaxHardPowerCuts)
        {
            return new VmHangAssessment(
                VmHangVerdict.GiveUp,
                $"already spent all {_options.MaxHardPowerCuts} hard power cut(s) on this recovery");
        }

        var observed = DescribeState(powerState);
        return new VmHangAssessment(
            VmHangVerdict.HardPowerCut,
            $"a forced shutdown went unanswered for {Describe(sinceStopRequested)} and the VM is still {observed}; "
                + "the guest is not running the integration services a graceful stop needs");
    }

    /// <summary>
    /// The VM is powered on but its agent has not reconnected. This is the frozen-boot case, and
    /// the one that needs the heartbeat guard: a slow cold boot and a wedged one look identical
    /// from outside until either the agent appears or the hypervisor gives up on the guest.
    /// </summary>
    public VmHangAssessment AssessBootHang(
        string? powerState,
        VmHeartbeatStatus heartbeat,
        TimeSpan sinceStarted,
        int hardPowerCutsUsed)
    {
        if (!_options.Enabled)
        {
            return new VmHangAssessment(
                VmHangVerdict.KeepWaiting,
                "hard power-cut recovery is disabled in host config (vmHangRecovery.enabled)");
        }

        // Anything other than Running means some other transition is already in flight - a restore
        // pass, an operator, a stop that has not settled. Cutting power into that would race it.
        if (!IsState(powerState, RunningState))
        {
            return new VmHangAssessment(
                VmHangVerdict.KeepWaiting,
                $"the VM is {DescribeState(powerState)}, not Running; a power cut only applies to a guest that is powered on");
        }

        // The guard that keeps this from ever being an assumption. Hyper-V is in contact with the
        // guest OS, so Windows is up and scheduling - this is not a machine stuck on the boot logo,
        // and cutting its power would be destroying a live system to fix something a power cut
        // cannot fix. Whatever is wrong (agent service down, network, credentials) needs a
        // different answer, so this returns KeepWaiting and lets the caller's own timeout decide.
        if (heartbeat == VmHeartbeatStatus.Ok)
        {
            return new VmHangAssessment(
                VmHangVerdict.KeepWaiting,
                "Hyper-V's heartbeat reports contact with the guest OS, so Windows is running and this is not a wedged boot; "
                    + "a power cut is not the right recovery and was not attempted");
        }

        if (hardPowerCutsUsed >= _options.MaxHardPowerCuts)
        {
            return new VmHangAssessment(
                VmHangVerdict.GiveUp,
                $"already spent all {_options.MaxHardPowerCuts} hard power cut(s) on this recovery");
        }

        if (heartbeat == VmHeartbeatStatus.NoContact)
        {
            if (sinceStarted < _options.HangSuspectedAfter)
            {
                return new VmHangAssessment(
                    VmHangVerdict.KeepWaiting,
                    $"no heartbeat yet, but only {Describe(sinceStarted)} since power-on; "
                        + $"a cold boot is given {Describe(_options.HangSuspectedAfter)} before it counts as wedged");
            }

            return new VmHangAssessment(
                VmHangVerdict.HardPowerCut,
                $"powered on {Describe(sinceStarted)} ago with no heartbeat contact and no agent; "
                    + "the guest never reached a working Windows");
        }

        // Heartbeat unknown: the integration service is disabled, absent, or the worker node is too
        // old to report it. That is not evidence of a hang, so it buys no speed - the cut waits for
        // the caller's full reconnect budget to run out. At that point the alternative on the table
        // is a whole-node restart, so a power cut scoped to this one guest is the smaller action
        // even without corroboration.
        if (sinceStarted < _options.NoEvidenceGrace)
        {
            return new VmHangAssessment(
                VmHangVerdict.KeepWaiting,
                $"heartbeat is not reported for this VM, so there is no hang evidence; waiting the full "
                    + $"{Describe(_options.NoEvidenceGrace)} before treating it as wedged ({Describe(sinceStarted)} so far)");
        }

        return new VmHangAssessment(
            VmHangVerdict.HardPowerCut,
            $"powered on {Describe(sinceStarted)} ago with no agent, and heartbeat is not reported for this VM so a "
                + "wedged boot cannot be ruled out; cutting power rather than escalating to a node restart");
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

    /// <summary>
    /// Maps Hyper-V's Heartbeat <c>PrimaryStatusDescription</c> onto the three cases this policy
    /// cares about. "Lost Communication" is deliberately folded in with "No Contact": a guest that
    /// answered and then stopped is precisely the mid-restart freeze this recovery exists for.
    /// </summary>
    public static VmHeartbeatStatus ParseHeartbeat(string? primaryStatusDescription)
    {
        var value = primaryStatusDescription?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return VmHeartbeatStatus.Unknown;
        }

        return string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase)
            ? VmHeartbeatStatus.Ok
            : VmHeartbeatStatus.NoContact;
    }
}

public sealed record VmHangRecoveryOptions(
    bool Enabled,
    TimeSpan HangSuspectedAfter,
    TimeSpan NoEvidenceGrace,
    TimeSpan SettleBeforeRestart,
    int MaxHardPowerCuts);
