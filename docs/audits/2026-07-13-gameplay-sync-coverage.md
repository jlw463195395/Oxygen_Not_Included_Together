# 玩法同步覆盖静态审计

审计日期：2026-07-13

代码基线：`9a7aca573f22e02bae6bd115abdc034cc08ab1f3`；其后的 `fe6b2ef9abe7d0b44bbc827e90e5010500a3949e` 仅修改维护文档，Mod 源码未变。

范围：仅关注真实联机操作、实时性、同步正确性和性能；身份认证、权限、反作弊及玩家资产隔离不在范围内。

状态：只读静态分析，尚未经过 U59 编译或双实例运行验证。

---

## 审计结论

这是一个**宿主权威、客户端大量停掉本地模拟，再靠事件包、周期快照和整档 hard sync 维持观感**的实现。核心联机操作已有相当覆盖，但覆盖明显分成三层：

- **实时事件同步**：玩家工具操作、建筑配置、Operational 状态、复制人位置/动画/效果、拾取与搬运视觉、植物生成/移除、暂停/倍速。
- **周期校正**：复制人 vitals、气液格子、气液管道、部分电力设备、自动化组件、植物生长、研究、任务 UI。
- **高风险缺口**：世界资源统计、纯温度/病菌变化、运输轨道、完整电网状态、动物玩法状态、技能经验与洗点、装备实体/耐久/储气，以及全部 U59 水生专属机制。

> 这是只读静态审计；没有执行 build、test 或游戏运行。仓库仍以 U57-700386 为目标，而不是当前 U59-740622：`Directory.Build.props:45-48`。

---

## 按玩家实际玩法的覆盖矩阵

### 1. 复制人 chores、移动和工作

**已实时/准实时同步**

- 宿主每 200 ms 检查复制人动作、目标格、动画、工作状态和手持符号；发生变化立即发 unreliable 包，另有 1 秒 heartbeat：
  `Networking/Components/DuplicantStateSender.cs:49-60,74-130`
- 实体位置移动超过 0.05 格即可发送，最短约一帧，静止每秒 heartbeat；客户端 lerp，误差超过 1.5 格直接 snap：
  `Networking/Components/EntityPositionHandler.cs:16-31,84-118,125-151`
- 动画 `Play/Queue`、动画覆盖、symbol visibility/override 都有宿主事件包：
  `Patches/KleiPatches/KAnimControllerBase_Patches.cs:31-55,57-133,171-245`
  `Patches/KleiPatches/SymbolOverrideController_Patch.cs:23-83`
- 工作开始/停止及进度 UI 有事件同步：
  `Patches/DuplicantActions/StandardWorker_Patches.cs:23-97`
- 个人工作优先级、技能学习、日程分配等均有对应事件包入口：
  `Patches/Duplicant/ChoreConsumerPatch.cs:10-42`
  `Patches/Duplicant/MinionResumePatch.cs:10-39`

**仅周期/UI 校正**

- 复制人任务列表不是完整 chore 模拟，而是玩家打开任务侧栏后订阅，宿主每 0.5 秒发 UI 快照：
  `Networking/Components/DuplicantChoreBroadcaster.cs:12-18,33-47,82-92`
- 世界层面的周期 chore 校正实际上只覆盖拖地标记；挖掘和拖地各约每 4 秒轮转一次：
  `Networking/Components/WorldStateSyncer.cs:215-226,237-256,327-352`

**缺口或仅视觉**

- 客户端直接关闭 `ChoreDriver`、`ChoreConsumer`、`MinionBrain`、`Sensors` 和所有状态机，因此客户端展示的是宿主状态投影，不是完整 chore 状态副本：
  `Scripts/Duplicants/MinionMultiplayerInitializer.cs:59-72`
- 旧的客户端 navigator transition controller 整个文件被块注释，实际移动主要依赖实体坐标插值，而非完整路径/transition 重放：
  `Networking/Components/DuplicantClientController.cs:1-320`
- `DuplicantStateSender` 通过 chore 名称字符串猜测 Building/Digging/Eating/Sleeping/Carrying，未知或 U59 新 chore 很可能落到 `Other`：
  `Networking/Components/DuplicantStateSender.cs:202-239`
