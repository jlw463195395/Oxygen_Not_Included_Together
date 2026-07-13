# 实时同步正确性静态审计

审计日期：2026-07-13

代码基线：`9a7aca573f22e02bae6bd115abdc034cc08ab1f3`；其后的 `fe6b2ef9abe7d0b44bbc827e90e5010500a3949e` 仅修改维护文档，Mod 源码未变。

范围：仅关注真实联机操作、实时性、同步正确性和性能；身份认证、权限、反作弊及玩家资产隔离不在范围内。

状态：只读静态分析，尚未经过 U59 编译或双实例运行验证。

---

## 审计结论

只读审计了当前 `liweijin` 分支：

- HEAD：`9a7aca573f22e02bae6bd115abdc034cc08ab1f3`
- 工作树最终为干净状态。
- 未执行 build、test、游戏启动或运行时验证。
- 未创建、修改任何文件。
- 按要求忽略了身份认证、资产归属、反作弊及对抗性安全。

静态审计确认：当前实现存在多条会造成**永久主客分歧、状态倒退、明显跳变、Hard Sync 卡住或断线后无法恢复**的路径。最优先应先修 Hard Sync 生命周期和无版本的 unreliable 状态同步，再修客户端自愈逻辑。

---

# 具体问题

## P0：应首先修复

### 1. Hard Sync 不是原子快照，传输期间仍可执行并丢失玩法操作

- **位置**
  - `Misc/World/GameServerHardSync.cs`
  - `GameServerHardSync.PerformHardSync`
  - `GameServerHardSync.HardSyncCoroutine`
  - `Networking/Packets/World/SaveFileRequestPacket.cs`
  - `SaveFileRequestPacket.SendSaveFileToAll`
- **触发条件**
  - 主机开始 Hard Sync 后，客户端或主机继续执行建造、工具、研究、侧屏设置等操作。
  - 多个客户端同时 Hard Sync。
- **根因**
  - Hard Sync 只暂停游戏速度，没有建立“同步 epoch/事务边界”，也没有冻结输入或停止处理改变世界的网络包。
  - 每个客户端分别调用 `GetWorldSave()`，不是一次快照复用于所有客户端。
  - `hardSyncInProgress` 按估算传输时间清除，而不是按客户端实际下载、加载、重连和 ACK 完成。
  - `HardSyncCompletePacket` 没有任何发送点。
- **玩家可见影响**
  - 快照生成后发生的建造、取消、研究或配置操作，会在客户端加载快照时消失。
  - 不同客户端可能收到不同时间点的存档。
  - UI 显示 Hard Sync 已结束，但客户端仍在下载或加载。
  - 随后只能再次 Hard Sync 才能恢复一致。
- **严重度**：**阻断 / P0**
- **建议**
  1. 一次生成带 `syncEpoch` 的不可变快照并复用于全部客户端。
  2. Hard Sync 期间暂停世界写操作，或把快照后的操作写入有序增量日志，在加载后重放。
  3. 每个客户端必须回报 `downloaded → loaded → reconnected → appliedEpoch`。
  4. 只有全部有效客户端确认后才能结束 Hard Sync。

---

### 2. Save transfer 没有唯一传输代次，重传可把两份存档拼在一起

- **位置**
  - `Networking/Packets/World/SaveFileRequestPacket.cs`
  - `SaveFileRequestPacket.StreamChunks`
  - `Networking/SaveFileTransferManager.cs`
  - `SaveFileTransferManager.StartTransfer`
  - `Misc/World/SaveChunkAssembler.cs`
  - `SaveChunkAssembler.ReceiveChunk`
- **触发条件**
  - 同一客户端对同一世界重试下载。
  - 丢块、损坏后发起完整重传。
  - 前一次传输仍有延迟块到达时启动下一次 Hard Sync。
