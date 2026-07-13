# ONI Together — Liweijin Maintained Branch

## Project identity

- Upstream: `https://github.com/Lyraedan/Oxygen_Not_Included_Together`
- Fork: `https://github.com/jlw463195395/Oxygen_Not_Included_Together`
- Upstream mirror branch: `main`
- Maintained branch: `liweijin`
- Local source-reading workspace: `/home/jlw/projects/Oxygen_Not_Included_Together`
- Remote build/test workspace: `root@192.168.0.110:/opt/mengyao-build/Oxygen_Not_Included_Together` (8ka)
- SSH key from the control host: `/home/jlw/.ssh/jlw-8card-coolify`

## Non-negotiable workflow rules

1. Treat ONI Together as the sole upstream.
2. Synchronization is one-way: pull/fetch upstream changes and merge them into `liweijin` after review. Do not routinely send fixes upstream; only do so when the user explicitly approves or feedback is exceptionally necessary. The `upstream` remote has push URL `DISABLED` on maintained workspaces to prevent accidental pushes.
3. Keep `main` as a clean fast-forward mirror of `upstream/main`. Never place project-specific commits on `main`.
4. Put all maintained fixes, compatibility work, documentation, and releases on `liweijin` or short-lived branches based on it.
5. Never run builds, tests, package restore, publicizer, game launch, or runtime smoke checks on the control host. All such work must run on 8ka.
6. The control host may only perform lightweight source reading, Git metadata inspection, diff review, documentation edits, and other non-build static analysis.
7. Do not commit or redistribute Klei game DLLs, decompiled ONI source, game assets, Steam credentials, or private keys.
8. `Directory.Build.props.user`, `PublicisedAssembly/`, build outputs, and local game libraries remain untracked.
9. Every client and host in a multiplayer test must use the exact same game build, DLC configuration, mod DLL, packet registry, and protocol version.
10. Do not call a change verified until fresh build/test/runtime evidence exists from 8ka after the final edit.

## Product priority

This is a trusted friends/family co-op Mod for a single-player colony simulation. Optimize for gameplay, true concurrent control, low latency, visual continuity, simulation agreement, and recovery from drift.

- Do not spend maintenance time on account authentication, authorization systems, anti-cheat, adversarial clients, player-owned inventories, or per-player asset isolation unless the user explicitly changes scope.
- Player/connection IDs are routing and session-lifecycle tools, not a security boundary.
- Prioritize player-visible synchronization: cursors and tools, duplicant movement/animation/chores, buildings, world cells, conduits, automation, resources, plants/critters, pause/speed, save loading, reconnect, and hard sync.
- Treat frame pacing, network bandwidth, GC pressure, packet loss behavior, interpolation, viewport catch-up, and host/client divergence as first-class correctness concerns.
- Every networking review must actively search for synchronization bugs, stale state, unsynchronized gameplay paths, latency spikes, redundant scans/packets, and avoidable hard syncs.

## One-way upstream synchronization

Preferred flow:

```bash
git fetch upstream
git switch main
git merge --ff-only upstream/main
git push origin main

git switch liweijin
git switch -c merge/upstream-YYYYMMDD
git merge main
```

Resolve and review the merge on the temporary branch. Build and test only on 8ka. Merge the temporary branch into `liweijin` only after remote verification.

Do not automatically merge upstream directly into a release used by players. Pay special attention to:

- packet types, packet fingerprints, serialization field order, and protocol version;
- save transfer and hard-sync behavior;
- Harmony patch target signatures;
- game build compatibility and `TargetGameVersion`;
- host/client authority boundaries;
- Steamworks and Riptide transport changes;
- changes to `staticID`, package version, or generated metadata.

## Current baseline (2026-07-13)

- Forked from upstream commit `1030b2685e8f729b928cd3928e50dbbfd25dacd1`.
- Upstream public release: `v0.7.3-alpha`.
- Main mod target: `netstandard2.1`; dedicated-server experiment: `net8.0`.
- Main entry point: `ONI_Together/MultiplayerMod.cs` (`MultiplayerMod : UserMod2`).
- Packet registration: `ONI_Together/Networking/Packets/Architecture/PacketRegistry.cs`.
- Protocol guard: `ONI_Together/Networking/ProtocolCompatibility.cs`.
- Session state: `ONI_Together/Networking/MultiplayerSession.cs`.
- Main mod build depends on the licensed ONI installation's `OxygenNotIncluded_Data/Managed` DLLs and publicizes `Assembly-CSharp.dll` plus `Assembly-CSharp-firstpass.dll` during the remote build.
- 8ka currently owns all future build/test execution. Keep large/reproducible artifacts under `/opt/mengyao-build`, not on the control host.

## Verification expectations on 8ka

Before marking a code change complete, choose the relevant subset and record exact output:

- restore pinned/local .NET tools;
- compile the affected project(s);
- run available automated/unit/debug tests;
- inspect Harmony patch failures and ONI logs;
- package the Local mod folder and inspect generated `mod.yaml` / `mod_info.yaml`;
- perform host/client smoke tests when networking or simulation behavior changed;
- validate Steam and/or LAN paths as applicable;
- verify hard sync, save transfer, version rejection, and reconnect behavior when touched;
- report artifact path, version, supported ONI build, Git commit, and SHA-256.

A compile-only result is not runtime verification. A local source check is not a build or test.

## License boundary

ONI Together is MIT-licensed. Preserve the upstream copyright and MIT notice. Oxygen Not Included itself is proprietary; reference locally owned game assemblies for development but never add them or decompiled game source to this repository.