- `StandardWorker` 明确排除了牧场、抽液站、睡眠、装瓶、冰壶等 workable 的工作事件；这些依赖通用动画/位置，需重点实测：
  `Patches/DuplicantActions/StandardWorker_Patches.cs:27-57`

---

### 2. Vitals、状态、效果

**已周期校正**

- 每个复制人每秒发送所有 `Amounts.ModifierList` 数值及身上病菌，客户端直接覆盖：
  `Networking/Synchronization/VitalStatsSyncer.cs:15-19,35-56`
  `Networking/Packets/DuplicantActions/VitalStatsPacket.cs:21-32,88-109`
- Effects 的添加/移除是宿主实时事件，客户端禁止自行产生未授权 effect：
  `Patches/Duplicant/EffectsPatch.cs:48-87`
- 状态项 UI：被选中时约 0.5 秒，未选中约 5 秒；并且只对客户端视口附近实体发送：
  `Networking/Components/StatusBroadcaster.cs:10-16,32-59,62-88`

**缺口**

- vitals 是 1 秒 unreliable 全值校正，没有独立可靠关键事件；死亡、濒死、窒息、疾病切换等应验证丢包和状态机已关闭时的 UI/动画一致性。
- bionic 只有崩溃保护，源码自己标注“TODO ensure this gets synced properly”，没有油量、模块状态等专门同步：
  `Patches/Bionics/BionicPatches.cs:13-16`

---

### 3. Skills

**已实时同步**

- `MinionResume.MasterSkill` 触发 `SkillMasteryPacket`，宿主应用后转发客户端：
  `Patches/Duplicant/MinionResumePatch.cs:10-39`
  `Networking/Packets/DuplicantActions/SkillMasteryPacket.cs:30-43,61-99`

**明显缺口**

- 包只包含 `NetId + SkillId`，不含技能点、经验、士气需求、帽子选择、技能重置/洗点状态。
- 代码明确承认“points are desynced”，仍直接再次调用 `MasterSkill` 扣点：
  `Networking/Packets/DuplicantActions/SkillMasteryPacket.cs:82-93`
- 没有技能全量周期快照；漏掉事件只能等 hard sync。
- 优先实测客户端学习技能、连续学习多级技能、技能洗点、技能点不足及 U59 新技能。

---

### 4. Equipment、服装、气压服与搬运物

**已实时但主要是视觉**

- 复制人 storage 的 Store/Remove 会创建或清除客户端背部搬运物代理：
  `Patches/World/StoragePatches.cs:14-50,90-139`
- `DuplicantCarryItemPacket` 明确在客户端创建 `GameObject + KBatchedAnimController` 代理，并不还原真实物品库存：
  `Networking/Packets/DuplicantActions/DuplicantCarryItemPacket.cs:54-121,141-168`
- `ToolEquipPacket` 同样是在手部骨骼上生成动画对象，并按少量硬编码工作特效映射：
  `Networking/Packets/DuplicantActions/ToolEquipPacket.cs:54-89,92-112`
- 服装/suit 外观可能通过 anim override 和 symbol override 实时跟随。

**缺口**

- 没有找到 `Equippable/Equipment/Unequip` 等语义状态的同步入口；装备槽、服装属性、耐久、磨损、喷气服燃料、铅服等只能依赖效果/动画或整档。
- 客户端禁止 `SuitTank.ConsumeGas`，且缺组件时直接把气罐当成 75 kg 满罐；这是保命/显示兜底，不是同步：
  `Patches/World/SuitTankPatches.cs:10-37,41-60`
- suit locker 的“请求/不请求/丢弃服装”配置有事件包，但不等于复制人已穿装备状态：
  `Patches/World/SideScreen/SuitLockerPatches.cs:15-98`

---

### 5. 建筑建造、拆除、配置和运行状态

**已实时同步**

- Build、deconstruct、cancel、copy settings、priority、toggle、slider、filter、capacity、door、access control、fabricator 等有大量工具/侧栏事件包。
- 所有 `Operational.IsOperational/IsActive/IsFunctional` setter 都由宿主广播，客户端 getter 改读远端 wrapper：
  `Patches/World/Buildings/Operational_Patch.cs:19-59,69-123`
  `Scripts/Buildings/ClientReceiver_Operational.cs:18-31`
