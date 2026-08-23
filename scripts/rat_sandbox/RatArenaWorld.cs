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
/// 鼠煞枪击竞技场（探索场景，不进矩阵）：封闭矩形大房间里，鼠煞满油门追逐第一人称玩家，
/// 近身后按存活手数攻击——≥1 手扑抓束缚 + 咬合（锁镜头窗内把玩家拉到定距），零手改无束缚
/// 撕咬（边追边咬，蓄势期跑开可躲）；玩家的反制是手枪——每发命中掉血（头 ×3）+ 部位击退
/// + 硬直（封攻击门 + 短吃痛窗，姿态保持凶相），臂/腿各扛 LimbHp 发、打空才断肢（断臂缩攻击门、
/// 断腿转爬行追击，全部内核涌现），血量归零倒地、静止后尸体永久冻结。断落段独立 Verlet 跌地。R 重开。
///
/// 玩法全部在宿主层；内核只走攻击接缝（GrabTarget/MouthDrive 输入 + HandsOnTarget/
/// CrawlFactor 观测 + Sever 断肢 + R18 的 Impact 部位冲击与 Conscious 死亡开关——
/// 全部 opt-in，回归路线零漂移）。相位机构造见 <see cref="ArenaPhase"/> 的转移图注释。
/// 命中判定的关节几何走 <see cref="Render.RatFiendJointMath"/> 单一真相源。
/// 本场景只有第一人称与正式渲染视图：无自由飞相机、无白盒切换、无决定论路线机器。
/// </summary>
public partial class RatArenaWorld : Node3D
{
    private const double TickDt = 0.025;
    private const int TicksPerSecond = 40;

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

    /// <summary>追杀期的常开嘴开度（MouthDrive 分量，叠加 Gait 基础开度）：
    /// 逼近也龇着嘴。0 = 关闭该效果。R18 起追逐恒满油门（近身减速机制取消——站位
    /// 修正改由束缚期的锁镜头窗完成，见 ApplyGrabPositionCorrection）。</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float ChaseMouthDrive { get; set; } = 0.6f;

    [ExportGroup("Arena / Attack")]
    /// <summary>攻击门基准距离（站立、双臂俱在档）：胸到瞄准点 ≤ 此距离且面向大致对准才发起。
    /// 断手会缩门（见 AttackRangeOneArmScale / AttackRangeNoArmsScale）；爬行按 CrawlFactor
    /// 连续缩到 CrawlAttackRange。</summary>
    [Export(PropertyHint.Range, "0.5,6,0.05")]
    public float AttackStartRange { get; set; } = 1.8f;

    /// <summary>爬行档攻击门（米，R19）：站立扑抓靠冲刺动量滑进手可及圈（臂长 1.15 +
    /// 抓住判定 0.25 = 1.40m），爬行推进弱、扑抓期双手又离地断了推进——从 1.8m 起手必然
    /// 停在可及圈外反复摆「抱抓姿势」空放（用户实测「提前进入抓握状态却抓不住」的根因）。
    /// 爬行门收进可及圈内，出手即中。必须 ≤ 臂长+抓住判定半径（1.40m），否则死区复发。</summary>
    [Export(PropertyHint.Range, "0.5,3,0.05")]
    public float CrawlAttackRange { get; set; } = 1.3f;

    /// <summary>抓取双手横向分开（米，内核 opt-in 旋钮）：0 = 两手追同一点逐位重合——两套
    /// 手爪几何完全叠置 z-fighting 闪烁，读成「手一直在颤抖」（R19 实测束缚稳态手的物理
    /// 位移亚毫米，抖的是深度冲突）。默认分开约一掌，读成掐住两肩。</summary>
    [Export(PropertyHint.Range, "0,0.3,0.01")]
    public float GrabHandSpread { get; set; } = 0.09f;

    /// <summary>断一只手后的攻击门缩放（用户定调：单断收益要「不明显但不能没有」）。</summary>
    [Export(PropertyHint.Range, "0.5,1,0.01")]
    public float AttackRangeOneArmScale { get; set; } = 0.94f;

    /// <summary>双手全断后的攻击门缩放（只剩撕咬，无控制；仍不宜缩太多）。</summary>
    [Export(PropertyHint.Range, "0.5,1,0.01")]
    public float AttackRangeNoArmsScale { get; set; } = 0.88f;

    /// <summary>扑抓期玩家距离超过 攻击门 × 此倍率即放弃。</summary>
    [Export(PropertyHint.Range, "1,4,0.05")]
    public float AttackAbortScale { get; set; } = 1.5f;

    /// <summary>扑抓超时（秒）：这么久还没双手到位就放弃。</summary>
    [Export(PropertyHint.Range, "0.25,5,0.05")]
    public float StrikeTimeoutSeconds { get; set; } = 1.0f;

    /// <summary>咬合脚本：血盆大口蓄势的 tick 数（MouthDrive=1），之后闭口 = 咬中。
    /// R18 语义翻转：旧脚本蓄势微张、咬合窗反而张大——渲染的撕咬顿挫由「1→0 闭口沿」
    /// 触发，旧序把顿挫推迟到放人之后，用户实测「咬得不明显」的根因。</summary>
    [Export(PropertyHint.Range, "1,200,1")]
    public int BiteWindupTicks { get; set; } = 20;

    /// <summary>闭口保持的 tick 数（咬住撕扯的窗口），之后放人/收招。</summary>
    [Export(PropertyHint.Range, "1,200,1")]
    public int BiteSnapHoldTicks { get; set; } = 12;

    /// <summary>无手撕咬的落咬判距（米，怪物头到瞄准点）：闭口那 tick 玩家在此距离内才算
    /// 咬中——没有束缚，蓄势期玩家跑开就落空。</summary>
    [Export(PropertyHint.Range, "0.3,3,0.05")]
    public float BiteReach { get; set; } = 1.1f;

