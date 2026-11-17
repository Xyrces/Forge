# Linux host and restart observations

Inspected November 16, 2026, Eastern time. These are read-only observations of the existing host, apart from the separately authorized SSH setup and plugged-in availability settings described below. No Forge deployment, restart, queue edit, gate change, model request, or game run was performed.

## Actual host

| Item | Observed |
|---|---|
| Host | `192.168.68.78`, `jtn5016-ThinkPad-P16s-Gen-4-AMD` |
| CPU | AMD Ryzen AI 9 HX PRO 370, 12 cores / 24 logical CPUs |
| GPU | AMD Strix Radeon 880M/890M PCI device; CPU identifies Radeon 890M |
| Memory | Approximately 53 GiB total, 41 GiB available during inspection |
| Disk | Root filesystem 937 GiB, 531 GiB used, 359 GiB available |
| Kernel | Linux 7.0.0-34-generic |
| Desktop user's installed SDK | `/home/jtn5016/.dotnet/dotnet --version`: 10.0.302 |
| Noninteractive SSH environment | `dotnet` absent from default PATH, even for the desktop user |
| GPU device nodes | `/dev/dri/card1`, `/dev/dri/renderD128`; access depends on worker identity/session ACLs |

This is a plausible single native-test host. It is not a measured guarantee of parallel gameplay capacity. TimeLoop's repository already accepts Raster on this hardware and records NRD performance limitations. No new gameplay or GPU benchmark was run.

## The service is already running

The **user** unit `/home/jtn5016/.config/systemd/user/forge.service` is active/running, started October 3, with PID 7388 during inspection. The system-level `forge` unit is absent/inactive. The user unit reports:

- Working directory `/home/jtn5016/.local/state/forge`.
- Approximately 409 MiB cgroup memory in use; `MemoryMax=infinity`.
- `PrivateDevices=no`, `ProtectHome=no`.
- Executable `/home/jtn5016/.dotnet/dotnet`, assembly `/home/jtn5016/.local/share/forge/current/Forge.Core.dll`.
- Current release link resolves to `/home/jtn5016/.local/share/forge/releases/qa-evidence-hardening-56`.
- HTTPS `GET /api/meta/buildinfo` reports `1.0.0+4ac44895c05122ff5705a32b4ceb93e60a6e72e5`, runtime `.NET 10.0.10`. This matches the reviewed current GitHub source.
- The dashboard is listening on ports 80/443, not the documented development default 4097. HTTPS requests to `https://localhost` succeeded using normal certificate validation.

The packaged system service's `MemoryMax=8G`, `PrivateDevices=true`, `ProtectHome=true` restrictions are therefore **not the effective settings of this deployment**. They remain relevant if reinstalling from the template, but must not be reported as a current GPU blocker. Desktop/display availability, isolated saves, renderer/export ownership, and native process cleanup still require qualification.

Whitelisted fields from `/home/jtn5016/.config/forge/appsettings.json` show SQL Server storage, InProcess execution, and Kilo/Kimi/MiniMax provider configuration. Secret values and database connection strings were not printed. Effective DB-backed model overrides were not inventoried, so configured defaults are not proof of the actual model for every run. A Headroom process was listening locally on 8787; that observation alone does not prove Forge routes requests through it.

## Existing work is still registered

Read-only dashboard snapshots around 19:54 Eastern returned:

| Project | Returned tasks by status | Active sprint |
|---|---|---|
| Forge | 26 Closed, 3 Completed, 1 Blocked | `sprint-aff83f3504` |
| PortHorizon | 408 Closed, 416 Completed, 23 Pending, 1 Blocked | `sprint-929836d6d3` |
| Talaria | 30 Closed, 28 Completed | None |

TimeLoop is not registered. Design, groom, sprint, and merge stage gates all report open. These are point-in-time API counts, not a forensic history or a promise that no job can start. The service heartbeat is current. `/api/forgesystem/health` reported projectCount=1 while `/api/projects` listed three; this discrepancy needs operational diagnosis before using that health number as a registry count. The state endpoint's `schemaVersion=4` is not treated as proof of the database migration version.

