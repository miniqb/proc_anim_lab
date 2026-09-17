using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Spider;
using ProcAnim.Core.Terrain;
using ProcAnimLab.DaddyLongLegsSandbox; // 房间尺度常量真相源 DaddyLongLegsMazeBuilder（RoomHeight/WallThickness）
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.SpiderSandbox;

/// <summary>
/// 蜘蛛跳跃攻击竞技场（探索场景，不进矩阵）：封闭大房间（可开障碍物），蜘蛛以之字/爬墙/上顶
/// 的潜行路线接近第一人称玩家（<see cref="SpiderStalkPlanner"/>），猎物进入**按所处表面
/// 缩放的攻击范围**（地面最小 &lt; 墙 &lt; 天花板最大——正下方的人也够得到）且视线通畅时，
/// 短蓄势后由 <see cref="SpiderLeapPlanner"/> 反解起跳速度精确扑向猎物胸口（攻距与跳速
/// 解耦：设定攻距优先、速度按目标现算，1m 与 9m 都命中）；命中 = 玩家击退 + 蜘蛛略朝后
/// 反弹再落地（≙ RW BigSpider 撞猎物即反向 Jump），两者天然拉开距离；扑空则抛物线落地。
/// 玩家的反制是手枪：命中扣血（默认 3 枪死）+ 反向冲量；墙/顶上被击中走击落保持期
/// （<c>Launch(impulse, KnockOffHoldTicks)</c>：腿不抓、撞面反弹离面、落地才重新站稳），地面上被推退
/// 一截；血量归零倒地、落地后翻身腹朝天蜷腿，静止后尸体永久冻结。1/2/3 切换蜘蛛预设，R 重开。
/// 飞行中身体按截止式对准（到点前正面朝猎物、背朝天，等角速度不瞬切；LeapFacing 每 tick 跟猎物）。
///
/// 玩法全部在宿主层；内核只走 opt-in 接缝（BeginLeap / Launch / Conscious + Leaping 观测），
/// 回归路线零漂移。相位机见 <see cref="ArenaPhase"/>。
/// 无头自检：<c>--bot=still|strafe --ticks=N [--preset=name] [--shoot-every=N]
/// [--shoot-on=ground|wall|ceiling] [--hp=N]</c> 用脚本化玩家跑固定 tick 数，每次命中后 60 tick
/// 打 <c>[SPIDER-ARENA-HIT]</c>（下落/后退/回抓），结尾打 <c>[SPIDER-ARENA-RESULT]</c>。
/// </summary>
public partial class SpiderArenaWorld : Node3D
{
	private const double TickDt = 0.025;
	private const int TicksPerSecond = 40;

	/// <summary>两端出生点离端墙的距离（怪物 +X 端 / 玩家 −X 端，BoxRoomArenaBuilder 惯例）。</summary>
	private const float SpawnEndInset = 4f;

	/// <summary>玩家胶囊几何（镜像 ArenaFirstPersonPlayer 规格：r0.35 / h1.7）——扑击命中判定用。</summary>
	private const float PlayerCapsuleRadius = 0.35f;
	private const float PlayerCapsuleBottom = 0.35f;
	private const float PlayerCapsuleTop = 1.35f;
	private const float PlayerChestHeight = 0.9f;

	private const float TracerSeconds = 0.06f;
	private const float HitMarkerSeconds = 0.18f;
	private const float ToastSeconds = 1.0f;
	private const float PouncedPromptSeconds = 1.2f;

	private const float KickDegrees = 0.9f;
	private const float KickOffsetMeters = 0.012f;
	private const float KickFrequencyHz = 11f;
	private const float KickMax = 2f;
	private const float KickDecaySeconds = 0.3f;

	/// <summary>路径点停滞判据：这么久没有朝路径点缩短距离就重新规划。</summary>
	private const float WaypointStallSeconds = 1.2f;

	/// <summary>死亡定格判据（同鼠煞竞技场）。</summary>
	private const float CorpseStillVelocity = 0.003f;
	private const int CorpseStillTicks = 20;

	// ---- Inspector 导出（Daddy 纪律：全 Inspector、默认值唯一真相源、生效值打在 ready 行）----

	[ExportGroup("Arena / Creature")]
	[Export(PropertyHint.Enum, "spider-small,spider-large,spider-lean")]
	public string DefaultPreset { get; set; } = "spider-small";

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

	/// <summary>房内障碍物（柱 / 台阶 / 箱堆 / 挂墙搁板）：蜘蛛可附着任意表面的展示件。</summary>
	[Export]
	public bool Obstacles { get; set; } = true;

	[ExportGroup("Arena / Stalk")]
	/// <summary>每个接近路径点离身体的距离（米）——内核 MoveTarget 契约要求「邻近可达」。</summary>
	[Export(PropertyHint.Range, "1,6,0.1")]
	public float StalkStepRadius { get; set; } = 2.6f;

	[Export(PropertyHint.Range, "1,10,0.1")]
	public float ClimbScanRadius { get; set; } = 4.5f;

	/// <summary>墙面/顶面候选的评分加成：越大越爱爬墙上顶。</summary>
	[Export(PropertyHint.Range, "0,2,0.05")]
	public float WallBonus { get; set; } = 0.5f;

	[Export(PropertyHint.Range, "0,2,0.05")]
	public float CeilingBonus { get; set; } = 0.8f;

	/// <summary>之字权重（期望侧逐点交替）与直线惩罚：0 = 直奔猎物。</summary>
	[Export(PropertyHint.Range, "0,2,0.05")]
	public float ZigzagWeight { get; set; } = 0.5f;

	[Export(PropertyHint.Range, "0,2,0.05")]
	public float StraightPenalty { get; set; } = 0.35f;

	/// <summary>猎物水平距离小于此值时改为绕行（冷却期在猎物身边转圈）。</summary>
	[Export(PropertyHint.Range, "0,8,0.1")]
	public float CircleRadius { get; set; } = 3.0f;

	/// <summary>攻击冷却期间墙/顶候选加成的倍率（≙ RW 跳后 `AI.stayAway`）：扑完先退上墙/顶，
	/// 下一击从高处更大攻距发起——「扑击 → 上墙 → 俯冲」的节奏来源。1 = 不偏置。</summary>
	[Export(PropertyHint.Range, "1,5,0.1")]
	public float CooldownClimbBias { get; set; } = 2.2f;

	[Export(PropertyHint.Range, "0.2,3,0.05")]
	public float WaypointArriveRadius { get; set; } = 0.55f;

	[Export(PropertyHint.Range, "0.5,10,0.1")]
	public float WaypointTimeoutSeconds { get; set; } = 3.0f;

	[Export(PropertyHint.Range, "0,100000,1")]
	public int StalkSeed { get; set; } = 7;

	[ExportGroup("Arena / Attack")]
	/// <summary>三档攻击范围（米，主身体节到猎物胸口）：按支撑法线与世界上方向的夹角
	/// 分段线性插值——地面 → 墙 → 天花板。物理上墙/顶起跳能借势跳更远；顶面档必须盖住
	/// 「人在正下方」的房高（默认 9 &gt; 3.2m 房高）。跳速另按目标现算，与这三档解耦。</summary>
	[Export(PropertyHint.Range, "0.5,20,0.1")]
	public float GroundAttackRange { get; set; } = 3.5f;

	[Export(PropertyHint.Range, "0.5,20,0.1")]
	public float WallAttackRange { get; set; } = 6.0f;

	[Export(PropertyHint.Range, "0.5,20,0.1")]
	public float CeilingAttackRange { get; set; } = 9.0f;

	/// <summary>名义扑击速度（米/秒）：飞行时长以「距离 ÷ 该速度」为中心搜索，
	/// 真正的起跳速度由规划器按目标反解（近则慢、远则快）。</summary>
	[Export(PropertyHint.Range, "2,30,0.5")]
	public float LeapPreferredSpeedMps { get; set; } = 9f;

