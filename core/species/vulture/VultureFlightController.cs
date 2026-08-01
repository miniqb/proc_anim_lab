using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.Vulture;

/// <summary>
/// 秃鹫式飞行控制器 = Body（风筝刚架 + 头拴绳）+ 若干 VultureWing（拍动/抓地翅膀）
/// + 升力/推进/悬停/配平。这是 Vulture 专属 flight backend，与
/// <see cref="LizardLocomotionController"/> 并列（该类注释预留的第一个并列生物控制器），
/// 共享 Body/地形原语，互不引用。
///
/// 与蜥蜴的根本路线差异（≙ 反编译 Vulture.Act + VultureTentacle.Fly）：
/// · **重力全程常开**（GravityScale 恒 1）——没有重力开关；悬空全靠升力对抗。
/// · 升力 = 与拍翅相位同步的 sin² 脉冲（谷值恰为 0），只注入**后脊柱单 chunk**，
///   重力却摊在全部躯干 chunk 上——约束松弛把脉冲摊分（≈÷4），周期均值与重力平衡。
///   「悬停时上下颠簸」不是 bug，是升力脉冲的直接后果（RW 手感的核心）。
/// · 垂直控制不对称：上升靠爬升脉冲 + 拍翅；**下降不施向下力**，靠倒拨拍翅相位
///   把它冻结在低升力半区 = 收翅滑翔（FlapGlideRate）。
/// · 无 locomotion 状态机：模式在**每只翅膀**上（Flap/Grab），身体状态（AirBorne=全翅
///   Flap / 栖息 = 有翅抓附）从翅膀模式组合涌现；起飞/降落由 MoveTarget 几何触发。
///
/// 输入契约与蜥蜴同名同义：MoveDir（3D 意图方向）/RunSpeed（油门）/MoveTarget（直喂点，
/// 秃鹫的原生形态——RW 就是喂路径格）。Shift/Teleport/Launch 契约方法同名移植。
/// 速度注入一律 headroom 钳制（项目范式：RW 瞄路径格天然限速，我们的胡萝卜连续跟随，
/// 必须显式封顶）。确定性：固定顺序、零随机（RW StuckBehavior 的随机抖动不移植）。
/// </summary>
public sealed class VultureFlightController
{
    public readonly Body Body;
    public readonly List<VultureWing> Wings = new();

    /// <summary>前脊柱（≙ bodyChunks[0] mainBodyChunk）：意图/悬停锚参照点，俯仰配平的「头端」。</summary>
    public readonly BodyChunk FrontSpine;

    /// <summary>后脊柱（≙ bodyChunks[1]）：升力注入点、悬停升力点、俯仰配平的「尾端」。</summary>
    public readonly BodyChunk RearSpine;

    public readonly BodyChunk ShoulderL;
    public readonly BodyChunk ShoulderR;

    /// <summary>头（喙，≙ bodyChunks[4]）：不进刚架，只有 PullOnly 拴绳 + 伺服悬持。</summary>
    public readonly BodyChunk Head;

    /// <summary>移动意图方向（**3D** 单位向量或零——与蜥蜴的 XZ 平面意图不同，飞行要竖直分量）。
    /// MoveTarget 非 null 时由内核每 tick 导出（宿主写入被覆盖，tick 末清零不残留）。</summary>
    public Vector3 MoveDir;

    /// <summary>移动意图强度 ∈ [0,1]。低于死区视为零输入。</summary>
    public float RunSpeed;

    /// <summary>可选直喂目标点（世界系，≙ RW 寻路器给的路径格中心——秃鹫的原生输入形态）。
    /// 到点 → AtMoveTarget，意图清零 → 悬停涌现（≙ hoverStill）。落点贴近地形时
    /// 降落自动触发（翅膀转 Grab）；远/悬空目标从栖息态自动触发起飞。
    /// Shift 随世界平移它，Teleport 作废清 null。</summary>
    public Vector3? MoveTarget;

    /// <summary>到点判定半径（米，3D 距离，前脊柱到喂点）。</summary>
    public float MoveTargetArriveRadius = 0.6f;

    public bool AtMoveTarget { get; private set; }

    /// <summary>统一意图死区（与蜥蜴同值同义，独立常量——翅膀/控制器不得反向引用蜥蜴类）。</summary>
    public const float MoveIntentDeadzone = 0.1f;

    public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
        && (MoveTarget is not null ? !AtMoveTarget : MoveDir != Vector3.Zero);

