# ONI Together Project Memory

Last grounded review: 2026-07-13
Reviewed baseline: `1030b2685e8f729b928cd3928e50dbbfd25dacd1` (`v0.7.3-alpha` code line)

This document is durable project-specific context for maintaining the `liweijin` branch. Update it when architecture, build prerequisites, branch policy, or verified behavior changes. Do not use it as a task-progress log.

## Repository and branch model

- Canonical upstream: `Lyraedan/Oxygen_Not_Included_Together`
- Maintained fork: `jlw463195395/Oxygen_Not_Included_Together`
- `main`: clean one-way mirror of `upstream/main`
- `liweijin`: maintained branch for private fixes and releases
- The `upstream` remote is fetch-only in practice; its push URL is set to `DISABLED` on maintained workspaces.
- Upstream feedback is exceptional, not routine. Pull and merge upstream changes; do not maintain a bidirectional contribution workflow unless explicitly approved.

Workspaces:

- Control/source-reading host: `/home/jlw/projects/Oxygen_Not_Included_Together`
- Build/test host 8ka: `/opt/mengyao-build/Oxygen_Not_Included_Together`

All builds, restores required for builds, tests, game launches, packaging, and runtime smoke checks execute on 8ka. The control host is limited to source reading, Git/diff inspection, and documentation work.

## Product model

ONI Together adds cooperative control over one shared Oxygen Not Included colony. It is not player-owned duplicants or separate colonies. The host is authoritative for major simulation state; clients send commands and consume synchronized state/events. The project compensates for incomplete live synchronization with save transfer and optional/manual hard sync.

Maintenance scope is deliberately gameplay-first. This is trusted friends/family co-op, so account authentication, authorization, anti-cheat, adversarial-client hardening, per-player inventory ownership, and asset isolation are out of scope unless explicitly requested. Connection/player identifiers matter only for correct packet routing, reconnect, cursor ownership, and cleanup. Engineering effort goes to real-time concurrent control, low latency, smooth interpolation, complete gameplay coverage, host/client agreement, performance, and fast recovery from drift.

Supported network choices are abstracted behind transport interfaces:

- Steamworks lobby/P2P transport;
- Riptide LAN/direct-IP transport;
- LAN save transfer can use a separate TCP file-transfer path.

Lobby limits in `NetworkConfig` are minimum 2, default 4, maximum 16. These are protocol/UI limits, not a stability guarantee.

## Solution shape

`ONI_Together.sln` currently includes four projects:

1. `ONI_Together/ONI_Together.csproj`
   - Main game Mod.
   - `netstandard2.1`, C# preview, unsafe enabled.
   - Packages include PLib and Riptide.
   - Uses ILRepack to produce one main Mod DLL.
   - Generates `mod.yaml` and `mod_info.yaml` during build.

2. `Shared/Shared.csproj`
   - Shared packet registration, profiling, helpers, and interfaces.
   - `DoNotBuildAsMod=true`.

3. `ONI_Together_API/ONI_Together_API.csproj`
   - Third-party Mod integration API/NuGet package.
   - Uses MinVer; references `Shared`.

4. `ONI_Together_DedicatedServer/ONI_Together_DedicatedServer.csproj`
   - Experimental `net8.0` executable.
   - Not a production-ready headless ONI server.

The repository contains roughly 489 tracked C# files at this baseline; the overwhelming majority live under `ONI_Together/`.

## Initialization and runtime loop

Primary entry point:

- `ONI_Together/MultiplayerMod.cs`
- `MultiplayerMod : KMod.UserMod2`

`OnLoad` performs PLib setup, options registration, asset-bundle loading, diagnostics, Steam lobby initialization, and creation of the persistent `Multiplayer_Modules` GameObject. Important attached components include:

- `NetworkingComponent`
- `UIVisibilityController`
- `MainThreadExecutor`
- `CursorManager`
- `PingManager`
- `WorldStateSyncer`
- `PlantGrowthSyncer`
- `ConduitFlowSyncer`
- `AnimSyncCoordinator`
- `AnimResyncRequester`
- `BulkPacketMonitor`
- `LogicStateSyncer`

`OnAllModsLoaded` registers packet types, initializes integrations, optionally discovers debug tests, selects the Steamworks transport by default, and captures Unity's main thread context.

`NetworkingComponent.Update` is the central per-frame network pump:

- advances the Unity task scheduler;
- calls `GameServer.Update()` for hosts;
- calls `GameClient.Poll()` for clients;
- checks inactive save transfers on clients;
- flushes transport packet queues.

## Session, transport, and connection state

Key files:

- `ONI_Together/Networking/MultiplayerSession.cs`
- `ONI_Together/Networking/NetworkConfig.cs`
- `ONI_Together/Networking/GameServer.cs`
- `ONI_Together/Networking/GameClient.cs`
- `ONI_Together/Networking/States/`
- `ONI_Together/Networking/Transport/`

`MultiplayerSession` stores host/client role, local/host IDs, connected players, cursors, and known names. It is global static state and therefore a high-risk lifecycle/reset area.

`NetworkConfig` selects one of two transports:

- `STEAMWORKS`
- `RIPTIDE`

The abstract boundaries are:

- `TransportServer`
- `TransportClient`
- `TransportPacketSender`

Concrete implementations live under `Transport/Steamworks` and `Transport/Riptide` (some namespaces still retain older Steam/Lan naming). When changing transport behavior, verify both implementations and all identity/connection conversions.

`GameClient` validates:

- protocol metadata;
- packet registry fingerprint;
- DLC configuration;
- active Mod list.

It also owns save request flow and currently contains incomplete/disabled automatic reconnect logic. Do not assume reconnect is production-ready merely because state and coroutine code exist.

## Packet architecture and compatibility

Key files:

- `ONI_Together/Networking/Packets/Architecture/IPacket.cs`
- `ONI_Together/Networking/Packets/Architecture/PacketRegistry.cs`
- `ONI_Together/Networking/Packets/Architecture/PacketHandler.cs`
- `ONI_Together/Networking/Packets/Architecture/PacketSender.cs`
- `ONI_Together/Networking/ProtocolCompatibility.cs`
- `Shared/Helpers/PacketRegistrationHelper.cs`

At Mod load, `PacketRegistry.RegisterDefaults()` reflects over the executing assembly and auto-registers concrete `IPacket` implementations unless they opt out.

Packet IDs are derived from type identity through `API_Helper.GetHashCode`. The compatibility fingerprint is SHA-256 over sorted registered integer packet IDs, truncated to an `Int32`. Current explicit protocol version is `1`.

Consequences:

- renaming/moving packet types can change IDs;
- adding/removing registered packets changes the fingerprint;
- the fingerprint checks the registry set, not every serialization field layout;
- changing serialization order or interpretation without changing packet identity can pass the fingerprint while still corrupting communication;
- any wire-format incompatible change should deliberately update protocol/version handling and receive host/client rejection tests.

`PacketHandler` reads the packet ID, creates the registered packet, calls `Deserialize`, and dispatches `OnDispatched`. Its `readyToProcess` gate drops packets while false and force-recovers after 60 seconds. Changes around world load/hard sync must audit this gate carefully.

`PacketSender` handles serialization, reliable/unreliable send modes, bulk aggregation, per-connection broadcasting, and viewport filtering. `IViewportCullable` packets are only sent to players whose tracked viewport contains the packet cell.

## Authority and synchronization model

The codebase synchronizes behavior through several layers:

- Harmony patches intercept player tools, game UI, state machines, workables, buildings, research, schedules, and game lifecycle events.
- Tool packets replicate dig/build/deconstruct/mop/harvest/prioritize and related commands.
- World and component syncers replicate selected simulation state.
- Host/client-specific patches suppress or replace portions of client simulation.
- Observer/viewport-based filtering reduces traffic for entities and structures outside all player views.
- Bulk packets aggregate high-frequency compatible payloads.
- Save transfer and hard sync repair accumulated divergence.

## Runtime sync cadence

The network pump runs every Unity frame, but the game state is not serialized as a full-frame stream. Synchronization is hybrid and subsystem-specific:

- Remote cursors and their build/drag/path visualizers are sent every `0.1s` (nominally 10 Hz) and interpolated locally.
- Host entity positions are checked every frame. A packet is sent after at least `0.016s` when movement exceeds `0.05` world units, plus a `1s` heartbeat. Clients lerp small errors and snap errors above `1.5` units.
- Duplicant action, animation, target, held-item, and working state are checked every `200ms`, sent on change, and forced by a `1s` heartbeat.
- Detailed duplicant chore/errand UI snapshots are subscription-driven and sent every `0.5s` or immediately when requested.
- Work progress and many structure snapshots use about `0.5s`; automation state uses `1s`; vitals use `1s`; aggregate resources use `3s`.
- Gas/liquid reconciliation starts from `1.5s` and adapts upward under load.
- Dig, mop/chore, and research reconciliation are staggered one category per second, so each category normally repeats every `4s`.
- Building inventory reconciliation is a `30s` safety pass; normal building actions are event-driven and should appear much sooner.
- The first `5s` after world load is a grace period for several background syncers, not a permanent five-second gameplay delay.
- The save-transfer `5s` value is a missing-chunk ACK retransmission timeout, not the visual or gameplay synchronization interval.