- 动画建筑带 `AnimStateSyncer`：
  `Patches/World/Buildings/BuildingComplete_Patches.cs:16-28`

**部分周期校正**

- 专门的结构快照默认 0.5 秒采样、变化时 unreliable 发送，客户端超过 2 秒未收到则可靠请求：
  `Networking/Components/StructureStateSyncers/StructureSyncerBase.cs:13-32,64-114,117-163`
- 但只注册了：
  - 电池；
  - 发电机；
  - StorageLocker、RationBox、CargoBay、CargoBayCluster；
  - FlushToilet/Toilet；
  - Reactor。
  `Patches/World/StructureSyncPatch.cs:13-105`
- storage 快照会重建内容、质量、温度、病菌，但只覆盖上述少数容器：
  `Networking/Components/StructureStateSyncers/StorageStateSyncer.cs:26-57`

**重要缺口**

- 通用建筑列表 30 秒 reconciliation 虽然存在，但入口被注释，实际未启用：
  `MultiplayerMod.cs:82-85`
  `Networking/Components/BuildingSyncer.cs:15-16,49-86`
- 因而漏掉一次“建造完成/拆除完成”事件时，没有一般性的建筑拓扑自愈，只能依靠 save hard sync。
- 大量建筑只同步侧栏配置或 Operational 三布尔值，不同步内部 storage、计量器、状态机变量、配方进度和产物。
- U59 新建筑若不实现现有接口、或 Harmony 方法签名变化，将完全绕过现有注册。

---

### 6. 世界资源、地面物品、库存统计

**已实时同步**

- 挖掘生成矿物带元素、质量、温度、病菌和 NetId 的可靠生成事件：
  `Patches/World/WorldDamagePatch.cs:32-94`
- Pickupable Take、TakeUnit、CleanUp 和 Storage Store/Remove 有事件通知：
  `Patches/World/PickupablePatches.cs:12-86`
  `Patches/World/StoragePatches.cs:14-50,90-139`

**严重缺口**

- 世界资源统计 `ResourceSyncer.Update()` 第一行直接 `return`，注释明确说明“不工作”；客户端资源栏没有权威周期来源：
  `Networking/Synchronization/ResourceSyncer.cs:13-29`
- 客户端虽然 patch 了 `WorldInventory.GetAmount`，但字典通常为空，因为宿主发送逻辑永远不执行：
  `Networking/Synchronization/ResourceSyncer.cs:83-100`
- 一般地面物品的质量、温度、病菌、堆叠拆分/合并、腐败进度、位置没有统一周期快照。
- StorageItemPacket 的某些路径标为 `NetId = 0 // FX Only`，说明只是视觉/弹字，不保证实体库存：
  `Patches/World/StoragePatches.cs:39-50`

这是目前最明显的玩家可见同步缺口之一。

---

### 7. 世界气液、温度和病菌

**已实时/周期同步**

- `SimMessages.ModifyCell` 会把元素、温度、质量、病菌排入批处理：
  `Patches/World/SimMessagesPatch.cs:9-38`
- 世界气液每 1.5 秒扫描客户端/宿主视口和一个后台块；低 FPS 或多人时可自适应放慢到 9 秒：
  `Networking/Components/WorldStateSyncer.cs:20-28,204-213,711-750,760-779`
- 格子包包含元素、质量、温度和病菌，并在客户端以 `Replace` 写入 sim：
  `Networking/Packets/World/WorldUpdatePacket.cs:13-20,86-123`
- FallingWater 粒子有事件同步，客户端禁止自行创建：
  `Patches/World/FallingWaterPatch.cs:29-59`

**关键缺陷**

- 周期扫描的 shadow 只保存**元素和质量**；只有元素变化或质量变化超过 10 g 才发送。
  纯温度变化、病菌变化不会触发周期包：
  `Networking/Components/WorldStateSyncer.cs:34-35,676-688,793-818`