    /// <summary>咬合放开后的攻击冷却（秒；扑空/落空走更短的 0.5s 常量）。</summary>
    [Export(PropertyHint.Range, "0.25,20,0.25")]
    public float AttackCooldownSeconds { get; set; } = 2.5f;

    /// <summary>放人瞬间给玩家的背向推离速度（米/秒）——别站在原地立刻被再抓。</summary>
    [Export(PropertyHint.Range, "0,10,0.25")]
    public float ReleaseShoveSpeed { get; set; } = 3.0f;

    /// <summary>束缚期镜头接管的阻尼时间常数（≈ 该秒数内基本完成对准怪物头）；
    /// R18 起也是束缚站位修正的收敛窗（同一段时间里把玩家拉到定距，用户定调）。</summary>
    [Export(PropertyHint.Range, "0.1,3,0.05")]
    public float CameraTakeoverSeconds { get; set; } = 0.4f;

    /// <summary>束缚定距（米，水平）：锁镜头窗内把玩家平滑拉到怪物正面这么远——取代
    /// 旧的近身减速机制（爬行时按 CrawlFactor 再收 15%，怪趴得低、嘴更靠前）。</summary>
    [Export(PropertyHint.Range, "0.3,3,0.05")]
    public float GrabHoldDistance { get; set; } = 0.9f;

    /// <summary>爬行瞄点高度（米，玩家脚底起算）：爬行胸高 ~0.3m + 臂长 1.15m 够不到
    /// 1.55m 的眼点——旧版爬行扑抓必然超时空放（用户实测「爬着抓不住人」的根因）。
    /// 瞄点按 CrawlFactor 从眼高连续降到小腿高，站立行为逐字不变。</summary>
    [Export(PropertyHint.Range, "0.1,1.5,0.05")]
    public float CrawlAimHeight { get; set; } = 0.45f;

    [ExportGroup("Arena / Damage")]
    /// <summary>血量（发）：非头部命中每发 1 点，头部 × HeadDamageMultiplier。R20 调到 10
    /// 配合肢体血量——断完四肢正好 8 发，躯干再补两枪毙命。</summary>
    [Export(PropertyHint.Range, "1,50,1")]
    public int MonsterHp { get; set; } = 10;

    [Export(PropertyHint.Range, "1,10,1")]
    public int HeadDamageMultiplier { get; set; } = 3;

    /// <summary>每条臂/腿的血量（发）：打空才断肢，一枪打不断（R20 用户定调）；命中同时
    /// 照常扣总血量。1 = 旧的一枪断。SeverOnHit 关闭时不扣肢体血、永不断。</summary>
    [Export(PropertyHint.Range, "1,10,1")]
    public int LimbHp { get; set; } = 2;

    /// <summary>命中击退冲量（米/tick，沿枪向水平分量注入命中 chunk——内核 Impact 接缝）。
    /// 头部另给 0.5× 到胸（脖是 PullOnly，纯头冲量只甩头不退身）。</summary>
    [Export(PropertyHint.Range, "0,0.4,0.01")]
    public float HitImpactHead { get; set; } = 0.12f;

    [Export(PropertyHint.Range, "0,0.4,0.01")]
    public float HitImpactBody { get; set; } = 0.05f;

    [Export(PropertyHint.Range, "0,0.4,0.01")]
    public float HitImpactLimb { get; set; } = 0.04f;

    /// <summary>头部命中冲量的向上偏置（叠加在归一化枪向上再归一化；0.6 ≈ 冲量抬 31°）：
    /// 甩头带仰角——直挺挺水平后拽读起来木讷（R19 用户定调）。只偏头 chunk，胸的退身
    /// 分量保持水平。</summary>
    [Export(PropertyHint.Range, "0,2,0.05")]
    public float HitImpactHeadUpBias { get; set; } = 0.6f;

    /// <summary>臂命中的躯干拧转（度，内核 ImpactTwist 接缝）：转向按 力臂×枪向 的转矩符号
    /// ——打右臂右肩向后拧（俯视顺时针），打左臂反向。0 = 关闭。</summary>
    [Export(PropertyHint.Range, "0,45,1")]
    public float HitTwistArmDegrees { get; set; } = 16f;

    /// <summary>腿命中的躯干拧转（度，力臂在髋侧，幅度比臂小）。</summary>
    [Export(PropertyHint.Range, "0,45,1")]
    public float HitTwistLegDegrees { get; set; } = 9f;

    /// <summary>命中硬直（秒，按部位）：硬直期**只封攻击判定门**——贴脸走过也不会被抓，
    /// 但姿态保持凶相、追逐照常（R19 用户定调：不许垂头垂手）。再次命中取 max 刷新。
    /// 断肢命中走 Limb 档（Sever 踉跄冲量另行叠加）。0 = 该部位硬直关闭。</summary>
    [Export(PropertyHint.Range, "0,3,0.05")]
    public float StunHeadSeconds { get; set; } = 0.8f;

    [Export(PropertyHint.Range, "0,3,0.05")]
    public float StunBodySeconds { get; set; } = 0.35f;

    [Export(PropertyHint.Range, "0,3,0.05")]
    public float StunLimbSeconds { get; set; } = 0.5f;

    /// <summary>吃痛回神窗（秒，硬直的前缀窗，取 min(硬直, 此值)）：窗内不喂移动意图——
    /// 击退冲量与拧身不被追逐推进/朝向回拽掩掉，怪物定住瞪人吃完这一下，窗尽恢复追逐时
    /// SlewFacing 把拧开的朝向甩回猎物（拧身—瞪视—甩回的踉跄全程涌现）。姿态不垮：
    /// LookTarget 仍钉住玩家（头照瞪、手照举）+ 龇嘴不收。</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float StunReelSeconds { get; set; } = 0.25f;

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

