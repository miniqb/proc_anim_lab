# Vulture 3D 飞行控制器

`VultureFlightController` 是与 `LizardLocomotionController` 并列的**飞行**生物后端（与
Spider / Centipede / Cicada 同为平行物种控制器），只共享 `Body` / chunk / connection /
`ITerrainQuery`，互不引用。

实现依据为本机 Rain World `Vulture` / `VultureTentacle` 的反编译研究（升力、拍翅、悬停、降落
数值逐行核实换算，`1px = 0.025m`），只移植行为结构与单位关系，不包含原游戏源码。

> **装配 / 输入 / 输出契约的真相源是** [`porting_contract.md`](porting_contract.md)
> §2.5 与 §4.1b。本文档记录的是**机制为什么长这样**——与地面生物的路线差异、涌现边界、
> 三轮实测逼出来的门控，以及落地即修的评审轮。

## 1. 与地面生物的根本路线差异

**重力常开，没有重力开关。** 这是本后端与 Lizard/Spider 最大的分歧点：

- **升力 = 与拍翅相位同步的 sin² 脉冲**（谷值恰为 0），只注入**后脊柱单 chunk**；重力摊在
  4 个躯干 chunk 上，约束松弛把脉冲摊分 ≈÷4 后，周期均值与重力平衡。
- **悬停时的上下颠簸是这套机制的直接后果（≙ RW 手感），不是 bug。** 不要把它当抖动去「修」。
- **下降不施向下力**：意图朝下时倒拨拍翅相位（`FlapGlideRate` ≙ `wingFlap -= 1/70`），
  冻结在低升力半区滑翔。

## 2. 身体与翅膀

- 身体 = **K4 风筝刚架的 3D 化**：前脊柱 +0.4m / 后脊柱 −0.25m / 双肩 ±0.5m，六条 Rigid 全
  三角化，`RestLength` 取出生几何（= RW 26 / 40 / 22.36 / 25.61px 逐位同构）。共面静息态的
  无穷小柔性 = 有机机体微弯。
- 头 = PullOnly 拴绳（`WeightA=0` ≙ `weightSymmetry 0`，**头的重量拖不动身体**）+ 头部伺服
  （RW 物理脖链的收缩：飞行 0.2 / 着地 0.75 父系阻尼 + 朝「前向×脖长」静息位吸），渲染层画
  脖曲线。
- 翅膀 = `VultureWing` **新类，不复用 `Limb`**——plant-and-trail 是地面步态状态机，且 `Limb`
  被既有基线钉死。段链粒子 + 只抗拉绳约束（≙ `stiff=false`）+ **对身体零回传**
  （≙ `pullAtConnectionChunk=0`，唯一例外 = 抓地悬挂拉力 `(0.9L−d)×0.2`）。
- `Flap` 模式 = 全局相位行波（快下拍 15 tick + 慢回收 25 tick = 1s 周期，翼尖滞后半周期）
  + 翼根两节硬驱动。**扫掠幅度 5~15m 故意远超可达距离——伺服饱和截断才是有效机制**，不要
  把它当几何目标去修正。
- `Grab` 模式 = 射线找抓点（锚点直射 + 投影采样，**允许天花板底面**——秃鹫倒挂）→ 翅尖硬钉
  → 逐节贴附计支撑。

## 3. 涌现与确定性边界

- **没有 locomotion 状态机**：模式在每只翅膀上（`VultureWing.WingMode.Flap|Grab`），
  `AirBorne` / 栖息由翅膀组合涌现。
- 起飞 / 降落由 `MoveTarget` 几何触发：落点贴地探测 + 进入 `LandEngageRadius` → 全翅 Grab +
  `AirBrake(30)` + 5s 切换锁；栖息 + 远/悬空目标 → TakeOff + 30 tick 助推（RW 喷气推进器
  `jetFuel` / Utility 系统的收缩）。
- **零随机**：RW `StuckBehavior` 的随机抖动不移植。`Flap→Grab` 需要「刚架贴地形」或「持续
  >10 tick 无意图」的证据——**翅尖刷墙 / 悬垂头擦地 / 换路点单 tick 空窗都不算**（fly 路线
  三轮实测逼出来的门控）。