- **根因**
  - `transferId` 只是由文件名生成。
  - 服务端活动传输 key 是 `clientID + transferId`，新传输直接覆盖旧传输状态。
  - 客户端组装器只按 `FileName` 分组，完全忽略 `TransferId`。
  - 旧传输 coroutine 仍会继续发送并标记新传输的 ACK 状态。
- **玩家可见影响**
  - 两个时间点的存档块混合，最终文件可能无法加载。
  - 更危险的是文件格式仍可解析，但世界状态来自两个不同快照。
  - 下载反复重启或 Hard Sync 永远无法完成。
- **严重度**：**阻断 / P0**
- **建议**
  - 每次传输使用随机且单调的 `transferId/syncEpoch`。
  - 文件名、客户端、epoch 必须共同组成组装 key。
  - 启动新 epoch 时显式取消旧 sender coroutine、ACK 状态和客户端组装状态。
  - 完成前校验整份文件的长度和哈希。

---

### 3. Steam 客户端掉线后仍留在 Ready 集合，Hard Sync 可永久等待

- **位置**
  - `Networking/Transport/Steamworks/SteamworksServer.cs`
  - `SteamworksServer.OnClientClosed`
  - `Networking/ReadyManager.cs`
  - `ReadyManager.IsEveryoneReady`
  - `ReadyManager.RefreshReadyState`
- **触发条件**
  - Hard Sync 期间某个 Steam 客户端超时、崩溃或主动离开。
- **根因**
  - `OnClientClosed` 只把 `player.Connection` 设为 `null`，没有从 `ConnectedPlayers` 移除玩家。
  - `IsEveryoneReady()` 不检查连接是否仍有效，仍将该玩家的 `Unready` 状态纳入判断。
- **玩家可见影响**
  - 主机一直显示等待该玩家。
  - 其他已完成加载的玩家也无法结束同步界面。
- **严重度**：**阻断 / P0**
- **建议**
  - Ready barrier 只统计当前 epoch 中仍有效的连接。
  - 断线时从 barrier 移除，或明确将 epoch 标记失败并允许主机重试/跳过。
  - Steam 与 LAN 使用统一连接生命周期模型。

---

### 4. 普通断线没有实际重连能力

- **位置**
  - `Networking/GameClient.cs`
  - `GameClient.ShowMessageAndReturnToTitle`
  - `GameClient.AutoReconnectCoroutine`
- **触发条件**
  - 游戏中发生瞬时断网、主机 Wi‑Fi 抖动或 transport timeout。
- **根因**
  - 自动重连调用整段被注释。
  - 当前流程等待约 3 秒后强制退出世界并返回前端。
  - `ReconnectFromCache` 实际只用于加载主机存档后的预期重连。
- **玩家可见影响**
  - 短暂网络波动直接踢出本局。
  - 无法在当前世界内恢复，只能重新加入并重新下载存档。
- **严重度**：**阻断 / P0**
- **建议**
  - 实现有界退避重连。
  - 重连后先请求主机当前 epoch 和增量序号；无法补齐时再自动 Hard Sync。
  - 重连期间冻结客户端写操作和陈旧 UI 状态，而不是直接销毁当前局。

---

## P1：会造成永久或长时间分歧

### 5. Unreliable 状态包在发送前推进 shadow，丢掉最后一包后不会自愈

- **位置**
  - `Networking/Components/WorldStateSyncer.cs`
  - `WorldStateSyncer.ScanArea`
  - `Misc/World/WorldUpdateBatcher.cs`
  - `WorldUpdateBatcher.Flush`
  - `Networking/Packets/World/WorldUpdatePacket.cs`
- **触发条件**
  - 气液格变化的最后一个数据报丢失。
  - 数据报乱序，旧状态晚于新状态到达。
- **根因**
  - host 在 unreliable send 成功与否未知时就更新 `_shadowElements/_shadowMass`。
  - `WorldUpdatePacket` 没有 epoch、tick 或 per-cell revision。
  - 客户端无条件应用收到的更新，旧包能覆盖新包。
  - 不再变化的格子不会重新发送。
