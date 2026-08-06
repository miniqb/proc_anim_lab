# Humanoid 3D 控制器（双足 / 拾荒者）

`HumanoidLocomotionController` 是与 `LizardLocomotionController` 并列的**双足**后端。共享层
（`Body` / `BodyChunk` / `ChunkConnection` / `Limb` / `LizardLocomotionController`）**零改动**
——落地时 20 条蜥蜴矩阵基线与 smoke `ExpectedHash` 逐位不变（改动前后矩阵输出除日志横幅
逐字节 diff 为空）。

实现依据为本机 Rain World `Scavenger` / `ScavengerHand` 的反编译逐行核实，只移植行为结构与
单位关系，不包含原游戏源码。

代码位置：`core/species/humanoid/`（`Arm.cs` / `HumanoidLocomotionController.cs` /
`HumanoidParams.cs`）。**唯一的跨物种依赖边**是 Humanoid → Lizard（腿复用 `Limb` 的 opt-in
`LookaheadTicks` + `MoveIntentDeadzone` 常量），由 smoke `[CORE-MODULARITY]` 白名单钉住。

> **装配 / 输入 / 输出契约的真相源是** [`porting_contract.md`](porting_contract.md)
> §4.2 与 §5.4。本文档记录的是**机制的来源与各轮修复**——尤其是几处「读反了反编译」的教训。

## 1. 核心机制（全部出自 Scavenger 反编译）

### 1.1 清醒近地 = 失重伺服木偶

`Scavenger.Act` 对三 chunk 的 `vel.y += gravity` 是**抵消** chunk 级重力，**不是施加**。

> **探索期曾读反方向** —— 在带重力语义下，站立力偶的平衡倾角只有 79°、瘫平不起。改为
> 「`Conscious && Grounded` → `GravityScale 0`」后全部行为涌现归位。这是本后端最贵的一次误读。

### 1.2 站立 = 姿态误差非线性力偶（零 knockdown 状态机）

`WeightedPush(胸, 髋, up, LerpMap(dot, -1, 1, 5.5px, 0.3px))` + 头部双伺服 + 髋高度伺服
（拉向地面上方 `RideHeight`，≙ 拉向格中心）。

摔倒 / 爬起 / 眩晕瘫倒**全由 `Conscious` 开关与力偶存在性涌现**，击飞后约 31 tick 自动爬起。

> **确定性内核的注意事项**：完全对称的直立杆是**不稳定平衡点**。宿主要「击晕倒地」必须配
> 一次轻推（`Launch` 小冲量）——零随机内核没有噪声帮它倒。

### 1.3 手臂两条独立通道（RW 分层原样移植）

| 层 | 内容 | 是否回传力 |
|----|------|-----------|
| 物理层 | `KnucklePos` 撑点（胸前探地射线，plant-and-trail 的手版）驱动腾空俯仰泵 | **是**，指关节行走的真推进辅助 |
| 肢体层 | `Arm` 粒子：三模式 Dangle/HuntAbsolute/HuntRelative + `ConnectToShoulder`（adaptVel 0.4 / exaggerate 0.1 甩动感）+ 腋窝排斥 + 帧末速度参数复位 | **否**，纯可视化 |

### 1.4 手臂优先级链（≙ `ScavengerHand.Update` 扁平 if-else）

昏迷 → 投掷 → 蓄力 → 指向 → 持物 → 撑地锁点 → 闲置垂手，链尾统一臂长约束。

扇扫（10 根 0..±25°）**每 tick 只允许一只手**（奇偶交替，预算 + 确定性双保）；人形合计
17.4 射线/tick，比蜥蜴还省。

### 1.5 非移动 API

`PointTarget`（指向 sin 伸缩）、`Carrying`（主手携带位，被持物由宿主硬钉 `MainHandPos`
≙ grasp 0）、`StartThrowCharge` / `ReleaseThrow`（蓄力强制停驶 + 身后蓄力位抖动 → 出手返回
初速 + 头甩，物体归宿主）。**RW 的随机抖动全换 `sin(TickIndex)` 相位。**

## 2. 品种预设

`AllHumanoids()` 是**独立路由表，不进 `AllBreeds()`**：

| ID | 说明 | hwalk 2000 tick 路点 |
|----|------|---------------------|
| `scavenger` | 忠实换算基线 | 27 |
| `brute` | 魁梧重型，深弓背猛冲 | 23 |
| `waif` | 瘦小敏捷，近直立小快步 | 34 |

## 3. 修复轮记录

