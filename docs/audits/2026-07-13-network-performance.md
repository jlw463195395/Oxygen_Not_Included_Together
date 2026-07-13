# 联机性能静态审计

审计日期：2026-07-13

代码基线：`9a7aca573f22e02bae6bd115abdc034cc08ab1f3`；其后的 `fe6b2ef9abe7d0b44bbc827e90e5010500a3949e` 仅修改维护文档，Mod 源码未变。

范围：仅关注真实联机操作、实时性、同步正确性和性能；身份认证、权限、反作弊及玩家资产隔离不在范围内。

状态：只读静态分析，尚未经过 U59 编译或双实例运行验证。

---

## 审计结论

已对 `/home/jlw/projects/Oxygen_Not_Included_Together` 当前 `liweijin` 分支提交 `9a7aca5` 做只读静态审计。基线提交 `9a7aca573f22e02bae6bd115abdc034cc08ab1f3` 即当前 HEAD。

未执行 build/test，未修改或创建文件。以下按优先级列出主要发现。

### P0：联机卡顿、恢复失败或内存持续增长

1. **LAN 大包分片在丢包后永久残留且永远无法恢复**
   - `RiptidePacketSender.cs:26-29,66-89`：超过 1000 B 的所有包拆成约 980 B 的 `ChunkedPacket`。
   - `ChunkedPacket.cs:14,37-69`：接收端只保存在静态 `_pendingChunks`，没有超时、上限、发送者维度、NACK 或重传。
   - 原包如果是 `Unreliable`，子分片也全部不可靠；任一分片丢失，整包不派发，残缺数组永久留存。
   - 受影响的大包包括 5 秒全量植物、世界更新等。玩家表现为植物/世界状态偶发长期不刷新，并伴随内存缓慢增长。
   - **优化**：不可靠状态包必须在发送前按真实序列化大小拆成独立、可单独应用的 MTU 内记录批次；通用分片仅用于可靠流，并增加 `(connection, sequence)`、超时清理和严格内存上限。

2. **保存传输存在多层重复可靠、巨量复制和每帧突发**
   - 默认块为 256 KiB：`Configuration.cs:205-208`。
   - `SaveFileRequestPacket.cs:124-177` 每帧发送 2 块，即每帧最多 512 KiB；注释把“30 MB/s 理论值”当作优化目标，未根据队列时间/未确认字节做背压。
   - 每块至少经历：
     - `byte[256KiB]` 复制；
     - `SerializeSaveFileChunk()` 的 `MemoryStream.ToArray()`；
     - 外层 `SecureTransferPacket` 再序列化；
     - LAN 再拆约 **268 个** 980 B 分片。
   - 外层默认为可靠，Riptide 分片也可靠，同时应用层又逐块 ACK、5 秒重传：`SaveFileTransferManager.cs:132-149,163-195`，属于重复可靠机制。
   - `CheckForLostChunks()` 每帧执行：`GameServer.cs:82-84`；循环从 0 扫到 `HighestAckReceived+10`，并非注释声称的“最后 ACK 附近窗口”。
   - **玩家表现**：加入/硬同步时主机 GC 峰值、帧时间尖峰、可靠队列阻塞实时操作；低带宽链路出现重复重传和更慢恢复。
   - **优化**：LAN 优先只走已有 TCP 文件传输；UDP fallback 使用 16–32 KiB 流式窗口、累计 ACK/位图 ACK、RTT/RTO、明确在途字节上限。Steam 使用单一可靠层，不再套应用级 ACK；按 `m_usecQueueTime` 和未确认可靠字节逐帧调速。

3. **PacketTracker 长期持有完整包对象和大数组**
   - `PacketTracker.cs:34-40` 固定保留收发各 50,000 条，共 100,000 个包引用。
   - `TrackSent/TrackIncoming` 将 `IPacket` 本体放入环形缓冲：`138-155`。
   - 保存块、植物/建筑列表、压缩数据等对象图在覆盖前都不能 GC；一次存档传输可使整个存档块在收发两侧继续驻留。
   - **优化**：默认仅记录 type/id、大小、时间和计数；详细 payload 检视改为显式开启、低容量采样，并对 `byte[]`/列表只保留长度或截断副本。