    // —— 品种参数（工厂灌入，语义见 VultureBreedParams）——
    public float LiftForce = 0.14f;
    public float LiftShare = 0.5f;
    public float LateralRecoil = 0.065f;
    public float FlightPropulsion = 0.015f;
    public float CrawlPropulsion = 0.03f;
    public float ClimbPulse = 0.0875f;
    public float MaxFlySpeed = 0.12f;
    public float MaxRiseSpeed = 0.12f;
    public float HoverSpring = 0.015f;
    public float PitchTrim = 0.0475f;
    public float FlapFastRate = 1f / 30f;
    public float FlapSlowRate = 0.02f;
    public float FlapGlideRate = 1f / 70f;
    public float FlapSweepMin = 5f;
    public float FlapSweepMax = 15f;
    public float NeckLength = 1.0f;

    // —— 涌现/协调参数（≙ RW 计数器，全部确定性）——
    /// <summary>降落触发距离：目标贴近地形且前脊柱进入该半径 → 全翅转 Grab（≙ 目的格
    /// 地形距离 ≤8 tile 的收缩版——我们没有格距图，用到点距离 + 落点贴地探测替代）。</summary>
    public float LandEngageRadius = 1.5f;

    /// <summary>起飞触发距离：栖息态下目标超出该距离（或悬空）→ TakeOff。</summary>
    public float TakeoffEngageRadius = 2.5f;

    /// <summary>降落后的模式切换锁（≙ dontSwitchModesCounter = 200：5 秒禁自动再切）。</summary>
    public int LandingModeLockTicks = 200;

    /// <summary>起飞后的模式切换锁（防止起飞瞬间贴地计数又把翅膀掰回 Grab）。</summary>
    public int TakeoffModeLockTicks = 40;

    /// <summary>Grab 翅找不到抓点这么久 → 自动改飞（≙ framesWithoutReaching > 60）。</summary>
    public int GrabGiveUpTicks = 60;

    /// <summary>同对另一翅已飞这么久且本翅无吸附 → 跟飞（≙ 另翅已飞 >100 tick 分支）。</summary>
    public int PairFollowFlyTicks = 100;

    /// <summary>Flap 翅撞地形迟滞计数满值 → 转 Grab（≙ framesOfHittingTerrain 30）。</summary>
    public int TerrainHitToGrabTicks = 30;

    /// <summary>坠落自救阈值（米/tick，≙ vel.y < −10px = −0.25）：全翅 Grab 且无吸附时展翅。</summary>
    public float FallRescueSpeed = 0.25f;

    // —— 状态（外部只读）——
    /// <summary>拍翅相位 ∈[0,1)（≙ wingFlap）：快半程 = 动力下拍（sin&gt;0 高升力）。</summary>
    public float WingFlap { get; private set; } = 0.75f; // 出生在低升力半区（收翅栖息）

    /// <summary>拍翅振幅 ∈[0,1]（≙ wingFlapAmplitude）：任一翅 Flap → 涨到 1，全 Grab → 0。</summary>
    public float WingFlapAmplitude { get; private set; }

    /// <summary>全翅 Flap（≙ AirBorne）。</summary>
    public bool AirBorne
    {
        get
        {
            foreach (VultureWing w in Wings)
            {
                if (w.Mode != VultureWing.WingMode.Flap)
                {
                    return false;
                }
            }
            return Wings.Count > 0;
        }
    }

