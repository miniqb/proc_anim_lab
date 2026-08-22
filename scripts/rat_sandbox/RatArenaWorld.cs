using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Species.RatFiend;
using ProcAnim.Core.Terrain;
using ProcAnimLab.DaddyLongLegsSandbox; // 房间尺度常量真相源 DaddyLongLegsMazeBuilder（RoomHeight）
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.RatArena;

/// <summary>
/// 鼠煞枪击竞技场（探索场景，不进矩阵）：封闭矩形大房间里，鼠煞持续追逐第一人称玩家，
/// 近身后双臂前伸抓取，抓住即束缚 + 咬合连击；玩家的反制是手枪——头/躯干命中记数，
/// 臂/腿命中当场断肢（断臂削攻击、断腿转爬行追击，全部内核涌现），断落段独立 Verlet
/// 跌地。闭环「追逐 → 扑抓 → 束缚咬合 → 挣开推离 → 再追」+ R 重开。
///
/// 玩法全部在宿主层；内核只走既有攻击接缝（GrabTarget/MouthDrive 输入 +
/// HandsOnTarget/CrawlFactor 观测 + Sever 断肢），零内核改动（Daddy 竞技场惯例）。
/// 命中判定的关节几何走 <see cref="Render.RatFiendJointMath"/> 单一真相源。
/// 本场景只有第一人称与正式渲染视图：无自由飞相机、无白盒切换、无决定论路线机器。
/// </summary>
public partial class RatArenaWorld : Node3D
{
    private const double TickDt = 0.025;
    private const int TicksPerSecond = 40;

    /// <summary>
    /// 玩家胶囊的镜像常量（真相源 <see cref="ArenaFirstPersonPlayer"/> 的胶囊
    /// r0.35 / 中心抬 0.85）：GrabTarget 与命中遮挡都以这个球心为准。改玩家规格需同步。
    /// </summary>
    private const float PlayerChunkCenterY = 0.85f;
    private const float PlayerChunkRadius = 0.35f;

    /// <summary>两端出生点离端墙的距离（怪物 +X 端 / 玩家 −X 端，BoxRoomArenaBuilder 惯例）。</summary>
    private const float SpawnEndInset = 4f;

    /// <summary>扑抓超时/脱围放弃后的短恢复（秒）——比咬合后的完整冷却短，扑空要很快再追。</summary>
    private const float StrikeAbortRecoverSeconds = 0.5f;

    /// <summary>枪线显示时长（秒，渲染侧）、命中闪标时长与命中 toast 停留时长。</summary>
    private const float TracerSeconds = 0.06f;
    private const float HitMarkerSeconds = 0.18f;
    private const float ToastSeconds = 1.0f;

    /// <summary>咬合后中央大字「BITTEN ×n」的停留时长（秒）。</summary>
    private const float BittenPromptSeconds = 1.2f;

    /// <summary>断肢/咬合镜头 kick：多正弦 × 冲量包络（Daddy UpdateCameraShake 只留冲量项），
    /// 旋转峰值 0.8° / 位移 0.01m，基频同 Daddy 竞技场手感档。</summary>
    private const float KickDegrees = 0.8f;
    private const float KickOffsetMeters = 0.01f;
    private const float KickFrequencyHz = 11f;
    private const float KickMax = 2f;

    // ---- Inspector 导出（Daddy 纪律：命令行零个、全 Inspector、默认值唯一真相源，
    //      生效值打在 ready 行）----

    [ExportGroup("Arena / Creature")]
    [Export(PropertyHint.Enum, "gaunt,dusk,broad,whelp")]
    public string DefaultPreset { get; set; } = "gaunt";

    [Export]
    public bool FormalRender { get; set; } = true;

    [Export(PropertyHint.Range, "40,1000,1")]
    public int HostPhysicsTps { get; set; } = 40;

    [Export(PropertyHint.Range, "1,100,0.5")]
    public float GravityMps2 { get; set; } = 36f;

    [ExportGroup("Arena / Room")]
    [Export(PropertyHint.Range, "12,80,1")]
    public float ArenaWidth { get; set; } = 26f;

    [Export(PropertyHint.Range, "12,80,1")]
    public float ArenaDepth { get; set; } = 20f;

    [ExportGroup("Arena / Chase")]
    [Export(PropertyHint.Range, "0.05,8,0.05")]
    public float ChaseArriveRadius { get; set; } = 1.2f;

    /// <summary>油门远近映射：距离 ≥ Far 全速冲刺，≤ Near 收到 RunSpeedNear 的逼近慢步。</summary>
    [Export(PropertyHint.Range, "1,40,0.5")]
    public float RunFarDistance { get; set; } = 6f;

    [Export(PropertyHint.Range, "0.5,20,0.5")]
    public float RunNearDistance { get; set; } = 2.5f;

    [Export(PropertyHint.Range, "0.05,1,0.05")]
    public float RunSpeedNear { get; set; } = 0.35f;

    [ExportGroup("Arena / Attack")]
    /// <summary>攻击门：胸到玩家胶囊心 ≤ 此距离且面向大致对准才发起扑抓。</summary>
    [Export(PropertyHint.Range, "0.5,6,0.05")]
    public float AttackStartRange { get; set; } = 1.8f;