4. **主线程执行器吞吐只有 2 个任务/秒，且线程不安全**
   - `MainThreadExecutor.cs:15,34-57` 使用普通 `List<Action>`，TCP 后台线程可直接 `Add`，没有锁。
   - 每次只执行索引 0，然后固定等待 0.5 秒并 `RemoveAt(0)`；后者还是 O(n) 搬移。每项都格式化时间并写日志。
   - TCP 下载进度及完成/错误回调都经这里排队：`TcpFileTransferClient.cs:86,104,113`。
   - **玩家表现**：下载进度和完成加载可能落后数秒至数十秒；并发添加时有数据竞争。
   - **优化**：改为 `ConcurrentQueue<Action>`，主线程每帧在 1–2 ms 时间预算内批量 drain；UI 进度按“最新值覆盖”而不是逐个排队。

---

### P1：主线程 CPU、GC 和网络队列

5. **所有网络反序列化和包应用都在帧内同步完成**
   - `NetworkingComponent.cs:31-57` 每帧直接轮询并应用网络消息。
   - Steam 主机单帧最多同步处理 128 条，客户端默认 16 条：`Configuration.cs:206,219`；`SteamworksServer.cs:129-143`、`SteamworksClient.cs:142-169`。
   - LAN 客户端 `while TryDequeue` 无单帧上限：`RiptideClient.cs:167-191`；突发时会把整个积压在一帧处理完。
   - `PacketHandler.cs:45-68` 同步完成创建、反序列化、`OnDispatched()` 和追踪；世界包可在其中调用大量 `SimMessages.ModifyCell`，建筑/植物包可全表 reconciliation。
   - **优化**：接收与解析分层；主线程按时间预算/优先级消费。游标、位置、工具命令优先；全量 reconcile 分帧；同实体陈旧状态在入队前合并。

6. **Steam 每帧产生固定数组和逐消息 byte[]**
   - `SteamworksServer.cs:130,137`、`SteamworksClient.cs:143,155`：每帧新建 `IntPtr[]`，每消息新建 `byte[]` 后 `Marshal.Copy`。
   - `Configuration.GetHostProperty/GetClientProperty` 又在每帧轮询路径通过反射取属性：`Configuration.cs:137-169`。
   - Steam 发送端每包执行 `MemoryStream.ToArray()`、`AllocHGlobal`、`Marshal.Copy`、`FreeHGlobal`：`PacketSender.cs:56-67`、`SteamworksPacketSender.cs:26-55`。
   - 广播给 3 个客户端时同一逻辑包会完整序列化并复制 3 次。
   - **优化**：缓存 poll 限额；复用 `IntPtr[]`；包先序列化一次，再向多个连接发送同一缓冲；引入池化 writer/buffer，避免每包非托管分配。

7. **通用包创建在每条入站消息上使用反射**
   - `PacketRegistry.cs:82-88` 每条消息调用 `Activator.CreateInstance(packetType)`。
   - Bulk 内每个子包再次调用：`BulkSenderPacket.cs:71-79`。
   - **优化**：注册时编译并缓存 `Func<IPacket>` 构造委托。对高频定长包可使用值读取器或受控对象池；反射只留在启动注册阶段。

8. **可选 packet queue 是错误优化：无界、陈旧、最低仍为 500 pps**
   - `TransportPacketSender.cs:9-27` 队列无上限；丢弃旧包的逻辑被注释。
   - `Flush()` 按 `int(MaxPacketsPerSecond * deltaTime)` 取整，每连接独立配额；低帧率时积压和一帧发送量同时增加：`35-54`。
   - 配置被限制在 500–1000 pps：`Configuration.cs:89-95`，无法提供真正低带宽模式。
   - 源码自己承认客户端会“落后并追赶”：`TransportPacketSender.cs:14-24`。
   - **优化**：不要用 FIFO 限制状态同步。按类别设计：
     - 状态包按实体/键只保留最新值；
     - 命令包保持顺序且有上限；
     - token bucket 以字节而非包数限流；
     - 超载时丢弃陈旧不可靠状态，而不是让客户端回放历史。

9. **LAN 重传参数会扩大可靠队列和头部阻塞**
   - 客户端和服务端都禁用 poor-quality disconnect，并将发送尝试提高到 30：`RiptideClient.cs:106-110`、`RiptideServer.cs:140-143`。
   - 对 2–4 人家庭局，短暂抖动应快速以最新状态自愈；30 次可靠重试会让旧存档块/旧状态长期占据队列。
   - **优化**：命令和控制包可靠；周期状态不可靠且可覆盖；恢复使用显式快照。恢复默认 Riptide 的合理重试范围，并以可靠队列年龄触发降级/重连。

