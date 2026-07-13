# Phase 0 synchronization foundations — implementation record

Date: 2026-07-13
Branch: `liweijin`
Baseline: `70e71e9b394555ef71de5253941a7e3778c4eeb2`

## Scope

This phase begins implementation with two protocol-neutral fixes that can be exercised without licensed ONI assemblies. It deliberately does not change packet IDs, serialized fields, Harmony targets, or the simulation cadence.

## Confirmed root causes

### Entity snapshot freshness mixes unrelated clocks

`EntityPositionHandler.TryRequestEntityPositionIfVisible` compared Unity's receiver-local `Time.unscaledTime` (seconds since this process started) with the host's Unix wall-clock timestamp. After the first snapshot, the subtraction is a very large negative number, so a visible entity can never become stale and request recovery.

The host timestamp remains useful for ordering snapshots produced by the same host. Snapshot age must instead be measured from the receiver-local monotonic time at which the accepted snapshot arrived.

### Main-thread dispatch is unsafe and capped at two actions per second

`MainThreadExecutor` accepted callbacks from TCP transfer threads into a plain `List<Action>`, while a Unity coroutine concurrently inspected and removed entries. It executed one callback, waited 500 ms, and recursively started another coroutine. A save transfer can enqueue many progress callbacks, so this creates minutes of UI/load backlog and races on the list.

The replacement is a thread-safe FIFO drained from `Update` in bounded batches. A failing action or logger cannot prevent later actions from running.

## Implemented slice

- Added `Shared.Networking.SnapshotFreshness`.
- Recorded local monotonic receipt time only after an entity snapshot passes strict ordering; older and equal-timestamp duplicate snapshots are rejected without refreshing freshness.
- Completed the stale-recovery route by populating the existing `EntityPositionRequestPacket.RequesterId`; without it, the host attempted to send the reliable recovery snapshot to player ID `0`.
- Replaced the coroutine/list dispatcher with `Shared.Networking.FrameActionQueue`.
- Drain at most 128 queued actions per Unity frame.
- Added an ONI/Unity-independent executable regression harness under `tests/`.

## Test evidence

TDD RED evidence on 8ka:

1. `SnapshotFreshness.cs` absent: compilation failed with `CS2001`.
2. `FrameActionQueue.cs` absent: compilation failed with `CS2001`.
3. An error handler throwing stopped queue draining: `RESULT 9 passed, 1 failed`.
4. Independent review found that equal-timestamp duplicate snapshots still refreshed freshness; `SnapshotOrdering.cs` was absent in the new RED test and compilation failed with `CS2001`.

Latest focused GREEN evidence on 8ka:

```text
PASS missing snapshot is stale
PASS recent local receipt is fresh
PASS receipt at threshold remains fresh
PASS receipt older than threshold is stale
PASS local clock rollback does not mark stale
PASS older snapshot is rejected
PASS duplicate snapshot is rejected
PASS newer snapshot is accepted
PASS frame queue preserves FIFO order
PASS frame queue enforces per-drain budget
PASS frame queue continues after action failure
PASS frame queue survives error-handler failure
PASS frame queue accepts concurrent producers
RESULT 13 passed, 0 failed
```

This proves only the pure synchronization primitives. It does not prove the Unity integration or runtime behavior.

## Fresh Hard Sync call-graph findings

The deeper call-graph review confirms that Hard Sync is currently a UI/timing-driven flow rather than an atomic synchronization transaction:

1. Host pauses and broadcasts an empty `HardSyncPacket`.
2. Clients set only a boolean `IsHardSyncInProgress`.
3. Host marks clients unready and invokes `SendSaveFileToAll`.
4. `SendSaveFileToAll` calls `SaveHelper.GetWorldSave` separately for each client.
5. Every `GetWorldSave` call executes `SaveLoader.Instance.Save(path)` before reading bytes; clients can therefore receive different snapshots and the host performs repeated full saves.
6. The host executes another save solely to estimate transfer duration.
7. Hard Sync clears `hardSyncInProgress` after an estimated delay, independent of TCP completion, UDP ACK completion, client load, reconnect, or applied world epoch.
8. UDP transfer IDs derive from filename, so a retry or later sync can collide with stale chunks/ACKs from an earlier generation.
9. The client assembler keys in-progress downloads by filename, not transfer generation.
10. Ready status has no sync epoch. A delayed `Ready` from an older reconnect can satisfy the current barrier.
11. `AllClientsReadyPacket` closes overlays but does not explicitly commit an epoch; game unpause calls are commented out.
12. TCP failure requests a fresh UDP save, which can differ from the snapshot originally announced for the same Hard Sync.

## Required Hard Sync state machine

The next protocol-changing slice should introduce an explicit transaction:

```text
Idle
  -> Capturing(syncEpoch)
  -> Announced(syncEpoch, snapshotId, metadata)
  -> Transferring(per-client)
  -> Downloaded(per-client)
  -> Loading(per-client)
  -> Reconnected(per-client, appliedEpoch)
  -> Committed(all required clients applied the same epoch)
  -> Idle
```

Minimum invariants:

- Capture exactly one immutable byte array per Hard Sync.
- Reuse it for every client and for TCP-to-UDP fallback.
- Give every transfer a unique generation ID; filename is display metadata only.
- Include `syncEpoch` in hard-sync announcement, transfer chunks/progress/ACKs, ready status, and final commit.
- Ignore packets from old or unknown epochs.
- Complete on an explicit applied barrier, never estimated duration.
- Define disconnect policy explicitly: remove a departed client from the barrier or abort; never wait indefinitely.
- Keep the world paused until commit/abort policy runs, while network polling and transfer callbacks continue.

## 64 Hz relationship

These fixes are prerequisites for smooth high-frequency networking but do not change ONI's simulation tick. The intended architecture remains:

- input commands: event-driven and host ordered;
- network pump: every render frame with a bounded main-thread budget;
- host timeline: 64 Hz logical numbering where useful;
- visible movement snapshots: adaptive 20–30 Hz, interpolated every render frame;
- cursor: 30 Hz default, optional 60 Hz LAN;
- world/building state: dirty/event-driven with low-frequency authoritative repair.

A fixed 64 Hz scheduler cannot guarantee 15.625 ms Unity application latency when the ONI main thread itself exceeds that budget. Queue age, processing time, packet rate, bytes, RTT, jitter, dropped stale revisions, and correction distance must therefore be measured before raising cadences.

## Verification blocker

A fresh 8ka restore succeeded, but `dotnet build Shared/Shared.csproj -c Release` remains blocked by missing licensed ONI/U59 assemblies (`UnityEngine`, `TMPro`, `HarmonyLib`, `GameHashes`, and related types). Full compilation, U59 compatibility, and dual-instance behavior remain unverified until those assemblies are legally installed on 8ka.