- 因而恒定质量的固体导热、封闭气体升降温、病菌繁殖/死亡可能长期不校正；除非对应路径恰好调用已 patch 的 `SimMessages.ModifyCell`。
- 批处理器虽写着 10 秒 flush，但气液扫描每次会主动 `Flush()`；实际延迟受 1.5–9 秒扫描节奏影响：
  `Misc/World/WorldUpdateBatcher.cs:13-16,42-47`
  `Networking/Components/WorldStateSyncer.cs:753-757`
- 世界更新是 unreliable，且没有温度/病菌全量强制刷新，丢包后的自愈不完整。

---

### 8. 管道与运输

**气管/液管覆盖较好**

- 质量、元素、温度、病菌：1.5 秒 delta，4.5 秒强制重发可见管段：
  `Networking/Components/ConduitFlowSyncer.cs:34-41,154-200,223-260`
- 空管首次进料有事件 fast path，帧末合并成 reliable 包：
  `Patches/World/ConduitFlowFastPathPatch.cs:11-18,47-66`
  `Networking/Components/ConduitFlowSyncer.cs:302-380`
- 客户端直接 `SetContents`：
  `Networking/Components/ConduitFlowSyncer.cs:263-299`

**缺口**

- 源码明确声明 SolidConduitFlow/运输轨道不在范围内：
  `Networking/Components/ConduitFlowSyncer.cs:27-29`
- empty→non-empty fast path 发给所有客户端，没有按视口筛选；周期发送也在收集“有人可见”后调用 `SendToAllClients`，会造成额外流量：
  `Networking/Components/ConduitFlowSyncer.cs:174-200,364-380`
- 阀门开关配置可以实时同步，但稳定流量仍有 1.5 秒阶梯延迟；应实测桥接、分流、过滤器、储液库放空和跨世界管道。

---

### 9. 电网

**已同步**

- 电池、所有 `Generator`/`EnergyGenerator` 每 0.5 秒采样变化；客户端禁掉电池本地充放电和发电机燃料/发电模拟：
  `Patches/World/StructureSyncPatch.cs:13-38`
  `Patches/World/BatteryClientSimSkipPatch.cs:7-33`
  `Patches/World/GeneratorClientSimSkipPatch.cs:18-50`
- 电池同步 Joules；发电机同步 Joules 和部分燃料计量器：
  `Networking/Components/StructureStateSyncers/BatteryStateSyncer.cs:24-48`
  `Networking/Components/StructureStateSyncers/EnergyGeneratorSyncer.cs:24-50`

**缺口**

- 没有完整 circuit snapshot：发电/耗电合计、变压器上下游、电线负载、过载时间、断路状态、smart battery 信号、电网拓扑等未统一同步。
- EnergyConsumer 主要把客户端 powered getter 映射到 `Operational` wrapper，不是同步真实电路状态：
  `Patches/World/EnergyConsumer_Patches.cs:16-61`
- 电线建造/拆除依赖工具事件；通用建筑/网络拓扑 reconciliation 未启用，漏包后可能永久分叉。
- 结构快照只有“变化才发送”，电池/发电机 `ShouldForceSync()` 返回 false；unreliable 包丢失后主要依赖客户端 2 秒 stale request，而非宿主定期 heartbeat：
  `StructureStateSyncers/BatteryStateSyncer.cs:64-67`
  `StructureStateSyncers/EnergyGeneratorSyncer.cs:53-56`

---

### 10. 自动化

**已有周期快照和请求恢复**

- 1 秒采样，变化时按视口 unreliable 发送；客户端 2 秒 stale 后可靠请求：
  `Networking/Components/LogicStateSyncer.cs:14-17,64-128,135-165`
- 覆盖 Switch、LogicGate、Memory、Counter、Filter、Buffer、Timer、Ribbon reader/writer、Automatable 的部分内部字段：
  `Networking/Components/LogicStateSyncer.cs:238-331,336-435`
- 建筑 OnSpawn 自动注册上述组件：
  `Patches/World/LogicBuildingRegistrationPatch.cs:12-42`

**缺口**

- 这是已知组件白名单，不是完整自动化网络/端口状态快照。
- 未见 logic wire/ribbon 每个端口值、网络拓扑、发信器/收信器、跨世界逻辑网络、U59 新传感器专门覆盖。
- 多数逻辑参数设置依赖各侧栏 Harmony patch；新类或签名变化将静默漏掉。