---

### P1：每帧扫描和错误的 viewport 优化

10. **实体位置组件对每个复制人和动画生物逐帧运行**
   - 注入点：`DuplicantPatch.cs:30-31`、`EntityTemplatesPatch.cs:33-36`。
   - `EntityPositionHandler.Update()` 每个实体每帧执行；主机移动时最快约每 16 ms 发一次，即上限约 **62.5 Hz/实体**：`EntityPositionHandler.cs:42-65,84-118`。
   - 位置包应用层约 31 B；20 个移动实体 × 3 客户端 × 60 Hz 已约 **111,600 B/s**，尚未含 transport/Steam 头和序列化成本。大量 critter 时线性放大。
   - 客户端每实体每帧还计算本地 viewport：`67-81`，其中访问 `Camera.main` 并做两次 `ViewportToWorldPoint`：`WorldStateSyncer.cs:151-171`。
   - **优化**：中央位置同步器维护活跃移动实体集合；10–20 Hz 快照，位置量化，静止心跳 1–2 Hz；每帧只计算一次共享 viewport；按客户端 interest set 发送。

11. **客户端位置补包的 stale 判断使用了两个不兼容时钟**
   - 包内 `Timestamp` 是 Unix 毫秒：`EntityPositionHandler.cs:112`。
   - 客户端却以 `Time.unscaledTime - serverTimestamp/1000f` 判断是否超过 2 秒：`73-78`。
   - 收到第一包后右侧约为 17 亿秒，差值长期为巨大负数，永远不会 stale；后续丢包或离开/进入 viewport 时不会请求恢复。
   - **玩家表现**：生物永久停在旧位置，直到别的机制纠正或重载。
   - **优化**：保存“本地收到包的 unscaledTime”，用它判断陈旧；协议时间戳只用于序号/排序，最好改为每实体递增 sequence。

12. **世界气液扫描会重复扫描所有玩家 viewport**
   - `WorldStateSyncer.cs:667-758` 每 1.5 秒扫描主机 viewport、每个客户端 viewport、pinned area 和一个 32×32 后台块。
   - 标准 256×384 世界为 98,304 cells；影子数组约 576 KiB。viewport 重叠时仍逐矩形重复遍历，虽然第二遍通常不会重复发包。
   - 自适应逻辑在低 FPS 时把同步间隔乘 1.5–3：`764-779`，会把“主机掉帧”直接转化为客户端更严重的视觉陈旧，是错误的反馈优化。
   - **优化**：把 viewport 合并为 dirty chunk 位图；每 tick 扫固定 cell 预算；优先事件/模拟脏标记，后台只做低频校验。低 FPS 时减少后台工作，但不要降低当前可见区域的关键状态频率。

13. **ConduitFlowSyncer 每 1.5 秒全地图扫一次**
   - `ConduitFlowSyncer.cs:164-195` 对 `Grid.CellCount` 全扫描，再判断是否有管道。
   - 256×384 世界约 **65,536 cell checks/s**；初始化还扫描全图两遍：`137-150,210-220`。
   - 7 个 shadow 数组约 **2.63 MiB**。
   - 更严重的是，它只用“是否有任意客户端看见该 cell”作为发送条件，但随后 `SendToAllClients`：`174-200`。不同玩家分散观看时，每人仍收到所有人的可见管道数据。
   - **优化**：维护管道 cell 索引/由 conduit 事件驱动 dirty set；按 recipient 分桶批量发送，不能 `visible-to-any` 后广播所有人。

14. **多个 viewport 优化只检查“任意人可见”，然后广播所有客户端**
   - `StatusBroadcaster.cs:66-89` 同样如此。
   - `WorldUpdateBatcher.cs:93,110` 将所有 viewport 的世界变化混在一起广播全部客户端。
   - `PlantGrowthSyncer.cs:143-164`、`BuildingSyncer.cs:57-87` 完全不裁剪，全殖民地快照广播。
   - 实际实现 `IViewportCullable` 的只有 `EntityPositionPacket`；接口覆盖远低于设计意图。
   - **优化**：interest management 以每客户端 chunk subscription 为核心；同一 tick 每个 chunk 序列化一次，再发送给订阅该 chunk 的连接。进入 viewport 时发送 chunk snapshot，离开后停止增量。