    /// <summary>true=臂/腿命中扣肢体血量、打空断肢；false=只记命中、不扣肢体血
    /// （观察追逐/抓取闭环时关掉）。</summary>
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

    /// <summary>死亡定格判据：全 chunk 速度低于此值（米/tick）连续这么多 tick → 尸体永久
    /// 冻结（此后不再 Tick 内核——完全固定、零物理零交互，用户定调）。</summary>
    private const float CorpseStillVelocity = 0.003f;
    private const int CorpseStillTicks = 20;

    /// <summary>相位机（R18 重构，R19 收敛）：冷却与硬直都不占相位——冷却 = _attackReadyTick
    /// 时间戳（旧 Recover 相位删除）；硬直 = _stunUntilTick（封攻击判定门）+ _reelUntilTick
    /// （吃痛回神窗，Chase 内部的喂入分支）两个时间戳（旧 Stunned 相位删除——用户定调
    /// 硬直期姿态保持凶相、追逐照常，它本质是「带门禁的 Chase」不是独立相位）。
    /// 转移图：Chase → Strike/Bite（攻击门 = 冷却过 + 硬直过 + 距离 + 面向，按存活手数选路）；
    /// Strike → Grabbed（双手到位）/ Chase（超时/脱围）；Grabbed → Chase（咬合放人）；
    /// Bite → Chase（落咬/落空）；命中硬直从 Strike/Grabbed/Bite 抢占回 Chase（放人 + 压冷却，
    /// 见 ApplyStun）；血量归零 → Dead（终态，仅 R 重开离开）。</summary>
    private enum ArenaPhase
    {
        Chase,
        Strike,
        Grabbed,
        Bite,
        Dead,
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
    private long _biteStartTick;
    private bool _biteLanded;
    private long _attackReadyTick;
    private long _stunUntilTick;
    private long _reelUntilTick;
    private int _hp;
    private readonly int[] _limbHp = new int[4]; // 逐肢剩余发数，RatFiendLimbId 序
    private int _limbHpMax; // 出生时冻结的 LimbHp——toast 分母不读活导出项，中途改 Inspector 不骗人
    private int _corpseStillStreak;
    private bool _corpseFrozen;
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
        if (!FiniteNonNegative(ChaseMouthDrive) || ChaseMouthDrive > 1f)
            return Fail($"ChaseMouthDrive must be in [0,1], got {ChaseMouthDrive}");
        if (!FinitePositive(AttackStartRange))
            return Fail($"AttackStartRange must be finite and positive, got {AttackStartRange}");
        if (!FinitePositive(CrawlAttackRange))
            return Fail($"CrawlAttackRange must be finite and positive, got {CrawlAttackRange}");
        if (!FiniteNonNegative(GrabHandSpread))
            return Fail($"GrabHandSpread must be finite and >= 0, got {GrabHandSpread}");
        if (!FinitePositive(AttackRangeOneArmScale) || AttackRangeOneArmScale > 1f
            || !FinitePositive(AttackRangeNoArmsScale) || AttackRangeNoArmsScale > 1f)
        {
            return Fail($"attack range scales must be in (0,1], got " +
                        $"{AttackRangeOneArmScale}/{AttackRangeNoArmsScale}");
        }
        if (!float.IsFinite(AttackAbortScale) || AttackAbortScale < 1f)
            return Fail($"AttackAbortScale must be finite and >= 1, got {AttackAbortScale}");
        if (!FinitePositive(StrikeTimeoutSeconds))
            return Fail($"StrikeTimeoutSeconds must be finite and positive, got {StrikeTimeoutSeconds}");
        if (BiteWindupTicks < 1)
            return Fail($"BiteWindupTicks must be >= 1, got {BiteWindupTicks}");
        if (BiteSnapHoldTicks < 1)
            return Fail($"BiteSnapHoldTicks must be >= 1, got {BiteSnapHoldTicks}");
        if (!FinitePositive(BiteReach))
            return Fail($"BiteReach must be finite and positive, got {BiteReach}");
        if (!FinitePositive(AttackCooldownSeconds))
            return Fail($"AttackCooldownSeconds must be finite and positive, got {AttackCooldownSeconds}");
        if (!float.IsFinite(ReleaseShoveSpeed) || ReleaseShoveSpeed < 0f)
            return Fail($"ReleaseShoveSpeed must be finite and >= 0, got {ReleaseShoveSpeed}");
        if (!FinitePositive(CameraTakeoverSeconds))
            return Fail($"CameraTakeoverSeconds must be finite and positive, got {CameraTakeoverSeconds}");
        if (!FinitePositive(GrabHoldDistance))
            return Fail($"GrabHoldDistance must be finite and positive, got {GrabHoldDistance}");
        if (!FinitePositive(CrawlAimHeight))
            return Fail($"CrawlAimHeight must be finite and positive, got {CrawlAimHeight}");
        if (MonsterHp < 1)
            return Fail($"MonsterHp must be >= 1, got {MonsterHp}");
        if (HeadDamageMultiplier < 1)
            return Fail($"HeadDamageMultiplier must be >= 1, got {HeadDamageMultiplier}");
        if (LimbHp < 1)
            return Fail($"LimbHp must be >= 1, got {LimbHp}");
        if (!FiniteNonNegative(HitImpactHead) || !FiniteNonNegative(HitImpactBody)
            || !FiniteNonNegative(HitImpactLimb))
        {
            return Fail($"hit impacts must be finite and >= 0, got " +
                        $"{HitImpactHead}/{HitImpactBody}/{HitImpactLimb}");
        }
        if (!FiniteNonNegative(StunHeadSeconds) || !FiniteNonNegative(StunBodySeconds)
            || !FiniteNonNegative(StunLimbSeconds))
        {
            return Fail($"stun seconds must be finite and >= 0, got " +
                        $"{StunHeadSeconds}/{StunBodySeconds}/{StunLimbSeconds}");
        }
        if (!FiniteNonNegative(HitImpactHeadUpBias))
            return Fail($"HitImpactHeadUpBias must be finite and >= 0, got {HitImpactHeadUpBias}");
        if (!FiniteNonNegative(HitTwistArmDegrees) || !FiniteNonNegative(HitTwistLegDegrees))
        {
            return Fail($"hit twist degrees must be finite and >= 0, got " +
                        $"{HitTwistArmDegrees}/{HitTwistLegDegrees}");
        }
        if (!FiniteNonNegative(StunReelSeconds))
            return Fail($"StunReelSeconds must be finite and >= 0, got {StunReelSeconds}");
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
        _controller.GrabHandSpread = GrabHandSpread; // 内核 opt-in：抓取双手横向分开
        _hp = MonsterHp;
        _limbHpMax = LimbHp;
        for (int i = 0; i < _limbHp.Length; i++)
            _limbHp[i] = _limbHpMax;
        _corpseFrozen = false;
        _corpseStillStreak = 0;
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
    /// 内核 Tick（尸体冻结后跳过——完全固定零物理）→ 相位推进（读观测量）→ 断落段积分。</summary>
    private void RunCoreTick()
    {
        _tick++;
        _terrain.Bind(GetWorld3D().DirectSpaceState);
        ProcessQueuedShot();
        FeedMonster();
        if (!_corpseFrozen)
            _controller.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
        AdvancePhase();
        PieceTick();
    }

