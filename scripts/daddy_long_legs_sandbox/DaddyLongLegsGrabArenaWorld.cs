using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DaddyLongLegs;
using ProcAnim.Core.Terrain;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// 抓取竞技场（探索场景，不进矩阵）：封闭矩形大房间里，Daddy 持续追逐第一人称玩家，
/// 空闲触手在几何可及时提前伸抓，接触锁定后进入「束缚 → 连打挣脱拔河 → 成功失能 /
/// 失败拖入吞食 → R 重开」的完整捕食闭环。
///
/// 玩法全部在宿主层；内核只走既有「外部够取/拉扯」纯值通道
/// （<c>TryAssignExternalTarget</c> / <c>TargetEffects</c> / <c>StunTentacle</c>），
/// 契约边界见 <c>DaddyLongLegsTargetContracts</c> 与 docs/daddy_long_legs_controller.md §7.2。
/// 本场景只有第一人称：无自由飞相机、无拖拽、无决定论路线机器。
/// </summary>
public partial class DaddyLongLegsGrabArenaWorld : Node3D
{
	private const double TickDt = 0.025;
	private const int TicksPerSecond = 40;

	/// <summary>宿主目标的稳定 ID（非零即可；本场景只有玩家一个目标）。</summary>
	private const ulong PlayerTargetId = 0x9_1A_7E_12UL;

	/// <summary>
	/// 玩家胶囊的镜像常量（真相源 <see cref="DaddyLongLegsSandboxPlayer"/> 的胶囊
	/// r0.35 / 中心抬 0.85）：触手要「摸到」的目标球心与半径。改玩家规格需同步。
	/// </summary>
	private const float PlayerChunkCenterY = 0.85f;
	private const float PlayerChunkRadius = 0.35f;

	/// <summary>挣脱成功提示的停留时长与吞没渐暗的时长（tick）。</summary>
	private const int PromptFlashTicks = 48;
	private const int DevourFadeTicks = 32;

	/// <summary>挣脱猛拽脉冲形状：首 tick 速度 = 拽近量 × (1−衰减率)，逐 tick 几何衰减放完；
	/// 无距离下限——拽到玩家球与任意 body chunk 相交即提前吞没（不等倒计时）；
	/// 竖直分量按 <see cref="TugVerticalScale"/> 压扁，拽是横向拖拽、不把人吊起来。</summary>
	private const float TugDecayRate = 0.82f;
	private const float TugVerticalScale = 0.5f;

	/// <summary>挣脱镜头震动包络：快起（挣扎一激活就抖起来）慢收（停止时自然收敛不硬切，
	/// 也是「被拖走/吞没不震」语义下残余震动的消失速度）；猛拽冲量单独按更快节奏衰减。</summary>
	private const float ShakeAttackTau = 0.06f;
	private const float ShakeReleaseTau = 0.12f;
	private const float ShakeKickDecayTau = 0.25f;
	/// <summary>猛拽冲量累积上限：密集猛拽 + 高 TugKick 调参下冲量会无界堆积，把震动
	/// 幅度放大到镜头贴俯仰夹角时翻过竖直极点（相机侧另有硬夹兜底，这里限源头）。</summary>
	private const float ShakeKickMax = 3f;

	// 痛缩垂吊：吊点以向下为主、带少量外偏免得盘进身体；吊点不低于地板上方此高度
	// （本场景房间地板恒为 y=0，探索场景直接用常量，不做地形查询）。
	private const float FlinchHangSideRatio = 0.35f;
	private const float FlinchHangFloorClearance = 0.4f;

	/// <summary>断落段接手牵引的宿主目标 ID 基（+触手编号；与玩家 ID 域错开）。</summary>
	private const ulong PieceTargetIdBase = 0x9_2B_00_00UL;

	/// <summary>枪线显示时长（秒，渲染侧）与命中闪标时长。</summary>
	private const float TracerSeconds = 0.06f;
	private const float HitMarkerSeconds = 0.18f;

	// ---- Inspector 导出（本场景纯交互，无命令行参数）----

	[ExportGroup("Arena / Creature")]
	[Export(PropertyHint.Enum, "brother,daddy,terror")]
	public string DefaultPreset { get; set; } = "terror";

	[Export]
	public long DefaultSeed { get; set; } = 5;

	[Export]
	public bool FormalRender { get; set; } = true;

	[Export(PropertyHint.Range, "40,1000,1")]
	public int HostPhysicsTps { get; set; } = 40;

	[Export(PropertyHint.Range, "1,100,0.5")]
	public float GravityMps2 { get; set; } = 36f;

	/// <summary>
	/// 透传内核 <c>GravityCancellationGain</c>（支撑重力回补增益）。1.2=原作值：移动中
	/// 满支撑净向上 0.2g，矮天花板房间里追逐会慢慢贴顶；1.0=移动中恰好中性、不上浮，
	/// 但整体身姿也会变矮。内核校验域 [0,2]。
	/// </summary>
	[Export(PropertyHint.Range, "0,2,0.01")]
	public float GravityCancellationGain { get; set; } = 1.2f;

	[ExportGroup("Arena / Room")]
	[Export(PropertyHint.Range, "12,80,1")]
	public float ArenaWidth { get; set; } = 30f;

	[Export(PropertyHint.Range, "12,80,1")]
	public float ArenaDepth { get; set; } = 24f;

	/// <summary>两端出生点离端墙的距离；怪物与玩家分列 ±X 两端。</summary>
	[Export(PropertyHint.Range, "2,10,0.5")]
	public float SpawnEndInset { get; set; } = 4f;

	/// <summary>dir=逐 tick 喂方向（迷宫里更稳的喂法）；target=喂世界点。M 键运行时切换。</summary>
	[ExportGroup("Arena / Chase")]
	[Export(PropertyHint.Enum, "dir,target")]
	public string ChaseDrive { get; set; } = "dir";

	/// <summary>追逐目标点 = 玩家位置 + 此抬升。玩家矮，直接用脚下位置会压着地面追。</summary>
	[Export(PropertyHint.Range, "0,3.2,0.05")]
	public float ChaseHeightOffset { get; set; } = 1.6f;

	[Export(PropertyHint.Range, "0.05,8,0.05")]
	public float ChaseArriveRadius { get; set; } = 1.1f;

	/// <summary>触手锚点到玩家距离 ≤ 触手冻结长度 × 此比例即发起伸抓（逐触手，非全局定距）。</summary>
	[ExportGroup("Arena / Grab")]
	[Export(PropertyHint.Range, "0.3,1.5,0.05")]
	public float GrabStartReachRatio { get; set; } = 0.95f;

	/// <summary>超出触手长度 × 此比例放弃本次伸抓（≙ 原作 hunt 放弃距离 idealLength×1.5）。</summary>
	[Export(PropertyHint.Range, "1,3,0.05")]
	public float GrabAbortReachRatio { get; set; } = 1.5f;

	/// <summary>一次伸抓被放弃后，隔多少 tick 才再挑触手重试。</summary>
	[Export(PropertyHint.Range, "1,400,1")]
	public int GrabRetryTicks { get; set; } = 30;

	[ExportGroup("Arena / Struggle")]
	[Export(PropertyHint.Range, "1,200,1")]
	public int MashTargetPresses { get; set; } = 18;

	[Export(PropertyHint.Range, "1,60,0.5")]
	public float StruggleTimeLimitSeconds { get; set; } = 6f;

	/// <summary>挣脱期怪物每次猛拽把玩家拽近的距离（米）；0 = 关闭猛拽（完全钉住的旧行为）。</summary>
	[Export(PropertyHint.Range, "0,3,0.05")]
	public float StruggleTugDistance { get; set; } = 0.45f;

	/// <summary>猛拽平均间隔（秒）；实际每次带 ±25% 确定性抖动，避免机械节拍感。</summary>
	[Export(PropertyHint.Range, "0.3,10,0.1")]
	public float StruggleTugIntervalSeconds { get; set; } = 1.4f;

	/// <summary>挣脱成功后该触手的失能总时长（痛缩收回 + 蜷缩保持，结束后自动归队）。</summary>
	[Export(PropertyHint.Range, "0.5,30,0.5")]
	public float EscapeTentacleStunSeconds { get; set; } = 4f;

	/// <summary>痛缩收回耗时：触手从抓点抽回身体边所用秒数（失能总时长的前段，越短越像被烫到）。</summary>
	[Export(PropertyHint.Range, "0.1,2,0.05")]
	public float FlinchRetractSeconds { get; set; } = 0.4f;

	/// <summary>垂吊点离锚点的下垂距离（该触手冻结长度的比例）：越小吊得越贴身。</summary>
	[Export(PropertyHint.Range, "0.05,0.6,0.01")]
	public float FlinchTuckLengthRatio { get; set; } = 0.2f;

	/// <summary>挣脱成功后全局再抓宽限：给玩家拉开距离的时间。</summary>
	[Export(PropertyHint.Range, "0,30,0.25")]
	public float RegrabGraceSeconds { get; set; } = 1.5f;

	/// <summary>镜头接管的阻尼时间常数（≈ 该秒数内基本完成转向）。</summary>
	[Export(PropertyHint.Range, "0.1,3,0.05")]
	public float CameraTakeoverSeconds { get; set; } = 0.5f;

	/// <summary>视线与怪物身体夹角小于此角度（度）即视为「已对准」，挣脱流程与倒计时从此刻开始。</summary>
	[Export(PropertyHint.Range, "1,45,0.5")]
	public float AlignStartDegrees { get; set; } = 6f;