The user explicitly wants old service data discarded and the existing Azure SQL service retained. Reset only Forge-owned state, retire the old service's ownership, and create an empty registry with Forge as the first project; do not import the old backlog or registrations. This is a future implementation step, not a reset performed during review. Starting a second active dispatcher against the same state would remain unsafe even on SQL Server until distributed ownership is implemented. SQLite is not the proposed production store.

## SSH setup completed with authorization

The Windows saved host identity was stale. The current ED25519 fingerprint was independently verified through the connected Linux desktop, then the known-host entry was replaced with that verified key. The previous file was retained under the review's ignored `.portHorizon/review-ssh` directory. Host verification remains enabled.

The existing `timeloop-handoff` SSH account worked but lacked access to the desktop user's files and user service. With explicit user approval, this PC's existing public key was added to `/home/jtn5016/.ssh/authorized_keys` with restrictive permissions. The server's AllowUsers policy initially still restricted access to the handoff account.

At the user's request, the approved administrator command was entered in the Linux console. The user supplied the sudo password directly. The command backed up `/etc/ssh/sshd_config.d/00-timeloop-handoff.conf` as `.before-forge-review`, added `jtn5016@192.168.68.70` alongside the existing handoff entry, validated with `sshd -t`, and reloaded SSH. Password authentication, root login, and forwarding restrictions were retained. Successful subsequent SSH login as `jtn5016` and service/API reads establish the resulting access. No administrator password was requested or received in chat.

RDP control was stopped by the user; subsequent inspection used SSH only. The normal connection from this Windows PC is:

```text
ssh jtn5016@192.168.68.78
```

## Remaining host acceptance work

1. Qualify a fresh Forge-first deployment after implementation, retaining Azure SQL. The recommended target is single-node K3s with embedded etcd for cluster metadata and isolated CPU worker Jobs. First stop or deliberately isolate the existing process; do not replay old projects accidentally. Initial SSH checks found no docker, kubectl or k3s executable on PATH; no platform was installed.
2. Run the normal audit-enabled build, full offline suite, and deterministic engineering harness on Linux. The current review's Windows receipts do not certify these.
3. Establish a native worker with the exact TimeLoop engine/plugin/runtime profile, display/GPU access, one render/export lease, isolated saves/ports, and bounded logs/evidence.
4. Verify source and exported gameplay at both required resolutions. Test cancellation and restart without orphaned game processes or lost evidence.
5. Confirm API account/model access, credit expiry, durable spend policy, effective per-role overrides, and all stage holds before any paid pilot.

## Plugged-in availability settings applied

The host currently uses Wi-Fi (`wlp194s0`); Ethernet has no carrier. GNOME was configured to suspend after 3,600 seconds idle on AC and 900 seconds on battery. Its screen idle/blanking setting is 900 seconds. The wireless device advertises magic-packet wake support, but WoWLAN and the device's power-wakeup attribute were disabled. Hardware capability alone was not treated as proof of reliable wake-on-demand.

The user chose to keep the machine awake while plugged in and requested that its screen still turn off. Applied and read back:

- `org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type = 'nothing'` (was `'suspend'`).
- `org.gnome.desktop.session idle-delay = uint32 900` retained: the screen may blank after 15 minutes of inactivity. No display interaction or 15-minute idle test was forced while the user was working.
- Battery inactivity behavior retained: suspend after 900 seconds. Screen lock settings retained.
- `/etc/systemd/logind.conf.d/60-forge-ac-availability.conf` contains `[Login]` and `HandleLidSwitchExternalPower=ignore`. The user entered their password in a separate local SSH console; no password entered this chat or tool output. `systemd-logind` recorded a successful reload at 20:17:01 Eastern and remained active, with no desktop restart.

The user subsequently clarified that they do not close the lid. The lid setting was unnecessary for their reported problem; the idle-suspend change is the relevant fix. No further prompt was needed. These settings prevent ordinary automatic AC sleep; they do not promise access through a manual suspend, shutdown, power loss or Wi-Fi outage. Wake-on-demand was not enabled, and no suspend/reboot was performed.

Reversal if wanted: set the AC inactivity type back to `suspend`; remove only the named logind drop-in and reload logind. Do not alter unrelated power settings. This availability work is separate from the proposed Forge migration.