---

### 11. 植物和动物

#### 植物

**覆盖较强**

- 生成/移除是实时 lifecycle 事件：
  `Networking/Components/PlantGrowthSyncer.cs:32-39,68-82`
- 每 5 秒全量校正 prefab、位置、成熟度、枯萎、可收获、野生/种植状态，并可移除 phantom plant：
  `Networking/Components/PlantGrowthSyncer.cs:17-18,41-65,129-165,195-220`
- 这是少数真正有全量 reconciliation 的玩法域。

**仍需验证**

- 施肥、灌溉、环境需求进度、产物生成、变异种子/基因、植物专属状态机并未全部出现在快照字段中。
- lifecycle 事件发送未显式指定 send mode，需确认默认可靠性。

#### 动物

**主要是视觉同步，玩法状态缺口很大**

- 基础动物带 NetworkIdentity、位置和动画同步：
  `Patches/Critters/EntityTemplatesPatch.cs:19-36`
- 客户端关闭 CreatureBrain、Sensors 和所有状态机，只保留状态 UI receiver：
  `Scripts/Creatures/CreatureMultiplayerInitializer.cs:64-79`
- 因此移动/动画/status item 可见，但饥饿、年龄、驯化、幸福度、繁殖、蛋、放牧、捕捉、死亡、物种特有行为没有动物专用全量状态包。
- 新动物若仍走 `ExtendEntityToBasicCreature` 且 Harmony 签名匹配，至少可能获得位置/动画；否则连视觉同步都可能没有。

---

### 12. DLC U59 水生内容

**当前不能视为已覆盖。**

证据：

- 编译目标仍是 `QOL2025NovRelease=700386`：`Directory.Build.props:45-48`
- 源码中没有 U59、740622 或水生专属组件/建筑/chore 的显式适配。
- 只有通用的：
  - 基础 creature 扩展；
  - FallingWater；
  - 世界气液格子；
  - 气液 conduit；
  - 通用 building/config/animation 接口。

这最多意味着**某些水生实体可能获得位置、动画、格子液体观感**，不代表水生玩法状态同步。高风险项包括：

- 水下导航/游泳 transition 和新 NavType；
- 水生动物 AI、繁殖、捕食、驯化及 egg lifecycle；
- 新植物环境状态；
- 新水生 chore/workable；
- 新建筑内部状态、容器、泵送/流体特效；
- U59 改动后的 Harmony 方法签名；
- 新元素温度/病菌仅变化但质量不变的情况。

---

### 13. 暂停、倍速、世界时间、存读档

**暂停/倍速实时事件**

- `SetSpeed` 和 `TogglePause` 后立即发给其他 peer：
  `Patches/World/SpeedControlPatch.cs:15-28,36-52`
- 接收端直接切暂停和速度：
  `Networking/Packets/World/SpeedChangePacket.cs:46-71`

**风险**

- 任意客户端都向“所有其他 peer”发送，没有宿主统一排序/转发，也没有周期速度 heartbeat。多人同时切速时可能 last-writer-wins 或短暂分叉。
- `SetSpeed` 与 `TogglePause` 可能为一次用户操作重复触发两包，需运行时确认。
- 世界时间每游戏 1 秒由宿主 unreliable 校正，客户端 `GameClock.AddTime` 被禁：
  `Patches/GamePatches/GameClockPatch.cs:38-60,62-86`

**存档与 hard sync**

- 客户加入时可请求宿主 save；LAN 优先 TCP，其他路径分块发送：
  `Networking/Packets/World/SaveFileRequestPacket.cs:35-78,124-189`
- hard sync 会暂停、广播 HardSyncPacket、发送完整世界 save、让客户端重新 ready：
  `Misc/World/GameServerHardSync.cs:30-76`
- 可配置每周期开始后 5 秒 hard sync，也可手动触发：
  `Patches/GamePatches/GameClockPatch.cs:88-113`

**缺口/风险**

- `SaveLoaderPatch` 只处理“加载后启动房间”，没有看到宿主在联机中手动加载另一个档时自动通知所有客户端重载：
  `Patches/World/SaveLoaderPatch.cs:14-63`