	/// <summary>挣脱连打期的镜头震动旋转峰值（度；0 = 关闭震动）。只在挣扎流程激活后生效——
	/// 刚被抓的扭头对准与挣脱失败被拖走都不震。</summary>
	[Export(PropertyHint.Range, "0,6,0.1")]
	public float StruggleShakeDegrees { get; set; } = 1.5f;

	/// <summary>镜头震动的位移峰值（米，相机局部系横/竖向）。</summary>
	[Export(PropertyHint.Range, "0,0.2,0.005")]
	public float StruggleShakeOffsetMeters { get; set; } = 0.03f;

	/// <summary>镜头震动基频（Hz）：多条不可通约正弦叠成无规律抖动，这是最快一条的频率。</summary>
	[Export(PropertyHint.Range, "2,30,0.5")]
	public float StruggleShakeFrequencyHz { get; set; } = 11f;

	/// <summary>每次猛拽瞬间叠加的震动冲量倍率（快速衰减；0 = 猛拽不加震）。</summary>
	[Export(PropertyHint.Range, "0,6,0.25")]
	public float StruggleShakeTugKick { get; set; } = 2.2f;

	/// <summary>玩家视点进入身体质心此半径内视为「已完全拖进身体」。</summary>
	[ExportGroup("Arena / Devour")]
	[Export(PropertyHint.Range, "0.1,3,0.05")]
	public float EatRadius { get; set; } = 0.45f;

	/// <summary>拖拽收敛下限（米/秒）：内核拉力衰减到再小也至少按此速度收线。</summary>
	[Export(PropertyHint.Range, "0.1,8,0.1")]
	public float DragMinSpeed { get; set; } = 1.2f;

	/// <summary>拖拽速度上限（米/秒）：内核拉力逐 tick 累积，远距离抓取会一路加速，
	/// 这里决定「被收线」的最终观感快慢。</summary>
	[Export(PropertyHint.Range, "1,20,0.5")]
	public float DragMaxSpeed { get; set; } = 6f;

	/// <summary>吞没完成后到出现重开提示的停顿。</summary>
	[Export(PropertyHint.Range, "0,10,0.1")]
	public float EatenRestartDelaySeconds { get; set; } = 1.2f;

	/// <summary>命中判定的瞄准冗余（米）：加在触手段半径上。触手视觉极细（管径 0.05m），
	/// 冗余给足才好打；等价于「射线带半径」的胶囊扫掠。</summary>
	[ExportGroup("Arena / Gun")]
	[Export(PropertyHint.Range, "0,0.6,0.01")]
	public float GunAimAssistRadius { get; set; } = 0.22f;

	[Export(PropertyHint.Range, "5,200,1")]
	public float GunRange { get; set; } = 60f;

	[Export(PropertyHint.Range, "0.05,2,0.05")]
	public float GunCooldownSeconds { get; set; } = 0.25f;

	/// <summary>true=命中完整触手时真的从命中段打断；false=退回普通失能（痛缩），不断开。</summary>
	[Export]
	public bool SeverOnHit { get; set; } = true;

	/// <summary>断手时残肢至少保留的段数（太靠根的命中向外夹到这里）。</summary>
	[Export(PropertyHint.Range, "1,8,1")]
	public int SeverMinStumpSegments { get; set; } = 3;

	/// <summary>断落段至少的段数（太靠梢的命中向内夹到这里；总段数不足两者之和则只失能不断）。</summary>
	[Export(PropertyHint.Range, "1,8,1")]
	public int SeverMinFallSegments { get; set; } = 2;

	/// <summary>存在可接的断手时怪物优先走向断手（玩家靠太近仍会被其它触手抓）。
	/// false=断了不接：残肢失能恢复后就以短触手继续生活。</summary>
	[ExportGroup("Arena / Reattach")]
	[Export]
	public bool ReattachEnabled { get; set; } = true;

	/// <summary>残肢锚点到断落段断口的距离小于此值即开始牵引（无需精确站位）。</summary>
	[Export(PropertyHint.Range, "1,12,0.25")]
	public float ReattachStartRadius { get; set; } = 3.2f;

	/// <summary>断口与残肢尖端相触判定半径：小于此距离即接合复原。</summary>
	[Export(PropertyHint.Range, "0.05,1,0.05")]
	public float ReattachConnectRadius { get; set; } = 0.3f;

	/// <summary>单次牵引超时（秒）：拽了这么久还接不上就放弃，断落段跌回地面、冷却后重试。</summary>
	[Export(PropertyHint.Range, "1,30,0.5")]
	public float ReattachTimeoutSeconds { get; set; } = 6f;

	/// <summary>牵引失败/放弃后的重试冷却（秒）；牵引中被枪打断则还要叠加等残肢痛缩结束。</summary>
	[Export(PropertyHint.Range, "0,30,0.5")]
	public float ReattachRetryCooldownSeconds { get; set; } = 2f;

	// ---- 运行态 ----

	private enum ArenaPhase
	{
		Chase,
		Bound,
		Dragging,
		Eaten,
	}

	private readonly RaycastTerrainQuery _raycast = new();
	private RayDebugDraw _terrain = null!;
	private DaddyLongLegsGrabArenaBuilder _arena = null!;
	private DaddyLongLegsParams _preset = null!;
	private ulong _seed;
	private DaddyLongLegsLocomotionController _controller = null!;
	private DaddyLongLegsRenderer _renderer = null!;
	private ProcAnimLab.Render.IFormalRenderer? _formalRenderer;
	private bool _formalView;
	private DaddyLongLegsSandboxPlayer _player = null!;
	private DaddyLongLegsGrabHud _hud = null!;
	private Camera3D _bootCamera = null!;

	private Vector3 _gravityPerTick;
	private double _tickAccumulator;
	private long _tick;
	private bool _fatal;

	private ArenaPhase _phase = ArenaPhase.Chase;
	private bool _chaseDriveDir = true;

	// 抓取
	private int _grabTentacle = -1;
	private long _grabRetryAtTick;
	private long _regrabGraceUntilTick;

	// 痛缩失能（挣脱成功/中弹后借外部目标通道把触手抽回身体蜷缩，替代内核 Stun 软瘫）。
	// 多条可并行：宽限后再抓再挣脱，会在上一条还没痊愈时叠加第二条。
	private sealed class FlinchState
	{
		public int Tentacle;
		public long StartTick;
		public long UntilTick;
		public Vector3 FromPoint;
		public Vector3 PrevTarget;
		public bool Engaged;  // 已成功占住 ExternalReach 通道（断手后的新实例要等 1-2 tick 才接得上）
		public bool Detached; // 内核（地形回溯等）收走了任务：不再喂目标，只保留「疼着不抓」到期
	}

	private readonly List<FlinchState> _flinches = new();

	// 手枪
	private bool _shotQueued;
	private long _nextShotAtTick;
	private ProcAnimLab.Render.TubeMeshBuilder? _tracer;
	private readonly List<ProcAnimLab.Render.TubeStation> _tracerStations = new();
	private Vector3 _tracerFrom;
	private Vector3 _tracerTo;
	private float _tracerTtl;
	private float _hitMarkerTtl;

	// 断落段与接手牵引（每条触手至多一条断落段；牵引可多条并行）。
	private sealed class ReattachState
	{
		public int Tentacle;
		public long TimeoutTick;
		public Vector3 PrevBreakEnd;
	}

	private readonly List<DaddyLongLegsSeveredPiece> _pieces = new();
	private readonly List<ReattachState> _tractions = new();
	private readonly Dictionary<int, long> _reattachRetryAtTick = new();

	// 束缚 / 挣脱
	private double _takeoverElapsed;
	private bool _struggleActive;
	private int _mashCount;
	private long _struggleDeadlineTick;
	private long _promptFlashUntilTick = -1;
	private Vector3 _tugVel;
	private long _nextTugAtTick;
	private int _tugSerial;

	// 挣脱镜头震动（纯渲染侧：正弦叠噪 × 包络，无 RNG，不进物理与哈希）
	private float _shakeEnvelope;
	private float _shakeKick;
	private float _shakeTime;
	private bool _shakeApplied;

	// 拖拽 / 吞没
	private Vector3 _captiveVel;
	private Vector3 _pendingVelocityDelta;
	private Vector3 _pendingPositionCorrection;
	private long _eatenAtTick;

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

		_terrain = new RayDebugDraw(_raycast);
		_terrain.Build(this);

		try
		{
			_arena = new DaddyLongLegsGrabArenaBuilder(
				Vector3.Zero, ArenaWidth, ArenaDepth, SpawnEndInset);
		}
		catch (InvalidOperationException error)
		{
			GD.PushError($"[DADDY-ARENA] {error.Message}");
			_fatal = true;
			GetTree().Quit(2);
			return;
		}
		_arena.Build(this);

		_renderer = new DaddyLongLegsRenderer();
		_formalView = FormalRender;
		_chaseDriveDir = ChaseDrive == "dir";
		SpawnDaddy();

		_player = new DaddyLongLegsSandboxPlayer { Name = "ArenaPlayer" };
		AddChild(_player);
		_player.Place(_arena.PlayerSpawn, _arena.MonsterSpawn);
		_player.SetActive(true);

		_hud = new DaddyLongLegsGrabHud();
		_hud.Build(this);

		_tracer = new ProcAnimLab.Render.TubeMeshBuilder();
		_tracer.Build(this, srgbVertexColors: true);