    /// <summary>扑抓期玩家距离超过 AttackStartRange × 此倍率即放弃。</summary>
    [Export(PropertyHint.Range, "1,4,0.05")]
    public float AttackAbortScale { get; set; } = 1.5f;

    /// <summary>扑抓超时（秒）：这么久还没双手到位就放弃。</summary>
    [Export(PropertyHint.Range, "0.25,5,0.05")]
    public float StrikeTimeoutSeconds { get; set; } = 1.0f;

    /// <summary>咬合脚本：束缚后第这么多 tick 起张嘴（MouthDrive=1）。</summary>
    [Export(PropertyHint.Range, "1,200,1")]
    public int BiteWindupTicks { get; set; } = 20;

    /// <summary>张嘴保持的 tick 数，之后合嘴 = 咬中（计数 + 镜头 kick + 放人）。</summary>
    [Export(PropertyHint.Range, "1,200,1")]
    public int BiteHoldTicks { get; set; } = 20;

    /// <summary>咬合放人后的攻击冷却（Recover 相位时长；扑空走更短的 0.5s 常量）。</summary>
    [Export(PropertyHint.Range, "0.25,20,0.25")]
    public float AttackCooldownSeconds { get; set; } = 2.5f;

    /// <summary>放人瞬间给玩家的背向推离速度（米/秒）——别站在原地立刻被再抓。</summary>
    [Export(PropertyHint.Range, "0,10,0.25")]
    public float ReleaseShoveSpeed { get; set; } = 3.0f;

    /// <summary>束缚期镜头接管的阻尼时间常数（≈ 该秒数内基本完成对准怪物头）。</summary>
    [Export(PropertyHint.Range, "0.1,3,0.05")]
    public float CameraTakeoverSeconds { get; set; } = 0.4f;

    [ExportGroup("Arena / Gun")]
    [Export(PropertyHint.Range, "5,200,1")]
    public float GunRange { get; set; } = 60f;

    [Export(PropertyHint.Range, "0.05,2,0.05")]
    public float GunCooldownSeconds { get; set; } = 0.25f;

    /// <summary>逐部位瞄准冗余（米，加在测试体半径上）：头小、躯干中、四肢大——
    /// 四肢视觉极细（管径 0.035~0.05m），冗余给足才好打（Daddy 触手同一道理）。</summary>
    [Export(PropertyHint.Range, "0,0.6,0.01")]
    public float HeadAimAssist { get; set; } = 0.12f;

    [Export(PropertyHint.Range, "0,0.6,0.01")]
    public float TorsoAimAssist { get; set; } = 0.10f;

    [Export(PropertyHint.Range, "0,0.6,0.01")]
    public float LimbAimAssist { get; set; } = 0.22f;

    /// <summary>true=臂/腿命中当场断肢；false=只记命中（观察追逐/抓取闭环时关掉）。</summary>
    [Export]
    public bool SeverOnHit { get; set; } = true;

    [ExportGroup("Arena / Sever")]
    /// <summary>断肢镜头 kick 的衰减时间常数（秒）。</summary>
    [Export(PropertyHint.Range, "0.05,2,0.05")]
    public float SeverShakeSeconds { get; set; } = 0.3f;

    /// <summary>断肢踉跄冲量（米/tick，沿枪向水平分量注入胸/髋——内核 opt-in 参数）。</summary>
    [Export(PropertyHint.Range, "0,0.3,0.01")]
    public float SeverStaggerImpulse { get; set; } = 0.06f;

    // ---- 运行态 ----

    private enum ArenaPhase
    {
        Chase,
        Strike,
        Grabbed,
        Recover,
    }

    private readonly RaycastTerrainQuery _terrain = new();
    private BoxRoomArenaBuilder _arena = null!;
    private RatFiendParams _preset = null!;
    private RatFiendLocomotionController _controller = null!;
    private ProcAnimLab.Render.IFormalRenderer? _formal;
    private ArenaFirstPersonPlayer _player = null!;
    private RatArenaHud _hud = null!;
    private Camera3D _bootCamera = null!;

    private Vector3 _gravityPerTick;
    private Vector3 _roomMin;
    private Vector3 _roomMax;
    private double _tickAccumulator;
    private long _tick;
    private bool _fatal;

    private ArenaPhase _phase = ArenaPhase.Chase;
    private long _strikeStartTick;
    private long _grabStartTick;
    private long _recoverUntilTick;
    private int _biteCount;
    private long _bittenPromptUntilTick = -1;

    // 手枪
    private bool _shotQueued;
    private long _nextShotAtTick;
    private ProcAnimLab.Render.TubeMeshBuilder? _tracer;
    private readonly List<ProcAnimLab.Render.TubeStation> _tracerStations = new();
    private Vector3 _tracerFrom;
    private Vector3 _tracerTo;
    private float _tracerTtl;
    private float _hitMarkerTtl;
    private string _toastText = "";
    private float _toastTtl;

    // 断落段
    private readonly List<RatSeveredPiece> _pieces = new();

    // 镜头 kick（纯渲染侧：多正弦 × 冲量包络，无 RNG，不进物理与哈希）
    private float _kick;
    private float _kickTime;
    private bool _kickApplied;

    public override void _Ready()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        _bootCamera = GetNode<Camera3D>("Camera3D");
        if (!ValidateExports())
        {
            _fatal = true;
            GetTree().Quit(2);
            return;
        }