- **玩家可见影响**
  - 客户端某格气体、液体、质量、温度永久错误。
  - 液面或气体显示突然倒退。
  - 门、挖掘、落水等依赖格状态的画面与主机不一致。
- **严重度**：**严重 / P1**
- **建议**
  - 每格或每批增加主机 simulation revision。
  - 客户端拒绝旧 revision。
  - 定期发送当前 viewport 的权威全状态，而不只发送 delta。
  - shadow 记录“最后确认/最后周期发送状态”，不能把调用 send 等同于交付成功。

---

### 6. 管道“变空”状态丢包后明确无法自愈

- **位置**
  - `Networking/Components/ConduitFlowSyncer.cs`
  - `ConduitFlowSyncer.MaybeQueueCell`
  - `ConduitFlowSyncer.SyncConduits`
- **触发条件**
  - 管道从有内容变为空时的 unreliable 包丢失。
- **根因**
  - 发送空状态时 shadow 已被改为空。
  - 后续 force refresh 对“当前为空且 shadow 也为空”的格子直接跳过。
  - 因而 force refresh 无法重新断言空状态。
- **玩家可见影响**
  - 客户端管道中长期显示幽灵气体/液体。
  - 阀门关闭、储液库排空后，客户端仍显示旧内容。
- **严重度**：**严重 / P1**
- **建议**
  - force refresh 必须包含可见管道的空状态。
  - 加 per-cell revision，拒绝乱序倒退。
  - 或在客户端进入 viewport 时请求完整管网快照。

---

### 7. 自动化和结构状态的客户端 stale-repair 根本不会运行

- **位置**
  - `Networking/MultiplayerSession.cs`
  - `MultiplayerSession.SessionHasPlayers`
  - `Networking/Components/LogicStateSyncer.cs`
  - `LogicStateSyncer.Update`
  - `Networking/Components/StructureStateSyncers/StructureSyncerBase.cs`
  - `StructureSyncerBase.Update`
- **触发条件**
  - 自动化设备、传感器、电池、发电机或存储在客户端 viewport 外变化。
  - 对应 unreliable 变化包丢失。
- **根因**
  - 客户端的 `ConnectedPlayers` 按注释只保存 host，通常数量为 1。
  - `SessionHasPlayers` 要求 `ConnectedPlayers.Count > 1`。
  - 因此 `LogicStateSyncer.ClientUpdate` 和 `StructureSyncerBase.ClientUpdate` 会在入口直接返回。
  - host 已更新 `lastValue`，客户端重新看到设备时 host 也不会因“未变化”重发。
- **玩家可见影响**
  - 自动化闸门、计数器、内存开关、计时器停留在旧值。
  - 电池电量、发电机状态、库存显示永久陈旧。
  - 重新移动镜头也不能修复。
- **严重度**：**严重 / P1**
- **建议**
  - 将“本地是否存在其他玩家”与“客户端是否已连接 host”拆成两个条件。
  - 客户端 stale request 不应依赖 `ConnectedPlayers.Count > 1`。
  - host 对进入 viewport 的实体主动发一次可靠完整状态。

---

### 8. Steam 大型 unreliable 快照没有分片，发送失败被静默忽略

- **位置**
  - `Networking/Transport/Steamworks/SteamworksPacketSender.cs`
  - `SteamworksPacketSender.SendPacket`
  - `Networking/Packets/Architecture/PacketSender.cs`
  - `Networking/Components/PlantGrowthSyncer.cs`
  - `WorldStateSyncer.SyncDigging`
  - `WorldStateSyncer.SyncChores`
- **触发条件**
  - 植物较多、大面积挖掘、大量拖地任务，或光标携带较长 utility path。
