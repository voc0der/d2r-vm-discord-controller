# Host VM Power Lifecycle

D2RHost prevents a running GPU-P guest from crossing a physical-host sleep, hibernation, shutdown, or restart. Each master or worker node handles its own Hyper-V VMs and keeps its own durable restart list, so VM recovery does not depend on the master being online first.

## Configuration checklist

- Put every local controlled VM under that node's `accounts` object and set its exact Hyper-V `vmName`. A blank or missing `vmName` is not a lifecycle target.
- Keep worker accounts in the worker config only. Do not copy them into the master config.
- Make sure every mapped name passes that node's `allowedVmNamePrefixes`; the same guard applies to lifecycle state, stop, and start commands.
- Keep `databasePath` on persistent local storage. The `host_vm_resume` table in that database is the node's restart journal.
- Keep the installed `D2R Host Controller` scheduled task's startup trigger enabled. It is what restores recorded VMs after shutdown or restart.
- Update the master and all workers to the lifecycle-capable release. The master refuses every remote host power action until that worker advertises `vmSafeHostPowerTransitions:true` on its current heartbeat; after an update/reconnect, wait for that fresh status before retrying.

The two sample host configs mark the fields involved: [master](../../samples/d2r-host.config.example.json) and [worker](../../samples/d2r-host.worker.config.example.json).

## State behavior

| State at preparation | Before the host action | After resume or boot |
| --- | --- | --- |
| `Running` | Record before the first stop, run `Stop-VM -Force`, and confirm `Off` | Start and confirm `Running`, then remove the journal entry |
| `Off` | Leave it alone and do not record it | Leave it `Off` |
| Query failure or any other state | Do not force it; fail the host action | No new transition is attempted |

"Any other state" intentionally includes `Starting`, `Stopping`, `Paused`, and `Saved`. Resolve that state in Hyper-V and retry the system command. D2RHost will not guess whether it is safe to discard or recreate an ambiguous VM state.

Preparation reads every configured target before stopping any of them. It then writes the complete `Running` set to SQLite before the first stop, stops one VM at a time, and verifies `Off` after each stop. If a VM independently changes from `Running` to `Off` between the two checks, it is removed from the journal because D2RHost did not turn it off.

The physical host action is queued only after all recorded VMs confirm `Off`. If a stop or confirmation fails partway through, D2RHost does not sleep, shut down, or restart the host. It immediately runs the restore path over the journal, which starts VMs already stopped and recognizes VMs still `Running` without issuing a duplicate start.

Successful preparation also arms one host power transition in memory. If another sleep, shutdown, or restart arrives before the first transition resumes or fails, D2RHost rejects it rather than letting it replace or consume the first command's resume journal. Resume, failure recovery, and the shutdown/restart watchdog release the arm; this applies even when no VM was `Running` and the journal is empty.

## Resume and boot recovery

- Ordinary sleep blocks the D2RHost suspend call until Windows resumes. The process then consumes its local journal and starts the recorded VMs.
- Modern-Standby-only hardware uses hibernation for `/d2r system sleep`. If Windows preserves the process, it restores on return; if Windows starts a fresh process, startup recovery handles it.
- Shutdown and restart leave the journal on disk. The startup scheduled task launches D2RHost, which attempts recovery before normal Discord or worker operation.
- If `shutdown.exe` returns but Windows has not terminated D2RHost after 30 seconds, an in-process watchdog treats the host action as failed and restores the journaled VMs.
- A start entry is removed only after the VM confirms `Running`. A manually started VM is also safe: recovery sees it already `Running`, issues no duplicate `Start-VM`, and clears the entry.
- Failed rollback, resume, watchdog, and startup restores remain recorded and retry every 15 seconds while that D2RHost process is alive.

This journal is deliberately local. A worker may shut down before the master, and either PC may boot first; the worker's local config, Hyper-V access, and SQLite database are sufficient to restore its guests.