This is near-real-time cooperative play, not deterministic lockstep and not video/screen sharing. Every peer renders its own ONI instance; the host is the simulation authority and packets reconcile selected state.

High-risk directories:

- `ONI_Together/Patches/`
- `ONI_Together/Networking/Components/`
- `ONI_Together/Networking/Packets/`
- `ONI_Together/Scripts/`
- `ONI_Together/Misc/World/`

For every simulation fix, explicitly answer:

1. Does this run on host, client, or both?
2. Is the host authoritative result broadcast?
3. Can the client independently create/complete the same object or chore?
4. What stable identity locates the target after load or hard sync?
5. What happens if the object is outside every viewport?
6. What happens on packet duplication, loss, delay, or reordered arrival?
7. Does a late joiner recover the state from the save or need an additional packet?

## Save transfer and hard sync

Key files:

- `ONI_Together/Misc/World/GameServerHardSync.cs`
- `ONI_Together/Networking/SaveFileTransferManager.cs`
- `ONI_Together/Misc/World/SaveChunkAssembler.cs`
- `ONI_Together/Networking/Packets/World/SaveFileRequestPacket.cs`
- `SaveFileChunkPacket.cs`, `SecureTransferPacket.cs`, `ChunkAckPacket.cs`
- `ONI_Together/Networking/Transfer/TcpFileTransferServer.cs`
- `TcpFileTransferClient.cs`

Hard sync pauses the host simulation, marks clients unready, instructs clients to enter hard-sync flow, sends the current save, and waits for clients to load and report ready. It is sensitive to player-count accounting, disconnects, save size, chunk timing, and packet-processing gates.

The Steam-style save path tracks individual chunks and ACKs, selectively retransmits chunks missing for more than five seconds, and times out stale transfers after two minutes. LAN may use TCP for the file while Riptide handles live packets.

`HardSyncAtCycleStart` exists but defaults to `false` in `Configuration.ServerSettings`; it is an operator choice, not an unconditional behavior.

## Build system and legal dependency boundary

Build configuration is shared through:

- `Directory.Build.props`
- `Directory.Build.targets`
- local ignored `Directory.Build.props.user`
- `.config/dotnet-tools.json`

The main Mod build needs a legally obtained ONI installation's:

`OxygenNotIncluded_Data/Managed`