- **根因**
  - 代码定义 `MAX_PACKET_SIZE_UNRELIABLE = 1024`，但 Steam sender 不做分片。
  - Plant、Digging、Chore 等完整列表没有按该上限切包。
  - `SendMessageToConnection` 返回失败时仅返回 `false`，多数调用点忽略返回值。
- **玩家可见影响**
  - 植物完整同步在中后期殖民地可能整包消失。
  - 大面积挖掘/拖地标记无法修复。
  - 画长管线时其他玩家的光标或路径预览冻结。
- **严重度**：**严重 / P1**
- **建议**
  - 所有可变长包统一经过 transport-independent 分片层。
  - 分片包含 epoch、message ID、总长度、分片数、过期时间。
  - 发送失败必须记录并安排重试或降级为可靠传输。

---

### 9. LAN 分片只按本地 sequence ID 聚合，多客户端会串包

- **位置**
  - `Networking/Transport/Riptide/RiptidePacketSender.cs`
  - `RiptidePacketSender.SendChunked`
  - `Networking/Packets/Core/ChunkedPacket.cs`
  - `ChunkedPacket.OnDispatched`
- **触发条件**
  - 两个客户端同时向 host 发送超过 1000 字节的包。
  - unreliable 分片中任意一片丢失。
- **根因**
  - `_pendingChunks` 只按 `SequenceId` 建索引，不包含发送连接/玩家或 session epoch。
  - 每个进程的 `_nextSequenceId` 都从 0 开始。
  - 没有超时清理、长度校验或丢片重试。
- **玩家可见影响**
  - 不同玩家的分片可能合并，包解析失败或执行错误操作。
  - 丢一片后整条消息永远不执行，残留组装状态持续积累。
- **严重度**：**严重 / P1**
- **建议**
  - key 至少使用 `(connectionId, sessionEpoch, sequenceId)`。
  - 增加超时、总长度、哈希和 ACK/NACK。
  - 对关键工具命令使用完整消息级可靠性。

---

### 10. 客户端工具采用“各端先执行、host 无条件转发”，并发冲突没有权威结果

- **位置**
  - `Networking/Packets/Core/HostBroadcastPacket.cs`
  - `HostBroadcastPacket.OnDispatched`
  - `Networking/Packets/Tools/Build/BuildPacket.cs`
  - `BuildPacket.OnDispatched`
  - `Patches/ToolPatches/Build/BuildToolPatch.cs`
- **触发条件**
  - 两个玩家几乎同时在同格建造不同建筑。
  - 一人取消/拆除时另一人建造或改优先级。
  - host 应用操作失败，但来源客户端已经乐观成功。
- **根因**
  - 来源客户端先本地执行。
  - host 调用 `innerPacket.OnDispatched()` 后，无论成功与否都转发给其他客户端。
  - 命令没有 `commandId`、前置 revision、接受/拒绝结果或权威回执。
  - `BuildPacket` 即使 `builtItem == null` 仍继续记录并结束。
- **玩家可见影响**
  - 每个玩家可能保留自己的建造蓝图。
  - host 上失败的操作仍在其他客户端执行。
  - 建造、取消、拆除会出现重复、缺失或互相覆盖。
- **严重度**：**严重 / P1**
- **建议**
  - 客户端只发送 intent。
  - host 在指定 world revision 上验证并执行。
  - host 广播带 `commandId/result/newRevision` 的权威结果。
  - 客户端乐观预测必须能回滚。

---

### 11. 建筑周期校正不会删除 host 已不存在的建筑，补建信息也不完整

- **位置**
  - `Networking/Components/BuildingSyncer.cs`
  - `BuildingSyncer.Reconcile`
  - `BuildingSyncer.SpawnBuilding`
  - `Networking/Packets/Tools/Build/BuildCompletePacket.cs`
- **触发条件**
  - 客户端漏掉拆除/破坏事件。
  - 客户端漏掉最初 BuildPacket，但收到 BuildComplete。
  - 30 秒建筑校正尝试补救。