    // ---- 相位喂入（内核 Tick 之前写输入面）----

    private void FeedMonster()
    {
        switch (_phase)
        {
            case ArenaPhase.Chase:
                DriveChase();
                TryStartAttack();
                break;
            case ArenaPhase.Strike:
            case ArenaPhase.Grabbed:
                FeedStrikeOrGrabbed();
                break;
            case ArenaPhase.Bite:
                FeedBite();
                break;
            case ArenaPhase.Dead:
                break; // EnterDead 已清全部输入；Conscious=false 后内核只剩重力与约束
        }
    }

    /// <summary>怪物瞄准点：攻击门、逐 tick GrabTarget、脱围距离与束缚位置修正共用同一点
    /// ——瞄哪、抓哪、锁哪必须一致，否则扑抓落点与束缚会互相跳变。站立 = 玩家眼睛
    /// （读 <see cref="ArenaFirstPersonPlayer.EyePosition"/>，改玩家规格自动跟随；gaunt
    /// 臂长 1.15m、胸高约 1m，近身胸→眼 ≈0.8m 在可及半径内）；爬行按 CrawlFactor 连续
    /// 降到小腿高（CrawlAimHeight）——爬行胸高 ~0.3m，眼点距离 &gt; 臂长，抓眼必空。</summary>
    private Vector3 PlayerAimPoint()
    {
        Vector3 eye = _player.EyePosition;
        Vector3 low = _player.GlobalPosition + Vector3.Up * CrawlAimHeight;
        return eye.Lerp(low, Mathf.Clamp(_controller.CrawlFactor, 0f, 1f));
    }

    /// <summary>追逐驱动：MoveTarget 直喂玩家位置、恒满油门（R18 近身减速取消——站位
    /// 修正移到束缚期的锁镜头窗）。追杀观感：LookTarget 逐 tick 盯瞄准点 + 常开龇嘴。
    /// 吃痛回神窗（R19，命中后 min(硬直, StunReelSeconds)）：只掐掉移动意图——击退/拧身
    /// 不被推进与朝向回拽掩掉；LookTarget/龇嘴照旧 = 定住瞪人，头不垂手不落
    /// （LookTarget 分支的 LookArmRaise 权重下限把手钉在举位）。</summary>
    private void DriveChase()
    {
        _controller.GrabTarget = null;
        _controller.LookTarget = PlayerAimPoint();
        _controller.MouthDrive = ChaseMouthDrive;
        _controller.MoveDir = Vector3.Zero;
        if (_tick < _reelUntilTick)
        {
            _controller.MoveTarget = null;
            _controller.RunSpeed = 0f;
            return;
        }
        // 停靠半径钳到攻击门内侧（R19b 评审修复）：停靠 1.2m 是**水平**距离、攻击门是
        // 胸→瞄点 **3D** 距离——零手爬行档门 1.3×0.88=1.144m 小于停靠点的 3D 距离
        // ≈1.21m，怪物会在门外 6cm 处永久停车瞪人、Bite 永不发起（对静止玩家死锁）。
        // 站立各档门 ×0.85 仍 > 1.2，行为逐位不变。
        _controller.MoveTargetArriveRadius = Mathf.Min(ChaseArriveRadius,
            EffectiveAttackRange() * 0.85f);
        _controller.MoveTarget = _player.GlobalPosition;
        _controller.RunSpeed = 1f;
    }

    /// <summary>存活手数 → 攻击门距离（用户定调：断一只稍减一点点、全断再减一点点——
    /// 「打断双手才有足够明显的收益，单断收益不明显但不能没有」）。</summary>
    private int AliveArmCount()
    {
        int count = 0;
        foreach (RatArm arm in _controller.Arms)
        {
            if (!arm.Severed)
                count++;
        }
        return count;
    }

    /// <summary>爬行档 Strike 门的手可及上限（R19b 评审修复）：臂长 + 抓住判定的横向余量
    /// （GrabHandSpread 把接触圈缩成 sqrt(r²−spread²)），打 0.95 安全系数。CrawlAttackRange
    /// 只是手感档，几何上限必须按**当前预设臂长**现算——whelp 臂长 0.85 时固定 1.3m 会让
    /// R19 修掉的「反复摆抱抓空姿势」死区在小体格上逐字复发。</summary>
    private float CrawlStrikeReachCap()
    {
        float contact = _controller.Params.GrabContactRadius;
        float lateral = Mathf.Min(GrabHandSpread, contact * 0.9f);
        float slack = MathF.Sqrt(MathF.Max(0f, contact * contact - lateral * lateral));
        return (_controller.Params.ArmLength + slack) * 0.95f;
    }