Important references include `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, Harmony, Unity assemblies, Steamworks.NET, Newtonsoft.Json, ImGui, and related runtime assemblies.

Before resolving references, the build runs the local `assembly-publicizer` tool against the two Assembly-CSharp DLLs and writes results under ignored `PublicisedAssembly/`. Build output is ILRepacked, metadata YAML is generated, and files are copied to the configured Mod development folder.

Never commit:

- ONI Managed DLLs;
- publicized Klei assemblies;
- decompiled ONI source;
- Klei assets;
- Steam login material;
- generated binaries or local path configuration.

ONI Together itself is MIT-licensed. Preserve the original copyright and license notice.

## 8ka build environment

As of this review:

- OS: Ubuntu 22.04 x86_64
- Workspace: `/opt/mengyao-build/Oxygen_Not_Included_Together`
- Branch: `liweijin`
- .NET SDK: `8.0.128`
- Local tools restored:
  - `jetbrains.refasmer.clitool 2.0.3`
  - `bepinex.assemblypublicizer.cli 0.5.0-beta.2`
- Ignored `Directory.Build.props.user` is configured with:
  - game libraries: `/opt/mengyao-build/oni-game/OxygenNotIncluded_Data/Managed`
  - Mod output: `/opt/mengyao-build/oni-mod-output`
- Official SteamCMD is installed under `/opt/mengyao-build/steamcmd`.
- The repository currently declares ONI target build `700386` (`U57`).
- Current Steam public ONI as reviewed on 2026-07-13 is `U59-740622`, Steam build `24096873` (released 2026-07-07).
- The U57-to-U59 mismatch is a compatibility migration to verify, not merely a missing-file problem. Expect Klei/Unity API and Harmony target changes.
- An anonymous SteamCMD install was attempted and correctly refused with `No subscription`; ONI is paid content. Acquire it by copying from a legitimately owned installation or by authenticating SteamCMD interactively with an owning account. Never store Steam credentials in Git or scripts.
- ONI Managed DLLs are not yet present on 8ka.
- The Linux publicizer documentation additionally requires the .NET 6 runtime; the installed .NET 8 SDK alone may not satisfy that tool.
- No build or test has been run at this baseline because the licensed game references are missing.

The ONI files must be supplied from a legitimately owned installation without checking them into Git. Place them in a controlled, untracked location on 8ka and point `Directory.Build.props.user` to that location.

## Current test reality

The repository has in-game/debug test infrastructure under:

- `ONI_Together/DebugTools/UnitTests/`
- `ONI_Together/Tests/`

These are not equivalent to a mature standalone CI test suite. The only GitHub workflow currently present is named `DISABLED_publish.yml`, but GitHub still reports it as active: renaming a workflow file does not disable it. Its only recorded `v0.7.3` run failed while compiling `Shared` because the runner lacked the licensed ONI/Unity/Harmony references. It is not a working full-Mod build/test gate.

Therefore maintenance verification must combine:

- remote compilation on 8ka;
- focused executable tests where available;
- in-game debug tests;
- two-instance or two-machine host/client smoke tests;
- log inspection and state comparison;
- packaged artifact metadata and checksum verification.

## Known maintenance risks

- Global mutable static session state can survive or reset at the wrong lifecycle point.
- Harmony targets break when Klei changes signatures or state-machine internals.
- Packet fingerprints do not detect all wire-format changes.
- Client suppression patches can create frozen, duplicated, or independently simulated entities.
- Viewport culling can turn visual camera behavior into simulation/network correctness behavior.
- Hard sync correctness depends on save transfer, load lifecycle, ready state, and disconnect handling together.
- A successful host-side action does not prove the client applied or rendered it correctly.
- Steam and Riptide use different connection identity types hidden behind `object`, increasing runtime-cast risk.
- Dedicated-server code exists but should not be represented as a supported headless ONI runtime.

## Static synchronization and performance audit

The 2026-07-13 gameplay-first audit is preserved in:

- `docs/SYNC_PERFORMANCE_ROADMAP.md`
- `docs/audits/2026-07-13-sync-correctness.md`
- `docs/audits/2026-07-13-network-performance.md`
- `docs/audits/2026-07-13-gameplay-sync-coverage.md`

Highest-priority findings are non-atomic Hard Sync, save-transfer generation collisions, broken normal reconnect, unreliable state without revisions, LAN chunk loss/leaks, incorrect client stale-repair guards, non-authoritative concurrent tool commands, incomplete building topology recovery, and major gameplay gaps in resources, critters, equipment, skills, solid conduits, electrical topology, and U59 aquatic systems. These are static findings until reproduced and measured with U59 host/client instances on 8ka.

## First maintenance priorities

Before broad feature development, establish and continuously improve on 8ka:

1. a two-instance host/client smoke harness with synchronized video, timestamps, logs, packet metrics, and automated state comparisons;
2. an exhaustive gameplay synchronization matrix covering tools, duplicants, chores, buildings, world cells, conduits, automation, resources, plants/critters, pause/speed, save/load, reconnect, and hard sync;
3. latency, jitter, packet-loss, bandwidth, host-frame-time, client-frame-time, allocation, and GC measurements under representative 2-4 player colonies;
4. fixes for player-visible divergence, stale viewport state, jerky movement/animation, delayed actions, dropped unreliable updates, duplicate execution, and unnecessary hard syncs;
5. licensed ONI U59 Managed references and compatibility migration from declared target `U57-700386` to current `U59-740622`;
6. reproducible remote restore/build/package commands plus packet-registry and serialization compatibility checks;
7. host/client log collection and a release manifest containing Git commit, upstream base, Mod version, protocol version, ONI build, artifact SHA-256, tested DLC configuration, and measured multiplayer performance.

Authentication, authorization, anti-cheat, adversarial-client security, per-player item ownership, and player asset isolation are not maintenance priorities for this private cooperative game.