### 3.1 评审修复轮（多 agent 对抗评审，12 条核实命中）

**两条 HIGH 均在重力开关**：

1. 撑地探针法线门槛曾取 0.3，与滑墙 0.5 判据间留下 60°~72.5° 的**争议带**：陡面被当地面 →
   失重 + 髋伺服钉面 + 前置探地节节抬高 = **65° 陡壁全速爬升 49m 永久悬挂**（与「人形不爬墙」
   的声明直接矛盾）。
2. `contact` 项曾**不看接触法线**：贴 3m 竖墙的胸接触照样攒 `GroundedCounter`，击飞撞墙后
   半空反复失重成粘滞滑降。

**修复** = 「可站立地面」统一判据 `IsGroundNormal`（≥0.5，与墙分类同一条线，**不留争议带**）
套住探针与 chunk 接触两处。

其余修复：

- 前置探针 miss / 被拒补原位回退射线 + 蓄力停驶时不前置（崖边瞄准曾 200 tick 翻转 30 次；
  顶墙探针陷墙永不收敛）。
- `Arm.ConnectToShoulder` 的 Dangle 分支断开 Exaggerate/AdaptVel 肩速两项（≙ RW Dangle 传零
  ——垂摆不是僵直随行）。**此项为唯一有意的行为变更**，人形基线随之换新。
- `ReleaseThrow` 补 `!Conscious` 守卫（同帧置昏不能再拿出手向量）。
- `Shift` 补平移 `MainHandPos`（宿主钉持物的观测量）。
- 指向 sin 相位 `TickIndex % 62832`，防长驻宿主两天后 float 量化抽搐。
- 沙盒 CLI 品种名硬校验（打错静默回落 = 假绿）+ `--stun` / `--yank` 越界硬拒（事件没发生、
  断言静默蒸发 = 假绿）。

钉子：`[CORE-HUMANOID-GRAVITY]`（65° 坡不可爬 + 贴墙击飞零半空失重，`HalfSpaceTerrain` 解析
地形）与 STUN / SHIFT 扩展断言。

### 3.2 行走弓背与后仰摆（WalkLean 轮，用户白盒实测驱动）

**症状**：① 行走「像坐着」；② 躯干向移动**反**方向倾斜 + 周期性抖动。

**取证教训**：此前探针只看**无符号** `meanUpright`，看不出前后倾方向。带符号探针才证实平地
直行胸恒在髋后 0.18~0.37m（≈40° 后仰）、21 tick 周期摆。

**根因**：**knuckle 俯仰泵的腾空门槛移植错了**。RW 原门是「胸髋双双无地面接触」= 真弹道腾空
（走路不点火）；曾错用「双脚都没抓稳」当等价物——双足步态每步都有双摆相，泵在正常行走 60%
时间点火，且撑点一落到胸后（无符号距离 ≈ 近端 → t≈1）恒输出「髋前甩 / 胸后压」的后仰力矩。

**修复** = 门改 `!Grounded`（近地探针即本内核「有地面支撑」的语义载体）。

**修后的消融实证**（`scratchpad humanoid_probe` 的 ablate/tune 场景）：

- 巡航前倾的**真实杠杆**是 `Chest`/`Hips` **阻尼差**（0.9/0.8 → 胸恒比髋留速多，刚性杆把
  速度差转成前倾；拉平即直立）。
- 同轮补的 `LeanPush`（≙ L1956）在满油门时被推进余量钳制**完全吸收**（净效果零）——保留为
  RW 保真 + 低油门段有效，注释已如实标注。
- `HeadLean`（≙ L1957 行进中头探在胸前方）**真实有效**（头前探 +0.43m，其中 +0.24 是它的贡献）。

预设按真杠杆调深浅：brute 0.88/0.72（最深 up≈0.82）、scavenger 0.9/0.8（≈0.85）、waif
0.92/0.9（近直立 ≈0.95；`HeadDamping` 0.82→0.88 同轮修——**头伺服最大牵引必须 ≥
(1−HeadDamping) × 巡航速度**，否则头追不上巡航，退化成被脖子拖行垂在胸后）。

修后平地直行 lean 稳定 +0.24m 前倾、零抖动（min/max 差 0.001）。`humanoid-stun` /
`humanoid-act` 哈希逐位不变（站桩类零介入对照组）。

**遗留**：停驶收脚（坐姿的另一半根源：脚恒在髋前 ≥0.2m 的 plant-and-trail 换步阈值 + 平地
永不触发 `IdlePose`）与 `FeetDown` 调参未做。