    /// <summary>攻击门距离 = 姿态基准（站立 AttackStartRange ↔ 爬行档，按 CrawlFactor 连续
    /// 混合）× 存活手数缩放。爬行必须收窄（R19）：站立扑抓靠 3.4m/s 冲刺动量滑进手可及圈
    /// 补上「门距 &gt; 可及」的死区；爬行推进弱、扑抓期双手离地推进即断——死区里只会反复
    /// 摆抱抓空姿势。爬行 Strike 档再钳到 CrawlStrikeReachCap（按预设臂长）；零手 Bite 档
    /// 不钳（门管的是起咬距离，落咬由头→瞄点 ≤ BiteReach 判）。</summary>
    private float EffectiveAttackRange()
    {
        int arms = AliveArmCount();
        float crawlGate = arms > 0
            ? Mathf.Min(CrawlAttackRange, CrawlStrikeReachCap())
            : CrawlAttackRange;
        float baseRange = Mathf.Lerp(AttackStartRange, crawlGate,
            Mathf.Clamp(_controller.CrawlFactor, 0f, 1f));
        return baseRange * arms switch
        {
            2 => 1f,
            1 => AttackRangeOneArmScale,
            _ => AttackRangeNoArmsScale,
        };
    }

    /// <summary>攻击门（仅 Chase）：冷却过 + 硬直过 + 距离进门 + 面向大致对准 → 按存活手数
    /// 选路：≥1 手 = 扑抓（Strike，成功束缚 + 咬）；零手 = 无束缚撕咬（Bite，边追边咬）。
    /// 爬行扑抓/撕咬与站立同一条链（瞄点随 CrawlFactor 降高、门距随 CrawlFactor 收窄）。</summary>
    private void TryStartAttack()
    {
        if (_tick < _attackReadyTick || _tick < _stunUntilTick)
            return;
        Vector3 target = PlayerAimPoint();
        Vector3 to = target - _controller.Chest.Pos;
        float distance = to.Length();
        if (distance > EffectiveAttackRange() || distance < 1e-4f)
            return;
        if (_controller.Facing.Dot(to / distance) < 0.2f)
            return;
        if (AliveArmCount() > 0)
        {
            _controller.GrabTarget = target;
            _phase = ArenaPhase.Strike;
            _strikeStartTick = _tick;
            GD.Print($"[RAT-ARENA] strike start dist={distance:F2}m t={_tick}");
        }
        else
        {
            _phase = ArenaPhase.Bite;
            _biteStartTick = _tick;
            _biteLanded = false;
            GD.Print($"[RAT-ARENA] bite lunge start dist={distance:F2}m t={_tick}");
        }
    }

    /// <summary>扑抓/束缚期喂入：原地（零移动意图）+ 逐 tick GrabTarget=瞄准点；束缚期
    /// 跑咬合脚本（R18 语义翻转：蓄势血盆大口 → 闭口咬住——渲染撕咬顿挫挂在 1→0 沿）。</summary>
    private void FeedStrikeOrGrabbed()
    {
        _controller.MoveTarget = null;
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 0f;
        _controller.LookTarget = null; // 攻击链的头朝向由 GrabTarget 独占
        _controller.GrabTarget = PlayerAimPoint();
        _controller.MouthDrive = _phase == ArenaPhase.Grabbed
            ? BiteScriptDrive(_tick - _grabStartTick)
            : ChaseMouthDrive; // 扑抓中龇着嘴
    }

    /// <summary>无手撕咬喂入：继续满油门追（扑向玩家的动量本身就是「咬」的前冲），
    /// 嘴跑同一份咬合脚本。玩家没被束缚——蓄势期跑开就落空。</summary>
    private void FeedBite()
    {
        DriveChase();
        _controller.MouthDrive = BiteScriptDrive(_tick - _biteStartTick);
    }

    /// <summary>咬合脚本（Grabbed/Bite 共用）：蓄势窗大张（1）→ 闭口窗（0）= 咬住/落咬。</summary>
    private float BiteScriptDrive(long since) =>
        since < BiteWindupTicks ? 1f : 0f;

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

    private void AdvancePhase()
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
                float distance = PlayerAimPoint().DistanceTo(_controller.Chest.Pos);
                if (_tick - _strikeStartTick
                        >= (long)MathF.Ceiling(StrikeTimeoutSeconds * TicksPerSecond)
                    || distance > EffectiveAttackRange() * AttackAbortScale)
                {
                    GD.Print($"[RAT-ARENA] strike aborted dist={distance:F2}m t={_tick}");
                    ReleasePlayer(shove: false);
                    BeginCooldown(StrikeAbortRecoverSeconds);
                }
                break;
            }
            case ArenaPhase.Grabbed:
            {
                ApplyGrabPositionCorrection();
                long since = _tick - _grabStartTick;
                if (since == BiteWindupTicks)
                {
                    // 闭口起始 tick = 咬中（渲染的撕咬顿挫同一时刻在 1→0 沿上触发）。
                    LandBite();
                }
                if (since >= BiteWindupTicks + BiteSnapHoldTicks)
                {
                    ReleasePlayer(shove: true);
                    BeginCooldown(AttackCooldownSeconds);
                }
                break;
            }
            case ArenaPhase.Bite:
            {
                long since = _tick - _biteStartTick;
                if (since == BiteWindupTicks)
                {
                    // 落咬判定：闭口 tick 玩家还在嘴前（头→瞄点 ≤ BiteReach）才算咬中——
                    // 无束缚，蓄势期跑开就落空（这就是无手档的反制窗口）。
                    float reach = _controller.Head.Pos.DistanceTo(PlayerAimPoint());
                    if (reach <= BiteReach)
                    {
                        _biteLanded = true;
                        LandBite();
                    }
                    else
                    {
                        GD.Print($"[RAT-ARENA] bite missed reach={reach:F2}m t={_tick}");
                    }
                }
                if (since >= BiteWindupTicks + BiteSnapHoldTicks)
                {
                    _controller.MouthDrive = 0f;
                    BeginCooldown(_biteLanded ? AttackCooldownSeconds : StrikeAbortRecoverSeconds);
                }
                break;
            }
            case ArenaPhase.Dead:
                TickCorpseFreeze();
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