	/// <summary>起跳速度硬上限（米/秒）；超过即视为够不到（正常不该触发——攻距优先）。</summary>
	[Export(PropertyHint.Range, "5,60,0.5")]
	public float LeapMaxSpeedMps { get; set; } = 26f;

	/// <summary>弹道相对起跳点的最大抬升（米）：房高 3.2，过高的抛物线会撞顶。</summary>
	[Export(PropertyHint.Range, "0.2,5,0.1")]
	public float LeapMaxRise { get; set; } = 1.6f;

	[Export(PropertyHint.Range, "1,40,1")]
	public int LeapMinTicks { get; set; } = 6;

	[Export(PropertyHint.Range, "10,200,1")]
	public int LeapMaxTicks { get; set; } = 60;

	/// <summary>猎物速度提前量系数（0 = 不预判，1 = 完全预判到落点时刻）。</summary>
	[Export(PropertyHint.Range, "0,1.5,0.05")]
	public float LeadFactor { get; set; } = 0.7f;

	/// <summary>起跳前蓄势（秒，原地压低身体）：给玩家一个可读的预兆窗。0 = 立刻跳。</summary>
	[Export(PropertyHint.Range, "0,2,0.05")]
	public float WindupSeconds { get; set; } = 0.3f;

	/// <summary>命中后的攻击冷却（秒）；扑空走更短的 MissCooldownSeconds。</summary>
	[Export(PropertyHint.Range, "0.25,20,0.25")]
	public float AttackCooldownSeconds { get; set; } = 2.5f;

	[Export(PropertyHint.Range, "0.1,10,0.1")]
	public float MissCooldownSeconds { get; set; } = 1.0f;

	/// <summary>扑击命中判定的额外半径（米，加在身体节半径 + 玩家胶囊半径上）。</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float PounceHitAssist { get; set; } = 0.15f;

	/// <summary>命中给玩家的水平击退速度（米/秒，走玩家外部冲量通道，≈0.5s 内衰减）。</summary>
	[Export(PropertyHint.Range, "0,12,0.25")]
	public float PounceKnockbackSpeed { get; set; } = 4.0f;

	/// <summary>击退的竖直分量（米/秒）。注意外部冲量通道的竖直分量会按几何级数累加
	/// （水平每步被覆写、竖直不会），有效值 ≈ 6.7×——取小。</summary>
	[Export(PropertyHint.Range, "0,2,0.05")]
	public float PounceKnockbackUp { get; set; } = 0.2f;

	/// <summary>命中后蜘蛛的反弹速度（米/秒）与向上偏置（≙ RW 撞猎物后朝反向 +10px 起跳）。</summary>
	[Export(PropertyHint.Range, "0,20,0.25")]
	public float ReboundSpeedMps { get; set; } = 5.5f;

	[Export(PropertyHint.Range, "0,3,0.05")]
	public float ReboundUpBias { get; set; } = 0.9f;

	/// <summary>真正起跳前要求视线通畅（主身体节 → 胸口无地形遮挡）。</summary>
	[Export]
	public bool RequireLineOfSight { get; set; } = true;

	[ExportGroup("Arena / Damage")]
	[Export(PropertyHint.Range, "1,50,1")]
	public int SpiderHp { get; set; } = 3;

	/// <summary>命中给蜘蛛的反向冲量（米/秒，沿枪向）+ 向上分量；走内核 Launch：全腿松手
	/// + 重力回归，地面上被推退一截再抓地（默认约 0.8m）。</summary>
	[Export(PropertyHint.Range, "0,20,0.25")]
	public float HitImpulseMps { get; set; } = 10.5f;

	[Export(PropertyHint.Range, "0,10,0.25")]
	public float HitImpulseUpMps { get; set; } = 6.2f;

	/// <summary>墙/顶上被击中的击落保持期（tick）：腿不找抓点、自由落体、法线缓慢翻正。没有它，
	/// 枪向冲量把蜘蛛压在墙上，腿在下滑半米内就把身体抓回去（旧症状：墙上打不掉）。</summary>
	[Export(PropertyHint.Range, "0,40,1")]
	public int KnockOffHoldTicks { get; set; } = 12;

	/// <summary>墙/顶上被击中时，冲量打进表面的分量按此比例反射为离面速度（撞面反弹），
	/// 让身体真正离开表面而不是贴着滑落。</summary>
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float KnockOffBounce { get; set; } = 0.35f;

	/// <summary>命中硬直（秒）：封攻击门（潜行照常）；正在蓄势/飞行时被打断回潜行。</summary>
	[Export(PropertyHint.Range, "0,3,0.05")]
	public float HitStunSeconds { get; set; } = 0.6f;

	[ExportGroup("Arena / Gun")]
	[Export(PropertyHint.Range, "5,200,1")]
	public float GunRange { get; set; } = 60f;

	[Export(PropertyHint.Range, "0.05,2,0.05")]
	public float GunCooldownSeconds { get; set; } = 0.25f;

	/// <summary>瞄准冗余（米）：身体球 / 腿胶囊各自加在视觉半径上（腿极细，冗余给足）。</summary>
	[Export(PropertyHint.Range, "0,0.6,0.01")]
	public float BodyAimAssist { get; set; } = 0.12f;

	[Export(PropertyHint.Range, "0,0.6,0.01")]
	public float LegAimAssist { get; set; } = 0.10f;

	// ---- 运行态 ----

	/// <summary>相位机：Stalk（潜行接近 + 攻击门）→ Windup（蓄势）→ Leap（弹道飞行，逐 tick
	/// 命中判定）→ Rebound（命中后反弹飞行，落地压冷却）/ 直接落地扑空压短冷却 → Stalk；
	/// 命中硬直从 Windup/Leap/Rebound 抢占回 Stalk；血量归零 → Dead（终态）。
	/// 冷却与硬直都是时间戳，不占相位。</summary>
	private enum ArenaPhase
	{
		Stalk,
		Windup,
		Leap,
		Rebound,
		Dead,
	}

	private enum BotMode
	{
		None,
		Still,
		Strafe,
	}

	private readonly RaycastTerrainQuery _terrain = new();
	private BoxRoomArenaBuilder _arena = null!;
	private SpiderBreedParams _breed = null!;
	private SpiderLocomotionController _controller = null!;
	private ProcAnimLab.Render.IFormalRenderer? _formal;
	private SpiderBodyRenderer? _debugRenderer;
	private ArenaFirstPersonPlayer _player = null!;
	private SpiderArenaHud _hud = null!;
	private SpiderStalkPlanner _stalk = null!;
	private Camera3D _bootCamera = null!;

	private Vector3 _gravityPerTick;
	private double _tickAccumulator;
	private long _tick;
	private bool _fatal;

	private ArenaPhase _phase = ArenaPhase.Stalk;
	private long _windupStartTick;
	private long _attackReadyTick;
	private long _stunUntilTick;
	private int _hp;
	private int _corpseStillStreak;
	private bool _corpseFrozen;
	private int _pounceCount;
	private int _leapAttempts;
	private int _leapMisses;
	private long _pouncedPromptUntilTick = -1;
	private SpiderSurfaceKind _leapSurface;
	private float _leapRange;
	private SpiderLeapPlan _leapPlan;
	private Vector3 _lastPlayerPos;
	private Vector3 _playerVelPerTick;

	// 潜行路径点
	private SpiderStalkWaypoint? _waypoint;
	private long _waypointSetTick;
	private float _waypointBestDistance;
	private long _waypointProgressTick;
	private int _waypointsPlanned;
	private readonly int[] _surfaceTicks = new int[3];

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
	private int _shotsFired;
	private int _shotsHit;

	// 镜头 kick（纯渲染侧）
	private float _kick;
	private float _kickTime;
	private bool _kickApplied;

	// 命中探针：每次命中后跟踪 60 tick（下落深度 / 水平后退 / 回抓），bot 结果与 HUD 日志共用
	private const int HitProbeTicks = 60;
	private long _hitProbeTick = -1;
	private SpiderSurfaceKind _hitProbeSurface;
	private bool _hitProbeOnSurface;
	private bool _hitProbeReleased;
	private int _hitProbeRegripTick;
	private Vector3 _hitProbePos;
	private float _hitProbeMinY;
	private int _groundHits;
	private float _groundRecoilSum;
	private int _knockOffs;
	private int _knockOffsFell;