### 3.3 双手撑地重合（HandSpread 轮，用户白盒实测驱动）

**症状**：行走中两只手撑地时锁到同一个世界点完全重合。

**根因** = 扇扫分离的 3D 化几何错误：RW 原版两手分离靠**扫描角 ±4° 偏置**
（`CheckForGrabPos` 的 `limbNumber==0 ? -4° : +4°`——2D 侧视唯一的分离维度）；移植时换成
「起点沿侧轴错开」，但射线仍瞄**同一个** `KnucklePos`——kp 本身落在地面上，朝单点**汇聚**的
射线让起点侧向错开在命中处被精确抵消，两手 `GrabPos` 逐位相同。`Arm` 只有手-胸腋窝排斥、
没有手-手分离项，锁点吸附把重合稳定维持。

> 双手**同时**撑地本身是 RW 保真的，错的只是**同点**。

**修复** = 分离做在**目标点与扫描角**上（只落 `TryFindGrabPos` 一处，人形独享路径）：扇扫
中轴改瞄 `kp + right·Side·KnucklePlantSpread`（0.2m —— 落点左右分开 ~0.4m，斜面最坏收缩仍
>0.24m）+ 补回 RW 原版每手 ±4° 俯仰偏置（`KnuckleScanBiasDeg`，落点前后交错）。

钉子 `[CORE-HUMANOID-HANDS]`：平地直行双手同锁相位 ≥100 tick + 锁点最小间距 ≥0.15m（两常量
归零精确复现 `minPlantSep=0.000` 红灯，门有效性已验证；修后实测 0.458m）。

### 3.4 头部掉头响应（HeadTurn 轮，用户白盒实测驱动）

**症状**：行进中掉头，胸部很快转向而头要「好一会儿才慢慢靠过来」。

**取证**：无引擎 reverse 场景（巡航 300 tick → 瞬间 180° 反转）证实胸 2 tick 完成翻转，头
80 tick 才横越、**110 tick（2.75s）才就位**。

**力学根因** = **头伺服在巡航中恒饱和**：满速巡航仅维持头前探所需的每 tick 修正 ≈0.125m
恰好等于 `HeadServoRange` 钳制（RW 5px 直译），掉头时**零牵引余量**——头只能靠 PullOnly 脖子
拖行横越。

**修复两旋钮**（8 组合参数扫描选定，全组合零振铃、零站姿抖动）：

1. `HeadServoRange` 0.125 → 0.25（**主杠杆**，有意偏离 RW 原值：只翻倍远场牵引，近场刚度
   「力 ∝ 距离 × 增益」与站姿稳定性不变，单它就 110 → 22 tick）。
2. `HeadDamping` 三预设统一 0.88（scavenger/brute 从 RW 原值 0.8 提高，waif 已是）——削掉
   巡航拖拽预算，叠加后就位 **14 tick（~0.35s）**。

`HeadServoGain` 保持 RW 原值 0.16 不动。三预设就位 14/14/16 tick。

> 本轮阻尼在**站桩姿态也生效**，`humanoid-stun` / `humanoid-act` 哈希一并换新——站桩对照组
> 豁免在此轮不适用。

### 3.5 双足高频蹭步（LegGait 轮，用户白盒实测驱动）

**症状**：行走（brute 最显眼）双腿高频低距离往前蹭而非正常迈步。

**取证**：步频锁死 5~7 tick/步（brute 每秒 8 步）、步幅仅腿长 0.4~0.7 倍、**释放时脚还在髋前**
（brute +0.33）、brute 触地 3 tick 永远达不到 `LegGripDelay=4` 的 Gripping 判定（抓地占比恒 0）。

**根因** = 蜥蜴 `Limb` 的 oldestGrip 步态错开（「其余腿全抓稳 → 本腿松开」）在**两腿拓扑下
退化**成「对脚一落地本脚即松」：触地被锁死在对腿落地延迟、与身体速度无关；释放时超前量立即
高于重新迈步阈值 → 下 tick 直接再找落点 —— 高频小碎步的正反馈。

**反编译核实**：`ScavengerLeg` **根本不用成对协调**，而是独立前瞻点循环——
`IdealPos = 髋 + clamp(髋速 × 10, 腿长)`，锁点离 `IdealPos` 超腿长才松、松开即重找无摆动期
门槛、`FindGrip` 搜索半径以 `IdealPos` 为心（锁点允许暂超腿长，靠 `ConnectToPoint` 拖住）。