15. **动画“分片扫描”仍然是全量 O(N) 遍历**
   - `AnimSyncCoordinator.RunTick()` 每 200 ms 先复制整个 `TrackedSyncers` 到新 List：`81-92,141`。
   - 随后循环整个 List，仅用 `i % 5` 跳过 4/5：`147-156`；因此仍然每 200 ms 遍历 N，每秒检查 5N 次并分配 5 个 List。
   - 每个 syncer 每秒构造一次 `AnimSyncPacket` 后才判断可见及发送间隔：`AnimStateSyncer.cs:60-99`、`AnimSyncCoordinator.cs:208-249`。
   - **优化**：真正维护 5 个稳定 shard，tick 只遍历一个；先用缓存 cell/viewport 判断，再构建 snapshot；进入可见集或动画事件变化时标脏。

16. **LogicStateSyncer 每秒对所有逻辑建筑制造 Dictionary 和大量 GetComponent**
   - `LogicStateSyncer.cs:80-129` 每秒遍历 `_tracked`。
   - `SampleBuilding()` 每次先 `new Dictionary<string, Variant>()`，再依次尝试十余种 `GetComponent`：`238-333`。
   - 1000 个逻辑建筑意味着至少约 1000 个字典/秒及大量组件查找，即便状态完全没变化。
   - **优化**：注册时确定具体 adapter、缓存组件引用；optional state 使用小型 struct/位掩码；由逻辑事件标记 dirty，周期仅抽样校验少量 shard。

17. **每建筑 `StructureSyncerBase.Update()` 导致大量 MonoBehaviour 调度**
   - `StructureSyncerBase.cs:44-68` 每个组件每帧进入 Update，主机每 0.5 秒采样；客户端每个可见组件还做 stale request。
   - 每个组件持有独立 `HashSet`：`23`。
   - **优化**：集中式注册表 + 分片 tick；共享 recipient scratch；变化事件驱动，低频 heartbeat。

---

### P2：带宽、聚合和序列化设计

18. **Bulk 聚合本身制造多份 byte[]，并统一强制可靠立即发送**
   - 每个子包先独立 `MemoryStream.ToArray()`：`PacketSender.cs:159,187-195`。
   - 再由 `BulkSenderPacket` 写长度和数据：`BulkSenderPacket.cs:30-42`。
   - flush 一律 `ReliableImmediate`：`PacketSender.cs:120`，即使内层状态可由下一批自愈。
   - 每 100 ms `Keys.ToList()`：`PacketSender.cs:81-98`，产生临时列表。
   - LAN 容量只累计子 payload 和一个 4 B header，漏算每子包 4 B 长度、count、外层 packet id：`167-180`。因此“512 B 限制”实际可越界并再次触发分片。
   - Steam 路径没有按真实 MTU 的 byte cap；最大 500 条的包可能很大。
   - **优化**：直接向可增长/池化 buffer 写入，避免子 `byte[]`；按真实 `writer.Position` 切包；聚合保留原 QoS；对可覆盖事件按 NetId/cell 去重。

19. **WorldUpdateBatcher 的 5.38 B/更新估算不可靠**
   - 单条原始字段实际为 **19 B**：`WorldUpdatePacket.cs:31-40`。
   - `WorldUpdateBatcher.cs:68-101` 用固定“测得压缩后 5.38 B”预测批大小，但压缩比取决于数据；包头也低估。
   - 每个小包单独 Deflate：`WorldUpdatePacket.cs:26-46`，会造成 CPU/GC，且小包压缩收益有限。
   - `SyncGasLiquid()` 每 1.5 秒立即 `Flush()`：`WorldStateSyncer.cs:753-754`，所以 `WorldUpdateBatcher.Update()` 的 10 秒定时器实际基本无效。
   - **优化**：先构建 MTU 内原始块或边写边测真实长度；使用 cell delta/varint、半精度或量化 mass/temp，通常比每小包 Deflate 更稳定；移除双重 flush 机制。

