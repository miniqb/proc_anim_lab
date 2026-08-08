using System;
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

	/// <summary>挣脱成功后该触手的失能时长（内核 Stun：自动松手 + 软瘫下垂）。</summary>
	[Export(PropertyHint.Range, "0.5,30,0.5")]
	public float EscapeTentacleStunSeconds { get; set; } = 4f;

	/// <summary>挣脱成功后全局再抓宽限：给玩家拉开距离的时间。</summary>
	[Export(PropertyHint.Range, "0,30,0.25")]
	public float RegrabGraceSeconds { get; set; } = 1.5f;

	/// <summary>镜头接管的阻尼时间常数（≈ 该秒数内基本完成转向）。</summary>
	[Export(PropertyHint.Range, "0.1,3,0.05")]
	public float CameraTakeoverSeconds { get; set; } = 0.5f;

	/// <summary>视线与怪物身体夹角小于此角度（度）即视为「已对准」，挣脱流程与倒计时从此刻开始。</summary>
	[Export(PropertyHint.Range, "1,45,0.5")]
	public float AlignStartDegrees { get; set; } = 6f;

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

	// 束缚 / 挣脱
	private double _takeoverElapsed;
	private bool _struggleActive;
	private int _mashCount;
	private long _struggleDeadlineTick;
	private long _promptFlashUntilTick = -1;
	private Vector3 _tugVel;
	private long _nextTugAtTick;
	private int _tugSerial;

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
		if (!float.IsFinite(RegrabGraceSeconds) || RegrabGraceSeconds < 0f)
			return Fail($"RegrabGraceSeconds must be finite and >= 0, got {RegrabGraceSeconds}");
		if (!FinitePositive(CameraTakeoverSeconds))
			return Fail($"CameraTakeoverSeconds must be finite and positive, got {CameraTakeoverSeconds}");
		if (!FinitePositive(AlignStartDegrees))
			return Fail($"AlignStartDegrees must be finite and positive, got {AlignStartDegrees}");
		if (!FinitePositive(EatRadius))
			return Fail($"EatRadius must be finite and positive, got {EatRadius}");
		if (!FinitePositive(DragMinSpeed))
			return Fail($"DragMinSpeed must be finite and positive, got {DragMinSpeed}");
		if (!float.IsFinite(DragMaxSpeed) || DragMaxSpeed <= DragMinSpeed)
			return Fail($"DragMaxSpeed ({DragMaxSpeed}) must exceed DragMinSpeed ({DragMinSpeed})");
		if (!float.IsFinite(EatenRestartDelaySeconds) || EatenRestartDelaySeconds < 0f)
			return Fail($"EatenRestartDelaySeconds must be finite and >= 0, got {EatenRestartDelaySeconds}");

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

		switch (_phase)
		{
			case ArenaPhase.Chase:
				DriveChase();
				UpdateGrabAttempt();
				break;
			default:
				FeedIdleStance();
				break;
		}

		FeedGrabSnapshot();
		_controller.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
		ApplyTargetEffects();
		UpdatePhaseAfterTick();
	}

	// ---- 追逐 ----

	private void DriveChase()
	{
		Vector3 target = _player.GlobalPosition + Vector3.Up * ChaseHeightOffset;
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
			if (!tentacle.CanAcceptExternalTarget)
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
		{
			int stunTicks = Math.Max(1,
				(int)MathF.Ceiling(EscapeTentacleStunSeconds * TicksPerSecond));
			_controller.StunTentacle(_grabTentacle, stunTicks); // 内部自动 ClearExternalTarget
		}
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

		bool formalOn = _formalRenderer is not null && _formalView;
		if (formalOn && _formalRenderer is { } formal)
			formal.Draw(interpolation, (float)delta);
		else
			_renderer.Render(interpolation);
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
		_hud.SetStatus(
			$"DADDY GRAB ARENA — phase={_phase} drive={DriveName()} dist={distance:F1}m " +
			$"grab={grab} mash={_mashCount}/{MashTargetPresses}\n" +
			"[M] chase drive  [R] restart  [V] render  [F1] hud  [F3] rays  [Esc] mouse");

		switch (_phase)
		{
			case ArenaPhase.Chase:
				if (_tick < _promptFlashUntilTick)
					_hud.SetPrompt("BROKE FREE!", "the tentacle is limp for a while — run");
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
		if (_fatal || @event is not InputEventKey { Pressed: true, Echo: false } key)
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
