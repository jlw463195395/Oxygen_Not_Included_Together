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
- ONI Managed DLLs are not yet present on 8ka.
- No build or test has been run at this baseline because the licensed game references are missing.

The ONI files must be supplied from a legitimately owned installation without checking them into Git. Place them in a controlled, untracked location on 8ka and point `Directory.Build.props.user` to that location.

## Current test reality

The repository has in-game/debug test infrastructure under:

- `ONI_Together/DebugTools/UnitTests/`
- `ONI_Together/Tests/`

These are not equivalent to a mature standalone CI test suite. The only GitHub workflow currently present is a disabled API publishing workflow; it is not an active full Mod build/test gate.

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

## First maintenance priorities

Before broad feature development, establish on 8ka:

1. licensed ONI Managed references for the exact supported game build;
2. reproducible `Directory.Build.props.user` generation outside Git;
3. clean restore/build/package command;
4. a packet registry and serialization compatibility gate;
5. a two-instance host/client smoke harness or documented manual equivalent;
6. log collection for host and client;
7. a release manifest containing Git commit, upstream base, Mod version, protocol version, ONI build, artifact SHA-256, and tested DLC configuration.