- **根因**
  - `Reconcile` 明确对“remote 没有该 cell+layer”的本地建筑不做任何操作，所以幽灵建筑不会被删。
  - fallback 补建只传 `Cell + PrefabName`，使用中立朝向、默认材料和 facade。
  - `BuildCompletePacket` 只在目标层已有 `existing` 时真正构建；完全缺失时不会补建。
- **玩家可见影响**
  - 客户端保留已拆建筑。
  - 漏掉完工后可能等到周期校正才出现。
  - 补出的建筑朝向、材质、皮肤或内部状态错误。
- **严重度**：**严重 / P1**
- **建议**
  - 建筑全量快照应包含唯一 ID、存在性 tombstone、朝向、材料、温度、facade、层和状态 revision。
  - `BuildCompletePacket` 在不存在 ghost 时也必须可幂等创建。
  - 删除必须通过权威 tombstone 修复。

---

## P2：明显跳变、延迟或局部错误

### 12. Duplicant 位置 stale 检测混用了两套时钟，收到一次包后不再主动补救

- **位置**
  - `Networking/Components/EntityPositionHandler.cs`
  - `EntityPositionHandler.TryRequestEntityPositionIfVisible`
  - `EntityPositionHandler.SendPositionUpdate`
- **触发条件**
  - 收到过至少一次位置包，随后持续丢包或 host viewport 状态丢失。
- **根因**
  - `serverTimestamp` 是 Unix 毫秒。
  - stale 检查用 `Time.unscaledTime - serverTimestamp/1000f`。
  - 结果长期为巨大负数，所以永远不会超过 stale threshold。
- **玩家可见影响**
  - Duplicant/生物停在旧位置。
  - 下一次成功更新若误差超过 1.5 格会直接瞬移。
- **严重度**：**高 / P2**
- **建议**
  - 分开保存 `serverRevision` 和本地 `lastReceiveUnscaledTime`。
  - stale 检测只使用本地接收时间。
  - 加插值缓冲和受限外推，避免包恢复时硬跳。

---

### 13. 动画对账使用发送时 elapsed time，延迟较高时会周期性倒退

- **位置**
  - `Networking/Components/AnimReconciliationHelper.cs`
  - `AnimReconciliationHelper.Reconcile`
  - `Networking/Packets/DuplicantActions/DuplicantStatePacket.cs`
  - `Networking/Packets/Animation/AnimSyncPacket.cs`
- **触发条件**
  - RTT/排队延迟超过约 150 ms，或 unreliable 包乱序。
- **根因**
  - 包内只有发送时的 `ElapsedTime`，没有 host tick 或发送时间补偿。
  - 本地与该旧 elapsed 差超过 0.15 秒就直接 `SetElapsedTime`。
  - 包没有 revision，旧包晚到也会被应用。
- **玩家可见影响**
  - 工作、挖掘、行走等动画周期性回跳。
  - 动画与位置、工作进度对不上。
- **严重度**：**高 / P2**
- **建议**
  - 使用单调 host tick，并按估算单向延迟推进目标 elapsed。
  - 对 loop 动画按周期求最短相位差。
  - 拒绝旧 revision，采用缓慢校正而不是频繁硬设时间。

---

### 14. 植物全量快照无版本，可让新植物暂时消失或成熟度倒退

- **位置**
  - `Networking/Components/PlantGrowthSyncer.cs`
  - `PlantGrowthSyncer.SendPlantStates`
  - `PlantGrowthSyncer.OnPlantStateReceived`
  - `Networking/Packets/World/PlantGrowthStatePacket.cs`
- **触发条件**
  - 旧的 unreliable 全量快照晚于可靠的 spawn/remove lifecycle 包到达。
- **根因**
  - 全量快照无 epoch/revision。
  - 客户端把快照中不存在的所有本地植物当 phantom 删除。
  - lifecycle 和 snapshot 使用不同可靠性，跨通道没有统一顺序。
