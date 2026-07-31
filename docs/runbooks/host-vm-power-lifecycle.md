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

## Troubleshooting

If the Discord command says VM preparation failed and the host stays awake:

1. Read the named VM and state in the command result.
2. On that physical Hyper-V host, run `Get-VM -Name '<vmName>' | Select-Object Name,State`.
3. Confirm the config's `vmName` is exact and allowed by `allowedVmNamePrefixes`.
4. Move a paused, saved, or transitional VM deliberately to either `Running` or `Off`, then retry.
5. Check the node's `logs` directory for the underlying PowerShell error if state lookup, stop, or confirmation failed.

If a host returns but an expected VM stays off:

1. Confirm the VM really was `Running` when D2RHost prepared the power action. A VM already `Off` is intentionally never started.
2. Confirm the startup scheduled task ran and is still configured for `SYSTEM` with highest privileges.
3. Check the D2RHost startup log for `VM restore remains pending` and the VM-specific error.
4. Verify Hyper-V is available and the configured PowerShell timeout is sufficient. Startup recovery retries Hyper-V while the persisted list is non-empty.
5. Do not delete the SQLite journal merely to silence the retry: doing so tells D2RHost to forget that it owes that VM a restart. Resolve the named state/configuration error or start the VM manually; the next restore pass will recognize `Running` and clear it safely.