## Warmup failure ladder

Follow-auto counts consecutive outer `menu_ready` failures per account (a successful warmup, or a
live preflight proving warmup was unnecessary, resets that account to zero) and escalates in two
stages:

| Consecutive failures | Action |
| --- | --- |
| 5 | Power-cycle **that account's VM only**: `vm_stop`, confirm `Off`, `vm_start`, confirm `Running`, then wait for its agent to reconnect. A guest that freezes at either step gets its power cut - see [Wedged VM recovery](#wedged-vm-recovery-hard-power-cut). The strike count restarts so the rebuilt guest gets a full budget. |
| 5 more (after a cycle) | Restart the **physical node** that owns the account, via the VM-safe path above: record and stop its Running VMs, restart the host, restore only what it stopped, and resume the follow run. |

VM first, node second, because one wedged guest is the common case - a client that cannot
initialize its graphics device, a Battle.net install that will not repair - and rebooting the
physical host for it takes every healthy sibling VM down too. A guest that comes back broken a
second time is evidence the problem is below the guest, which is what the node restart is for.

A VM cycle that cannot run at all (no `vmName` mapping, the owning worker offline, PowerShell
refused) is recorded as unavailable **without** clearing the strikes, so the very next failure
escalates straight to the node instead of retrying an impossible cycle. Both stages are per
account: two accounts on one node each get their own VM cycle, while their node restart is
latched so it happens once.

The confirmation reads `State` from `vm_status`, accepting both the numeric `VMState` that
`ConvertTo-Json` emits and a string, so a worker node on an older build still confirms correctly.

## Wedged VM recovery (hard power cut)

A VM told to restart can freeze partway through it - typically sitting on the Windows boot logo,
sometimes with the spinner underneath, never reaching a desktop. Nothing inside the guest can
report that, because its agent never started, and **`vm_stop` cannot clear it either**: `Stop-VM
-Force` is a *guest-cooperative* shutdown routed through the integration services, and a guest
frozen that early is not running them. Only `Stop-VM -TurnOff` - the hypervisor equivalent of
holding the power button - gets the machine back.

Before this existed, both of the ladder's dead ends escalated straight to a node restart:

| Dead end | Old behavior | Now |
| --- | --- | --- |
| Graceful stop never confirms `Off` | Recovery fails, next strike restarts the node | `vm_turnoff`, confirm `Off`, settle, continue the cycle |
| VM reaches `Running` but the agent never reconnects | Recovery fails after 20 minutes, next strike restarts the node | Assess the guest; if wedged, `vm_turnoff`, settle, `vm_start`, and restart the reconnect budget |

**A power cut is never a first resort, and never an assumption.** Three things enforce that:

1. **Placement.** Every cut happens where the host had *already* given up and would otherwise
   restart the whole physical node, taking every healthy sibling VM with it. A cut scoped to the
   one wedged guest is strictly less disruptive than the action it replaces.
2. **Evidence.** A live guest is never cut. Hyper-V's Heartbeat integration service is now read
   alongside `State` in `vm_status`; if it reports contact with the guest OS, Windows is up and
   scheduling, so this is not a frozen boot and the policy refuses - whatever is keeping the agent
   away needs a different fix. Only a guest the hypervisor cannot reach is a candidate, and only
   after a grace window long enough to cover an ordinary cold boot.
3. **Bounds.** Cuts are capped per recovery. A guest that will not return after its allowance is a
   host or hardware problem, and looping would only delay the node escalation that can help.

Decision table (`VmHangRecoveryPolicy`, pinned by `VmHangRecoveryPolicyTests`):