- 注速一律做 headroom 钳制（`MaxFlySpeed` / `MaxRiseSpeed`）：RW 瞄路径格天然限速，连续喂点
  必须显式封顶（同蜥蜴 `MaxMoveSpeed` 的论证）。

## 4. 品种预设

`VultureBreedParams` 与蜥蜴 `BreedParams` 是**两张互不混装的表**（`AllVultureBreeds()` /
`IsVultureBreed(name)` 路由）：

| ID | 说明 |
|----|------|
| `vulture` | 基准，双翅 8 节 5.5m |
| `king` | ×1.4 质量、10 节 6.75m 长翅 |
| `swift` | 0.8 体格、快拍短翅（本项目原创） |
| `quad` | 四翼（≙ Miros 拓扑），`LiftShare = 1.4 / 翅数` |

沙盒数字行 `9, 0, -, =` 续接（`5~8` 归蜈蚣）；`--breed=vulture|king|swift|quad` 自动分派生物
类别（与 `--creature=centipede/...` 互斥）。

## 5. 落地即修的对抗性评审轮（四缺陷，零误报）

全部四条都有 smoke 钉子（`[CORE-VULTURE-CONTRACT]`）：

1. **`AtMoveTarget` 迟滞态绑定具体目标**（换点即复位）——RW 原生 0.5m 格距下，旧版会连环假
   到达。钉子：密集喂点 10/10 无假到达。
2. **悬停锚只在真到点时取喂点**——零油门 + 远处残留目标曾以 ~89% 巡航速度绕过油门自动驾驶。
   钉子：停车水平漂移 <0.5m。
3. **升力注入不设 `AirBorne` 外层门**（逐翅注入 ≙ RW 写在每只翅膀的 Fly 分支里）——混合翅态
   下仍在拍的翅膀继续托身体，「单翅失能 → 失衡侧倾」的涌现以此为前提。钉子：混合态波峰注入。
4. **坠落自救补 `landingBrake < 1` 门 + 收紧全翅 Grab**——俯冲降落曾被自救掰回 Flap 再吃 5s
   切换锁。钉子：俯冲 engage→吸附 ≤100 tick。

## 6. 直喂契约的飞行版教训（实测）

- 巡航路点须离地形 **≥ 降落贴地探测深度（1.2m）**，否则内核会如实降落。
- 下降段要给滑翔垂度留约 **1m 余量**，否则擦墙顶。

## 7. 正式渲染

秃鹫的 `IFormalRenderer` 经 `FormalRendererFactory` 分派：K4 正交基替换派躯干 + 羽毛刀片扇
（逐羽滞后低通）。细节见 [`rainworld_render_research.md`](rainworld_render_research.md) §5。

> 注意：秃鹫渲染件在**线性解读**顶点色的条件下调色定型（与 Lizard/Centipede 同批）。翻转到
> `srgbVertexColors: true` 需要重调色，属已知遗留。

## 8. 回归

秃鹫没有独立矩阵脚本——它的配置在**主矩阵**里：

- `core/smoke` 四断言：`[CORE-VULTURE-FLIGHT]`（2000 tick 起飞→巡航→悬停→降落栖息，双跑
  bit-exact + 哈希基线 `ExpectedVultureHash`）、`[CORE-VULTURE-LAUNCH]`、
  `[CORE-VULTURE-SHIFT]`（rebase 逐字段）、`[CORE-VULTURE-ASSEMBLY]`（四预设装配不变量），
  外加 `[CORE-VULTURE-CONTRACT]`（§5 的四条评审修复）。
- `tools/run_matrix.sh` 四配置：`vulture` / `vulture-king` / `vulture-swift` 的 fly 环线反复
  越 3m 薄墙（≥21/24/28 路点 + 飞行占比 ≥80% + 越墙高度），以及 `vulture-perch` 的真降落断言。
- 2000 tick 参考值：`vulture` 29、`king` 32、`swift` 37 路点；800 tick perch 降落约 t255。

```bash
dotnet run --no-restore --project core/smoke
./tools/run_matrix.sh
```