    /// <summary>任一翅已吸附（栖息/悬挂的判据）。</summary>
    public bool AnyWingAttached
    {
        get
        {
            foreach (VultureWing w in Wings)
            {
                if (w.Attached)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>支撑度 ∈[0.1,1]（≙ num2 = √(Σ Support × 1/翅数)，下限 0.1）：
    /// 全部身体力的统一系数。滞后一 tick 使用（与蜥蜴支撑系同一约定）。</summary>
    public float SupportValue { get; private set; } = 0.5f;

    /// <summary>悬停锚点（≙ hoverPos：hoverStill 时锚定，漂离 1.5m 重锚）。</summary>
    public Vector3? HoverAnchor { get; private set; }

    /// <summary>降落刹车剩余 tick（≙ landingBrake：着陆 30 / 起飞 5，期间全 chunk ×0.7）。</summary>
    public int LandingBrakeTicks { get; private set; }

    /// <summary>起飞助推剩余 tick（RW 靠 VultureThruster Activate(30)——喷气推进器的
    /// Utility/jetFuel 系统不移植，收缩为固定时长的方向助推）。</summary>
    public int TakeoffBoostTicks { get; private set; }

    /// <summary>模式切换锁剩余 tick（≙ dontSwitchModesCounter）。</summary>
    public int ModeSwitchLockTicks { get; private set; }

    /// <summary>本 tick 推进意图（观测：渲染层画胡萝卜/朝向用）。</summary>
    public Vector3 LastMoveIntent { get; private set; }

    private Vector3 _brakePos;
    private bool _lastHoverStill;
    private int _noIntentTicks;
    private Vector3? _lastFedTarget;
    private readonly BodyChunk[] _frame;

    public VultureFlightController(Body body, BodyChunk frontSpine, BodyChunk rearSpine,
        BodyChunk shoulderL, BodyChunk shoulderR, BodyChunk head)
    {
        Body = body;
        FrontSpine = frontSpine;
        RearSpine = rearSpine;
        ShoulderL = shoulderL;
        ShoulderR = shoulderR;
        Head = head;
        _frame = new[] { frontSpine, rearSpine, shoulderL, shoulderR };
    }

    /// <summary>rebase：身体、翅膀、悬停锚、直喂目标整体平移，速度/吸附/相位原样保留
    /// （契约同蜥蜴 Shift——只适用于地形随体平移的场景）。</summary>
    public void Shift(Vector3 delta)
    {
        Body.Shift(delta);
        foreach (VultureWing wing in Wings)
        {
            wing.Shift(delta);
        }
        if (MoveTarget is { } fed)
        {
            MoveTarget = fed + delta;
        }
        if (_lastFedTarget is { } lastFed)
        {
            // 迟滞绑定的目标记忆随世界平移——漏掉会让 Shift 后第一 tick 误判「换了点」。
            _lastFedTarget = lastFed + delta;
        }
        if (HoverAnchor is { } anchor)
        {
            HoverAnchor = anchor + delta;
        }
        _brakePos += delta;
    }

    /// <summary>teleport：平移并作废一切位置态记忆——全翅松手（旧抓点对新位置无效）、
    /// 全翅转 Flap（空中重入，坠落自救兜底）、悬停锚/直喂目标/刹车清空。</summary>
    public void Teleport(Vector3 delta)
    {
        Shift(delta);
        foreach (VultureWing wing in Wings)
        {
            wing.ForceRelease();
            wing.SwitchMode(VultureWing.WingMode.Flap);
        }
        MoveTarget = null;
        AtMoveTarget = false;
        _lastFedTarget = null;
        HoverAnchor = null;
        LandingBrakeTicks = 0;
        TakeoffBoostTicks = 0;
        ModeSwitchLockTicks = 0;
    }

    /// <summary>宿主冲量注入（击飞/弹射）：全 chunk 加同一速度增量，全翅松手转 Flap
    /// ——被抛掷的鸟挣扎展翅，落回飞行态（≙ RW 坠落自救的主动版）。</summary>
    public void Launch(Vector3 velocityPerTick)
    {
        foreach (BodyChunk c in Body.Chunks)
        {
            c.Vel += velocityPerTick;
        }
        foreach (VultureWing wing in Wings)
        {
            wing.ForceRelease();
            wing.SwitchMode(VultureWing.WingMode.Flap);
        }
        HoverAnchor = null;
        LandingBrakeTicks = 0;
        TakeoffBoostTicks = 0;
        ModeSwitchLockTicks = 0;
    }

    /// <summary>
    /// 唯一入口：意图导出 → 相位/振幅 → 模式决策 → 身体力 → Body.Tick → 翅膀 → 支撑更新
    /// （翅膀晚于身体物理、支撑滞后一 tick，与蜥蜴帧序同一约定）。
    /// </summary>
    public void Tick(in TickContext ctx)
    {
        Vector3 up = ctx.GravityPerTick.LengthSquared() > 1e-12f
            ? -ctx.GravityPerTick.Normalized()
            : Vector3.Up;

        bool derivedMove = MoveTarget is not null;
        if (MoveTarget is { } fed)
        {
            // 到达信号带迟滞（进 1×/出 2× 半径）：悬停时升力谷值的下沉会把身体带出
            // 到点半径，无迟滞会在「悬停↔爬回」间逐 tick 抖动（宿主换点信号也跟着抖）。
            // 迟滞态绑定具体目标：宿主换点必须先复位，否则新点落在旧点的 2× 出圈半径内
            // 会被瞬时误报到达——RW 原生喂点节奏是 0.5m 格距，逐格推进的宿主会被连环
            // 假到达骗到「巡逻正常」而鸟原地悬停（评审确认缺陷 #1）。
            if (_lastFedTarget != fed)
            {
                AtMoveTarget = false;
                _lastFedTarget = fed;
            }
            float arriveDist = (fed - FrontSpine.Pos).Length();
            AtMoveTarget = arriveDist <= (AtMoveTarget
                ? MoveTargetArriveRadius * 2f
                : MoveTargetArriveRadius);
            MoveDir = AtMoveTarget ? Vector3.Zero : Dir(FrontSpine.Pos, fed);
        }
        else
        {
            AtMoveTarget = false;
            _lastFedTarget = null;
        }

        Vector3 intent = HasMoveIntent && MoveDir != Vector3.Zero ? MoveDir.Normalized() : Vector3.Zero;
        LastMoveIntent = intent;
        _noIntentTicks = intent == Vector3.Zero ? _noIntentTicks + 1 : 0;
        bool airBorne = AirBorne;
        bool hoverStill = airBorne && intent == Vector3.Zero;

        UpdateFlapPhase(airBorne, intent, up);
        UpdateFlapAmplitude();
        UpdateWingModes(ctx, intent, up);
        airBorne = AirBorne; // 模式决策可能刚切换（起飞/降落当 tick 生效）

        ApplyBodyDamping(airBorne, up);
        ApplyPitchTrim(up);
        if (airBorne)
        {
            if (hoverStill)
            {
                ApplyHover(up);
            }
            else
            {
                HoverAnchor = null;
                ApplyFlight(intent, up);
            }
        }
        else
        {
            HoverAnchor = null;
            ApplyCrawl(intent);
        }
        // 升力注入不设 AirBorne 外层门（≙ RW 升力写在每只翅膀的 Fly 分支里，不问另一翅
        // 状态）：混合翅态（一翅 Grab 过渡/quad 局部转抓）下仍在拍的翅膀必须继续托身体
        // ——「单翅失能 → 失衡侧倾」的涌现正来自逐翅注入（评审确认缺陷 #3）。
        // 内部循环自带 Mode==Flap 过滤，全翅 Grab（栖息）时天然零注入。
        ApplyWingLift(up);
        _lastHoverStill = hoverStill;

        ApplyLandingBrake();
        ApplyTakeoffBoost(intent, up);
        ApplyHeadServo(intent, airBorne);

        Body.Tick(ctx);
        TickWings(ctx, intent, up);
        UpdateSupport();

        if (derivedMove)
        {
            MoveDir = Vector3.Zero; // 派生意图不跨 tick 残留（同蜥蜴约定）
        }
    }

    /// <summary>拍翅相位（≙ Vulture.cs L789-800 + L1152）：快半程 1/30（动力下拍 15 tick）、
    /// 慢半程 0.02（回收 25 tick）；意图向下时倒拨 1/70——慢半程净速率趋零 =
    /// 相位冻结在低升力半区，收翅滑翔下降（「不施向下力」的实现）。</summary>
    private void UpdateFlapPhase(bool airBorne, Vector3 intent, Vector3 up)
    {
        WingFlap += WingFlap < 0.5f ? FlapFastRate : FlapSlowRate;
        if (airBorne && intent.Dot(up) < -0.2f)
        {
            WingFlap -= FlapGlideRate;
        }
        if (WingFlap > 1f)
        {
            WingFlap -= 1f;
        }
    }

    /// <summary>振幅（≙ L1107-1118）：任一翅 Flap → +1/30 涨到 1（0.75s 展开——起飞瞬间
    /// 升力未满，由起飞助推补）；全部 Grab → 归零（栖息收翅）。</summary>
    private void UpdateFlapAmplitude()
    {
        bool anyFlap = false;
        foreach (VultureWing w in Wings)
        {
            anyFlap |= w.Mode == VultureWing.WingMode.Flap;
        }
        WingFlapAmplitude = anyFlap
            ? Mathf.Min(1f, WingFlapAmplitude + 1f / 30f)
            : 0f;
    }

    /// <summary>
    /// 模式决策（≙ Act L998-1101 + VultureTentacle 自主切换；全部确定性计数，
    /// RW 的随机抖动/杆子搜索不移植）：
    /// · 坠落自救：快速下坠 + 全翅 Grab 无吸附 → 全翅展开（≙ L1091-1101）。
    /// · 降落：直喂目标贴近地形且已进入触发半径 → 全翅 Grab + AirBrake(30) + 5s 切换锁。
    /// · 起飞：栖息态 + 意图目标远/悬空/向上 → TakeOff（全翅 Flap + AirBrake(5) + 助推）。
    /// · 逐翅自主：Grab 久抓不到 → 改飞；同对翅长期先飞 → 跟飞；Flap 持续撞地形 → 转抓。
    /// </summary>
    private void UpdateWingModes(in TickContext ctx, Vector3 intent, Vector3 up)
    {
        if (ModeSwitchLockTicks > 0)
        {
            ModeSwitchLockTicks--;
        }

        // 坠落自救不受切换锁约束但受降落刹车门（≙ RW L1091：vel.y<−10 且**全翅** Climb
        // 且无抓地且 landingBrake<1）——刹车门是着陆减速期的防弹跳保护：快速下降着陆时
        // 竖速仍超阈值，没有它自救会把刚转 Grab 的翅膀掰回 Flap，而降落分支又被 5s
        // 切换锁挡住，鸟在目标上空弹跳悬停（评审确认缺陷 #4）。
        bool allGrab = Wings.Count > 0;
        foreach (VultureWing w in Wings)
        {
            allGrab &= w.Mode == VultureWing.WingMode.Grab;
        }
        if (LandingBrakeTicks < 1 && allGrab && !AnyWingAttached
            && RearSpine.Vel.Dot(up) < -FallRescueSpeed)
        {
            foreach (VultureWing w in Wings)
            {
                w.SwitchMode(VultureWing.WingMode.Flap);
            }
            return;
        }

        if (ModeSwitchLockTicks > 0)
        {
            return;
        }

        bool airBorne = AirBorne;
        if (airBorne && MoveTarget is { } target
            && (target - FrontSpine.Pos).Length() < LandEngageRadius
            && TerrainNear(ctx, target, intent, up))
        {
            // 降落（≙ L1071-1079）：全翅转抓 + 空气刹车 + 禁自动再切。
            foreach (VultureWing w in Wings)
            {
                w.SwitchMode(VultureWing.WingMode.Grab);
            }
            AirBrake(30);
            ModeSwitchLockTicks = LandingModeLockTicks;
            return;
        }

        if (!airBorne && HasMoveIntent)
        {
            bool aerialTarget = MoveTarget is { } t
                ? (t - FrontSpine.Pos).Length() > TakeoffEngageRadius || !TerrainNear(ctx, t, intent, up)
                : intent.Dot(up) > 0.3f;
            if (aerialTarget)
            {
                TakeOff();
                return;
            }
        }

        // 逐翅自主切换（固定列表序保确定性）。Flap→Grab 需要**刚架**贴地形的证据或
        // 持续无移动意图：RW Fly 模式的翅节撞墙会走穿墙相位（terrainHitsBeforePhase=0），
        // 撞地形转爬实际发生在身体蹭进通道时——只看翅节刷墙会让高空横穿墙顶的巡航
        // （翅尖拍动下探 ~3.5m 扫过墙体）被误判成「该抓墙了」（fly 路线实测）。
        // 证据只认四个刚架 chunk：头是拴绳上的钟摆（悬垂比前脊柱低 ~1.3m），滑翔下降
        // 的过冲让它擦一下地板不代表身体进了通道（fly 路线第二轮实测）。
        bool bodyTouching = FrontSpine.TerrainContact || RearSpine.TerrainContact
            || ShoulderL.TerrainContact || ShoulderR.TerrainContact;
        foreach (VultureWing w in Wings)
        {
            if (w.Mode == VultureWing.WingMode.Grab)
            {
                bool giveUp = !w.Attached && w.FramesWithoutGrip > GrabGiveUpTicks;
                bool followPair = !w.Attached && w.Pair is { Mode: VultureWing.WingMode.Flap } pair
                    && pair.ModeTicks > PairFollowFlyTicks;
                if (giveUp || followPair)
                {
                    w.SwitchMode(VultureWing.WingMode.Flap);
                }
            }
            else if (w.TerrainHitFrames >= TerrainHitToGrabTicks
                && (bodyTouching || _noIntentTicks > 10))
            {
                // 无意图路径要求**持续**无意图：巡航换路点的单 tick 到达空窗不算——
                // 横穿墙顶攒下的撞击计数还没衰减完，会在那一 tick 被误当成栖息意愿。
                w.SwitchMode(VultureWing.WingMode.Grab);
            }
        }
    }

    /// <summary>起飞（≙ TakeOff L1333-1347）：全翅 Flap + AirBrake(5) + 固定时长助推
    /// （RW 用两台喷气推进器 Activate(30)；jetFuel/Utility 系统不移植）。</summary>
    private void TakeOff()
    {
        foreach (VultureWing w in Wings)
        {
            w.SwitchMode(VultureWing.WingMode.Flap);
        }
        AirBrake(5);
        TakeoffBoostTicks = 30;
        ModeSwitchLockTicks = TakeoffModeLockTicks;
    }

    /// <summary>空气刹车（≙ AirBrake：记录当时后脊柱位置为刹车锚点）。</summary>
    private void AirBrake(int ticks)
    {
        LandingBrakeTicks = ticks;
        _brakePos = RearSpine.Pos;
    }

    /// <summary>落点贴地探测：目标沿重力向短射线（栖息面）或沿意图水平向（墙面）命中
    /// 即视为「地形近旁」——降落/起飞判定的地形接近性（≙ RW 目的格地形距离图的射线替代）。</summary>
    private bool TerrainNear(in TickContext ctx, Vector3 target, Vector3 intent, Vector3 up)
    {
        if (ctx.Terrain.Raycast(target + up * 0.3f, target - up * 1.2f, out _))
        {
            return true;
        }
        Vector3 horiz = intent - up * intent.Dot(up);
        return horiz.LengthSquared() > 1e-8f
            && ctx.Terrain.Raycast(target, target + horiz.Normalized() * 1.0f, out _);
    }

    /// <summary>阻尼与栖息辅助（≙ L936-956）：头恒 ×0.9；躯干飞行 ×0.98、栖息
    /// ×Lerp(0.98,0.9,support)；栖息且有支撑时部分反重力——抓得越牢辅助越小
    /// （余量由吸附兜住），抓得虚反而上浮助其够到抓点。飞行态**没有**反重力：
    /// 升力全部来自拍翅脉冲。</summary>
    private void ApplyBodyDamping(bool airBorne, Vector3 up)
    {
        Head.Vel *= 0.9f;
        float support = SupportValue;
        foreach (BodyChunk c in FrameChunks())
        {
            if (airBorne)
            {
                c.Vel *= 0.98f;
            }
            else
            {
                c.Vel *= Mathf.Lerp(0.98f, 0.9f, support);
                if (support > 0.1f)
                {
                    c.Vel += up * Mathf.Lerp(0.03f, 0.0125f, support);
                }
            }
        }
    }

    /// <summary>俯仰配平（≙ L957-958）：尾端抬、头端压 ∝ 速度——移动越快身体轴越
    /// 「头朝下」。一对反向竖直力就是速度→俯仰角的全部实现，无姿态控制器。</summary>
    private void ApplyPitchTrim(Vector3 up)
    {
        float speedFac = Mathf.Clamp(
            Mathf.InverseLerp(0.025f, 0.175f, FrontSpine.Vel.Length()), 0f, 1f);
        float trim = PitchTrim * SupportValue * speedFac;
        RearSpine.Vel += up * trim;
        FrontSpine.Vel -= up * trim;
    }

    /// <summary>悬停（≙ L1119-1131）：锚定当前位（漂离 1.5m 重锚），后脊柱微升力 +
    /// 全躯干强阻尼 + 朝锚点弹簧。上下颠簸来自升力脉冲的谷值归零，弹簧只负责钉住位置。</summary>
    private void ApplyHover(Vector3 up)
    {
        if (!_lastHoverStill || HoverAnchor is null
            || (FrontSpine.Pos - HoverAnchor.Value).Length() > 1.5f)
        {
            // 直喂**到点**悬停：锚钉在喂点上（≙ RW hoverPos = 到达格中心），弹簧把身体
            // 钉回目标而不是「恰好漂到的地方」。必须查 AtMoveTarget——零油门（RunSpeed
            // 死区）也会走进 hover 分支，此时喂点可能远在天边：拿它当锚等于绕过油门
            // 全速自动驾驶过去（弹簧饱和注入不乘 RunSpeed，评审确认缺陷 #2）。
            HoverAnchor = AtMoveTarget && MoveTarget is { } fed ? fed : FrontSpine.Pos;
        }
        Vector3 anchor = HoverAnchor.Value;
        float support = SupportValue;
        RearSpine.Vel += up * (0.0025f * support);
        foreach (BodyChunk c in FrameChunks())
        {
            c.Vel *= Mathf.Lerp(1f, 0.9f, support);
            c.Vel += (anchor - FrontSpine.Pos).LimitLength(0.25f) / 0.25f * (HoverSpring * support);
        }
    }

    /// <summary>移动分支（≙ L1132-1191）：垂直分量整形（上行减半、**永不主动下压**——
    /// 下行交给滑翔相位），目标在上方时后脊柱吃爬升脉冲，全躯干朝意图注速。
    /// 注入按 headroom 钳制（RW 无显式极速、靠乘性阻尼涌现；我们的胡萝卜连续跟随，
    /// MaxFlySpeed/MaxRiseSpeed 是显式刹车，同蜥蜴 MaxMoveSpeed 论证）。</summary>
    private void ApplyFlight(Vector3 intent, Vector3 up)
    {
        if (intent == Vector3.Zero)
        {
            return;
        }
        float support = SupportValue;
        float vertical = intent.Dot(up);
        Vector3 a = intent;
        if (vertical > 0f)
        {
            a -= up * (vertical * 0.5f); // 上行分量减半：竖直航向主要交给爬升脉冲
        }
        else
        {
            a -= up * vertical; // 下行分量清零：永不主动下压，下降靠冻结拍翅相位
        }

        if (vertical > 0.2f)
        {
            float riseHeadroom = MaxRiseSpeed - AverageFrameVelocity().Dot(up);
            if (riseHeadroom > 0f)
            {
                RearSpine.Vel += up * Mathf.Min(ClimbPulse, riseHeadroom);
            }
        }

        if (a.LengthSquared() < 1e-8f)
        {
            return;
        }
        Vector3 aDir = a.Normalized();
        float force = FlightPropulsion * support * RunSpeed;
        foreach (BodyChunk c in FrameChunks())
        {
            float headroom = MaxFlySpeed - c.Vel.Dot(aDir);
            if (headroom > 0f)
            {
                c.Vel += a * Mathf.Min(force, headroom);
            }
        }
    }

    /// <summary>贴地爬行（≙ 同一施力代码的非 AirBorne 档，力 1.2px）：栖息态朝近处
    /// 地面目标蹒跚挪动——RW 秃鹫在地面就是用翅膀撑着爬。</summary>
    private void ApplyCrawl(Vector3 intent)
    {
        if (intent == Vector3.Zero)
        {
            return;
        }
        float force = CrawlPropulsion * SupportValue * RunSpeed;
        foreach (BodyChunk c in FrameChunks())
        {
            float headroom = MaxFlySpeed - c.Vel.Dot(intent);
            if (headroom > 0f)
            {
                c.Vel += intent * Mathf.Min(force, headroom);
            }
        }
    }

    /// <summary>升力注入（≙ VultureTentacle L387-393，每只 Flap 翅一份）：
    /// (share+share·sin2πφ)² 脉冲——峰值满力、**谷值恰好为 0**（悬停颠簸的来源），
    /// 只打后脊柱（重力摊 4 chunk、脉冲靠约束摊分，周期均值与重力平衡）。
    /// 横向反冲沿本翅水平展向反方向：双翅逐 tick 近抵消，残差 = 有机侧摆；
    /// 翅膀数不对称（受伤/品种）时自然侧倾。竖直注入按 MaxRiseSpeed headroom 钳制。</summary>
    private void ApplyWingLift(Vector3 up)
    {
        float ampFac = Mathf.Lerp(0.5f, 1f, WingFlapAmplitude);
        float wave = LiftShare + LiftShare * Mathf.Sin(Mathf.Tau * WingFlap);
        float lift = wave * wave * LiftForce * ampFac;
        foreach (VultureWing w in Wings)
        {
            if (w.Mode != VultureWing.WingMode.Flap)
            {
                continue;
            }
            // headroom 逐翅重算：多翅同 tick 叠加注入不得越过上升极速。
            float headroom = MaxRiseSpeed - AverageFrameVelocity().Dot(up);
            if (headroom > 0f && lift > 0f)
            {
                RearSpine.Vel += up * Mathf.Min(lift, headroom);
            }
            Vector3 spanHoriz = SpanHorizontal(w, up);
            RearSpine.Vel -= spanHoriz * (wave * LateralRecoil * ampFac);
        }
    }

    /// <summary>降落/起飞刹车（≙ landingBrake L632-640）：全 chunk 强衰减 + 后脊柱
    /// 弹回刹车锚点（≤0.05 米/tick）。</summary>
    private void ApplyLandingBrake()
    {
        if (LandingBrakeTicks <= 0)
        {
            return;
        }
        LandingBrakeTicks--;
        foreach (BodyChunk c in Body.Chunks)
        {
            c.Vel *= 0.7f;
        }
        RearSpine.Vel += (_brakePos - RearSpine.Pos).LimitLength(1f) * 0.05f;
    }

    /// <summary>起飞助推（RW 双喷气推进器 Activate(30) 的收缩版）：固定 30 tick 沿
    /// 「上 + 意图」注速，起飞瞬间振幅未满的升力空窗由它补。headroom 钳制。</summary>
    private void ApplyTakeoffBoost(Vector3 intent, Vector3 up)
    {
        if (TakeoffBoostTicks <= 0)
        {
            return;
        }
        TakeoffBoostTicks--;
        Vector3 dir = (up * 0.7f + intent * 0.5f);
        if (dir.LengthSquared() < 1e-8f)
        {
            dir = up;
        }
        dir = dir.Normalized();
        foreach (BodyChunk c in FrameChunks())
        {
            float headroom = MaxRiseSpeed - c.Vel.Dot(up);
            if (headroom > 0f)
            {
                c.Vel += dir * Mathf.Min(0.015f, headroom);
            }
        }
    }

    /// <summary>头部悬持（RW 物理脖链的收缩：头由 PullOnly 拴绳 + 本伺服悬持，渲染层
    /// 画脖曲线）。≙ L1399-1424 配方：先在身体参考系内强阻尼（飞行 0.2 / 着地 0.75
    /// ——附肢跟飞的关键），再朝「前脊柱 + 前向×脖长（+ 意图偏置）」的静息位吸
    /// （力度飞行 2px=0.05 / 着地 6px=0.15）。头有重力（≙ RW 头是 body chunk），
    /// 伺服强度足以对抗。</summary>
    private void ApplyHeadServo(Vector3 intent, bool airBorne)
    {
        Vector3 forward = FrontSpine.Rotation; // 前脊柱参照后脊柱：指向体前
        if (forward.LengthSquared() < 1e-8f)
        {
            forward = Vector3.Up;
        }
        Vector3 lookDir = intent == Vector3.Zero
            ? forward
            : (forward * 0.6f + intent * 0.4f).Normalized();
        Vector3 rest = FrontSpine.Pos + lookDir * NeckLength;

        float relDamping = airBorne ? 0.2f : 0.75f;
        Head.Vel = (Head.Vel - FrontSpine.Vel) * relDamping + FrontSpine.Vel;
        Head.Vel += (rest - Head.Pos).LimitLength(1f) * (airBorne ? 0.05f : 0.15f);
    }

    /// <summary>固定顺序更新每只翅膀（身体物理之后，≙ RW 帧序），传入统一相位与几何基。</summary>
    private void TickWings(in TickContext ctx, Vector3 intent, Vector3 up)
    {
        float sweep = Mathf.Lerp(FlapSweepMin, FlapSweepMax, WingFlapAmplitude);
        Vector3 bodyUp = BodyUp(up);
        foreach (VultureWing w in Wings)
        {
            Vector3 spanRaw = Dir(w.OppositeAnchor.Pos, w.Anchor.Pos);
            Vector3 spanHoriz = SpanHorizontal(w, up);
            // 展向 = 本肩背离对肩 与 纯水平外侧 的 50/50 混合（≙ VT L377-378：
            // 身体倾斜时翼展跟着转一半）；第二对翅膀再向后倾（四翼拓扑）。
            Vector3 spanMix = spanRaw.Lerp(spanHoriz, 0.5f);
            if (w.SpanTilt > 0f)
            {
                Vector3 forward = FrontSpine.Rotation;
                spanMix -= forward * w.SpanTilt;
            }
            spanMix = spanMix.LengthSquared() < 1e-8f ? spanRaw : spanMix.Normalized();

            if (w.Mode == VultureWing.WingMode.Flap)
            {
                w.TickFlap(ctx, WingFlap, WingFlapAmplitude, sweep, spanMix, spanHoriz, bodyUp);
            }
            else
            {
                // 期望抓点方向（≙ UpdateDesiredGrabPos）：本侧斜下方，移动时三成向意图偏。
                Vector3 desired = (spanHoriz - up * 0.8f).Normalized();
                if (intent != Vector3.Zero)
                {
                    desired = (desired * 0.7f + intent * 0.3f).Normalized();
                }
                w.TickGrab(ctx, desired, spanMix, SupportValue, w.Anchor.Vel);
            }
        }
    }

    /// <summary>支撑度更新（翅膀更新后重算，下 tick 使用）：√(Σ Support / 翅数)，下限 0.1
    /// （≙ num2 = √(Σ×0.5) 的翅数归一版——四翼品种同一量纲）。</summary>
    private void UpdateSupport()
    {
        float sum = 0f;
        foreach (VultureWing w in Wings)
        {
            sum += w.Support();
        }
        float normalized = Wings.Count == 0 ? 0f : sum / Wings.Count;
        SupportValue = Mathf.Max(0.1f, Mathf.Sqrt(Mathf.Clamp(normalized, 0f, 1f)));
    }

    /// <summary>机体上方向：右（肩 L→R）× 前（后脊柱→前脊柱）；退化回世界上。
    /// 拍动法向——RW 2D 的 perp 摆动在 3D 必须显式选定这根轴（CLAUDE「3D 朝向边界」）。</summary>
    private Vector3 BodyUp(Vector3 worldUp)
    {
        Vector3 right = ShoulderR.Pos - ShoulderL.Pos;
        Vector3 forward = FrontSpine.Pos - RearSpine.Pos;
        Vector3 bodyUp = right.Cross(forward);
        if (bodyUp.LengthSquared() < 1e-8f)
        {
            return worldUp;
        }
        bodyUp = bodyUp.Normalized();
        // 翻倒时不把「下」当「上」拍：与世界上反向则翻转（拍动轴保持朝天半球）。
        return bodyUp.Dot(worldUp) < 0f ? -bodyUp : bodyUp;
    }

    /// <summary>本翅水平展向（外侧）：肩轴在水平面内的投影，退化回肩轴原方向。</summary>
    private Vector3 SpanHorizontal(VultureWing w, Vector3 up)
    {
        Vector3 spanRaw = Dir(w.OppositeAnchor.Pos, w.Anchor.Pos);
        Vector3 horiz = spanRaw - up * spanRaw.Dot(up);
        return horiz.LengthSquared() < 1e-8f ? spanRaw : horiz.Normalized();
    }

    private Vector3 AverageFrameVelocity()
    {
        return (FrontSpine.Vel + RearSpine.Vel + ShoulderL.Vel + ShoulderR.Vel) * 0.25f;
    }

    private BodyChunk[] FrameChunks() => _frame;

    private static Vector3 Dir(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        return d.LengthSquared() < 1e-12f ? Vector3.Up : d.Normalized();
    }
}