	// 无头自检
	private BotMode _bot = BotMode.None;
	private long _botTicks;
	private int _botShootEvery;
	private SpiderSurfaceKind? _botShootOn;
	private string? _cliPreset;

	// 事件截图（--screenshot-dir=，需窗口模式）：追拍相机对准蜘蛛，飞行中段 / 墙面站稳 /
	// 顶面站稳各抓一张；两帧序列：切相机 → 下一帧取上一帧渲染结果 → 还原玩家相机。
	private string? _screenshotDir;
	private readonly HashSet<string> _screenshotsTaken = new();
	private string? _pendingShotName;
	private int _pendingShotStage;

	public override void _Ready()
	{
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
		System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

		_bootCamera = GetNode<Camera3D>("Camera3D");
		ParseArgs();
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
			_arena = new BoxRoomArenaBuilder(Vector3.Zero, ArenaWidth, ArenaDepth, SpawnEndInset);
		}
		catch (InvalidOperationException error)
		{
			GD.PushError($"[SPIDER-ARENA] {error.Message}");
			_fatal = true;
			GetTree().Quit(2);
			return;
		}
		_arena.Build(this);
		if (Obstacles)
		{
			BuildObstacles();
		}

		_stalk = new SpiderStalkPlanner(StalkSeed);
		ApplyStalkParams();

		_player = new ArenaFirstPersonPlayer { Name = "ArenaPlayer" };
		AddChild(_player);
		_player.Place(_arena.PlayerSpawn, _arena.MonsterSpawn);
		_player.SetActive(true);
		_lastPlayerPos = _player.GlobalPosition;

		SpawnSpider();

		_hud = new SpiderArenaHud();
		_hud.Build(this);

		_tracer = new ProcAnimLab.Render.TubeMeshBuilder();
		_tracer.Build(this, srgbVertexColors: true);

		if (_bot != BotMode.None)
		{
			_player.ScriptedInput = Vector2.Zero;
		}