- hard sync 的 `hardSyncInProgress` 是按预估传输时长结束，不是等待所有客户端 ACK/加载完成：
  `Misc/World/GameServerHardSync.cs:63-74`
- 传输管理器有 ACK，但 hard-sync 协程没有以 ACK 完成作为 barrier：
  `Networking/SaveFileTransferManager.cs:89-125`
- hard sync 后自动恢复原倍速的代码被注释，可能持续暂停或由玩家手动恢复：
  `Misc/World/GameServerHardSync.cs:75-77`

---

## 最值得优先实测的清单

### P0：最可能直接影响“能不能真正一起玩”

1. **U59 水生完整冒烟矩阵**
   - 新建、指派、取消所有新 chore；
   - 水下移动、游泳动画、路径跨液面；
   - 新动物出生/捕捉/喂养/繁殖/死亡；
   - 新植物种植/枯萎/成熟/收获；
   - 新建筑每个侧栏选项和内部状态。
2. **世界温度/病菌纯变化**
   - 封闭固体/气体质量不变，只加热或降温；
   - 病菌只繁殖/死亡、不改变质量；
   - 客户端观察 30–60 秒是否仍不更新。
3. **资源栏和实际库存**
   - 同时挖掘、搬运、堆叠拆分、烹饪、腐败、跨世界运输；
   - 对比宿主/客户端资源栏、容器内容和地面实体。
4. **动物真实状态而非动画**
   - 对比年龄、饥饿、驯化、幸福度、繁殖进度、蛋、死亡和牧场任务。
5. **电网分叉**
   - 跨视口建/拆线、变压器、smart battery、过载、断线、重连；
   - 对比电路概览和每个设备 powered/active 状态。
6. **多人同时暂停/改速 + hard sync**
   - 两客户端同帧切速；
   - hard sync 传输慢/丢包；
   - 客户端未完成加载时宿主是否提前解除 hard-sync 状态。

### P1：高频可见瑕疵

7. 技能学习、连续升级、洗点、技能点/经验一致性。
8. 气压服穿脱、氧量/燃料、耐久、衣物属性和死亡边界。
9. 固体运输轨道全流程。
10. 特殊 workable：牧场、抽液站、睡眠、装瓶、冰壶、望远镜。
11. 新客户端把镜头移动到之前无人观察的管道、自动化和建筑区域后的 catch-up。
12. 人数增加、低 FPS 时世界格子同步降至 9 秒后的体感和漂移。

---

## 最值得优先补齐的清单

1. **启用并修正世界资源权威快照**：先解决 `ResourceSyncer` 被硬禁用的问题。
2. **世界格子 shadow 加入温度和病菌**，并设计低频强制刷新，保证 unreliable 自愈。
3. **U59 水生专属适配层**：先基于 U59 assemblies 盘点新 NavType、creature、plant、workable、building/component，再增加显式注册和测试矩阵。
4. **动物语义状态快照**：至少年龄、生命、饥饿、驯化、幸福度、繁殖、egg/lifecycle。
5. **装备语义同步**：装备槽、服装/气压服 prefab、耐久、储气/燃料，而不是仅同步 kanim/symbol。
6. **技能全量快照**：mastered skill 集合、技能点、经验、洗点；客户端操作统一由宿主确认。
7. **电网/自动化网络级快照**：拓扑、circuit ID、负载、供需、过载及 logic port 值。
8. **SolidConduitFlow/运输轨道同步**。
9. **建筑拓扑 reconciliation**：恢复或重做已注释的 `BuildingSyncer`，避免建造/拆除单包丢失后永久分叉。
10. **速度宿主排序和 heartbeat**；hard sync 改为等待所有客户端传输 ACK、加载完成和 Ready barrier 后再结束。

---

## 工作区说明

- 未创建或修改任何文件。
- 未执行 build、test、restore 或游戏启动。
- 工作区原先已有未提交的 `AGENTS.md` 修改；本次未触碰。
- 主要限制：当前没有 U59 assemblies/runtime，因此 U59 部分只能根据 U57 源码缺少显式适配、现有通用入口及 Harmony 目标做静态风险判断，不能宣称运行时兼容。