        _gravityPerTick = new Vector3(0f, -GravityMps2 * (float)(TickDt * TickDt), 0f);
        Engine.PhysicsTicksPerSecond = HostPhysicsTps;

        try
        {
            _arena = new BoxRoomArenaBuilder(
                Vector3.Zero, ArenaWidth, ArenaDepth, SpawnEndInset);
        }
        catch (InvalidOperationException error)
        {
            GD.PushError($"[RAT-ARENA] {error.Message}");
            _fatal = true;
            GetTree().Quit(2);
            return;
        }
        _arena.Build(this);
        // 断落物钳位面取墙体**内表面**：四墙墙心骑在内径边界平面上、向房内突出半墙厚——
        // 名义平面钳位会让断肢半埋进墙（评审修复轮）。地板/天花板走面恰在名义平面上，不缩。
        float halfWall = DaddyLongLegsMazeBuilder.WallThickness * 0.5f;
        _roomMin = _arena.Origin + new Vector3(halfWall, 0f, halfWall);
        _roomMax = _arena.Origin + new Vector3(
            _arena.InteriorWidth - halfWall,
            DaddyLongLegsMazeBuilder.RoomHeight,
            _arena.InteriorDepth - halfWall);

        SpawnRat();

        _player = new ArenaFirstPersonPlayer { Name = "ArenaPlayer" };
        AddChild(_player);
        _player.Place(_arena.PlayerSpawn, _arena.MonsterSpawn);
        _player.SetActive(true);

        _hud = new RatArenaHud();
        _hud.Build(this);

        _tracer = new ProcAnimLab.Render.TubeMeshBuilder();
        _tracer.Build(this, srgbVertexColors: true);