**修复** = `Limb.LookaheadTicks` **opt-in**（默认 0 = 蜥蜴路径逐位不变；人形工厂设 10 ≙ RW
字面量）：flag 路径换前瞻点释放 + 可及判定改以 `IdealPos` 为心全腿长半径（**以锚点为心会拒掉
全部前伸落点**，waif 曾被压成 4 tick 碎步），跳过 trail / oldestGrip / extraLongStep 三段。

**支撑保序 guard**（确定性内核对 RW 环境噪声的显式等价物，先例 = 随机抖动 → sin 相位）：对脚
踩稳才允许本脚松开 + 腿表 tick 顺序打破同 tick 双到期（先 tick 腿松开清零计数、后 tick 腿持稳
半拍）—— 两脚 `FindGrip` 目标前后对称，站定时落到同一 x、之后同起同落成跳步（逐 tick 日志
证实），guard 反相自锁、无周期漂移；1.75×腿长失效阀防对脚长期找不到点时本脚被拖行钉死。

**修后**：brute **14 tick / 0.82m（1.16× 腿长）**、触地 10 tick、抓地 50%；scavenger
9t/0.62m；waif 7t/0.64m（回到它本来正常的值）。双悬空 0%、单脚支撑期 44~58%、释放点回髋附近
（−0.00~+0.06）。

钉子 `[CORE-HUMANOID-GAIT]`：步幅 / 周期 / 触地 / 释放位置 / 抓地占比 / 零双悬空 + brute
Gripping 专项（`LookaheadTicks` 归零精确复现全套修前数字红灯）。蜥蜴 20 条 + smoke
`ExpectedHash` 逐位不变（flag 默认 0 有数学保证 + 矩阵实证）。

## 4. 正式渲染

人形的 `IFormalRenderer` 经 `FormalRendererFactory` 分派：五点脊柱背凸腰点驼背扫管、近黑竖长
头椭球 + 头色牙刀片刺出轮廓 + 满饱和对比色斜吊眼（眨眼 / 瞳孔 / 昏迷半睁）、seed 冻结的
eartlers 分支模板（dominance 定尺寸）、脖管体→头顶点色渐变、`TwoBoneIk` 肘膝 + 深色手套手瘤、
持物 / 投掷画长矛钉 `MainHandPos`/`Dir`。`scavenger` 土黄 / `brute` 暗棕巨角红缝眼 / `waif`
灰绿冰瞳追视。

顶点色走 `TubeMeshBuilder.Build(srgbVertexColors: true)`，在所见空间调色。细节见
[`rainworld_render_research.md`](rainworld_render_research.md) §5。

## 5. 沙盒与回归

- 沙盒：`--species=humanoid`（+`--route=hwalk|hact` / `--stun=T,D`，与 `--creature=` / 秃鹫
  `--breed=` 互斥）；下拉框换人形品种（数字行 12 键已被蜥蜴 1~4 / 蜈蚣 5~8 / 秃鹫 9,0,-,= 占满）；
  交互键 **P**=指向 / **C**=持物 / **T**=按住蓄力松开投掷。`HumanoidSandboxDriver` /
  `HumanoidRenderer` 独立于蜥蜴路径。
- 人形没有独立矩阵脚本——8 项配置在**主矩阵**里：`humanoid` ×2（hwalk 坡→平地→跨台阶巡逻 +
  双跑 diff）、`humanoid-40`（时基不变性）、`humanoid-yank`（行进中击飞限时回正续走）、
  `humanoid-stun`（昏迷瘫倒 + 苏醒爬起）、`humanoid-act`（指向→持物→蓄力停驶→出手动作脚本）、
  `humanoid-brute` / `humanoid-waif`（变体巡逻）。哈希基线在 `run_matrix.sh` 顶部
  `HASH_HUMANOID_*`。
- `core/smoke` 八断言：`[CORE-HUMANOID-DET / STAND / STUN / ACT / SHIFT / GRAVITY / HANDS /
  GAIT]`，基线 `HumanoidExpectedHash`。

```bash
dotnet run --no-restore --project core/smoke
./tools/run_matrix.sh
```

## 6. 遗留

- 斜坡 / 台阶通过靠悬浮伺服前置探地（0.3m lead），更陡地形未覆盖（60° 以上现在**正确地**
  不可站立）。
- 手臂在爬杆 / 荡杆（RW Climb/Swing）不在范围——本仓无杆地形。
- 崖边探针回退在 smoke 无有限地板地形，靠评审复现验证，**未固化为断言**。
- 停驶收脚与 `FeetDown` 调参（见 §3.2 遗留）。