		float minLength = float.PositiveInfinity;
		float maxLength = 0f;
		foreach (DaddyTentacle tentacle in _controller.Tentacles)
		{
			minLength = MathF.Min(minLength, tentacle.Length);
			maxLength = MathF.Max(maxLength, tentacle.Length);
		}
		GD.Print($"[DADDY-ARENA] ready preset={_preset.StableId} seed={_seed} " +
				 $"tentacles={_controller.Tentacles.Count} " +
				 $"length={minLength:F2}..{maxLength:F2}m drive={DriveName()} " +
				 $"startDistance={_controller.BodyCenter.DistanceTo(PlayerEye()):F1}m");
	}

	private bool ValidateExports()
	{
		if (ResolvePreset(DefaultPreset) is not { } preset)
			return Fail($"DefaultPreset '{DefaultPreset}' is not brother/daddy/terror or a stable id");
		if (DefaultSeed < 0)
			return Fail($"DefaultSeed must be >= 0, got {DefaultSeed}");
		if (HostPhysicsTps is < 40 or > 1000)
			return Fail($"HostPhysicsTps must be in [40,1000], got {HostPhysicsTps}");
		if (!FinitePositive(GravityMps2))
			return Fail($"GravityMps2 must be finite and positive, got {GravityMps2}");
		if (!float.IsFinite(GravityCancellationGain) || GravityCancellationGain is < 0f or > 2f)
			return Fail($"GravityCancellationGain must be in [0,2] (core validation range), got {GravityCancellationGain}");
		if (ChaseDrive is not ("dir" or "target"))
			return Fail($"ChaseDrive must be 'dir' or 'target', got '{ChaseDrive}'");
		if (!float.IsFinite(ChaseHeightOffset) || ChaseHeightOffset < 0f)
			return Fail($"ChaseHeightOffset must be finite and >= 0, got {ChaseHeightOffset}");
		if (!FinitePositive(ChaseArriveRadius))
			return Fail($"ChaseArriveRadius must be finite and positive, got {ChaseArriveRadius}");
		if (!FinitePositive(GrabStartReachRatio))
			return Fail($"GrabStartReachRatio must be finite and positive, got {GrabStartReachRatio}");
		if (!float.IsFinite(GrabAbortReachRatio) || GrabAbortReachRatio <= GrabStartReachRatio)
			return Fail($"GrabAbortReachRatio ({GrabAbortReachRatio}) must exceed GrabStartReachRatio ({GrabStartReachRatio})");
		if (GrabRetryTicks < 1)
			return Fail($"GrabRetryTicks must be >= 1, got {GrabRetryTicks}");
		if (MashTargetPresses < 1)
			return Fail($"MashTargetPresses must be >= 1, got {MashTargetPresses}");
		if (!FinitePositive(StruggleTimeLimitSeconds))
			return Fail($"StruggleTimeLimitSeconds must be finite and positive, got {StruggleTimeLimitSeconds}");
		if (!float.IsFinite(StruggleTugDistance) || StruggleTugDistance < 0f)
			return Fail($"StruggleTugDistance must be finite and >= 0 (0 disables tugs), got {StruggleTugDistance}");
		if (!FinitePositive(StruggleTugIntervalSeconds))
			return Fail($"StruggleTugIntervalSeconds must be finite and positive, got {StruggleTugIntervalSeconds}");
		if (!FinitePositive(EscapeTentacleStunSeconds))
			return Fail($"EscapeTentacleStunSeconds must be finite and positive, got {EscapeTentacleStunSeconds}");
		if (!FinitePositive(FlinchRetractSeconds))
			return Fail($"FlinchRetractSeconds must be finite and positive, got {FlinchRetractSeconds}");
		if (!FinitePositive(FlinchTuckLengthRatio) || FlinchTuckLengthRatio > 1f)
			return Fail($"FlinchTuckLengthRatio must be in (0,1], got {FlinchTuckLengthRatio}");
		if (!float.IsFinite(RegrabGraceSeconds) || RegrabGraceSeconds < 0f)
			return Fail($"RegrabGraceSeconds must be finite and >= 0, got {RegrabGraceSeconds}");
		if (!FinitePositive(CameraTakeoverSeconds))
			return Fail($"CameraTakeoverSeconds must be finite and positive, got {CameraTakeoverSeconds}");
		if (!FinitePositive(AlignStartDegrees))
			return Fail($"AlignStartDegrees must be finite and positive, got {AlignStartDegrees}");
		if (!float.IsFinite(StruggleShakeDegrees) || StruggleShakeDegrees < 0f)
			return Fail($"StruggleShakeDegrees must be finite and >= 0 (0 disables shake), got {StruggleShakeDegrees}");
		if (!float.IsFinite(StruggleShakeOffsetMeters) || StruggleShakeOffsetMeters < 0f)
			return Fail($"StruggleShakeOffsetMeters must be finite and >= 0, got {StruggleShakeOffsetMeters}");
		if (!FinitePositive(StruggleShakeFrequencyHz))
			return Fail($"StruggleShakeFrequencyHz must be finite and positive, got {StruggleShakeFrequencyHz}");
		if (!float.IsFinite(StruggleShakeTugKick) || StruggleShakeTugKick < 0f)
			return Fail($"StruggleShakeTugKick must be finite and >= 0, got {StruggleShakeTugKick}");
		if (!FinitePositive(EatRadius))
			return Fail($"EatRadius must be finite and positive, got {EatRadius}");
		if (!FinitePositive(DragMinSpeed))
			return Fail($"DragMinSpeed must be finite and positive, got {DragMinSpeed}");
		if (!float.IsFinite(DragMaxSpeed) || DragMaxSpeed <= DragMinSpeed)
			return Fail($"DragMaxSpeed ({DragMaxSpeed}) must exceed DragMinSpeed ({DragMinSpeed})");
		if (!float.IsFinite(EatenRestartDelaySeconds) || EatenRestartDelaySeconds < 0f)
			return Fail($"EatenRestartDelaySeconds must be finite and >= 0, got {EatenRestartDelaySeconds}");
		if (!float.IsFinite(GunAimAssistRadius) || GunAimAssistRadius < 0f)
			return Fail($"GunAimAssistRadius must be finite and >= 0, got {GunAimAssistRadius}");
		if (!FinitePositive(GunRange))
			return Fail($"GunRange must be finite and positive, got {GunRange}");
		if (!FinitePositive(GunCooldownSeconds))
			return Fail($"GunCooldownSeconds must be finite and positive, got {GunCooldownSeconds}");
		if (SeverMinStumpSegments < 1)
			return Fail($"SeverMinStumpSegments must be >= 1, got {SeverMinStumpSegments}");
		if (SeverMinFallSegments < 1)
			return Fail($"SeverMinFallSegments must be >= 1, got {SeverMinFallSegments}");
		if (!FinitePositive(ReattachStartRadius))
			return Fail($"ReattachStartRadius must be finite and positive, got {ReattachStartRadius}");
		if (!FinitePositive(ReattachConnectRadius))
			return Fail($"ReattachConnectRadius must be finite and positive, got {ReattachConnectRadius}");
		if (!FinitePositive(ReattachTimeoutSeconds))
			return Fail($"ReattachTimeoutSeconds must be finite and positive, got {ReattachTimeoutSeconds}");
		if (!float.IsFinite(ReattachRetryCooldownSeconds) || ReattachRetryCooldownSeconds < 0f)
			return Fail($"ReattachRetryCooldownSeconds must be finite and >= 0, got {ReattachRetryCooldownSeconds}");

		preset.GravityCancellationGain = GravityCancellationGain;
		_preset = preset;
		_seed = (ulong)DefaultSeed;
		return true;

		static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;
		static bool Fail(string message)
		{
			GD.PushError($"[DADDY-ARENA] invalid scene configuration: {message}");
			return false;
		}
	}

	private static DaddyLongLegsParams? ResolvePreset(string name)
	{
		switch (name)
		{
			case "brother":
				return DaddyLongLegsFactory.Brother();
			case "daddy":
				return DaddyLongLegsFactory.Daddy();
			case "terror":
				return DaddyLongLegsFactory.Terror();
			default:
				return DaddyLongLegsFactory.TryByStableId(name, out DaddyLongLegsParams parameters)
					? parameters
					: null;
		}
	}

	/// <summary>（重）建控制器与两套渲染件。Build 自带 Clear，重开安全。</summary>
	private void SpawnDaddy()
	{
		_controller = DaddyLongLegsFactory.CreateController(_arena.MonsterSpawn, _preset, _seed);
		_renderer.Build(this, _controller);
		_formalRenderer?.Clear();
		_formalRenderer = new ProcAnimLab.Render.DaddyLongLegsFormalRenderer(_controller);
		_formalRenderer.Build(this);
		ApplyRenderView();
	}

	private void ApplyRenderView()
	{
		bool formalOn = _formalRenderer is not null && _formalView;
		_renderer.SetVisible(!formalOn);
		_formalRenderer?.SetVisible(formalOn);
	}

	// ---- 固定步长循环 ----

	public override void _PhysicsProcess(double delta)
	{
		if (_fatal)
			return;

		_raycast.Bind(GetWorld3D().DirectSpaceState);
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

	private void RunCoreTick()
	{
		_tick++;
		_terrain.BeginTick();
		ProcessQueuedShot();

		switch (_phase)
		{
			case ArenaPhase.Chase:
				DriveMonster();
				UpdateGrabAttempt();
				break;
			default:
				FeedIdleStance();
				break;
		}

		FlinchTick();
		// 牵引在痛缩之后：中弹中断先由痛缩占位，本 tick 牵引立刻观测到并放手。
		ReattachTick();
		PieceTick();
		FeedGrabSnapshot();
		_controller.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
		ApplyTargetEffects();
		UpdatePhaseAfterTick();
	}

	// ---- 追逐 / 取断手（仅 Chase 相位驱动移动；束缚流程内不许边抓人边走向断手）----

	private void DriveMonster()
	{
		// 优先接断手：存在可尝试的断落段就走向断口（玩家靠太近仍会被其它触手抓走）。
		Vector3 target = TrySelectFetchPiece(out DaddyLongLegsSeveredPiece? piece)
				&& piece is not null
			? piece.BreakEndPosition + Vector3.Up * ChaseHeightOffset
			: _player.GlobalPosition + Vector3.Up * ChaseHeightOffset;
		if (_chaseDriveDir)
		{
			// ≙ 迷宫 dir 喂法：3D 方向 + 按水平距离饱和的油门；到达判定同样只看水平。
			Vector3 delta = target - _controller.BodyCenter;
			Vector3 horizontal = new(delta.X, 0f, delta.Z);
			float horizontalDistance = horizontal.Length();
			_controller.MoveTarget = null;
			_controller.MoveDir = delta.LengthSquared() > 1e-10f
				? delta.Normalized()
				: Vector3.Zero;
			_controller.RunSpeed = Mathf.Clamp(
				horizontalDistance / Math.Max(1e-3f, ChaseArriveRadius * 2f), 0f, 1f);
			return;
		}
		_controller.MoveTargetArriveRadius = ChaseArriveRadius;
		_controller.MoveTarget = target;
		_controller.MoveDir = Vector3.Zero;
		_controller.RunSpeed = 1f;
	}

	/// <summary>挑最近的、可尝试接回（残肢不疼、冷却已过、未在牵引）的断落段作为行走目标。</summary>
	private bool TrySelectFetchPiece(out DaddyLongLegsSeveredPiece? selected)
	{
		selected = null;
		if (!ReattachEnabled)
			return false;
		float bestDistance = float.PositiveInfinity;
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
		{
			if (piece.State == DaddyLongLegsSeveredPiece.PieceState.Traction
				|| FindTraction(piece.TentacleIndex) is not null
				|| !CanTentacleAttemptReattach(piece.TentacleIndex))
			{
				continue;
			}
			float distance = _controller.BodyCenter.DistanceTo(piece.BreakEndPosition);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				selected = piece;
			}
		}
		return selected is not null;
	}

	/// <summary>束缚/拖拽/吞没期间不给任何移动意图：生物原地自持站立（不强行定住）。</summary>
	private void FeedIdleStance()
	{
		_controller.MoveTarget = null;
		_controller.MoveDir = Vector3.Zero;
		_controller.RunSpeed = 0f;
	}

	// ---- 抓取：发起 / 放弃 / 快照喂入 / 效果消费 ----

	/// <summary>玩家胶囊球心：触手要接触的目标（与追逐抬升无关）。</summary>
	private Vector3 PlayerChunkCenter() =>
		_player.GlobalPosition + Vector3.Up * PlayerChunkCenterY;

	private Vector3 PlayerEye() => _player.EyePosition;

	private void UpdateGrabAttempt()
	{
		Vector3 grabPoint = PlayerChunkCenter();
		if (_grabTentacle >= 0)
		{
			DaddyTentacle tentacle = _controller.Tentacles[_grabTentacle];
			if (tentacle.Task != DaddyTentacleTask.ExternalReach)
			{
				// 被地形恢复等内核路径清掉了任务：放弃本次，冷却后重挑。
				AbandonGrabAttempt("task lost");
				return;
			}
			float distance = tentacle.Anchor.Pos.DistanceTo(grabPoint);
			if (distance > tentacle.Length * GrabAbortReachRatio)
			{
				_controller.ClearExternalTarget(_grabTentacle);
				AbandonGrabAttempt($"out of reach ({distance:F1}m > {tentacle.Length * GrabAbortReachRatio:F1}m)");
			}
			return;
		}

		if (_tick < _grabRetryAtTick || _tick < _regrabGraceUntilTick)
			return;

		// 逐触手几何判定（不是全局固定距离）：锚点到玩家 ≤ 冻结长度 × 比例即可发起，
		// 多条可及时选剩余可伸展量（slack）最大的那条。
		int best = -1;
		float bestSlack = 0f;
		for (int i = 0; i < _controller.Tentacles.Count; i++)
		{
			DaddyTentacle tentacle = _controller.Tentacles[i];
			if (!tentacle.CanAcceptExternalTarget || IsFlinching(i))
				continue;
			float slack = tentacle.Length * GrabStartReachRatio
				- tentacle.Anchor.Pos.DistanceTo(grabPoint);
			if (slack > bestSlack)
			{
				best = i;
				bestSlack = slack;
			}
		}
		if (best < 0)
			return;
		if (_controller.TryAssignExternalTarget(best, MakeSnapshot(pullTowardBody: false)))
		{
			_grabTentacle = best;
			GD.Print($"[DADDY-ARENA] reach start tentacle={best} " +
					 $"length={_controller.Tentacles[best].Length:F2}m slack={bestSlack:F2}m t={_tick}");
		}
		else
		{
			_grabRetryAtTick = _tick + GrabRetryTicks;
		}
	}

	private void AbandonGrabAttempt(string reason)
	{
		GD.Print($"[DADDY-ARENA] reach abandoned tentacle={_grabTentacle} ({reason}) t={_tick}");
		_grabTentacle = -1;
		_grabRetryAtTick = _tick + GrabRetryTicks;
	}

	private DaddyLongLegsTargetSnapshot MakeSnapshot(bool pullTowardBody)
	{
		// Bound 期玩家仍有重力自驱（空中被抓要落回地面）且会被猛拽，速度照实喂（含在途
		// 脉冲的位移速率），尖端才贴得住。
		Vector3 velocityPerTick = _phase switch
		{
			ArenaPhase.Chase => _player.Velocity * (float)TickDt,
			ArenaPhase.Bound => _player.Velocity * (float)TickDt + _tugVel,
			ArenaPhase.Dragging => _captiveVel,
			_ => Vector3.Zero,
		};
		return new DaddyLongLegsTargetSnapshot(
			PlayerTargetId,
			PlayerChunkCenter(),
			velocityPerTick,
			PlayerChunkRadius,
			1f,
			pullTowardBody);
	}

	/// <summary>已有伸抓触手时逐 tick 刷新目标快照（同 StableId 才允许原地更新）。</summary>
	private void FeedGrabSnapshot()
	{
		if (_grabTentacle < 0)
			return;
		_controller.TryAssignExternalTarget(
			_grabTentacle, MakeSnapshot(pullTowardBody: _phase == ArenaPhase.Dragging));
	}

	private void ApplyTargetEffects()
	{
		foreach (DaddyLongLegsTargetEffect effect in _controller.TargetEffects)
		{
			if (effect.TargetId != PlayerTargetId || effect.TentacleIndex != _grabTentacle)
				continue;
			if (_phase == ArenaPhase.Chase && effect.Reached)
			{
				EnterBound();
				return;
			}
			if (_phase == ArenaPhase.Dragging)
			{
				_pendingVelocityDelta += effect.VelocityDelta;
				_pendingPositionCorrection += effect.PositionCorrection;
			}
		}
	}

	// ---- 相位推进（tick 侧）----

	private void UpdatePhaseAfterTick()
	{
		switch (_phase)
		{
			case ArenaPhase.Bound:
				if (_grabTentacle < 0
					|| _controller.Tentacles[_grabTentacle].Task != DaddyTentacleTask.ExternalReach)
				{
					// 内核侧意外失手（理论上只有地形恢复路径）：按挣脱处理，防软锁。
					EscapeNow(byMash: false);
					return;
				}
				if (_struggleActive)
				{
					StruggleTugTick();
					if (_phase != ArenaPhase.Bound)
						break; // 拽进身体提前吞没，别再触发倒计时判定
					if (_tick >= _struggleDeadlineTick)
						BeginDragging();
				}
				break;
			case ArenaPhase.Dragging:
				DragTick();
				break;
			case ArenaPhase.Eaten:
				break;
		}
	}

	private void EnterBound()
	{
		_phase = ArenaPhase.Bound;
		_struggleActive = false;
		_mashCount = 0;
		_takeoverElapsed = 0.0;
		_tugVel = Vector3.Zero;
		_nextTugAtTick = long.MaxValue; // 对准前不猛拽；BeginStruggle 才排期
		// 快速再抓（RegrabGraceSeconds 调小）时上一轮挣扎的残余震动会漏进本轮扭头窗口，
		// 违反「扭头对准不震」——此刻残余幅度已极小，硬清不可见。
		_shakeEnvelope = 0f;
		_shakeKick = 0f;
		_player.InputLocked = true;
		_player.Velocity = Vector3.Zero;
		FeedIdleStance();
		GD.Print($"[DADDY-ARENA] snared tentacle={_grabTentacle} t={_tick} " +
				 $"dist={_controller.BodyCenter.DistanceTo(PlayerEye()):F1}m");
	}

	private void BeginStruggle()
	{
		_struggleActive = true;
		_struggleDeadlineTick = _tick
			+ (long)MathF.Ceiling(StruggleTimeLimitSeconds * TicksPerSecond);
		_tugSerial = 0;
		ScheduleNextTug();
		GD.Print($"[DADDY-ARENA] struggle start t={_tick} " +
				 $"target={MashTargetPresses} presses in {StruggleTimeLimitSeconds:F1}s");
	}

	/// <summary>
	/// 挣脱期间歇猛拽：到期发起一次朝身体的急拉脉冲（首 tick 最猛、几何衰减放完
	/// <see cref="StruggleTugDistance"/>），玩家不再钉在原地。位置直写与玩家自驱重力
	/// 共存（同拖拽阶段先例）。无距离下限：太近照常拽，一旦玩家球与任意 body chunk
	/// 相交即视为已被拽进身体，跳过剩余倒计时直接吞没。
	/// </summary>
	private void StruggleTugTick()
	{
		if (_tugVel.LengthSquared() > 1e-8f)
		{
			_player.GlobalPosition += _tugVel;
			_tugVel *= TugDecayRate;
			if (_tugVel.LengthSquared() <= 1e-8f)
				_tugVel = Vector3.Zero;
		}
		if (TouchesBody())
		{
			GD.Print($"[DADDY-ARENA] tugged into body — early devour t={_tick}");
			EnterEaten();
			return;
		}
		if (StruggleTugDistance <= 0f || _tick < _nextTugAtTick)
			return;
		ScheduleNextTug();
		Vector3 toBody = _controller.BodyCenter - PlayerChunkCenter();
		float distance = toBody.Length();
		if (distance <= 1e-3f)
			return;
		Vector3 direction = toBody / distance;
		direction.Y *= TugVerticalScale;
		direction = direction.Normalized();
		float reach = Mathf.Min(StruggleTugDistance, distance);
		_tugVel = direction * (reach * (1f - TugDecayRate));
		_shakeKick = MathF.Min(_shakeKick + StruggleShakeTugKick, ShakeKickMax);
		GD.Print($"[DADDY-ARENA] tug t={_tick} dist={distance:F1}m pull={reach:F2}m");
	}

	/// <summary>玩家胶囊球与任意 body chunk 球相交 = 已被拽进身体（比质心距离更贴合球团外形）。</summary>
	private bool TouchesBody()
	{
		Vector3 center = PlayerChunkCenter();
		foreach (BodyChunk chunk in _controller.Body.Chunks)
		{
			if (chunk.Pos.DistanceTo(center) <= chunk.Radius + PlayerChunkRadius)
				return true;
		}
		return false;
	}

	/// <summary>下一次猛拽排期：平均间隔 ± 25% 抖动（seed + 脉冲序号的确定性哈希，无全局随机）。</summary>
	private void ScheduleNextTug()
	{
		_tugSerial++;
		ulong hash = (_seed ^ (ulong)_tugSerial * 0x9E3779B97F4A7C15UL);
		hash ^= hash >> 29;
		hash *= 0xBF58476D1CE4E5B9UL;
		float fraction = (hash >> 40) / 16777216f;
		float jitter = 0.75f + 0.5f * fraction;
		_nextTugAtTick = _tick + Math.Max(8,
			(long)MathF.Round(StruggleTugIntervalSeconds * TicksPerSecond * jitter));
	}

	private void EscapeNow(bool byMash)
	{
		if (byMash && _grabTentacle >= 0)
			BeginFlinch(_grabTentacle);
		GD.Print($"[DADDY-ARENA] escape byMash={byMash} mash={_mashCount} " +
				 $"tentacle={_grabTentacle} t={_tick}");
		_grabTentacle = -1;
		_regrabGraceUntilTick = _tick
			+ (long)MathF.Ceiling(RegrabGraceSeconds * TicksPerSecond);
		_grabRetryAtTick = _tick;
		_phase = ArenaPhase.Chase;
		_struggleActive = false;
		_player.InputLocked = false;
		_player.MotionFrozen = false; // DragTick 安全网也走这里，别把冻结带回 Chase
		_promptFlashUntilTick = _tick + PromptFlashTicks;
	}

	/// <summary>
	/// 失能表现（挣脱成功 / 中弹）：不走内核 Stun（EnableStunLimp = 1.35× 重力自由绳，
	/// 拖地在 3D 里读作抽搐），改为占住同一条 ExternalReach 通道喂宿主合成目标——从起点
	/// 平滑抽回，垂吊在锚点下方（同内核 step-peel 收腿的伺服形状），到时清目标自动归队。
	/// 挣脱场景触手已在 ExternalReach（同 ID 无缝接管）；中弹/断手场景是 Locomotion 或
	/// 刚重建的新实例，靠逐 tick 重试挂接（Engaged 前不判 Detached）。任务全程不切换：
	/// 照样不承重，且到期前不参与抓取候选与接手（IsFlinching 双处排除）。
	/// </summary>
	private void BeginFlinch(int tentacleIndex) =>
		BeginFlinch(tentacleIndex, PlayerChunkCenter());

	private void BeginFlinch(int tentacleIndex, Vector3 fromPoint)
	{
		_flinches.Add(new FlinchState
		{
			Tentacle = tentacleIndex,
			StartTick = _tick,
			UntilTick = _tick + Math.Max(1,
				(long)MathF.Ceiling(EscapeTentacleStunSeconds * TicksPerSecond)),
			FromPoint = fromPoint,
			PrevTarget = fromPoint,
		});
		GD.Print($"[DADDY-ARENA] flinch start tentacle={tentacleIndex} t={_tick} " +
				 $"retract={FlinchRetractSeconds:F2}s recover at t={_flinches[^1].UntilTick}");
	}

	private bool IsFlinching(int tentacleIndex)
	{
		foreach (FlinchState flinch in _flinches)
		{
			if (flinch.Tentacle == tentacleIndex)
				return true;
		}
		return false;
	}

	/// <summary>
	/// 逐 tick 驱动全部痛缩：同 StableId 原地更新合成目标（SmoothStep 从抓点退到垂吊点，
	/// 垂吊点每 tick 按当前锚点重算、跟着身体走）。内核（地形回溯）收走任务的条目降级为
	/// Detached：不再喂目标（触手提前归队走路），但「疼着不抓」保留到期。相位无关：
	/// 束缚/吞没期间上一次的痛缩照常恢复。
	/// </summary>
	private void FlinchTick()
	{
		for (int n = _flinches.Count - 1; n >= 0; n--)
		{
			FlinchState flinch = _flinches[n];
			DaddyTentacle tentacle = _controller.Tentacles[flinch.Tentacle];
			// 只有挂接成功后失去任务才算被内核收走；挂接前（断手新实例、pendingRelease
			// 独占 tick）任务本来就不是 ExternalReach，继续重试而不是误判降级。
			if (!flinch.Detached && flinch.Engaged
				&& (flinch.Tentacle == _grabTentacle
					|| tentacle.Task != DaddyTentacleTask.ExternalReach))
			{
				flinch.Detached = true;
				GD.Print($"[DADDY-ARENA] flinch detached by core tentacle={flinch.Tentacle} t={_tick}");
			}
			if (_tick >= flinch.UntilTick)
			{
				if (!flinch.Detached && flinch.Engaged)
					_controller.ClearExternalTarget(flinch.Tentacle);
				GD.Print($"[DADDY-ARENA] flinch recovered tentacle={flinch.Tentacle} t={_tick}");
				_flinches.RemoveAt(n);
				continue;
			}
			if (flinch.Detached)
				continue;
			Vector3 anchor = tentacle.Anchor.Pos;
			Vector3 outward = anchor - _controller.BodyCenter;
			outward.Y = 0f;
			Vector3 fallback = flinch.FromPoint - anchor;
			fallback.Y = 0f;
			Vector3 side = outward.LengthSquared() > 1e-10f ? outward.Normalized()
				: fallback.LengthSquared() > 1e-10f ? fallback.Normalized()
				: Vector3.Forward;
			// 垂吊：主体向下耷拉、少量水平外偏；吊点托在地板上方，免得伺服目标穿地。
			float tuckDistance = tentacle.Length * FlinchTuckLengthRatio;
			Vector3 tuckPoint = anchor
				+ side * (tuckDistance * FlinchHangSideRatio)
				+ Vector3.Down * tuckDistance;
			tuckPoint.Y = MathF.Max(tuckPoint.Y, FlinchHangFloorClearance);
			long retractTicks = Math.Max(1,
				(long)MathF.Ceiling(FlinchRetractSeconds * TicksPerSecond));
			float progress = Mathf.Clamp(
				(float)(_tick - flinch.StartTick) / retractTicks, 0f, 1f);
			Vector3 target = flinch.FromPoint.Lerp(tuckPoint, Mathf.SmoothStep(0f, 1f, progress));
			if (_controller.TryAssignExternalTarget(flinch.Tentacle,
				new DaddyLongLegsTargetSnapshot(
					PlayerTargetId,
					target,
					target - flinch.PrevTarget,
					PlayerChunkRadius,
					1f,
					pullTowardBody: false)))
			{
				flinch.Engaged = true;
			}
			flinch.PrevTarget = target;
		}
	}

	// ---- 手枪：屏幕中心射线 → 逐段胶囊命中 → 断手/失能 ----

	/// <summary>枪口位置：视点右下前少许，枪线从这里出发才看得见（正对视线的线是一个点）。</summary>
	private Vector3 MuzzlePosition()
	{
		Basis basis = _player.EyeBasis;
		return _player.EyePosition
			+ basis.X * 0.12f - basis.Y * 0.09f - basis.Z * 0.25f;
	}

	/// <summary>输入侧只排队：DirectSpaceState 在物理步内才保证可查，射线留到下一个 core tick 打。
	/// 只有自由身（Chase 相位）能开枪——被束缚/拖拽/吞没期一律打不响，脱困只靠连打。</summary>
	private void TryFireGun()
	{
		if (_fatal || _phase != ArenaPhase.Chase
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
		if (_phase != ArenaPhase.Chase || _tick < _nextShotAtTick)
			return;
		_nextShotAtTick = _tick + Math.Max(1,
			(long)MathF.Ceiling(GunCooldownSeconds * TicksPerSecond));
		FireGun(_player.EyePosition, _player.EyeForward);
	}

	/// <summary>
	/// 命中判定全走宿主数学，不建物理形体：① 场景静态体射线截短射程（墙后打不到）；
	/// ② 身体球遮挡（打中球团无效果但挡住后面的触手）；③ 全部触手逐链边
	/// 「射线 vs 胶囊」（段半径 + 瞄准冗余），多条可中只取最近。每次射击约
	/// 触手数×段数 ≈ 200 次胶囊测试，只在扣扳机时发生，成本可忽略。
	/// </summary>
	private void FireGun(Vector3 from, Vector3 direction)
	{
		float maxDistance = GunRange;
		var query = PhysicsRayQueryParameters3D.Create(from, from + direction * GunRange);
		query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
		Godot.Collections.Dictionary wall = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (wall.Count > 0)
			maxDistance = from.DistanceTo((Vector3)wall["position"]);

		float bodyDistance = float.PositiveInfinity;
		foreach (BodyChunk chunk in _controller.Body.Chunks)
		{
			if (RayHitsSphere(from, direction, chunk.Pos, chunk.Radius, out float t)
				&& t < bodyDistance)
			{
				bodyDistance = t;
			}
		}

		int hitTentacle = -1;
		int hitEdge = -1;
		float hitDistance = float.PositiveInfinity;
		float occlusion = MathF.Min(maxDistance, bodyDistance);
		for (int i = 0; i < _controller.Tentacles.Count; i++)
		{
			DaddyTentacle tentacle = _controller.Tentacles[i];
			Vector3 previous = tentacle.Anchor.Pos;
			for (int s = 0; s < tentacle.Segments.Count; s++)
			{
				DaddyTentacleSegmentState segment = tentacle.Segments[s];
				float radius = segment.Radius + GunAimAssistRadius;
				if (RayHitsCapsule(from, direction, previous, segment.Pos, radius, out float t)
					&& t < hitDistance && t <= occlusion)
				{
					hitDistance = t;
					hitTentacle = i;
					hitEdge = s;
				}
				previous = segment.Pos;
			}
		}

		Vector3 tracerEnd;
		if (hitTentacle >= 0)
		{
			tracerEnd = from + direction * hitDistance;
			_hitMarkerTtl = HitMarkerSeconds;
			HitTentacle(hitTentacle, hitEdge, tracerEnd);
		}
		else if (bodyDistance <= maxDistance)
		{
			tracerEnd = from + direction * bodyDistance;
			GD.Print($"[DADDY-ARENA] shot hit body (no effect) t={_tick}");
		}
		else
		{
			tracerEnd = from + direction * maxDistance;
		}
		_tracerFrom = MuzzlePosition();
		_tracerTo = tracerEnd;
		_tracerTtl = TracerSeconds;
	}

	/// <summary>
	/// 命中处置：完整触手 → 从命中链边打断（命中位置只需大致对应，向两端各夹出
	/// 最小残肢/最小断落段）；残肢或段数不足 → 只痛缩失能，不可再断。
	/// 打断正抓着玩家的触手立刻放人（强反制）；牵引中的残肢中弹 = 接手失败。
	/// </summary>
	private void HitTentacle(int tentacleIndex, int edge, Vector3 hitPoint)
	{
		DaddyTentacle tentacle = _controller.Tentacles[tentacleIndex];
		int count = tentacle.Segments.Count;
		bool severable = SeverOnHit
			&& !_controller.IsTentacleSevered(tentacleIndex)
			&& count >= SeverMinStumpSegments + SeverMinFallSegments;
		if (!severable)
		{
			InterruptTractionFor(tentacleIndex, "stump shot");
			RemoveFlinchesFor(tentacleIndex);
			BeginFlinch(tentacleIndex, tentacle.Segments[^1].Pos);
			GD.Print($"[DADDY-ARENA] shot disabled tentacle={tentacleIndex} " +
					 $"segments={count} t={_tick}");
			return;
		}

		int keep = Math.Clamp(edge, SeverMinStumpSegments, count - SeverMinFallSegments);
		RemoveFlinchesFor(tentacleIndex); // 挣脱痛缩中的完整触手也能被打断：旧条目引用旧实例
		float linkLength = tentacle.LinkLength;
		DaddyTentacleSegmentState[] removed =
			_controller.SeverTentacle(tentacleIndex, keep);
		var piece = new DaddyLongLegsSeveredPiece(
			tentacleIndex,
			linkLength,
			removed,
			_arena.Origin,
			_arena.Origin + new Vector3(
				_arena.InteriorWidth, DaddyLongLegsMazeBuilder.RoomHeight, _arena.InteriorDepth));
		piece.BuildVisual(this, _preset.StableId);
		_pieces.Add(piece);
		GD.Print($"[DADDY-ARENA] severed tentacle={tentacleIndex} keep={keep} " +
				 $"fall={removed.Length} hit=({hitPoint.X:F1},{hitPoint.Y:F1},{hitPoint.Z:F1}) t={_tick}");

		// 残肢痛缩：从断口（新实例尖端）抽回。
		BeginFlinch(tentacleIndex, _controller.Tentacles[tentacleIndex].Segments[^1].Pos);

		if (tentacleIndex == _grabTentacle)
		{
			// 打断抓人的触手 = 立刻放人。EscapeNow 不叠加痛缩（断手已经疼了）。
			// 束缚期开枪已被相位门封死（脱困只靠连打），枪械暂时到不了这条分支；
			// 有意保留：这是设计资产，后续会有非枪击的断手来源（其它机制）复用它，
			// 同时兼作安全网——被抓期间无论谁断掉抓人触手，状态都不能悬空。
			if (_phase is ArenaPhase.Bound or ArenaPhase.Dragging)
			{
				EscapeNow(byMash: false);
			}
			else
			{
				_grabTentacle = -1;
				_grabRetryAtTick = _tick + GrabRetryTicks;
			}
		}
	}

	/// <summary>射线 vs 球：返回最近的非负相交距离。</summary>
	private static bool RayHitsSphere(
		Vector3 from, Vector3 direction, Vector3 center, float radius, out float distance)
	{
		distance = 0f;
		Vector3 offset = from - center;
		float b = offset.Dot(direction);
		float c = offset.LengthSquared() - radius * radius;
		float discriminant = b * b - c;
		if (discriminant < 0f)
			return false;
		float root = MathF.Sqrt(discriminant);
		float t = -b - root;
		if (t < 0f)
			t = -b + root;
		if (t < 0f)
			return false;
		distance = t;
		return true;
	}

	/// <summary>
	/// 射线 vs 胶囊（链边两端 + 半径）：取射线与线段的最近点对，距离 ≤ 半径即命中；
	/// 命中距离用最近点的射线参数近似（够「大致符合命中位置」的断点定位精度）。
	/// </summary>
	private static bool RayHitsCapsule(
		Vector3 from,
		Vector3 direction,
		Vector3 capsuleA,
		Vector3 capsuleB,
		float radius,
		out float distance)
	{
		distance = 0f;
		Vector3 segment = capsuleB - capsuleA;
		Vector3 offset = from - capsuleA;
		float segmentDot = segment.LengthSquared();
		float segmentAlongRay = segment.Dot(direction);
		float offsetAlongSegment = offset.Dot(segment);
		float offsetAlongRay = offset.Dot(direction);
		// 最近点对：s|S|² − t(S·D) = offset·S 与 t = s(S·D) − offset·D 联立
		// （offset = 射线原点 − 胶囊端 A；D 单位向量）。
		float denominator = segmentDot - segmentAlongRay * segmentAlongRay;
		float s = denominator > 1e-8f
			? Mathf.Clamp(
				(offsetAlongSegment - segmentAlongRay * offsetAlongRay) / denominator, 0f, 1f)
			: 0f;
		float t = MathF.Max(0f, s * segmentAlongRay - offsetAlongRay);
		Vector3 closestOnSegment = capsuleA + segment * s;
		Vector3 closestOnRay = from + direction * t;
		if (closestOnRay.DistanceSquaredTo(closestOnSegment) > radius * radius)
			return false;
		distance = t;
		return true;
	}

	// ---- 断落段与接手 ----

	private DaddyLongLegsSeveredPiece? FindPiece(int tentacleIndex)
	{
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
		{
			if (piece.TentacleIndex == tentacleIndex)
				return piece;
		}
		return null;
	}

	private ReattachState? FindTraction(int tentacleIndex)
	{
		foreach (ReattachState traction in _tractions)
		{
			if (traction.Tentacle == tentacleIndex)
				return traction;
		}
		return null;
	}

	private void RemoveFlinchesFor(int tentacleIndex)
	{
		for (int n = _flinches.Count - 1; n >= 0; n--)
		{
			if (_flinches[n].Tentacle == tentacleIndex)
				_flinches.RemoveAt(n);
		}
	}

	/// <summary>残肢是否可以（重新）尝试接手：不在疼、冷却已过。</summary>
	private bool CanTentacleAttemptReattach(int tentacleIndex)
	{
		if (IsFlinching(tentacleIndex) || tentacleIndex == _grabTentacle)
			return false;
		return !_reattachRetryAtTick.TryGetValue(tentacleIndex, out long retryAt)
			|| _tick >= retryAt;
	}

	/// <summary>牵引中断（中弹/超时/内核收走）：断落段跌回地面，冷却后再试。</summary>
	private void InterruptTractionFor(int tentacleIndex, string reason)
	{
		ReattachState? traction = FindTraction(tentacleIndex);
		if (traction is null)
			return;
		DaddyTentacle tentacle = _controller.Tentacles[tentacleIndex];
		if (tentacle.Task == DaddyTentacleTask.ExternalReach)
			_controller.ClearExternalTarget(tentacleIndex);
		FindPiece(tentacleIndex)?.EndTraction();
		_reattachRetryAtTick[tentacleIndex] = _tick + Math.Max(0,
			(long)MathF.Ceiling(ReattachRetryCooldownSeconds * TicksPerSecond));
		_tractions.Remove(traction);
		GD.Print($"[DADDY-ARENA] reattach interrupted tentacle={tentacleIndex} ({reason}) t={_tick}");
	}

	/// <summary>
	/// 接手主循环：① 活动牵引——残肢向断口伸（喂 ExternalReach 目标）、断落段被拽向
	/// 残肢尖端，两断点相触即复原；中弹（IsFlinching 顶替）/超时/内核收走则失败落回。
	/// ② 残肢空闲且断口进入 ReattachStartRadius 内 → 发起新牵引；多条可并行。
	/// 抓住玩家不影响已激活的牵引（站着也能拽），但移动优先级见 DriveMonster。
	/// </summary>
	private void ReattachTick()
	{
		for (int n = _tractions.Count - 1; n >= 0; n--)
		{
			ReattachState traction = _tractions[n];
			DaddyLongLegsSeveredPiece? piece = FindPiece(traction.Tentacle);
			if (piece is null)
			{
				_tractions.RemoveAt(n);
				continue;
			}
			DaddyTentacle tentacle = _controller.Tentacles[traction.Tentacle];
			if (IsFlinching(traction.Tentacle))
			{
				InterruptTractionFor(traction.Tentacle, "shot"); // 玩家打断了接手
				continue;
			}
			if (tentacle.Task != DaddyTentacleTask.ExternalReach)
			{
				InterruptTractionFor(traction.Tentacle, "core reclaimed");
				continue;
			}
			if (_tick >= traction.TimeoutTick)
			{
				InterruptTractionFor(traction.Tentacle, "timeout");
				continue;
			}

			Vector3 stumpTip = tentacle.Segments[^1].Pos;
			Vector3 breakEnd = piece.BreakEndPosition;
			piece.UpdateTractionTarget(stumpTip);
			_controller.TryAssignExternalTarget(traction.Tentacle,
				new DaddyLongLegsTargetSnapshot(
					PieceTargetIdBase + (ulong)traction.Tentacle,
					breakEnd,
					breakEnd - traction.PrevBreakEnd,
					0.1f,
					1f,
					pullTowardBody: false));
			traction.PrevBreakEnd = breakEnd;

			if (stumpTip.DistanceTo(breakEnd) <= ReattachConnectRadius)
				CompleteReattach(traction, piece);
		}

		if (!ReattachEnabled)
			return;
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
		{
			int index = piece.TentacleIndex;
			if (FindTraction(index) is not null || !CanTentacleAttemptReattach(index))
				continue;
			DaddyTentacle stump = _controller.Tentacles[index];
			if (!stump.CanAcceptExternalTarget)
				continue; // 被运动预算征用等：等职责分配器放人
			if (stump.Anchor.Pos.DistanceTo(piece.BreakEndPosition) > ReattachStartRadius)
				continue;
			Vector3 breakEnd = piece.BreakEndPosition;
			if (!_controller.TryAssignExternalTarget(index,
				new DaddyLongLegsTargetSnapshot(
					PieceTargetIdBase + (ulong)index,
					breakEnd,
					Vector3.Zero,
					0.1f,
					1f,
					pullTowardBody: false)))
			{
				continue;
			}
			piece.BeginTraction(stump.Segments[^1].Pos);
			_tractions.Add(new ReattachState
			{
				Tentacle = index,
				TimeoutTick = _tick + Math.Max(1,
					(long)MathF.Ceiling(ReattachTimeoutSeconds * TicksPerSecond)),
				PrevBreakEnd = breakEnd,
			});
			GD.Print($"[DADDY-ARENA] reattach traction start tentacle={index} " +
					 $"dist={stump.Anchor.Pos.DistanceTo(breakEnd):F2}m t={_tick}");
		}
	}

	private void CompleteReattach(ReattachState traction, DaddyLongLegsSeveredPiece piece)
	{
		var positions = new Vector3[piece.PointCount];
		piece.CopyPositions(positions);
		_controller.RestoreTentacle(traction.Tentacle, positions, piece.AverageVelocity());
		piece.ClearVisual();
		_pieces.Remove(piece);
		_tractions.Remove(traction);
		_reattachRetryAtTick.Remove(traction.Tentacle);
		GD.Print($"[DADDY-ARENA] reattached tentacle={traction.Tentacle} " +
				 $"segments={_controller.Tentacles[traction.Tentacle].Segments.Count} t={_tick}");
	}

	private void PieceTick()
	{
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
			piece.Tick(_gravityPerTick);
	}

	private void BeginDragging()
	{
		_phase = ArenaPhase.Dragging;
		_struggleActive = false;
		_player.MotionFrozen = true; // 位置改为拖拽外驱，停掉玩家自驱物理（含重力）
		_captiveVel = Vector3.Zero;
		_pendingVelocityDelta = Vector3.Zero;
		_pendingPositionCorrection = Vector3.Zero;
		GD.Print($"[DADDY-ARENA] struggle failed (mash={_mashCount}/{MashTargetPresses}) " +
				 $"— dragging in, t={_tick}");
	}

	private void DragTick()
	{
		if (_grabTentacle < 0
			|| _controller.Tentacles[_grabTentacle].Task != DaddyTentacleTask.ExternalReach)
		{
			EscapeNow(byMash: false);
			return;
		}

		Vector3 eye = PlayerEye();
		Vector3 body = _controller.BodyCenter;
		Vector3 toBody = body - eye;
		float distance = toBody.Length();
		if (distance <= EatRadius)
		{
			EnterEaten();
			return;
		}

		Vector3 direction = distance > 1e-6f ? toBody / distance : Vector3.Up;
		_captiveVel *= 0.94f;
		_captiveVel += _pendingVelocityDelta;
		_pendingVelocityDelta = Vector3.Zero;
		// 内核拉力朝 BodyCenter 衰减（distance×gain 封顶 0.10/tick）；补一个朝向分量下限，
		// 保证末段收敛不磨蹭。整体再夹一次防修正叠加出瞬移。
		float minimumPerTick = DragMinSpeed * (float)TickDt;
		float along = _captiveVel.Dot(direction);
		if (along < minimumPerTick)
			_captiveVel += direction * (minimumPerTick - along);
		_captiveVel = _captiveVel.LimitLength(DragMaxSpeed * (float)TickDt);

		_player.GlobalPosition += _captiveVel + _pendingPositionCorrection;
		_pendingPositionCorrection = Vector3.Zero;
	}

	private void EnterEaten()
	{
		_phase = ArenaPhase.Eaten;
		_eatenAtTick = _tick;
		_struggleActive = false; // 挣脱期拽进身体的提前吞没也走这里：收掉双条
		_player.MotionFrozen = true; // 从 Bound 直达时自驱物理还开着，钉随身体前必须停掉
		_player.InputLocked = true;
		if (_grabTentacle >= 0)
		{
			_controller.ClearExternalTarget(_grabTentacle); // 触手收回，猎物已在身体里
			_grabTentacle = -1;
		}
		GD.Print($"[DADDY-ARENA] devoured t={_tick} — press R to restart");
	}

	private void ResetRun()
	{
		if (_grabTentacle >= 0)
		{
			_controller.ClearExternalTarget(_grabTentacle);
			_grabTentacle = -1;
		}
		_flinches.Clear(); // controller 即将重建，旧触手引用作废
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
			piece.ClearVisual();
		_pieces.Clear();
		_tractions.Clear();
		_reattachRetryAtTick.Clear();
		_nextShotAtTick = 0;
		_tracerTtl = 0f;
		_hitMarkerTtl = 0f;
		SpawnDaddy();
		_player.InputLocked = false;
		_player.MotionFrozen = false;
		_player.Place(_arena.PlayerSpawn, _arena.MonsterSpawn);
		_player.SetActive(true); // 重新捕获鼠标（Esc 释放过也一并恢复）
		_phase = ArenaPhase.Chase;
		_struggleActive = false;
		_mashCount = 0;
		_tugVel = Vector3.Zero;
		_nextTugAtTick = 0;
		_tugSerial = 0;
		_shakeEnvelope = 0f;
		_shakeKick = 0f;
		_shakeTime = 0f;
		_player.SetCameraShake(Vector3.Zero, Vector3.Zero);
		_shakeApplied = false;
		_captiveVel = Vector3.Zero;
		_pendingVelocityDelta = Vector3.Zero;
		_pendingPositionCorrection = Vector3.Zero;
		_grabRetryAtTick = 0;
		_regrabGraceUntilTick = 0;
		_promptFlashUntilTick = -1;
		_hud.SetFadeAlpha(0f);
		GD.Print($"[DADDY-ARENA] reset t={_tick}");
	}

	// ---- 渲染帧：镜头接管 + 渲染 + HUD ----

	public override void _Process(double delta)
	{
		if (_fatal)
			return;

		float physicsDelta = 1f / Math.Max(1, Engine.PhysicsTicksPerSecond);
		float interpolation = Mathf.Clamp(
			(float)(_tickAccumulator / TickDt
				+ Engine.GetPhysicsInterpolationFraction() * physicsDelta / TickDt),
			0f, 1f);

		switch (_phase)
		{
			case ArenaPhase.Bound:
			case ArenaPhase.Dragging:
				UpdateCameraTakeover(delta, interpolation);
				break;
			case ArenaPhase.Eaten:
				PinEyeInsideBody(delta, interpolation);
				break;
		}
		UpdateCameraShake((float)delta);

		bool formalOn = _formalRenderer is not null && _formalView;
		if (formalOn && _formalRenderer is { } formal)
			formal.Draw(interpolation, (float)delta);
		else
			_renderer.Render(interpolation);
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
			piece.Render(interpolation);
		DrawTracer((float)delta);
		// 调试线必须每帧调用（Draw 开头 ClearSurfaces）；正式渲染下临时压 Enabled。
		bool terrainEnabled = _terrain.Enabled;
		_terrain.Enabled = terrainEnabled && !formalOn;
		_terrain.Draw(GetViewport().GetCamera3D() ?? _bootCamera, _controller.BodyCenter,
			_controller.LastMoveTargetKind, _controller.LastMoveTarget);
		_terrain.Enabled = terrainEnabled;

		UpdateHud();
	}

	private Vector3 InterpolatedBodyCenter(float interpolation)
	{
		Vector3 weighted = Vector3.Zero;
		float mass = 0f;
		foreach (BodyChunk chunk in _controller.Body.Chunks)
		{
			weighted += chunk.LerpPos(interpolation) * chunk.Mass;
			mass += chunk.Mass;
		}
		return mass > 0f ? weighted / mass : _controller.BodyCenter;
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

	/// <summary>
	/// 束缚/拖拽期镜头接管：yaw+pitch 指数阻尼转向插值身体质心（不瞬切）；
	/// Bound 中「对准」后才开挣脱流程（带超时兜底，转向不顺也不卡流程）。
	/// </summary>
	private void UpdateCameraTakeover(double delta, float interpolation)
	{
		Vector3 focus = InterpolatedBodyCenter(interpolation);
		Vector3 eye = PlayerEye();
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

		if (_phase != ArenaPhase.Bound || _struggleActive)
			return;
		_takeoverElapsed += delta;
		float errorDegrees = Mathf.RadToDeg(_player.EyeForward.AngleTo(to.Normalized()));
		if (errorDegrees <= AlignStartDegrees
			|| _takeoverElapsed >= Math.Max(1.2, CameraTakeoverSeconds * 2.5))
		{
			BeginStruggle();
		}
	}

	/// <summary>
	/// 挣脱连打期的镜头震动：多条不可通约正弦叠成无规律抖动（无 RNG、纯渲染侧），
	/// 猛拽瞬间叠一记快衰减冲量。只在 Bound 且挣扎流程已激活时驱动——刚被抓的扭头
	/// 对准（<see cref="_struggleActive"/> 未立）与拖拽/吞没相位目标包络为零，按
	/// <see cref="ShakeReleaseTau"/> 自然收敛后停喂（硬切到零画面会跳）。
	/// </summary>
	private void UpdateCameraShake(float delta)
	{
		float target = _phase == ArenaPhase.Bound && _struggleActive ? 1f : 0f;
		float tau = target > _shakeEnvelope ? ShakeAttackTau : ShakeReleaseTau;
		_shakeEnvelope += (target - _shakeEnvelope) * (1f - MathF.Exp(-delta / tau));
		_shakeKick *= MathF.Exp(-delta / ShakeKickDecayTau);
		float amplitude = _shakeEnvelope * (1f + _shakeKick);
		if (amplitude < 0.001f || StruggleShakeDegrees <= 0f)
		{
			if (_shakeApplied)
			{
				_player.SetCameraShake(Vector3.Zero, Vector3.Zero);
				_shakeApplied = false;
			}
			_shakeTime = 0f;
			return;
		}

		_shakeTime += delta * StruggleShakeFrequencyHz * Mathf.Tau;
		float t = _shakeTime;
		float rot = Mathf.DegToRad(StruggleShakeDegrees) * amplitude;
		// 频率比 1 / 0.61 / 0.83 / 0.47 / 0.73 互不成整数比，叠加后无循环节拍感。
		var euler = new Vector3(
			(MathF.Sin(t) * 0.6f + MathF.Sin(t * 0.61f + 1.7f) * 0.4f) * rot,
			(MathF.Sin(t * 0.83f + 4.2f) * 0.6f + MathF.Sin(t * 0.47f + 0.9f) * 0.4f) * rot,
			MathF.Sin(t * 0.73f + 2.6f) * rot * 0.5f);
		float sway = StruggleShakeOffsetMeters * amplitude;
		var offset = new Vector3(
			MathF.Sin(t * 0.89f + 0.4f) * sway,
			MathF.Sin(t * 1.13f + 3.1f) * sway * 0.7f,
			0f);
		_player.SetCameraShake(offset, euler);
		_shakeApplied = true;
	}

	/// <summary>吞没期：把视点平滑吸进身体质心并钉住（跟随质心漂移）。</summary>
	private void PinEyeInsideBody(double delta, float interpolation)
	{
		Vector3 focus = InterpolatedBodyCenter(interpolation);
		Vector3 eyeOffset = PlayerEye() - _player.GlobalPosition;
		Vector3 pinTarget = focus - eyeOffset;
		float blend = 1f - Mathf.Exp(-(float)delta / 0.12f);
		_player.GlobalPosition = _player.GlobalPosition.Lerp(pinTarget, blend);
	}

	private void UpdateHud()
	{
		float distance = _controller.BodyCenter.DistanceTo(PlayerEye());
		string grab = _grabTentacle >= 0
			? $"{_grabTentacle}({_controller.Tentacles[_grabTentacle].Role})"
			: "-";
		string flinch = "";
		foreach (FlinchState state in _flinches)
			flinch += $" flinch={state.Tentacle}({(state.UntilTick - _tick) / (float)TicksPerSecond:F1}s)";
		string pieces = "";
		foreach (DaddyLongLegsSeveredPiece piece in _pieces)
			pieces += $" piece={piece.TentacleIndex}({piece.State})";
		_hud.SetStatus(
			$"DADDY GRAB ARENA — phase={_phase} drive={DriveName()} dist={distance:F1}m " +
			$"grab={grab}{flinch}{pieces} mash={_mashCount}/{MashTargetPresses}\n" +
			"[LMB] shoot  [M] chase drive  [R] restart  [V] render  [F1] hud  [F3] rays  [Esc] mouse");
		_hud.SetCrosshair(
			_phase == ArenaPhase.Chase && Input.MouseMode == Input.MouseModeEnum.Captured,
			_hitMarkerTtl > 0f);

		switch (_phase)
		{
			case ArenaPhase.Chase:
				if (_tick < _promptFlashUntilTick)
					_hud.SetPrompt("BROKE FREE!", "the tentacle recoils in pain — run");
				else
					_hud.SetPrompt("");
				_hud.SetBars(false, 0f, 0f);
				_hud.SetFadeAlpha(0f);
				break;
			case ArenaPhase.Bound:
				if (_struggleActive)
				{
					float remaining = Mathf.Clamp(
						(_struggleDeadlineTick - _tick)
							/ (StruggleTimeLimitSeconds * TicksPerSecond),
						0f, 1f);
					_hud.SetPrompt("MASH [SPACE] TO BREAK FREE",
						$"{_mashCount}/{MashTargetPresses}");
					_hud.SetBars(true, (float)_mashCount / MashTargetPresses, remaining);
				}
				else
				{
					_hud.SetPrompt("SNARED!");
					_hud.SetBars(false, 0f, 0f);
				}
				break;
			case ArenaPhase.Dragging:
				_hud.SetPrompt("DRAGGED IN…");
				_hud.SetBars(false, 0f, 0f);
				break;
			case ArenaPhase.Eaten:
				long sinceEaten = _tick - _eatenAtTick;
				_hud.SetFadeAlpha(0.85f * Mathf.Clamp(
					sinceEaten / (float)DevourFadeTicks, 0f, 1f));
				bool restartArmed = sinceEaten
					>= (long)(EatenRestartDelaySeconds * TicksPerSecond);
				_hud.SetPrompt("DEVOURED", restartArmed ? "[R] RESTART" : "");
				_hud.SetBars(false, 0f, 0f);
				break;
		}
	}

	private string DriveName() => _chaseDriveDir ? "dir" : "target";

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
			case Key.Space:
				if (_phase == ArenaPhase.Bound && _struggleActive)
				{
					_mashCount++;
					if (_mashCount >= MashTargetPresses)
						EscapeNow(byMash: true);
				}
				break;
			case Key.M:
				_chaseDriveDir = !_chaseDriveDir;
				GD.Print($"[DADDY-ARENA] chase drive -> {DriveName()}");
				break;
			case Key.R:
				ResetRun();
				break;
			case Key.V:
				_formalView = !_formalView;
				ApplyRenderView();
				break;
			case Key.F1:
				_hud.ToggleStatusVisibility();
				break;
			case Key.F3:
				_terrain.Enabled = !_terrain.Enabled;
				break;
			case Key.Escape:
				Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
					? Input.MouseModeEnum.Visible
					: Input.MouseModeEnum.Captured;
				break;
		}
	}
}