        GD.Print($"[RAT-ARENA] ready preset={_preset.Id} tps={HostPhysicsTps} " +
                 $"formal={FormalRender} room={ArenaWidth:F0}x{ArenaDepth:F0}m " +
                 $"startDistance={_controller.Chest.Pos.DistanceTo(_player.EyePosition):F1}m");
    }

    private bool ValidateExports()
    {
        RatFiendParams? preset;
        try
        {
            preset = RatFiendFactory.ById($"ratfiend/{DefaultPreset}");
        }
        catch (ArgumentException)
        {
            return Fail($"DefaultPreset '{DefaultPreset}' is not gaunt/dusk/broad/whelp");
        }
        if (HostPhysicsTps is < 40 or > 1000)
            return Fail($"HostPhysicsTps must be in [40,1000], got {HostPhysicsTps}");
        if (!FinitePositive(GravityMps2))
            return Fail($"GravityMps2 must be finite and positive, got {GravityMps2}");
        if (!FinitePositive(ChaseArriveRadius))
            return Fail($"ChaseArriveRadius must be finite and positive, got {ChaseArriveRadius}");
        if (!FinitePositive(RunNearDistance) || !float.IsFinite(RunFarDistance)
            || RunFarDistance <= RunNearDistance)
        {
            return Fail($"RunFarDistance ({RunFarDistance}) must exceed RunNearDistance ({RunNearDistance})");
        }
        if (!FinitePositive(RunSpeedNear) || RunSpeedNear > 1f)
            return Fail($"RunSpeedNear must be in (0,1], got {RunSpeedNear}");
        if (!FinitePositive(AttackStartRange))
            return Fail($"AttackStartRange must be finite and positive, got {AttackStartRange}");
        if (!float.IsFinite(AttackAbortScale) || AttackAbortScale < 1f)
            return Fail($"AttackAbortScale must be finite and >= 1, got {AttackAbortScale}");
        if (!FinitePositive(StrikeTimeoutSeconds))
            return Fail($"StrikeTimeoutSeconds must be finite and positive, got {StrikeTimeoutSeconds}");
        if (BiteWindupTicks < 1)
            return Fail($"BiteWindupTicks must be >= 1, got {BiteWindupTicks}");
        if (BiteHoldTicks < 1)
            return Fail($"BiteHoldTicks must be >= 1, got {BiteHoldTicks}");
        if (!FinitePositive(AttackCooldownSeconds))
            return Fail($"AttackCooldownSeconds must be finite and positive, got {AttackCooldownSeconds}");
        if (!float.IsFinite(ReleaseShoveSpeed) || ReleaseShoveSpeed < 0f)
            return Fail($"ReleaseShoveSpeed must be finite and >= 0, got {ReleaseShoveSpeed}");
        if (!FinitePositive(CameraTakeoverSeconds))
            return Fail($"CameraTakeoverSeconds must be finite and positive, got {CameraTakeoverSeconds}");
        if (!FinitePositive(GunRange))
            return Fail($"GunRange must be finite and positive, got {GunRange}");
        if (!FinitePositive(GunCooldownSeconds))
            return Fail($"GunCooldownSeconds must be finite and positive, got {GunCooldownSeconds}");
        if (!FiniteNonNegative(HeadAimAssist) || !FiniteNonNegative(TorsoAimAssist)
            || !FiniteNonNegative(LimbAimAssist))
        {
            return Fail($"aim assists must be finite and >= 0, got " +
                        $"{HeadAimAssist}/{TorsoAimAssist}/{LimbAimAssist}");
        }
        if (!FinitePositive(SeverShakeSeconds))
            return Fail($"SeverShakeSeconds must be finite and positive, got {SeverShakeSeconds}");
        if (!FiniteNonNegative(SeverStaggerImpulse))
            return Fail($"SeverStaggerImpulse must be finite and >= 0, got {SeverStaggerImpulse}");

        _preset = preset;
        return true;

        static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;
        static bool FiniteNonNegative(float value) => float.IsFinite(value) && value >= 0f;
        static bool Fail(string message)
        {
            GD.PushError($"[RAT-ARENA] invalid scene configuration: {message}");
            return false;
        }
    }

    /// <summary>（重）建控制器与正式渲染件（重开安全）。出生朝向 = 望向玩家端。</summary>
    private void SpawnRat()
    {
        Vector3 forward = _arena.PlayerSpawn - _arena.MonsterSpawn;
        forward.Y = 0f;
        _controller = RatFiendFactory.CreateController(_arena.MonsterSpawn, forward, _preset);
        _formal?.Clear();
        _formal = null;
        if (FormalRender)
        {
            _formal = new ProcAnimLab.Render.RatFiendFormalRenderer(_controller, DefaultPreset);
            _formal.Build(this);
            _formal.SetVisible(true);
        }
    }

    // ---- 固定步长循环 ----

    public override void _PhysicsProcess(double delta)
    {
        if (_fatal)
            return;

        _tickAccumulator += delta;
        int safety = 0;
        while (_tickAccumulator + 1e-12 >= TickDt && safety++ < 32)
        {
            _tickAccumulator -= TickDt;
            RunCoreTick();
            if (_fatal)
                break;
        }
    }

    /// <summary>固定 tick 序（镜像 Daddy）：计数 → 绑定地形 → 消费排队射击 → 相位分支喂输入 →
    /// 内核 Tick → 相位推进（读观测量）→ 断落段积分。</summary>
    private void RunCoreTick()
    {
        _tick++;
        _terrain.Bind(GetWorld3D().DirectSpaceState);
        ProcessQueuedShot();

        switch (_phase)
        {
            case ArenaPhase.Chase:
                DriveMonster();
                TryStartAttack();
                break;
            case ArenaPhase.Strike:
            case ArenaPhase.Grabbed:
                HoldAndReach();
                break;
            case ArenaPhase.Recover:
                DriveMonster();
                break;
        }

        _controller.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
        UpdatePhaseAfterTick();
        PieceTick();
    }

    // ---- 追逐 / 攻击相位 ----

    /// <summary>玩家胶囊球心：抓取目标与位置修正的基准点（不是脚下也不是眼睛）。</summary>
    private Vector3 PlayerChunkCenter() =>
        _player.GlobalPosition + Vector3.Up * PlayerChunkCenterY;

    /// <summary>追逐驱动（Chase/Recover 共用）：MoveTarget 直喂玩家位置，油门按水平距离
    /// 远快近慢（贴身满速会一头撞穿玩家，扑抓窗口反而抓不稳）。</summary>
    private void DriveMonster()
    {
        _controller.GrabTarget = null;
        _controller.MouthDrive = 0f;
        _controller.MoveDir = Vector3.Zero;
        _controller.MoveTargetArriveRadius = ChaseArriveRadius;
        _controller.MoveTarget = _player.GlobalPosition;
        Vector3 delta = _player.GlobalPosition - _controller.Chest.Pos;
        float horizontal = new Vector2(delta.X, delta.Z).Length();
        _controller.RunSpeed = Mathf.Clamp(
            Mathf.Remap(horizontal, RunNearDistance, RunFarDistance, RunSpeedNear, 1f),
            RunSpeedNear, 1f);
    }

    /// <summary>攻击门（仅 Chase）：距离近 + 面向大致对准 + ≥1 臂未断 → 写 GrabTarget 进扑抓。
    /// 冷却由 Recover 相位承载（Recover 期不判门）。双臂全断 → 门永假（HUD 标 no-arms）。</summary>
    private void TryStartAttack()
    {
        if (BothArmsSevered())
            return;
        Vector3 target = PlayerChunkCenter();
        Vector3 to = target - _controller.Chest.Pos;
        float distance = to.Length();
        if (distance > AttackStartRange || distance < 1e-4f)
            return;
        if (_controller.Facing.Dot(to / distance) < 0.2f)
            return;
        _controller.GrabTarget = target;
        _phase = ArenaPhase.Strike;
        _strikeStartTick = _tick;
        GD.Print($"[RAT-ARENA] strike start dist={distance:F2}m t={_tick}");
    }

    /// <summary>扑抓/束缚期喂入：原地（零移动意图）+ 逐 tick GrabTarget=玩家胶囊心；
    /// 束缚期按咬合脚本驱动 MouthDrive（窗口内 1、其余 0）。</summary>
    private void HoldAndReach()
    {
        _controller.MoveTarget = null;
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 0f;
        _controller.GrabTarget = PlayerChunkCenter();
        if (_phase == ArenaPhase.Grabbed)
        {
            long since = _tick - _grabStartTick;
            _controller.MouthDrive =
                since >= BiteWindupTicks && since < BiteWindupTicks + BiteHoldTicks ? 1f : 0f;
        }
        else
        {
            _controller.MouthDrive = 0f;
        }
    }

    private bool BothArmsSevered() =>
        _controller.IsSevered(RatFiendLimbId.ArmLeft)
        && _controller.IsSevered(RatFiendLimbId.ArmRight);

    /// <summary>「抓住」判定：未断的臂全部到位（1 臂存活 = 1 只到位即可；零存活 = 假）。</summary>
    private bool AllAliveArmsOnTarget()
    {
        bool any = false;
        for (int i = 0; i < _controller.Arms.Count; i++)
        {
            if (_controller.Arms[i].Severed)
                continue;
            any = true;
            if (!_controller.HandsOnTarget[i])
                return false;
        }
        return any;
    }

    // ---- 相位推进（tick 侧，读内核 Tick 后的新观测量）----

    private void UpdatePhaseAfterTick()
    {
        switch (_phase)
        {
            case ArenaPhase.Strike:
            {
                if (AllAliveArmsOnTarget())
                {
                    EnterGrabbed();
                    break;
                }
                float distance = PlayerChunkCenter().DistanceTo(_controller.Chest.Pos);
                if (_tick - _strikeStartTick
                        >= (long)MathF.Ceiling(StrikeTimeoutSeconds * TicksPerSecond)
                    || distance > AttackStartRange * AttackAbortScale)
                {
                    GD.Print($"[RAT-ARENA] strike aborted dist={distance:F2}m t={_tick}");
                    EnterRecover(StrikeAbortRecoverSeconds, shove: false);
                }
                break;
            }
            case ArenaPhase.Grabbed:
            {
                ApplyGrabPositionCorrection();
                long since = _tick - _grabStartTick;
                if (since >= BiteWindupTicks + BiteHoldTicks)
                {
                    // 合嘴那 tick = 咬中：计数 + 提示 + 镜头 kick，然后放人进冷却。
                    _biteCount++;
                    _bittenPromptUntilTick = _tick
                        + (long)MathF.Ceiling(BittenPromptSeconds * TicksPerSecond);
                    ShowToast($"BITTEN x{_biteCount}");
                    AddKick();
                    GD.Print($"[RAT-ARENA] bite lands count={_biteCount} t={_tick}");
                    EnterRecover(AttackCooldownSeconds, shove: true);
                }
                break;
            }
            case ArenaPhase.Recover:
                if (_tick >= _recoverUntilTick)
                    _phase = ArenaPhase.Chase;
                break;
        }
    }

    private void EnterGrabbed()
    {
        _phase = ArenaPhase.Grabbed;
        _grabStartTick = _tick;
        _player.InputLocked = true; // 只锁输入，不动 MotionFrozen——重力自驱保留
        GD.Print($"[RAT-ARENA] grabbed t={_tick} " +
                 $"dist={_controller.Chest.Pos.DistanceTo(_player.EyePosition):F1}m");
    }

    /// <summary>束缚期玩家位置修正：每 tick 全量交付「双手中点 − 玩家胶囊心」
    /// （TentaclePlant 教训——不按质量缩小，缩了会被玩家自驱物理逐 tick 抵消掉）。
    /// 双手中点 = 存活臂手粒子均值（1 臂存活即单手位置）。</summary>
    private void ApplyGrabPositionCorrection()
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (RatArm arm in _controller.Arms)
        {
            if (arm.Severed)
                continue;
            sum += arm.Pos;
            count++;
        }
        if (count == 0)
            return; // 并发钉子会在断臂流里立刻退出 Grabbed，这里只是保护
        _player.GlobalPosition += sum / count - PlayerChunkCenter();
    }

    /// <summary>退出攻击占用：清 GrabTarget/MouthDrive、解锁玩家（曾被锁则背向推离），
    /// 进 Recover 冷却。扑空传短冷却、咬合/断臂中断传完整冷却。</summary>
    private void EnterRecover(float cooldownSeconds, bool shove)
    {
        _controller.GrabTarget = null;
        _controller.MouthDrive = 0f;
        bool wasLocked = _player.InputLocked;
        _player.InputLocked = false;
        if (shove && wasLocked && ReleaseShoveSpeed > 0f)
        {
            Vector3 away = _player.GlobalPosition - _controller.Chest.Pos;
            away.Y = 0f;
            if (away.LengthSquared() > 1e-8f)
                _player.Velocity += away.Normalized() * ReleaseShoveSpeed;
        }
        _phase = ArenaPhase.Recover;
        _recoverUntilTick = _tick + Math.Max(1,
            (long)MathF.Ceiling(cooldownSeconds * TicksPerSecond));
    }

    // ---- 手枪：排队 → 场景截程 → 部位裁决 → 断肢流 ----

    /// <summary>枪口位置：视点右下前少许，枪线从这里出发才看得见（正对视线的线是一个点）。</summary>
    private Vector3 MuzzlePosition()
    {
        Basis basis = _player.EyeBasis;
        return _player.EyePosition
            + basis.X * 0.12f - basis.Y * 0.09f - basis.Z * 0.25f;
    }

    /// <summary>输入侧只排队：DirectSpaceState 在物理步内才保证可查，射线留到下一个 core tick。
    /// Grabbed 期禁枪（准心同步隐藏）；Chase/Strike/Recover 都能开。</summary>
    private void TryFireGun()
    {
        if (_fatal || _phase == ArenaPhase.Grabbed
            || Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            return;
        }
        _shotQueued = true;
    }

    private void ProcessQueuedShot()
    {
        if (!_shotQueued)
            return;
        _shotQueued = false;
        if (_phase == ArenaPhase.Grabbed || _tick < _nextShotAtTick)
            return;
        _nextShotAtTick = _tick + Math.Max(1,
            (long)MathF.Ceiling(GunCooldownSeconds * TicksPerSecond));
        FireGun(_player.EyePosition, _player.EyeForward);
    }

    /// <summary>三级判定（Daddy 模式）：① 场景静态体射线截短射程（墙后打不到，排除玩家自身）；
    /// ② <see cref="RatHitProxies.ResolveShot"/> 13 个测试体取最近 t；③ 命中处置
    /// （toast + 断肢流）。每发 ~13 次解析求交，只在扣扳机 tick 发生。</summary>
    private void FireGun(Vector3 from, Vector3 direction)
    {
        float maxDistance = GunRange;
        var query = PhysicsRayQueryParameters3D.Create(from, from + direction * GunRange);
        query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
        Godot.Collections.Dictionary wall = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (wall.Count > 0)
            maxDistance = from.DistanceTo((Vector3)wall["position"]);

        RatHitReport? report = RatHitProxies.ResolveShot(
            _controller, from, direction, maxDistance,
            HeadAimAssist, TorsoAimAssist, LimbAimAssist);

        Vector3 tracerEnd = from + direction * (report?.Distance ?? maxDistance);
        if (report is { } hit)
        {
            _hitMarkerTtl = HitMarkerSeconds;
            HandleHit(hit, direction);
        }
        _tracerFrom = MuzzlePosition();
        _tracerTo = tracerEnd;
        _tracerTtl = TracerSeconds;
    }

    private void HandleHit(in RatHitReport hit, Vector3 gunDirection)
    {
        string label = PartLabel(hit);
        if (hit.Part is RatBodyPart.Arm or RatBodyPart.Leg)
        {
            if (hit.SeveredAlready)
            {
                ShowToast($"HIT: {label} (stump)");
                GD.Print($"[RAT-ARENA] shot hit stump {label} t={_tick}");
                return;
            }
            if (SeverOnHit)
            {
                SeverLimb(hit, gunDirection);
                ShowToast($"HIT: {label} -> SEVERED AT " +
                          (hit.Part == RatBodyPart.Arm ? "ELBOW" : "KNEE"));
                return;
            }
            ShowToast($"HIT: {label}");
            GD.Print($"[RAT-ARENA] shot hit {label} (sever disabled) t={_tick}");
            return;
        }
        // 头/躯干：探索场景无血量——记一发命中反馈即可。
        ShowToast($"HIT: {label}");
        GD.Print($"[RAT-ARENA] shot hit {label} t={_tick}");
    }

    private static string PartLabel(in RatHitReport hit) => hit.Part switch
    {
        RatBodyPart.Head => "HEAD",
        RatBodyPart.Torso => "TORSO",
        RatBodyPart.Arm => hit.Side < 0 ? "L-ARM" : "R-ARM",
        _ => hit.Side < 0 ? "L-LEG" : "R-LEG",
    };

    /// <summary>
    /// 断肢流：① 用 JointMath 当 tick 解出肘/膝作断口点（必须在 Sever 之前——腿的
    /// JointDist 会被 Sever 减半）；② 内核 Sever（踉跄冲量 = 枪向水平分量 × 幅度）；
    /// ③ 合成三点断落链（前臂 肘→腕→手 / 小腿 膝→踝→脚），velocity=TipVel 保插值无缝；
    /// ④ 镜头单发 kick；⑤ 并发钉子：断的是攻击占用臂（Strike/Grabbed 期）→ 立即
    /// 清 GrabTarget、强制 Recover、玩家解锁——状态永不悬空。断腿不进钉子：爬行继续追
    /// （内核涌现，宿主不设 Downed 相位）。
    /// </summary>
    private void SeverLimb(in RatHitReport hit, Vector3 gunDirection)
    {
        bool isArm = hit.Part == RatBodyPart.Arm;
        int index = hit.Side < 0 ? 0 : 1;
        RatFiendLimbId id = isArm
            ? (index == 0 ? RatFiendLimbId.ArmLeft : RatFiendLimbId.ArmRight)
            : (index == 0 ? RatFiendLimbId.LegLeft : RatFiendLimbId.LegRight);
        if (_controller.IsSevered(id))
            return; // 已断再调会 throw——ResolveShot 已按 SeveredAlready 滤过，这里双保险

        Vector3 spineUp = ProcAnimLab.Render.RatFiendJointMath.SpineUp(_controller);
        Vector3 facing = _controller.Facing;
        Vector3 right = ProcAnimLab.Render.RatFiendJointMath.Right(facing, spineUp);
        float crawl = _controller.CrawlFactor;
        Vector3 joint;
        if (isArm)
        {
            RatArm arm = _controller.Arms[index];
            Vector3 dorsal = ProcAnimLab.Render.RatFiendJointMath.Dorsal(facing, spineUp, crawl);
            Vector3 shoulder = ProcAnimLab.Render.RatFiendJointMath.Shoulder(
                _controller.Chest.Pos, right, spineUp, dorsal, arm.Side, _controller.Chest.Radius);
            joint = ProcAnimLab.Render.RatFiendJointMath.Elbow(
                shoulder, arm.Pos,
                ProcAnimLab.Render.RatFiendJointMath.ArmBone(arm.ArmLength),
                ProcAnimLab.Render.RatFiendJointMath.ArmPole(
                    right, spineUp, facing, arm.Side, _controller.RunBlend, crawl));
        }
        else
        {
            Limb leg = _controller.Legs[index];
            Vector3 hipJoint = ProcAnimLab.Render.RatFiendJointMath.HipJoint(
                _controller.Hips.Pos, right, leg.Side, _controller.Hips.Radius);
            joint = ProcAnimLab.Render.RatFiendJointMath.Knee(
                hipJoint, leg.Pos,
                ProcAnimLab.Render.RatFiendJointMath.LegBone(leg.JointDist),
                ProcAnimLab.Render.RatFiendJointMath.LegPole(
                    facing, right, spineUp, leg.Side, crawl));
        }

        Vector3 stagger = gunDirection;
        stagger.Y = 0f;
        stagger = stagger.LengthSquared() > 1e-8f
            ? stagger.Normalized() * SeverStaggerImpulse
            : Vector3.Zero;
        RatFiendSeveredLimbState state = _controller.Sever(id, stagger);

        // 三点链：断口(肘/膝) → 中节(腕/踝，从末端朝断口回缩一小截) → 末梢(手/脚)。
        Vector3 tip = state.TipPos;
        Vector3 towardJoint = joint - tip;
        Vector3 back = towardJoint.LengthSquared() > 1e-8f
            ? towardJoint.Normalized()
            : Vector3.Up;
        Vector3 mid = tip + back * (isArm ? 0.05f : 0.04f);
        var pts = new[] { joint, mid, tip };
        float[] radii = isArm
            ? new[] { 0.042f, 0.035f, 0.028f }
            : new[] { 0.050f, 0.040f, 0.030f };
        var piece = new RatSeveredPiece(pts, state.TipVel, radii, isArm, _roomMin, _roomMax);
        piece.BuildVisual(this);
        _pieces.Add(piece);
        AddKick();
        GD.Print($"[RAT-ARENA] severed {id} tipVel={state.TipVel.Length():F3} " +
                 $"pieces={_pieces.Count} t={_tick}");

        // 并发钉子：攻击占用臂被断 → 状态立即收干净。
        if (isArm && _phase is ArenaPhase.Strike or ArenaPhase.Grabbed)
        {
            GD.Print($"[RAT-ARENA] attack arm severed — force recover t={_tick}");
            EnterRecover(AttackCooldownSeconds, shove: true);
        }
    }

    private void PieceTick()
    {
        foreach (RatSeveredPiece piece in _pieces)
            piece.Tick(_gravityPerTick);
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTtl = ToastSeconds;
    }

    private void AddKick() => _kick = MathF.Min(_kick + 1f, KickMax);

    // ---- R 重开 ----

    private void ResetRun()
    {
        foreach (RatSeveredPiece piece in _pieces)
            piece.ClearVisual();
        _pieces.Clear();
        SpawnRat();
        _player.InputLocked = false;
        _player.Place(_arena.PlayerSpawn, _arena.MonsterSpawn);
        _player.SetActive(true); // 重新捕获鼠标（Esc 释放过也一并恢复）
        _phase = ArenaPhase.Chase;
        _strikeStartTick = 0;
        _grabStartTick = 0;
        _recoverUntilTick = 0;
        _biteCount = 0;
        _bittenPromptUntilTick = -1;
        _shotQueued = false;
        _nextShotAtTick = 0;
        _tracerTtl = 0f;
        _hitMarkerTtl = 0f;
        _toastTtl = 0f;
        _kick = 0f;
        _kickTime = 0f;
        _player.SetCameraShake(Vector3.Zero, Vector3.Zero);
        _kickApplied = false;
        GD.Print($"[RAT-ARENA] reset t={_tick}");
    }

    // ---- 渲染帧：镜头接管/kick + 渲染 + HUD ----

    public override void _Process(double delta)
    {
        if (_fatal)
            return;

        float physicsDelta = 1f / Math.Max(1, Engine.PhysicsTicksPerSecond);
        float interpolation = Mathf.Clamp(
            (float)(_tickAccumulator / TickDt
                + Engine.GetPhysicsInterpolationFraction() * physicsDelta / TickDt),
            0f, 1f);

        if (_phase == ArenaPhase.Grabbed)
            UpdateCameraTakeover(delta, interpolation);
        UpdateCameraKick((float)delta);

        _formal?.Draw(interpolation, (float)delta);
        foreach (RatSeveredPiece piece in _pieces)
            piece.Render(interpolation);
        DrawTracer((float)delta);
        UpdateHud((float)delta);
    }

    /// <summary>束缚期镜头接管（Daddy 对准数学）：yaw+pitch 指数阻尼转向怪物头（不瞬切）。</summary>
    private void UpdateCameraTakeover(double delta, float interpolation)
    {
        Vector3 focus = _controller.Head.LerpPos(interpolation);
        Vector3 eye = _player.EyePosition;
        Vector3 to = focus - eye;
        if (to.LengthSquared() < 1e-8f)
            return;

        float targetYaw = Mathf.Atan2(-to.X, -to.Z);
        float horizontal = new Vector2(to.X, to.Z).Length();
        float targetPitch = Mathf.Atan2(to.Y, horizontal);
        float tau = Mathf.Max(0.05f, CameraTakeoverSeconds) / 3f;
        float blend = 1f - Mathf.Exp(-(float)delta / tau);
        _player.SetLookAngles(
            Mathf.LerpAngle(_player.Yaw, targetYaw, blend),
            Mathf.Lerp(_player.CameraPitch, targetPitch, blend));
    }

    /// <summary>断肢/咬合镜头 kick：多条不可通约正弦 × 快衰减冲量包络（Daddy
    /// UpdateCameraShake 只留冲量项）。包络耗尽后喂一次零并停喂。</summary>
    private void UpdateCameraKick(float delta)
    {
        _kick *= MathF.Exp(-delta / MathF.Max(0.05f, SeverShakeSeconds));
        if (_kick < 0.005f)
        {
            if (_kickApplied)
            {
                _player.SetCameraShake(Vector3.Zero, Vector3.Zero);
                _kickApplied = false;
            }
            _kickTime = 0f;
            return;
        }

        _kickTime += delta * KickFrequencyHz * Mathf.Tau;
        float t = _kickTime;
        float rot = Mathf.DegToRad(KickDegrees) * _kick;
        // 频率比 1 / 0.61 / 0.83 / 0.47 / 0.73 互不成整数比，叠加后无循环节拍感。
        var euler = new Vector3(
            (MathF.Sin(t) * 0.6f + MathF.Sin(t * 0.61f + 1.7f) * 0.4f) * rot,
            (MathF.Sin(t * 0.83f + 4.2f) * 0.6f + MathF.Sin(t * 0.47f + 0.9f) * 0.4f) * rot,
            MathF.Sin(t * 0.73f + 2.6f) * rot * 0.5f);
        float sway = KickOffsetMeters * _kick;
        var offset = new Vector3(
            MathF.Sin(t * 0.89f + 0.4f) * sway,
            MathF.Sin(t * 1.13f + 3.1f) * sway * 0.7f,
            0f);
        _player.SetCameraShake(offset, euler);
        _kickApplied = true;
    }

    /// <summary>短寿命枪线（渲染侧，每帧重发管面；无枪线时发空帧清面）。</summary>
    private void DrawTracer(float delta)
    {
        if (_tracer is not { } tracer)
            return;
        _hitMarkerTtl = MathF.Max(0f, _hitMarkerTtl - delta);
        tracer.BeginFrame();
        if (_tracerTtl > 0f)
        {
            _tracerTtl -= delta;
            var color = new Color(1.0f, 0.86f, 0.44f);
            _tracerStations.Clear();
            _tracerStations.Add(new ProcAnimLab.Render.TubeStation(_tracerFrom, 0.008f, color));
            _tracerStations.Add(new ProcAnimLab.Render.TubeStation(
                _tracerFrom.Lerp(_tracerTo, 0.5f), 0.006f, color));
            _tracerStations.Add(new ProcAnimLab.Render.TubeStation(_tracerTo, 0.004f, color));
            tracer.AddTube(_tracerStations, Vector3.Up, 5);
        }
        tracer.EndFrame();
    }

    private void UpdateHud(float delta)
    {
        _toastTtl = MathF.Max(0f, _toastTtl - delta);
        _hud.SetToast(_toastTtl > 0f ? _toastText : "");
        _hud.SetCrosshair(
            _phase != ArenaPhase.Grabbed && Input.MouseMode == Input.MouseModeEnum.Captured,
            _hitMarkerTtl > 0f);

        // 四肢 o/X 四格（RatFiendLimbId 序：左臂 右臂 左腿 右腿）。
        string limbs = "";
        for (int i = 0; i < 4; i++)
            limbs += _controller.IsSevered((RatFiendLimbId)i) ? "X" : "o";
        float cooldown = _phase == ArenaPhase.Recover
            ? MathF.Max(0f, (_recoverUntilTick - _tick) / (float)TicksPerSecond)
            : 0f;
        string noArms = BothArmsSevered() ? " no-arms" : "";
        _hud.SetStatus(
            $"RAT ARENA — phase={_phase} crawl={_controller.CrawlFactor:F2} " +
            $"grips={_controller.CrawlGripCount} limbs={limbs} " +
            $"cooldown={cooldown:F1}s bites={_biteCount}{noArms}\n" +
            "[LMB] shoot  [R] restart  [F1] hud  [Esc] mouse");

        if (_phase == ArenaPhase.Grabbed)
            _hud.SetPrompt("GRABBED");
        else if (_tick < _bittenPromptUntilTick)
            _hud.SetPrompt($"BITTEN x{_biteCount}");
        else
            _hud.SetPrompt("");
    }

    // ---- 输入 ----

    public override void _Input(InputEvent @event)
    {
        if (_fatal)
            return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            TryFireGun();
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.PhysicalKeycode)
        {
            case Key.R:
                ResetRun();
                break;
            case Key.F1:
                _hud.ToggleStatusVisibility();
                break;
            case Key.Escape:
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible
                    : Input.MouseModeEnum.Captured;
                break;
        }
    }
}