| Heartbeat | State | Elapsed since power-on | Verdict |
| --- | --- | --- | --- |
| `OK` | `Running` | any | **Never cut** - the guest OS is alive |
| `No Contact` / `Lost Communication` | `Running` | < `hangSuspectedAfterSeconds` | Keep waiting (a cold boot shows no heartbeat too) |
| `No Contact` / `Lost Communication` | `Running` | >= `hangSuspectedAfterSeconds` | Hard power cut |
| Not reported | `Running` | < `noEvidenceGraceSeconds` | Keep waiting |
| Not reported | `Running` | >= `noEvidenceGraceSeconds` | Hard power cut |
| any | not `Running` | any | Keep waiting - another transition is in flight |

`Lost Communication` is deliberately treated as a hang rather than an unknown: a guest that
answered and then stopped is precisely the mid-restart freeze this exists for.

A heartbeat that is **not reported** - the integration service disabled per VM, absent from the
guest, or a worker node too old to send the field - is absence of evidence, not evidence of a hang.
It buys no speed: the cut waits out the full `noEvidenceGraceSeconds`, which defaults to the same
20 minutes the agent-reconnect budget already spent. An un-updated worker therefore degrades to the
old patient behavior rather than to a wrong decision. Such a worker also cannot run `vm_turnoff` at
all and answers `Unsupported worker command`; the recovery result says so explicitly and names
updating that node as the fix.

Configure under `vmHangRecovery` in the host config:

| Key | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | `false` restores the previous behavior exactly: report a failed recovery and escalate to the node. |
| `hangSuspectedAfterSeconds` | `300` | How long a powered-on guest may go with **no heartbeat contact** before it counts as wedged. Must clear a real cold boot; raise before lowering. |
| `noEvidenceGraceSeconds` | `1200` | The longer window used when heartbeat is not reported. Validation refuses a value below `hangSuspectedAfterSeconds`. |
| `settleSeconds` | `10` | How long the VM stays off before starting again. Hyper-V releases guest devices and memory asynchronously, and starting into that teardown is its own way to produce a wedged boot. |
| `maxHardPowerCuts` | `2` | Cuts allowed per recovery before escalating to the node. |

## Troubleshooting

If the Discord command says VM preparation failed and the host stays awake:

1. Read the named VM and state in the command result.
2. On that physical Hyper-V host, run `Get-VM -Name '<vmName>' | Select-Object Name,State`.
3. Confirm the config's `vmName` is exact and allowed by `allowedVmNamePrefixes`.
4. Move a paused, saved, or transitional VM deliberately to either `Running` or `Off`, then retry.
5. Check the node's `logs` directory for the underlying PowerShell error if state lookup, stop, or confirmation failed.

If a VM keeps needing its power cut:

1. The result and the follow-auto monitor both name the reason the policy acted (no heartbeat
   contact for N minutes, or a forced shutdown that went unanswered). Start there.
2. On the owning Hyper-V host, check the guest is not disabling its own integration services:
   `Get-VMIntegrationService -VMName '<vmName>' -Name Heartbeat`. `Enabled: False` means the host
   is running blind and always waiting out the full `noEvidenceGraceSeconds`.
3. Repeated wedged boots on the same guest usually mean a damaged guest OS or a storage problem,
   not a host bug. A power cut is an uncontrolled stop, and doing it repeatedly is itself a way to
   corrupt a guest, which is why `maxHardPowerCuts` exists.
4. Set `vmHangRecovery.enabled: false` to fall back to the previous behavior while investigating.

If a host returns but an expected VM stays off:

1. Confirm the VM really was `Running` when D2RHost prepared the power action. A VM already `Off` is intentionally never started.
2. Confirm the startup scheduled task ran and is still configured for `SYSTEM` with highest privileges.
3. Check the D2RHost startup log for `VM restore remains pending` and the VM-specific error.
4. Verify Hyper-V is available and the configured PowerShell timeout is sufficient. Startup recovery retries Hyper-V while the persisted list is non-empty.
5. Do not delete the SQLite journal merely to silence the retry: doing so tells D2RHost to forget that it owes that VM a restart. Resolve the named state/configuration error or start the VM manually; the next restore pass will recognize `Running` and clear it safely.