- **玩家可见影响**
  - 新种植物突然消失，下一次快照再出现。
  - 成熟度和枯萎状态倒退、跳变。
- **严重度**：**高 / P2**
- **建议**
  - lifecycle 与 snapshot 共用同一 host revision。
  - 只允许新快照删除 revision 更旧的实体。
  - 大型植物快照必须分页/分片后原子提交。

---

### 15. 研究进度把不同研究类型压成一个总百分比，再按比例写回，信息不可逆

- **位置**
  - `Networking/Components/WorldStateSyncer.cs`
  - `WorldStateSyncer.SyncResearchProgress`
  - `Networking/Packets/World/ResearchProgressPacket.cs`
  - `ResearchProgressPacket.OnDispatched`
- **触发条件**
  - 科技需要多种研究点，且各类型完成比例不同。
- **根因**
  - host 合并为单一 `Progress`。
  - client 对每种研究类型写入 `cost * Progress`。
  - 例如一种已 100%、另一种 0%，客户端会显示为两种各 50%。
- **玩家可见影响**
  - 研究进度条和各研究站贡献错误。
  - 完成前后可能明显跳变。
- **严重度**：**中 / P2**
- **建议**
  - 按 research type 发送实际 points/cost。
  - 增加 tech revision，拒绝旧进度。
  - 当前完整研究状态函数 `SyncResearch()` 没有调用点，也应接入周期自愈。

---

### 16. LAN TCP 完成回调通过非线程安全 List 排队，且主线程执行器固定每 0.5 秒只处理一个事件

- **位置**
  - `Networking/Transfer/TcpFileTransferClient.cs`
  - `TcpFileTransferClient.DownloadThread`
  - `Networking/Components/MainThreadExecutor.cs`
  - `MainThreadExecutor.QueueEvent`
  - `MainThreadExecutor.Execute`
- **触发条件**
  - TCP 下载线程与 Unity 主线程同时增删事件。
  - 多次下载、错误回调或大量其他主线程任务同时排队。
- **根因**
  - 后台线程直接向 `List<Action>` 添加，主线程同时读取和 `RemoveAt`。
  - 无锁、无 concurrent queue。
  - 每个事件后强制等待 0.5 秒。
- **玩家可见影响**
  - 下载完成后仍长时间停留在进度界面。
  - 极端情况下回调丢失/异常，无法开始加载，表现为 Hard Sync 卡住。
- **严重度**：**中 / P2**
- **建议**
  - 使用 `ConcurrentQueue<Action>`。
  - 每帧在时间预算内批量 drain。
  - 完成/错误回调应高于进度 UI 更新优先级。

---

# 建议修复顺序

1. **建立统一的 session epoch、host tick、command ID 和 per-state revision。**
2. **重构 Hard Sync 为单快照、客户端 ACK 驱动的事务；修复 Steam zombie ready。**
3. **实现真实断线重连和增量补齐/自动 Hard Sync。**
4. **修复 save transfer generation、取消旧传输、整文件哈希验证。**
5. **修复 world cells、conduit、logic、structure 的 unreliable 丢包与乱序自愈。**
6. **统一 Steam/LAN 分片层，解决超大包和 Riptide sender-key 冲突。**
7. **将建造及工具操作改为 host 接受/拒绝的权威命令流。**
8. **完善建筑存在性 tombstone 和完整建筑状态。**
9. **最后处理 Duplicant 插值/动画相位、植物 revision、研究分类型进度及 TCP dispatcher。**

## 交付状态

- **完成内容**：对网络传输、Hard Sync、存档加载、重连、光标/工具、Duplicant、建筑、气液、管道、自动化、植物和研究进行了只读静态审计。
- **文件变更**：无。
- **执行情况**：按要求未 build、未 test。
- **限制**：以上为源码可证实的问题路径；实际触发频率仍需后续在 host/client 运行环境中通过丢包、乱序、并发操作和 Hard Sync 故障注入验证。