20. **全量建筑快照可靠广播会造成周期性可靠队列尖峰**
   - 每 30 秒遍历所有 `BuildingCompletes`，包含重复 prefab 字符串：`BuildingSyncer.cs:15,57-86`。
   - 客户端收到后构造多个字典/集合并两次扫描本地建筑：`99-182`。
   - **玩家表现**：大型基地每 30 秒出现主机发送尖峰和客户端 reconciliation 帧尖峰。
   - **优化**：建筑创建/删除事件可靠增量；低频 snapshot 按 chunk、字符串表/Prefab hash，按帧预算校验。

21. **植物每 5 秒全量不可靠包，既浪费带宽又不可靠**
   - `PlantGrowthSyncer.cs:17,143-164` 全量发送所有植物，估算 48 B/株。
   - 1000 株约 48 KB/5 秒/客户端，且 LAN 会拆成约 50 个不可靠分片；任意分片丢失则整批不应用。
   - 客户端每次建立 3 个字典、3 个 HashSet、列表并遍历所有植物：`195-268`。
   - **优化**：生命周期可靠事件；成熟度/萎蔫变化按 viewport 的 dirty delta；进入 viewport 时发 chunk snapshot；数据包分批可独立应用。

22. **VitalStats 每复制人每秒发送完整字符串字典**
   - `VitalStatsSyncer.cs:35-56` 每 dupe 每秒发送一次。
   - `VitalStatsPacket.cs:29-32,43-49` 每次构造 `Dictionary<string,float>` 并重复写 Amount ID 字符串。
   - 已修复旧的“每 Amount 重发一次”风暴，但仍可进一步压缩。
   - **优化**：启动时固定 Amount ID→小整数；使用定长/位图 delta；只发送变化值并保留较低频完整 heartbeat。

23. **游标包固定 10 Hz，工具路径每次全量重编码**
   - `CursorManager.cs:15,69-84` 每玩家 10 Hz，无静止降频。
   - 每次都检查建造有效性、编码 utility path：`115-145`。
   - 客户端包到达时停止并重启 coroutine：`PlayerCursorPacket.cs:136-143,166-185`；每个玩家每秒 10 个 coroutine 生命周期。
   - 基础应用层大小约 51 B，4 人经主机转发约百级消息/秒；utility path 可显著放大。
   - **优化**：位置 10–15 Hz 仅移动时发送，静止 1 Hz；viewport 仅变化时发送；工具/路径独立、按变化发送。接收端保留一个持续插值器，不反复启动 coroutine。

---

### 客户端插值问题

24. **实体插值不是基于时间的 snapshot interpolation**
   - `EntityPositionHandler.cs:125-152` 每帧 `Lerp(current, latest, 20*dt)`，没有快照缓冲、速度、发送间隔或网络抖动估计。
   - 结果是帧率相关的指数追赶；突发包只保留最后目标；大于 1.5 格直接瞬移。
   - `serverTimestamp` 只做旧包排序，没有用于重建时间轴。
   - **优化**：20 Hz snapshots + sequence/server tick；客户端延迟约 80–120 ms 在两个 snapshot 间 Hermite/线性插值，短缺包时限时速度外推，超阈值再平滑纠偏。

25. **游标插值固定假设下一包恰好 100 ms 后到达**
   - `PlayerCursorPacket.cs:170-180` duration 固定等于发送间隔，未考虑 RTT/jitter。
   - 新包到达时旧 coroutine 被强制停止，抖动链路下会频繁改变速度。
   - **优化**：保留最近 2–3 个样本，用接收时间动态估计间隔；视觉位置独立持续更新，不为每包创建 coroutine。

---

## 建议实施顺序

1. 修复 `ChunkedPacket` 丢包/清理和实体 stale 时钟错误。
2. 保存传输改为 TCP/单一可靠流并加入真实背压。
3. PacketTracker 不再保留 payload；主线程 executor 改为线程安全的预算队列。
4. 给 Steam/LAN 入站处理加时间预算和优先级/状态合并。
5. 建立统一的 per-client chunk interest manager，修复“任意可见后广播所有人”。
6. 集中化实体位置、动画、逻辑、结构 tick，消除每实体 Update 和伪 shard。
7. 最后处理 pooled serialization、量化协议、字符串 ID 表和事件驱动 delta。

## 工作区情况

- **文件创建/修改**：无。
- **build/test**：按要求未执行。
- **限制**：结论来自静态代码证据；具体毫秒、GC 次数及实际带宽仍需后续在远程游戏环境中用 2–4 人、大殖民地、人工丢包/延迟场景采样确认。