    /// <summary>咬中结算（Grabbed 必中 / Bite 判距后调用）：计数 + 提示 + 镜头 kick。</summary>
    private void LandBite()
    {
        _biteCount++;
        _bittenPromptUntilTick = _tick
            + (long)MathF.Ceiling(BittenPromptSeconds * TicksPerSecond);
        ShowToast($"BITTEN x{_biteCount}");
        AddKick();
        GD.Print($"[RAT-ARENA] bite lands count={_biteCount} t={_tick}");
    }

    /// <summary>束缚期玩家站位修正（R18 重做——近身减速机制的替代）：目标位 = 怪物正面
    /// 水平 GrabHoldDistance 处（爬行按 CrawlFactor 再收 15%），锁镜头窗（CameraTakeover-
    /// Seconds）的指数收敛把玩家平滑拉过去，窗后余差 &lt;5% = 贴住定距。只修水平——竖直
    /// 交给玩家自驱重力/地面碰撞兜底。束缚期玩家水平自驱恒零（InputLocked 下每 tick 归零），
    /// 分步修正不会被自驱抵消（旧全量交付的 TentaclePlant 教训不适用于锁输入态）。</summary>
    private void ApplyGrabPositionCorrection()
    {
        Vector3 fwd = _controller.Facing;
        fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-8f)
            return;
        float hold = GrabHoldDistance
            * Mathf.Lerp(1f, 0.85f, Mathf.Clamp(_controller.CrawlFactor, 0f, 1f));
        Vector3 desired = _controller.Chest.Pos + fwd.Normalized() * hold;
        Vector3 delta = desired - _player.GlobalPosition;
        delta.Y = 0f;
        float k = 1f - MathF.Exp(
            -(float)TickDt * 3f / Mathf.Max(0.05f, CameraTakeoverSeconds));
        _player.GlobalPosition += delta * k;
    }

    /// <summary>解除攻击占用（Grabbed/Strike/Bite 通用）：清 GrabTarget/MouthDrive、解锁
    /// 玩家（曾被锁且 shove 则背向推离——别站在原地立刻被再抓）。</summary>
    private void ReleasePlayer(bool shove)
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
            {
                // 走外部冲量通道（评审修复 R18b）：直写 Velocity 会被玩家「无输入
                // 即刻停」语义在下一物理步一步归零——位移恒零的死机制。
                _player.AddImpulse(away.Normalized() * ReleaseShoveSpeed);
            }
        }
    }

    /// <summary>回 Chase 并压攻击冷却（时间戳制——冷却不占相位，追逐照常）。</summary>
    private void BeginCooldown(float cooldownSeconds)
    {
        _attackReadyTick = _tick + Math.Max(1,
            (long)MathF.Ceiling(cooldownSeconds * TicksPerSecond));
        _phase = ArenaPhase.Chase;
    }

    /// <summary>压硬直（命中触发，R19 起不占相位）：抢占 Strike/Grabbed/Bite 时先干净放人
    /// （无推离——玩家是被自己打断的）回 Chase；封攻击门到 _stunUntilTick + 压吃痛回神窗
    /// （min(硬直, StunReelSeconds)——Chase 内部的无移动意图前缀，见 DriveChase）。
    /// 时长按部位、重复命中取 max 刷新。0 秒 = 该部位硬直关闭。
    /// 抢占攻击相位时**必须压入攻击冷却**（评审修复 R18b）：冷却本来由相位完成行压入，
    /// 抢占跳过完成行——已咬中的 Bite 被枪打断反而免掉 2.5s 冷却、硬直一过立刻再咬，
    /// 开枪成了帮怪物提速。已咬中走完整冷却，蓄势/扑抓中被打断走短冷却；取 max 不缩短
    /// 断臂钉子已压入的时间戳。</summary>
    private void ApplyStun(float seconds)
    {
        if (seconds <= 0f)
            return; // 该部位硬直关闭 → 命中不打断攻击（冷却仍由相位完成行正常压入）
        if (_phase is ArenaPhase.Strike or ArenaPhase.Grabbed or ArenaPhase.Bite)
        {
            ReleasePlayer(shove: false);
            float cooldown = (_phase == ArenaPhase.Bite && _biteLanded)
                || (_phase == ArenaPhase.Grabbed && _tick - _grabStartTick >= BiteWindupTicks)
                ? AttackCooldownSeconds
                : StrikeAbortRecoverSeconds;
            _attackReadyTick = Math.Max(_attackReadyTick, _tick + Math.Max(1,
                (long)MathF.Ceiling(cooldown * TicksPerSecond)));
            _phase = ArenaPhase.Chase;
        }
        _stunUntilTick = Math.Max(_stunUntilTick,
            _tick + Math.Max(1, (long)MathF.Ceiling(seconds * TicksPerSecond)));
        float reel = MathF.Min(seconds, StunReelSeconds);
        if (reel > 0f)
        {
            _reelUntilTick = Math.Max(_reelUntilTick,
                _tick + Math.Max(1, (long)MathF.Ceiling(reel * TicksPerSecond)));
        }
    }

    /// <summary>死亡（血量归零）：放人、清全部输入、内核昏迷（Conscious=false → 伺服全停
    /// + 重力回归，倒地涌现）。终态——静止判据满足后尸体永久冻结（见 TickCorpseFreeze）。</summary>
    private void EnterDead()
    {
        ReleasePlayer(shove: false);
        _controller.LookTarget = null;
        _controller.MoveTarget = null;
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 0f;
        _controller.Conscious = false;
        _phase = ArenaPhase.Dead;
        _corpseStillStreak = 0;
        GD.Print($"[RAT-ARENA] dead hp=0 t={_tick}");
    }

    /// <summary>尸体静止判据：胸/髋/头 + 全部肢体粒子速度连续 CorpseStillTicks 低于阈值 →
    /// 永久冻结（此后不再 Tick 内核，完全固定零交互；速度 ≈0 时 Pos−LastPos 亚毫米，
    /// 渲染插值冻在原地不可见抖动）。</summary>
    private void TickCorpseFreeze()
    {
        if (_corpseFrozen)
            return;
        bool still = _controller.Chest.Vel.Length() < CorpseStillVelocity
            && _controller.Hips.Vel.Length() < CorpseStillVelocity
            && _controller.Head.Vel.Length() < CorpseStillVelocity;
        for (int i = 0; still && i < _controller.Arms.Count; i++)
            still = _controller.Arms[i].Vel.Length() < CorpseStillVelocity;
        for (int i = 0; still && i < _controller.Legs.Count; i++)
            still = _controller.Legs[i].Vel.Length() < CorpseStillVelocity;
        _corpseStillStreak = still ? _corpseStillStreak + 1 : 0;
        if (_corpseStillStreak >= CorpseStillTicks)
        {
            _corpseFrozen = true;
            GD.Print($"[RAT-ARENA] corpse frozen t={_tick}");
        }
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
    /// Grabbed 期禁枪（准心同步隐藏）；其余相位都能开——含 Bite 蓄势的反击窗与尸体补枪。</summary>
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

    /// <summary>命中处置固定序（R18）：① 伤害（头 ×HeadDamageMultiplier，断肢/残肢命中
    /// 一视同仁计 1——「非头部 10 枪」的用户口径）→ ② 部位击退（Impact 接缝）→ ③ 肢体
    /// 血量（R20：每肢 LimbHp 发，打空才走断肢流；扣肢体血与扣总血同发并行）→ ④ 血量
    /// 归零 = 死亡（压过硬直）→ ⑤ 硬直 + 命中反馈。尸体只回弹 toast，零效果。</summary>
    private void HandleHit(in RatHitReport hit, Vector3 gunDirection)
    {
        string label = PartLabel(hit);
        if (_phase == ArenaPhase.Dead)
        {
            ShowToast($"HIT: {label} (corpse)");
            return;
        }

        int damage = hit.Part == RatBodyPart.Head ? HeadDamageMultiplier : 1;
        _hp = Math.Max(0, _hp - damage);
        ApplyHitImpact(hit, gunDirection);

        bool severedNow = false;
        int limbHpLeft = -1;
        if (hit.Part is RatBodyPart.Arm or RatBodyPart.Leg
            && !hit.SeveredAlready && SeverOnHit)
        {
            int limb = (int)LimbIdOf(hit);
            _limbHp[limb] = Math.Max(0, _limbHp[limb] - 1);
            limbHpLeft = _limbHp[limb];
            if (limbHpLeft == 0)
            {
                SeverLimb(hit, gunDirection);
                severedNow = true;
            }
        }

        if (_hp <= 0)
        {
            EnterDead();
            ShowToast($"HIT: {label} -> KILLED");
            GD.Print($"[RAT-ARENA] shot hit {label} kills t={_tick}");
            return;
        }

        ApplyStun(hit.Part switch
        {
            RatBodyPart.Head => StunHeadSeconds,
            RatBodyPart.Torso => StunBodySeconds,
            _ => StunLimbSeconds,
        });
        if (severedNow)
        {
            ShowToast($"HIT: {label} -> SEVERED AT " +
                      (hit.Part == RatBodyPart.Arm ? "ELBOW" : "KNEE"));
        }
        else if (hit.SeveredAlready)
        {
            ShowToast($"HIT: {label} (stump)");
        }
        else if (limbHpLeft > 0)
        {
            ShowToast($"HIT: {label} ({limbHpLeft}/{_limbHpMax})");
        }
        else
        {
            ShowToast($"HIT: {label}");
        }
        string limbNote = limbHpLeft >= 0 ? $" limbHp={limbHpLeft}" : "";
        GD.Print($"[RAT-ARENA] shot hit {label} hp={_hp}{limbNote} t={_tick}");
    }

    /// <summary>部位击退（内核 Impact/ImpactTwist 接缝，opt-in）：沿枪向水平分量注入命中
    /// chunk，方向按部位塑形（R19——直挺挺水平后退读起来木讷）：
    /// 头 = 甩头带仰角（头 chunk 沿「枪向 + 向上偏置」满额 + 胸 0.5× 水平退身——脖是
    /// PullOnly，纯头冲量只甩头不退身）；躯干 = 胸满额 + 髋 0.7×（整体后错步）；
    /// 四肢 = 冲量转注相邻躯干 chunk（手/脚是追目标粒子，直接推粒子会被追猎伺服一 tick
    /// 吃掉）+ 躯干拧转（转向 = 力臂×枪向 的转矩符号：打右臂右肩向后拧）。踩地的脚被身体
    /// 甩在身后 → plant-and-trail 超阈重迈——踉跄退步全程涌现，宿主不摆姿势；拧开的朝向
    /// 由吃痛窗后的 SlewFacing 甩回猎物。</summary>
    private void ApplyHitImpact(in RatHitReport hit, Vector3 gunDirection)
    {
        Vector3 dir = gunDirection;
        dir.Y = 0f;
        if (dir.LengthSquared() < 1e-8f)
            return;
        dir = dir.Normalized();
        switch (hit.Part)
        {
            case RatBodyPart.Head:
            {
                Vector3 headDir = dir + Vector3.Up * HitImpactHeadUpBias;
                headDir = headDir.LengthSquared() > 1e-8f ? headDir.Normalized() : dir;
                _controller.Impact(_controller.Head, headDir * HitImpactHead);
                _controller.Impact(_controller.Chest, dir * (HitImpactHead * 0.5f));
                break;
            }
            case RatBodyPart.Torso:
                _controller.Impact(_controller.Chest, dir * HitImpactBody);
                _controller.Impact(_controller.Hips, dir * (HitImpactBody * 0.7f));
                break;
            case RatBodyPart.Arm:
                _controller.Impact(_controller.Chest, dir * HitImpactLimb);
                ApplyHitTwist(hit.Side, dir, HitTwistArmDegrees);
                break;
            default:
                _controller.Impact(_controller.Hips, dir * HitImpactLimb);
                ApplyHitTwist(hit.Side, dir, HitTwistLegDegrees);
                break;
        }
    }

    /// <summary>肢体命中的躯干拧转：力臂 = 命中侧的横向偏置（right×Side），转矩 =
    /// 力臂×枪向 的竖直分量定符号——同一发子弹打右臂与打左臂拧向相反，侧后方来弹
    /// 也自然给出正确转向（纯几何，无按侧硬编码）。</summary>
    private void ApplyHitTwist(int side, Vector3 gunDirHorizontal, float degrees)
    {
        if (degrees <= 0f || side == 0)
            return;
        Vector3 right = _controller.Facing.Cross(Vector3.Up);
        if (right.LengthSquared() < 1e-8f)
            return;
        Vector3 lever = right.Normalized() * side;
        float torqueY = lever.Cross(gunDirHorizontal).Y;
        if (Mathf.Abs(torqueY) < 1e-6f)
            return;
        _controller.ImpactTwist(MathF.Sign(torqueY) * Mathf.DegToRad(degrees), Vector3.Up);
    }

    /// <summary>命中报告 → 肢体 id（RatFiendLimbId 序：左臂 右臂 左腿 右腿）。仅对
    /// Arm/Leg 命中调用。</summary>
    private static RatFiendLimbId LimbIdOf(in RatHitReport hit) => hit.Part == RatBodyPart.Arm
        ? (hit.Side < 0 ? RatFiendLimbId.ArmLeft : RatFiendLimbId.ArmRight)
        : (hit.Side < 0 ? RatFiendLimbId.LegLeft : RatFiendLimbId.LegRight);

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
    /// ④ 镜头单发 kick；⑤ 并发钉子：断的是攻击占用臂（Strike/Grabbed 期）→ 立即放人 +
    /// 压完整冷却回 Chase——状态永不悬空。断腿不进钉子：爬行继续追
    /// （内核涌现，宿主不设 Downed 相位）。
    /// </summary>
    private void SeverLimb(in RatHitReport hit, Vector3 gunDirection)
    {
        bool isArm = hit.Part == RatBodyPart.Arm;
        int index = hit.Side < 0 ? 0 : 1;
        RatFiendLimbId id = LimbIdOf(hit);
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

        // 并发钉子：攻击占用臂被断 → 状态立即收干净（放人 + 完整冷却回 Chase）。
        // 独立于硬直存在——硬直秒数被调成 0 时这条不变量也必须成立；HandleHit 随后的
        // ApplyStun 随后在此基础上叠硬直/吃痛窗时间戳（冷却时间戳不受影响）。
        if (isArm && _phase is ArenaPhase.Strike or ArenaPhase.Grabbed)
        {
            GD.Print($"[RAT-ARENA] attack arm severed — force release t={_tick}");
            ReleasePlayer(shove: true);
            BeginCooldown(AttackCooldownSeconds);
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
        _biteStartTick = 0;
        _biteLanded = false;
        _attackReadyTick = 0;
        _stunUntilTick = 0;
        _reelUntilTick = 0;
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

        // 四肢四格（RatFiendLimbId 序：左臂 右臂 左腿 右腿）：数字 = 剩余肢体血量，X = 已断；
        // 斜杠分隔——LimbHp 可调到两位数，裸拼接会歧义。
        string limbs = "";
        for (int i = 0; i < 4; i++)
        {
            if (i > 0)
                limbs += "/";
            limbs += _controller.IsSevered((RatFiendLimbId)i) ? "X" : _limbHp[i].ToString();
        }
        float cooldown = _phase == ArenaPhase.Dead
            ? 0f
            : MathF.Max(0f, (_attackReadyTick - _tick) / (float)TicksPerSecond);
        float stun = _phase == ArenaPhase.Dead
            ? 0f
            : MathF.Max(0f, (_stunUntilTick - _tick) / (float)TicksPerSecond);
        string noArms = AliveArmCount() == 0 ? " no-arms" : "";
        string frozen = _corpseFrozen ? " frozen" : "";
        _hud.SetStatus(
            $"RAT ARENA — phase={_phase} hp={_hp}/{MonsterHp} " +
            $"crawl={_controller.CrawlFactor:F2} grips={_controller.CrawlGripCount} " +
            $"limbs={limbs} cooldown={cooldown:F1}s stun={stun:F1}s " +
            $"bites={_biteCount}{noArms}{frozen}\n" +
            "[LMB] shoot  [R] restart  [F1] hud  [Esc] mouse");

        if (_phase == ArenaPhase.Dead)
            _hud.SetPrompt("KILLED", $"{_biteCount} bites taken — R to restart");
        else if (_phase == ArenaPhase.Grabbed)
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