		GD.Print($"[SPIDER-ARENA] ready preset={_breed.Name} tps={HostPhysicsTps} " +
				 $"formal={FormalRender} room={ArenaWidth:F0}x{ArenaDepth:F0}m obstacles={Obstacles} " +
				 $"ranges={GroundAttackRange:F1}/{WallAttackRange:F1}/{CeilingAttackRange:F1}m " +
				 $"hp={SpiderHp} hit={HitImpulseMps:F1}+{HitImpulseUpMps:F1}m/s " +
				 $"knockOff={KnockOffHoldTicks}ticks/bounce{KnockOffBounce:F2} bot={_bot} botTicks={_botTicks} " +
				 $"shootOn={(_botShootOn is { } so ? so.ToString() : "any")} " +
				 $"startDistance={_controller.Primary.Pos.DistanceTo(_player.EyePosition):F1}m");
	}

	private void ParseArgs()
	{
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			if (arg.StartsWith("--bot=", StringComparison.Ordinal))
			{
				_bot = arg["--bot=".Length..] switch
				{
					"strafe" => BotMode.Strafe,
					_ => BotMode.Still,
				};
			}
			else if (arg.StartsWith("--ticks=", StringComparison.Ordinal)
				&& long.TryParse(arg["--ticks=".Length..], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out long ticks))
			{
				_botTicks = ticks;
			}
			else if (arg.StartsWith("--preset=", StringComparison.Ordinal))
			{
				_cliPreset = arg["--preset=".Length..];
			}
			else if (arg.StartsWith("--screenshot-dir=", StringComparison.Ordinal))
			{
				_screenshotDir = arg["--screenshot-dir=".Length..];
			}
			else if (arg.StartsWith("--tps=", StringComparison.Ordinal)
				&& int.TryParse(arg["--tps=".Length..], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out int tps))
			{
				HostPhysicsTps = tps; // 无头自检加速（矩阵同款：宿主 400Hz、逻辑仍固定一 tick）
			}
			else if (arg.StartsWith("--shoot-every=", StringComparison.Ordinal)
				&& int.TryParse(arg["--shoot-every=".Length..], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out int every))
			{
				_botShootEvery = every;
			}
			else if (arg.StartsWith("--shoot-on=", StringComparison.Ordinal))
			{
				_botShootOn = arg["--shoot-on=".Length..] switch
				{
					"wall" => SpiderSurfaceKind.Wall,
					"ceiling" => SpiderSurfaceKind.Ceiling,
					_ => SpiderSurfaceKind.Ground,
				};
			}
			else if (arg.StartsWith("--hp=", StringComparison.Ordinal)
				&& int.TryParse(arg["--hp=".Length..], NumberStyles.Integer,
					CultureInfo.InvariantCulture, out int hp))
			{
				SpiderHp = hp; // 击落探针要多次命中而不打死
			}
		}
		if (_cliPreset is not null)
		{
			DefaultPreset = _cliPreset;
		}
	}

	private bool ValidateExports()
	{
		try
		{
			_breed = SpiderFactory.ByName(DefaultPreset);
		}
		catch (ArgumentException)
		{
			return Fail($"DefaultPreset '{DefaultPreset}' is not spider-small/large/lean");
		}
		if (HostPhysicsTps is < 40 or > 1000)
			return Fail($"HostPhysicsTps must be in [40,1000], got {HostPhysicsTps}");
		if (!FinitePositive(GravityMps2))
			return Fail($"GravityMps2 must be finite and positive, got {GravityMps2}");
		if (!FinitePositive(StalkStepRadius) || !FinitePositive(ClimbScanRadius))
			return Fail("stalk radii must be finite and positive");
		if (!FiniteNonNegative(WallBonus) || !FiniteNonNegative(CeilingBonus)
			|| !FiniteNonNegative(ZigzagWeight) || !FiniteNonNegative(StraightPenalty)
			|| !FiniteNonNegative(CircleRadius))
		{
			return Fail("stalk weights must be finite and >= 0");
		}
		if (!float.IsFinite(CooldownClimbBias) || CooldownClimbBias < 1f)
			return Fail($"CooldownClimbBias must be finite and >= 1, got {CooldownClimbBias}");
		if (!FinitePositive(WaypointArriveRadius) || !FinitePositive(WaypointTimeoutSeconds))
			return Fail("waypoint arrive radius / timeout must be finite and positive");
		if (!FinitePositive(GroundAttackRange) || !FinitePositive(WallAttackRange)
			|| !FinitePositive(CeilingAttackRange))
		{
			return Fail("attack ranges must be finite and positive");
		}
		if (!FinitePositive(LeapPreferredSpeedMps) || !FinitePositive(LeapMaxSpeedMps)
			|| !FinitePositive(LeapMaxRise))
		{
			return Fail("leap speed / rise must be finite and positive");
		}
		if (LeapMinTicks < 1 || LeapMaxTicks < LeapMinTicks)
			return Fail($"leap tick range invalid: {LeapMinTicks}..{LeapMaxTicks}");
		if (!FiniteNonNegative(LeadFactor) || !FiniteNonNegative(WindupSeconds))
			return Fail("LeadFactor / WindupSeconds must be finite and >= 0");
		if (!FinitePositive(AttackCooldownSeconds) || !FinitePositive(MissCooldownSeconds))
			return Fail("cooldowns must be finite and positive");
		if (!FiniteNonNegative(PounceHitAssist) || !FiniteNonNegative(PounceKnockbackSpeed)
			|| !FiniteNonNegative(PounceKnockbackUp) || !FiniteNonNegative(ReboundSpeedMps)
			|| !FiniteNonNegative(ReboundUpBias))
		{
			return Fail("pounce hit / knockback / rebound values must be finite and >= 0");
		}
		if (SpiderHp < 1)
			return Fail($"SpiderHp must be >= 1, got {SpiderHp}");
		if (!FiniteNonNegative(HitImpulseMps) || !FiniteNonNegative(HitImpulseUpMps)
			|| !FiniteNonNegative(HitStunSeconds))
		{
			return Fail("hit impulse / stun must be finite and >= 0");
		}
		if (KnockOffHoldTicks < 0 || !FiniteNonNegative(KnockOffBounce) || KnockOffBounce > 1f)
			return Fail("KnockOffHoldTicks must be >= 0 and KnockOffBounce within [0,1]");
		if (!FinitePositive(GunRange) || !FinitePositive(GunCooldownSeconds))
			return Fail("gun range / cooldown must be finite and positive");
		if (!FiniteNonNegative(BodyAimAssist) || !FiniteNonNegative(LegAimAssist))
			return Fail("aim assists must be finite and >= 0");
		return true;

		static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;
		static bool FiniteNonNegative(float value) => float.IsFinite(value) && value >= 0f;
		static bool Fail(string message)
		{
			GD.PushError($"[SPIDER-ARENA] invalid scene configuration: {message}");
			return false;
		}
	}

	private void ApplyStalkParams()
	{
		_stalk.StepRadius = StalkStepRadius;
		_stalk.ClimbScanRadius = ClimbScanRadius;
		_stalk.WallBonus = WallBonus;
		_stalk.CeilingBonus = CeilingBonus;
		_stalk.ZigzagWeight = ZigzagWeight;
		_stalk.StraightPenalty = StraightPenalty;
		_stalk.CircleRadius = CircleRadius;
	}

	/// <summary>房内障碍物：顶天立地的柱、低台阶、箱堆、挂墙搁板——四种「任意表面」。</summary>
	private void BuildObstacles()
	{
		var root = new Node3D { Name = "Obstacles" };
		AddChild(root);
		var body = new StaticBody3D { Name = "ObstacleCollision" };
		root.AddChild(body);
		var material = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.36f, 0.33f, 0.30f),
			Roughness = 0.88f,
		};
		float w = ArenaWidth;
		float d = ArenaDepth;
		float h = DaddyLongLegsMazeBuilder.RoomHeight;
		AddBox(body, root, material, new Vector3(w * 0.40f, h * 0.5f, d * 0.30f),
			new Vector3(0.8f, h, 0.8f), "Pillar");
		AddBox(body, root, material, new Vector3(w * 0.62f, 0.25f, d * 0.72f),
			new Vector3(3.2f, 0.5f, 3.2f), "Step");
		AddBox(body, root, material, new Vector3(w * 0.27f, 0.6f, d * 0.78f),
			new Vector3(1.2f, 1.2f, 1.2f), "Crate");
		AddBox(body, root, material, new Vector3(w * 0.5f, 1.7f, 0.7f),
			new Vector3(4.0f, 0.3f, 1.3f), "Shelf");
	}

	private static void AddBox(StaticBody3D body, Node3D visuals, Material material,
		Vector3 center, Vector3 size, string name)
	{
		body.AddChild(new CollisionShape3D
		{
			Name = $"{name}Shape",
			Shape = new BoxShape3D { Size = size },
			Position = center,
		});
		visuals.AddChild(new MeshInstance3D
		{
			Name = $"{name}Mesh",
			Mesh = new BoxMesh { Size = size },
			Position = center,
			MaterialOverride = material,
		});
	}

	/// <summary>（重）建控制器与渲染件（重开/换预设安全）。出生在 +X 端地面，初始前向 −X（朝玩家端）。</summary>
	private void SpawnSpider()
	{
		Vector3 origin = new(_arena.MonsterSpawn.X, 0f, _arena.MonsterSpawn.Z);
		_controller = SpiderFactory.CreateSpiderController(origin, _breed);
		_controller.LimpBellyUp = true; // 死亡落地后主动翻身（腹朝天、腿蜷向天）
		_hp = SpiderHp;
		_corpseFrozen = false;
		_corpseStillStreak = 0;
		_formal?.Clear();
		_formal = null;
		_debugRenderer?.Clear();
		_debugRenderer = null;
		if (FormalRender)
		{
			_formal = new ProcAnimLab.Render.SpiderFormalRenderer(_controller, _breed.Name);
			_formal.Build(this);
			_formal.SetVisible(true);
		}
		else
		{
			_debugRenderer = new SpiderBodyRenderer();
			_debugRenderer.Build(this, _controller);
			_debugRenderer.SetVisible(true);
		}
		_waypoint = null;
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

	/// <summary>固定 tick 序：计数 → 绑定地形 → 玩家速度采样（提前量）→ 排队射击 → 相位喂入 →
	/// 内核 Tick（尸体冻结后跳过）→ 相位推进（读观测量）→ 无头自检。</summary>
	private void RunCoreTick()
	{
		_tick++;
		_terrain.Bind(GetWorld3D().DirectSpaceState);
		Vector3 playerPos = _player.GlobalPosition;
		_playerVelPerTick = playerPos - _lastPlayerPos;
		_lastPlayerPos = playerPos;

		BotTick();
		ProcessQueuedShot();
		FeedSpider();
		if (!_corpseFrozen)
			_controller.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
		AdvancePhase();
		TickHitProbe();
		if (_phase != ArenaPhase.Dead)
			_surfaceTicks[(int)CurrentSurface()]++;
		QueueEventScreenshots();
		BotFinish();
	}

	// ---- 相位喂入（内核 Tick 之前写输入面）----

	private void FeedSpider()
	{
		switch (_phase)
		{
			case ArenaPhase.Stalk when _tick >= _stunUntilTick:
				DriveStalk();
				TryStartAttack();
				break;
			case ArenaPhase.Stalk: // 命中硬直：不推进（击落保持期内内核本就忽略意图）
			case ArenaPhase.Windup:
			case ArenaPhase.Leap:
			case ArenaPhase.Rebound:
			case ArenaPhase.Dead:
				_controller.MoveTarget = null;
				_controller.MoveDir = Vector3.Zero;
				_controller.RunSpeed = 0f;
				if (_phase == ArenaPhase.Leap && _controller.Leaping)
				{
					// 飞行中持续对准猎物（内核按封顶角速度跟随，到点前正面朝目标）
					_controller.LeapFacing = HorizontalDir(PlayerChest() - _controller.Primary.Pos);
				}
				break;
		}
	}

	private Vector3 PlayerChest() => _player.GlobalPosition + Vector3.Up * PlayerChestHeight;

	/// <summary>水平方向（零向量 = 正上/正下：内核保持现有水平朝向）。</summary>
	private static Vector3 HorizontalDir(Vector3 delta)
	{
		delta.Y = 0f;
		return delta.LengthSquared() > 1e-8f ? delta.Normalized() : Vector3.Zero;
	}

	private SpiderSurfaceKind CurrentSurface() =>
		SpiderStalkPlanner.ClassifySurface(_controller.SupportNormal, Vector3.Up);

	/// <summary>攻击范围：按支撑法线与世界上方向的夹角在 地面 → 墙 → 顶 三档间分段线性插值。</summary>
	private float AttackRangeFor(Vector3 supportNormal)
	{
		float t = Mathf.Clamp((1f - supportNormal.Dot(Vector3.Up)) * 0.5f, 0f, 1f);
		return t < 0.5f
			? Mathf.Lerp(GroundAttackRange, WallAttackRange, t / 0.5f)
			: Mathf.Lerp(WallAttackRange, CeilingAttackRange, (t - 0.5f) / 0.5f);
	}

	private bool Footed() =>
		_controller.LegsGripping >= _controller.MinGroundedLegs && !_controller.ApplyGravity;

	/// <summary>潜行驱动：持有一个邻近路径点，到达/超时/停滞即重新规划；MoveDir = 路径点方向在
	/// 当前支撑切平面上的投影（跨面时先水平撞向墙脚，抓上墙后投影自然变成沿墙向上）。</summary>
	private void DriveStalk()
	{
		Vector3 p = _controller.Primary.Pos;
		bool replan = _waypoint is null;
		if (_waypoint is { } wp)
		{
			float distance = (wp.Point - p).Length();
			if (distance <= WaypointArriveRadius)
			{
				replan = true;
			}
			else if (_tick - _waypointSetTick > (long)(WaypointTimeoutSeconds * TicksPerSecond))
			{
				replan = true;
			}
			else
			{
				if (distance < _waypointBestDistance - 0.05f)
				{
					_waypointBestDistance = distance;
					_waypointProgressTick = _tick;
				}
				else if (_tick - _waypointProgressTick > (long)(WaypointStallSeconds * TicksPerSecond))
				{
					replan = true;
				}
			}
		}
		if (replan)
		{
			PlanWaypoint();
		}

		_controller.MoveTarget = null;
		if (_waypoint is { } target)
		{
			Vector3 n = _controller.SupportNormal;
			Vector3 toTarget = target.Point - p;
			Vector3 tangent = toTarget - n * toTarget.Dot(n);
			if (tangent.LengthSquared() < 1e-6f)
			{
				tangent = toTarget;
			}
			_controller.MoveDir = tangent.LengthSquared() > 1e-8f ? tangent.Normalized() : Vector3.Zero;
			_controller.RunSpeed = _controller.MoveDir == Vector3.Zero ? 0f : 1f;
		}
		else
		{
			// 规划失败的退化：直奔猎物（水平）。
			Vector3 direct = _player.GlobalPosition - p;
			direct.Y = 0f;
			_controller.MoveDir = direct.LengthSquared() > 1e-8f ? direct.Normalized() : Vector3.Zero;
			_controller.RunSpeed = _controller.MoveDir == Vector3.Zero ? 0f : 1f;
		}
	}

	private void PlanWaypoint()
	{
		Vector3 p = _controller.Primary.Pos;
		float climbBias = _tick < _attackReadyTick ? CooldownClimbBias : 1f;
		_stalk.WallBonus = WallBonus * climbBias;
		_stalk.CeilingBonus = CeilingBonus * climbBias;
		if (_stalk.TryPlan(_terrain, p, _controller.SupportNormal, Vector3.Up,
				_player.GlobalPosition, DaddyLongLegsMazeBuilder.RoomHeight,
				out SpiderStalkWaypoint wp))
		{
			_waypoint = wp;
			_waypointsPlanned++;
		}
		else
		{
			_waypoint = null;
		}
		_waypointSetTick = _tick;
		_waypointBestDistance = _waypoint is { } set ? (set.Point - p).Length() : 0f;
		_waypointProgressTick = _tick;
	}

	/// <summary>攻击门（仅 Stalk）：冷却过 + 硬直过 + 站稳 + 猎物在本表面档攻距内 + 视线通畅 +
	/// 规划器给得出无遮挡弹道 → 蓄势。规划在起跳那 tick 再做一次（猎物会动）。</summary>
	private void TryStartAttack()
	{
		if (_tick < _attackReadyTick || _tick < _stunUntilTick || !Footed())
			return;
		Vector3 chest = PlayerChest();
		float distance = (chest - _controller.Primary.Pos).Length();
		float range = AttackRangeFor(_controller.SupportNormal);
		if (distance > range || distance < 1e-3f)
			return;
		if (RequireLineOfSight && !HasLineOfSight(chest))
			return;
		if (!SpiderLeapPlanner.TryPlan(BuildLeapRequest(chest), _terrain, out _))
			return;

		_phase = ArenaPhase.Windup;
		_windupStartTick = _tick;
		_leapSurface = CurrentSurface();
		_leapRange = range;
		GD.Print($"[SPIDER-ARENA] windup surface={_leapSurface} dist={distance:F2}m " +
				 $"range={range:F1}m t={_tick}");
	}

	private bool HasLineOfSight(Vector3 chest)
	{
		Vector3 from = _controller.Primary.Pos;
		return !_terrain.Raycast(from, chest, out _);
	}

	private SpiderLeapRequest BuildLeapRequest(Vector3 chest)
	{
		float tickScale = (float)TickDt;
		return new SpiderLeapRequest(
			from: _controller.Primary.Pos,
			target: chest,
			targetVelocity: _playerVelPerTick * LeadFactor,
			gravityPerTick: _gravityPerTick,
			airFriction: _controller.AirborneAirFriction,
			surfaceNormal: _controller.SupportNormal,
			minOutwardSpeed: 0.02f,
			preferredSpeed: LeapPreferredSpeedMps * tickScale,
			minTicks: LeapMinTicks,
			maxTicks: LeapMaxTicks,
			maxSpeed: LeapMaxSpeedMps * tickScale,
			maxRise: LeapMaxRise,
			clearanceRadius: _controller.Primary.Radius);
	}

	/// <summary>蓄势结束：按此刻猎物位置/速度重新反解弹道并起跳；猎物已跑出视线/攻距则放弃。</summary>
	private bool PlanAndLeap()
	{
		Vector3 chest = PlayerChest();
		float distance = (chest - _controller.Primary.Pos).Length();
		if (distance > AttackRangeFor(_controller.SupportNormal) * 1.15f
			|| (RequireLineOfSight && !HasLineOfSight(chest))
			|| !SpiderLeapPlanner.TryPlan(BuildLeapRequest(chest), _terrain, out SpiderLeapPlan plan))
		{
			GD.Print($"[SPIDER-ARENA] leap aborted dist={distance:F2}m t={_tick}");
			return false;
		}
		_leapPlan = plan;
		_controller.BeginLeap(plan.LaunchVelocity, plan.FlightTicks,
			HorizontalDir(chest - _controller.Primary.Pos));
		_leapAttempts++;
		GD.Print($"[SPIDER-ARENA] leap surface={_leapSurface} dist={distance:F2}m " +
				 $"speed={plan.Speed / (float)TickDt:F1}m/s ticks={plan.FlightTicks} " +
				 $"rise={plan.PeakRise:F2}m t={_tick}");
		return true;
	}

	// ---- 相位推进（tick 侧，读内核 Tick 后的新观测量）----

	private void AdvancePhase()
	{
		switch (_phase)
		{
			case ArenaPhase.Windup:
			{
				long windupTicks = (long)MathF.Ceiling(WindupSeconds * TicksPerSecond);
				if (_tick - _windupStartTick >= windupTicks)
				{
					if (PlanAndLeap())
					{
						_phase = ArenaPhase.Leap;
					}
					else
					{
						BeginCooldown(MissCooldownSeconds);
					}
				}
				break;
			}
			case ArenaPhase.Leap:
			{
				if (_controller.Leaping)
				{
					if (_controller.LeapTicks >= 2 && SpiderTouchesPlayer())
					{
						LandPounce();
					}
				}
				else
				{
					_leapMisses++;
					GD.Print($"[SPIDER-ARENA] leap missed contact={_controller.LeapEndedByContact} t={_tick}");
					BeginCooldown(MissCooldownSeconds);
				}
				break;
			}
			case ArenaPhase.Rebound:
			{
				if (!_controller.Leaping)
				{
					BeginCooldown(AttackCooldownSeconds);
				}
				break;
			}
			case ArenaPhase.Dead:
				TickCorpseFreeze();
				break;
		}
	}

	/// <summary>扑击命中判定：任一身体节球与玩家胶囊（轴 0.35~1.35m，r0.35）相交（+冗余）。</summary>
	private bool SpiderTouchesPlayer()
	{
		Vector3 feet = _player.GlobalPosition;
		Vector3 a = feet + Vector3.Up * PlayerCapsuleBottom;
		Vector3 b = feet + Vector3.Up * PlayerCapsuleTop;
		foreach (BodyChunk chunk in _controller.Body.Chunks)
		{
			float reach = chunk.Radius + PlayerCapsuleRadius + PounceHitAssist;
			if (SegmentDistance(chunk.Pos, a, b) <= reach)
			{
				return true;
			}
		}
		return false;
	}

	private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
	{
		Vector3 ab = b - a;
		float len2 = ab.LengthSquared();
		float s = len2 > 1e-8f ? Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f) : 0f;
		return (p - (a + ab * s)).Length();
	}

	/// <summary>命中结算：玩家击退（外部冲量通道）+ 蜘蛛反向略朝上反弹（新弹道，落地才压冷却）
	/// + 计数/提示/镜头 kick。</summary>
	private void LandPounce()
	{
		_pounceCount++;
		Vector3 spider = _controller.Primary.Pos;
		Vector3 away = _player.GlobalPosition - spider;
		away.Y = 0f;
		if (away.LengthSquared() < 1e-6f)
		{
			away = _controller.LeapDirection;
			away.Y = 0f;
		}
		away = away.LengthSquared() > 1e-8f ? away.Normalized() : Vector3.Left;
		_player.AddImpulse(away * PounceKnockbackSpeed + Vector3.Up * PounceKnockbackUp);

		Vector3 rebound = (-away + Vector3.Up * ReboundUpBias);
		rebound = rebound.LengthSquared() > 1e-8f ? rebound.Normalized() : Vector3.Up;
		int reboundTicks = Mathf.Max(4, Mathf.RoundToInt(0.5f * TicksPerSecond));
		_controller.BeginLeap(rebound * (ReboundSpeedMps * (float)TickDt), reboundTicks, away); // 反弹仍面朝猎物
		_phase = ArenaPhase.Rebound;
		RequestShot($"pounce-{_leapSurface.ToString().ToLowerInvariant()}"); // 触及猎物那一刻的姿态（对准验收）
		_pouncedPromptUntilTick = _tick + (long)MathF.Ceiling(PouncedPromptSeconds * TicksPerSecond);
		ShowToast($"POUNCED x{_pounceCount}");
		AddKick();
		GD.Print($"[SPIDER-ARENA] pounce lands count={_pounceCount} surface={_leapSurface} " +
				 $"range={_leapRange:F1}m t={_tick}");
	}

	/// <summary>回 Stalk 并压攻击冷却（时间戳制——冷却不占相位，潜行/绕行照常）。</summary>
	private void BeginCooldown(float cooldownSeconds)
	{
		_attackReadyTick = _tick + Math.Max(1, (long)MathF.Ceiling(cooldownSeconds * TicksPerSecond));
		_phase = ArenaPhase.Stalk;
		_waypoint = null;
	}

	/// <summary>血量归零：内核昏迷（Conscious=false → 腿蜷缩、重力常开、倒地涌现）；终态。</summary>
	private void EnterDead()
	{
		_controller.MoveTarget = null;
		_controller.MoveDir = Vector3.Zero;
		_controller.RunSpeed = 0f;
		_controller.Conscious = false;
		_phase = ArenaPhase.Dead;
		_corpseStillStreak = 0;
		GD.Print($"[SPIDER-ARENA] dead hp=0 t={_tick}");
	}

	private void TickCorpseFreeze()
	{
		if (_corpseFrozen)
			return;
		bool still = true;
		foreach (BodyChunk chunk in _controller.Body.Chunks)
			still &= chunk.Vel.Length() < CorpseStillVelocity;
		foreach (SpiderLeg leg in _controller.Legs)
			still &= leg.Vel.Length() < CorpseStillVelocity;
		_corpseStillStreak = still ? _corpseStillStreak + 1 : 0;
		// 翻身（支撑法线滚到世界下方向）完成后才定格；万一始终未触地（翻身不启动）则按 4 倍静止期兜底。
		bool flipped = _controller.SupportNormal.Dot(Vector3.Down) > 0.98f;
		if (_corpseStillStreak >= CorpseStillTicks && (flipped || _corpseStillStreak >= CorpseStillTicks * 4))
		{
			_corpseFrozen = true;
			GD.Print($"[SPIDER-ARENA] corpse frozen t={_tick}");
		}
	}

	// ---- 手枪：排队 → 场景截程 → 部位裁决 → 冲量/掉血 ----

	private Vector3 MuzzlePosition()
	{
		Basis basis = _player.EyeBasis;
		return _player.EyePosition + basis.X * 0.12f - basis.Y * 0.09f - basis.Z * 0.25f;
	}

	private void TryFireGun()
	{
		if (_fatal || Input.MouseMode != Input.MouseModeEnum.Captured)
			return;
		_shotQueued = true;
	}

	private void ProcessQueuedShot()
	{
		if (!_shotQueued)
			return;
		_shotQueued = false;
		if (_tick < _nextShotAtTick)
			return;
		_nextShotAtTick = _tick + Math.Max(1, (long)MathF.Ceiling(GunCooldownSeconds * TicksPerSecond));
		FireGun(_player.EyePosition, _player.EyeForward);
	}

	/// <summary>三级判定：① 场景静态体射线截短射程（墙后打不到，排除玩家自身）；② 身体球 +
	/// 逐腿两段胶囊取最近 t；③ 命中处置。</summary>
	private void FireGun(Vector3 from, Vector3 direction)
	{
		_shotsFired++;
		float maxDistance = GunRange;
		var query = PhysicsRayQueryParameters3D.Create(from, from + direction * GunRange);
		query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
		Godot.Collections.Dictionary wall = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (wall.Count > 0)
			maxDistance = from.DistanceTo((Vector3)wall["position"]);

		bool hit = ResolveShot(from, direction, maxDistance, out float hitDistance, out string part);
		Vector3 tracerEnd = from + direction * (hit ? hitDistance : maxDistance);
		if (hit)
		{
			_hitMarkerTtl = HitMarkerSeconds;
			HandleHit(part, direction);
		}
		_tracerFrom = MuzzlePosition();
		_tracerTo = tracerEnd;
		_tracerTtl = TracerSeconds;
	}

	private bool ResolveShot(Vector3 from, Vector3 dir, float maxDistance,
		out float distance, out string part)
	{
		distance = float.PositiveInfinity;
		part = "";
		for (int i = 0; i < _controller.Body.Chunks.Count; i++)
		{
			BodyChunk chunk = _controller.Body.Chunks[i];
			if (RayHitMath.RayHitsSphere(from, dir, chunk.Pos, chunk.Radius + BodyAimAssist, out float t)
				&& t <= maxDistance && t < distance)
			{
				distance = t;
				part = i == 0 ? "HEAD" : "ABDOMEN";
			}
		}
		for (int i = 0; i < _controller.Legs.Count; i++)
		{
			SpiderLeg leg = _controller.Legs[i];
			float radius = leg.Radius * 0.9f + LegAimAssist;
			if (RayHitMath.RayHitsCapsule(from, dir, leg.RootPos, leg.KneePos, radius, out float tUpper)
				&& tUpper <= maxDistance && tUpper < distance)
			{
				distance = tUpper;
				part = $"LEG{i + 1}";
			}
			if (RayHitMath.RayHitsCapsule(from, dir, leg.KneePos, leg.Pos, radius, out float tLower)
				&& tLower <= maxDistance && tLower < distance)
			{
				distance = tLower;
				part = $"LEG{i + 1}";
			}
		}
		return !float.IsPositiveInfinity(distance);
	}

	/// <summary>命中处置：扣血 → 反向冲量（Launch：全腿松手 + 重力回归——墙/顶上掉下来）→
	/// 抢占蓄势/飞行回潜行 + 硬直 → 血量归零死亡。尸体只回弹 toast。</summary>
	private void HandleHit(string part, Vector3 gunDirection)
	{
		_shotsHit++;
		if (_phase == ArenaPhase.Dead)
		{
			ShowToast($"HIT: {part} (corpse)");
			return;
		}
		_hp = Math.Max(0, _hp - 1);
		SpiderSurfaceKind surface = CurrentSurface();
		bool onSurface = surface != SpiderSurfaceKind.Ground && Footed();
		Vector3 impulse = gunDirection.Normalized() * HitImpulseMps + Vector3.Up * HitImpulseUpMps;
		if (onSurface)
		{
			// 打进表面的分量反射为离面反弹，再进击落保持期：腿不抓、身体离面下落，落地才重新站稳。
			Vector3 n = _controller.SupportNormal;
			float into = -impulse.Dot(n);
			if (into > 0f)
				impulse += n * (into * (1f + KnockOffBounce));
			_controller.Launch(impulse * (float)TickDt, KnockOffHoldTicks);
		}
		else
		{
			_controller.Launch(impulse * (float)TickDt);
		}
		BeginHitProbe(surface, onSurface);

		if (_hp <= 0)
		{
			EnterDead();
			ShowToast($"HIT: {part} -> KILLED");
			GD.Print($"[SPIDER-ARENA] shot hit {part} kills t={_tick}");
			return;
		}
		if (_phase is ArenaPhase.Windup or ArenaPhase.Leap or ArenaPhase.Rebound)
		{
			BeginCooldown(MissCooldownSeconds);
		}
		_stunUntilTick = Math.Max(_stunUntilTick,
			_tick + Math.Max(1, (long)MathF.Ceiling(HitStunSeconds * TicksPerSecond)));
		string fall = onSurface ? " (knocked off)" : "";
		ShowToast($"HIT: {part} ({_hp}/{SpiderHp}){fall}");
		GD.Print($"[SPIDER-ARENA] shot hit {part} hp={_hp} surface={surface} t={_tick}");
	}

	private void BeginHitProbe(SpiderSurfaceKind surface, bool onSurface)
	{
		_hitProbeTick = _tick;
		_hitProbeSurface = surface;
		_hitProbeOnSurface = onSurface;
		_hitProbeReleased = false;
		_hitProbeRegripTick = -1;
		_hitProbePos = _controller.Primary.Pos;
		_hitProbeMinY = _hitProbePos.Y;
	}

	/// <summary>命中后 60 tick 结算：墙/顶命中要求真掉下来（下落 ≥0.8m，低位起点按落到地板放宽），
	/// 地面命中统计水平后退。</summary>
	private void TickHitProbe()
	{
		if (_hitProbeTick < 0)
			return;
		if (_phase == ArenaPhase.Dead)
		{
			_hitProbeTick = -1;
			return;
		}
		long age = _tick - _hitProbeTick;
		Vector3 p = _controller.Primary.Pos;
		_hitProbeMinY = MathF.Min(_hitProbeMinY, p.Y);
		_hitProbeReleased |= _controller.LegsGripping == 0;
		if (_hitProbeRegripTick < 0 && _hitProbeReleased && age > 1 && Footed())
			_hitProbeRegripTick = (int)age;
		if (age < HitProbeTicks)
			return;

		float drop = _hitProbePos.Y - _hitProbeMinY;
		Vector3 flat = p - _hitProbePos;
		flat.Y = 0f;
		float recoil = flat.Length();
		SpiderSurfaceKind now = CurrentSurface();
		// 「真掉下来」= 下落 ≥0.8m；贴近地面的低位墙面命中（起点不足 1.15m 高）按「落到地板」放宽。
		float fallGate = MathF.Min(0.8f, MathF.Max(0.2f, _hitProbePos.Y - 0.35f));
		bool fell = _hitProbeOnSurface && drop >= fallGate;
		if (_hitProbeOnSurface)
		{
			_knockOffs++;
			_knockOffsFell += fell ? 1 : 0;
		}
		else
		{
			_groundHits++;
			_groundRecoilSum += recoil;
		}
		GD.Print($"[SPIDER-ARENA-HIT] surface={_hitProbeSurface} knockOff={_hitProbeOnSurface} " +
				 $"drop={drop:F2}m recoil={recoil:F2}m released={_hitProbeReleased} " +
				 $"regrip@{_hitProbeRegripTick} now={now} footed={Footed()} fell={fell} t={_tick}");
		_hitProbeTick = -1;
	}

	private void ShowToast(string text)
	{
		_toastText = text;
		_toastTtl = ToastSeconds;
	}

	private void AddKick() => _kick = MathF.Min(_kick + 1f, KickMax);

	// ---- 无头自检 ----

	private void BotTick()
	{
		if (_bot == BotMode.None)
			return;
		if (_bot == BotMode.Strafe)
		{
			// 突进-停顿式绕圈（走 1s 停 1.5s，方向缓慢旋转）：玩家满速 5m/s 比任何预设的巡航
			// 都快，持续跑圈谁也追不上；停顿窗才给攻击门开的机会——更像真人「跑两步、站定开枪」。
			float phase = _tick * 0.015f;
			bool moving = _tick % 100 < 40;
			_player.ScriptedInput = moving
				? new Vector2(Mathf.Cos(phase), Mathf.Sin(phase))
				: Vector2.Zero;
		}
		if (_tick % 200 == 0)
		{
			Vector3 chest = PlayerChest();
			float distance = (chest - _controller.Primary.Pos).Length();
			bool los = HasLineOfSight(chest);
			bool planned = SpiderLeapPlanner.TryPlan(BuildLeapRequest(chest), _terrain, out _);
			GD.Print($"[SPIDER-ARENA-BOT] t={_tick} phase={_phase} surface={CurrentSurface()} " +
					 $"footed={Footed()} grips={_controller.LegsGripping} dist={distance:F2}m " +
					 $"range={AttackRangeFor(_controller.SupportNormal):F1}m los={los} plan={planned} " +
					 $"cooldown={MathF.Max(0f, (_attackReadyTick - _tick) / (float)TicksPerSecond):F1}s " +
					 $"waypoint={(_waypoint is { } wp ? wp.Kind.ToString() : "none")} " +
					 $"pos=({_controller.Primary.Pos.X:F1},{_controller.Primary.Pos.Y:F1},{_controller.Primary.Pos.Z:F1}) " +
					 $"player=({_player.GlobalPosition.X:F1},{_player.GlobalPosition.Z:F1})");
		}
		bool surfaceGate = _botShootOn is not { } wanted
			|| (CurrentSurface() == wanted && Footed() && _tick >= _stunUntilTick && _hitProbeTick < 0);
		if (_botShootEvery > 0 && _tick % _botShootEvery == 0 && _phase != ArenaPhase.Dead && surfaceGate)
		{
			Vector3 eye = _player.EyePosition;
			Vector3 aim = (_controller.Primary.Pos - eye).Normalized();
			if (_tick >= _nextShotAtTick)
			{
				_nextShotAtTick = _tick + Math.Max(1, (long)MathF.Ceiling(GunCooldownSeconds * TicksPerSecond));
				FireGun(eye, aim);
			}
		}
	}

	/// <summary>事件截图触发（tick 侧只排队，_Process 侧两帧完成）。</summary>
	private void QueueEventScreenshots()
	{
		if (_screenshotDir is null || _pendingShotName is not null)
			return;
		if (_phase == ArenaPhase.Leap && _controller.Leaping
			&& _controller.LeapTicks == Math.Max(1, _controller.LeapFlightTicks / 2))
		{
			RequestShot($"leap-{_leapSurface.ToString().ToLowerInvariant()}");
		}
		else if (_hitProbeTick >= 0 && _hitProbeOnSurface && _tick - _hitProbeTick == 6)
		{
			RequestShot($"knockoff-{_hitProbeSurface.ToString().ToLowerInvariant()}");
		}
		else if (_phase == ArenaPhase.Stalk && Footed())
		{
			SpiderSurfaceKind kind = CurrentSurface();
			if (kind != SpiderSurfaceKind.Ground)
				RequestShot($"stance-{kind.ToString().ToLowerInvariant()}");
		}
		else if (_phase == ArenaPhase.Dead && _corpseFrozen)
		{
			RequestShot("corpse");
		}
	}

	private void RequestShot(string name)
	{
		if (_screenshotDir is null || _pendingShotName is not null || _screenshotsTaken.Contains(name))
			return;
		_screenshotsTaken.Add(name);
		_pendingShotName = name;
		_pendingShotStage = 0;
	}

	/// <summary>截图两帧序列：0 = 把引导相机搬到蜘蛛斜后上方并设为当前；1 = 取上一帧渲染保存并还原。</summary>
	private void ProcessPendingScreenshot()
	{
		if (_pendingShotName is null || _screenshotDir is null)
			return;
		if (_pendingShotStage == 0)
		{
			_hud.Visible = false; // 截图不要 HUD 遮住主体
			Vector3 spider = _controller.Primary.Pos.Lerp(_controller.Rear.Pos, 0.5f);
			Vector3 fwd = _controller.Forward;
			Vector3 side = fwd.Cross(Vector3.Up);
			side = side.LengthSquared() > 1e-6f ? side.Normalized() : Vector3.Right;
			Vector3 offset = -fwd * 1.6f + side * 1.4f + Vector3.Up * 0.9f;
			if (_phase == ArenaPhase.Dead)
				offset = -fwd * 0.9f + side * 1.1f + Vector3.Up * 1.1f; // 尸体：侧上方俯拍看翻身
			else if (_pendingShotName.StartsWith("pounce-", StringComparison.Ordinal))
				offset = side * 1.7f + Vector3.Up * 0.6f - fwd * 0.3f; // 触及瞬间：纯侧拍（身后常是起跳墙）
			else if (_controller.SupportNormal.Y < -0.5f)
				offset = -fwd * 1.2f + side * 1.8f - Vector3.Up * 0.7f; // 顶面/俯冲：侧下方仰拍
			Vector3 camPos = spider + offset;
			camPos.Y = Mathf.Clamp(camPos.Y, 0.25f, DaddyLongLegsMazeBuilder.RoomHeight - 0.25f);
			// 相机别进墙/柱：蜘蛛→机位有地形就退到命中点前
			_terrain.Bind(GetWorld3D().DirectSpaceState);
			if (_terrain.Raycast(spider, camPos, out TerrainHit blocker))
				camPos = blocker.Point + (spider - camPos).Normalized() * 0.35f;
			_bootCamera.GlobalPosition = camPos;
			_bootCamera.LookAt(spider, Vector3.Up);
			_bootCamera.Current = true;
			_pendingShotStage = 1;
			return;
		}
		Image img = GetViewport().GetTexture().GetImage();
		string path = System.IO.Path.Combine(_screenshotDir, $"{_pendingShotName}.png");
		Error err = img.SavePng(path);
		GD.Print($"[SPIDER-ARENA] screenshot {(err == Error.Ok ? "saved" : $"FAILED ({err})")}: {path} t={_tick}");
		_bootCamera.Current = false;
		_hud.Visible = true;
		_player.SetActive(true);
		_pendingShotName = null;
	}

	private void BotFinish()
	{
		if (_bot == BotMode.None || _botTicks <= 0 || _tick < _botTicks)
			return;
		bool shootOk = _botShootEvery <= 0
			|| _botShootOn switch
			{
				SpiderSurfaceKind.Ground => _groundHits >= 1,
				SpiderSurfaceKind.Wall or SpiderSurfaceKind.Ceiling => _knockOffs >= 1 && _knockOffsFell == _knockOffs,
				_ => _phase == ArenaPhase.Dead,
			};
		bool pass = _leapAttempts >= 1 && (_surfaceTicks[1] + _surfaceTicks[2]) > 0
			&& (_botShootEvery > 0 ? shootOk : _pounceCount >= 1);
		float recoilAvg = _groundHits > 0 ? _groundRecoilSum / _groundHits : 0f;
		GD.Print($"[SPIDER-ARENA-RESULT] {(pass ? "PASS" : "FAIL")} preset={_breed.Name} bot={_bot} " +
				 $"ticks={_tick} leaps={_leapAttempts} pounces={_pounceCount} misses={_leapMisses} " +
				 $"waypoints={_waypointsPlanned} surfaceTicks=ground:{_surfaceTicks[0]}/wall:{_surfaceTicks[1]}" +
				 $"/ceiling:{_surfaceTicks[2]} shots={_shotsFired}/{_shotsHit} hp={_hp} phase={_phase} " +
				 $"frozen={_corpseFrozen} groundHits={_groundHits} recoilAvg={recoilAvg:F2}m " +
				 $"knockOffs={_knockOffsFell}/{_knockOffs}");
		_fatal = true;
		GetTree().Quit(pass ? 0 : 1);
	}

	// ---- R 重开 / 预设切换 ----

	private void ResetRun()
	{
		SpawnSpider();
		_player.Place(_arena.PlayerSpawn, _arena.MonsterSpawn);
		_player.SetActive(true);
		_lastPlayerPos = _player.GlobalPosition;
		_stalk.Reseed(StalkSeed);
		_phase = ArenaPhase.Stalk;
		_windupStartTick = 0;
		_attackReadyTick = 0;
		_stunUntilTick = 0;
		_pounceCount = 0;
		_leapAttempts = 0;
		_leapMisses = 0;
		_pouncedPromptUntilTick = -1;
		_shotQueued = false;
		_nextShotAtTick = 0;
		_shotsFired = 0;
		_shotsHit = 0;
		_hitProbeTick = -1;
		_groundHits = 0;
		_groundRecoilSum = 0f;
		_knockOffs = 0;
		_knockOffsFell = 0;
		_tracerTtl = 0f;
		_hitMarkerTtl = 0f;
		_toastTtl = 0f;
		_kick = 0f;
		_kickTime = 0f;
		_player.SetCameraShake(Vector3.Zero, Vector3.Zero);
		_kickApplied = false;
		Array.Clear(_surfaceTicks, 0, _surfaceTicks.Length);
		GD.Print($"[SPIDER-ARENA] reset preset={_breed.Name} t={_tick}");
	}

	private void SelectPreset(string name)
	{
		try
		{
			_breed = SpiderFactory.ByName(name);
		}
		catch (ArgumentException)
		{
			return;
		}
		ResetRun();
	}

	// ---- 渲染帧 ----

	public override void _Process(double delta)
	{
		if (_fatal)
			return;

		float physicsDelta = 1f / Math.Max(1, Engine.PhysicsTicksPerSecond);
		float interpolation = Mathf.Clamp(
			(float)(_tickAccumulator / TickDt
				+ Engine.GetPhysicsInterpolationFraction() * physicsDelta / TickDt),
			0f, 1f);

		UpdateCameraKick((float)delta);
		_formal?.Draw(interpolation, (float)delta);
		_debugRenderer?.Draw(interpolation);
		DrawTracer((float)delta);
		UpdateHud((float)delta);
		ProcessPendingScreenshot();
	}

	private void UpdateCameraKick(float delta)
	{
		_kick *= MathF.Exp(-delta / KickDecaySeconds);
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
		_hud.SetCrosshair(Input.MouseMode == Input.MouseModeEnum.Captured, _hitMarkerTtl > 0f);

		float cooldown = _phase == ArenaPhase.Dead
			? 0f
			: MathF.Max(0f, (_attackReadyTick - _tick) / (float)TicksPerSecond);
		float distance = (PlayerChest() - _controller.Primary.Pos).Length();
		float range = AttackRangeFor(_controller.SupportNormal);
		string frozen = _corpseFrozen ? " frozen" : "";
		string leap = _controller.Leaping ? $" leap={_controller.LeapTicks}/{_controller.LeapFlightTicks}" : "";
		_hud.SetStatus(
			$"SPIDER ARENA — {_breed.Name} phase={_phase} hp={_hp}/{SpiderHp} " +
			$"surface={CurrentSurface()} grips={_controller.LegsGripping} " +
			$"dist={distance:F1}m range={range:F1}m cooldown={cooldown:F1}s " +
			$"pounces={_pounceCount} leaps={_leapAttempts}{leap}{frozen}\n" +
			$"waypoint={(_waypoint is { } wp ? $"{wp.Kind} score={wp.Score:F2}" : "none")}");

		if (_phase == ArenaPhase.Dead)
			_hud.SetPrompt("KILLED", $"{_pounceCount} pounces taken — R to restart");
		else if (_tick < _pouncedPromptUntilTick)
			_hud.SetPrompt($"POUNCED x{_pounceCount}");
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
			case Key.Key1:
				SelectPreset("spider-small");
				break;
			case Key.Key2:
				SelectPreset("spider-large");
				break;
			case Key.Key3:
				SelectPreset("spider-lean");
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
