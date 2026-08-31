using System;
using System.Globalization;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Species.TentaclePlant;
using ProcAnim.Core.Terrain;
using ProcAnimLab.Render;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// 拟态草（肉质触手怪）吊顶伏击竞技场：第一人称亲历「天花板上那盏灯是活的」。
/// 探索场景，不进矩阵（≙ rat_arena 纪律：命令行零参数、全 Inspector 导出、
/// 默认值是唯一真相源、生效值打在 ready 行）。
///
/// 设定：怪物无眼，喉部灯泡发光并检测反射光的**变化**——静止即隐身。感知由
/// <see cref="TentaclePlantPerception"/> 实现：锁定锥（窄角短长，锥内移动累计到
/// 短阈值即锁定）+ 察觉区（宽角远半径，移动累计到长阈值触发探头搜索）；敏化随
/// 刺激抬升。突刺瞄"最后感知点"（非实时眼位）；攻击拉伸（内核 opt-in
/// StrikeStretchFactor）让攻距 ≈ 探测半径 + 锁定锥长、极限距离会咬空。
///
/// 宿主三相：Ambush（伪装吸顶灯，锁定 → 伪装态 ×10 充能突袭）→ Probe（察觉
/// 累计过阈：放弃伪装，<see cref="TentaclePlantProbePlanner"/> 动画探测点、头端
/// 张嘴跟随扫视；触发性聆听急转凝视；预算尽回伪装）→ Engage（锁定新鲜期内喂
/// 真目标，预张紧 ×10 充能出手；锁定过期转 Probe 搜索）。锁定/冷却/探头就绪都
/// 是时间戳或感知量，**不占相位**（R19 教训）。
///
/// 攻击本身是内核涌现的（充能满自动突刺），宿主只用 Target 快照的 HostVisible
/// 开关充能门。玩家恒 HostGrabbable=false，咬中判定宿主自做（Striking 期 Hand
/// 距玩家眼位 ≤ BiteRadius，每 AttackSerial 只结算一次）；探头期嘴撞到玩家走
/// 反射性咬合（脊髓反射，不经感知系统）。
/// </summary>
public partial class TentaclePlantArenaWorld : Node3D
{
	private const double TickDt = 0.025;
	private const float TicksPerSecond = 40f;
	private const ulong PlayerStableId = 0x504C414E544C414DUL;
	// 玩家喂内核的目标球：球心取眼位（= 相机高度，ArenaFirstPersonPlayer.EyePosition，
	// 脚底 +1.55m）——吊顶伏击者要冲着脸咬，不是腰腹；半径沿用胶囊半径。
	private const float PlayerChunkRadius = 0.35f;

	[ExportGroup("Arena")]
	[Export] public string DefaultPreset = "lurker";
	[Export(PropertyHint.Range, "40,1000,1")] public int HostPhysicsTps = 40;
	[Export] public float GravityMps2 = 36f;
	[Export(PropertyHint.Range, "12,40,0.5")] public float ArenaWidth = 16f;
	[Export(PropertyHint.Range, "12,40,0.5")] public float ArenaDepth = 16f;
	[Export(PropertyHint.Range, "1,8,0.5")] public float SpawnEndInset = 4f;

	[ExportGroup("Behavior")]
	/// <summary>每次突刺后的攻击冷却（时间戳制，冷却中不充能但仍盯人）。</summary>
	[Export(PropertyHint.Range, "0.5,10,0.1")] public float AttackCooldownSeconds = 2.5f;
	/// <summary>攻击弹性拉伸（内核 opt-in）：突刺窗口有效链长 ×系数，攻距 ≈ ×Length。</summary>
	[Export(PropertyHint.Range, "1,1.6,0.05")] public float StrikeStretchFactor = 1.5f;
	/// <summary>预张紧充能倍率（内核 opt-in）：锁定后 ceil(ChargeTicks/倍率) tick 出手。</summary>
	[Export(PropertyHint.Range, "1,20,1")] public int ProbeChargeMultiplier = 10;

	[ExportGroup("Perception")]
	/// <summary>锁定锥半角：伪装朝下时脚下光池（眼高平面）半径 ≈ 1.6·tan(半角)。</summary>
	[Export(PropertyHint.Range, "4,40,0.5")] public float LockConeHalfAngleDegrees = 16f;
	/// <summary>锁定锥长（从头部灯泡沿嘴 forward）：≈ 灯泡朝下恰好落地。</summary>
	[Export(PropertyHint.Range, "1,8,0.1")] public float LockConeLength = 3.2f;
	/// <summary>察觉区半角（同器官低信噪比面，宽得多）。</summary>
	[Export(PropertyHint.Range, "30,89,1")] public float AwareHalfAngleDegrees = 75f;
	/// <summary>察觉区半径：超出即绝对安全区（沿墙走不会被察觉）。</summary>
	[Export(PropertyHint.Range, "2,16,0.1")] public float AwareRadius = 6.5f;
	/// <summary>移动死区：低于该速度的眼位位移不算"变化"。</summary>
	[Export(PropertyHint.Range, "0,2,0.05")] public float MoveDeadzoneMps = 0.3f;
	/// <summary>锁定阈值（加权米）：跑速 5m/s 约 0.6s、潜行约 2 倍。</summary>
	[Export(PropertyHint.Range, "0.5,20,0.1")] public float LockThresholdMeters = 3.0f;
	/// <summary>察觉阈值（加权米）：跑速约 2.4s 触发探头。</summary>
	[Export(PropertyHint.Range, "2,60,0.5")] public float AwareThresholdMeters = 12.0f;
	/// <summary>锁定累计静止衰减半衰期。</summary>
	[Export(PropertyHint.Range, "0.2,30,0.1")] public float LockStillHalfLifeSeconds = 3.0f;
	/// <summary>锁定累计出锥衰减半衰期（稍快、不清零）。</summary>
	[Export(PropertyHint.Range, "0.2,30,0.1")] public float LockOutHalfLifeSeconds = 1.2f;
	/// <summary>察觉累计静止衰减半衰期。</summary>
	[Export(PropertyHint.Range, "0.2,30,0.1")] public float AwareStillHalfLifeSeconds = 6.0f;
	/// <summary>察觉累计出区衰减半衰期（稍快、不清零）。</summary>
	[Export(PropertyHint.Range, "0.2,30,0.1")] public float AwareOutHalfLifeSeconds = 2.5f;
	/// <summary>敏化增益（每感知到 1m 移动抬升多少累计倍率）。</summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float SensitizeGainPerMeter = 0.10f;
	/// <summary>敏化倍率上限。</summary>
	[Export(PropertyHint.Range, "1,6,0.1")] public float SensitizeMax = 3.0f;
	/// <summary>敏化恢复半衰期（静止时指数回落到 1）。</summary>
	[Export(PropertyHint.Range, "1,60,0.5")] public float SensitizeHalfLifeSeconds = 8.0f;
	/// <summary>锁定新鲜期：期内持续喂真目标（突刺仍瞄最后感知点）。</summary>
	[Export(PropertyHint.Range, "0.5,10,0.1")] public float LockHoldSeconds = 2.5f;
	/// <summary>光束回转速度：感知锥轴（=渲染嘴朝向）转向新目标方向的角速度上限。</summary>
	[Export(PropertyHint.Range, "30,720,10")] public float BeamTurnDegreesPerSecond = 240f;
	/// <summary>察觉区粗方位噪声（±度）。</summary>
	[Export(PropertyHint.Range, "0,60,1")] public float BearingErrorDegrees = 18f;
	/// <summary>察觉区幅度测距噪声（±比例）。</summary>
	[Export(PropertyHint.Range, "0,1,0.05")] public float RangeErrorFraction = 0.30f;

	[ExportGroup("Probe")]
	/// <summary>探测预算：伸得越长耗得越快，耗尽回伪装。</summary>
	[Export(PropertyHint.Range, "2,60,0.5")] public float ProbeBudgetSeconds = 12f;
	/// <summary>预算的伸长附加消耗系数（∝ 探测点距挂点 / 链长）。</summary>
	[Export(PropertyHint.Range, "0,4,0.1")] public float ProbeStretchCost = 1.5f;
	/// <summary>探测头动舒适半径 H（停头位置钳到此内；须 &lt; 链长）。</summary>
	[Export(PropertyHint.Range, "0.5,8,0.1")] public float ProbeHeadRadius = 2.3f;
	/// <summary>停头回撤比例：停在估计距离 − 比例×锥长处（锥尖够猎物、嘴不过冲）。</summary>
	[Export(PropertyHint.Range, "0.1,0.9,0.05")] public float StopBackoffRatio = 0.6f;
	/// <summary>探测点动画速度上限（慢于潜行玩家；头端伺服再叠一层滞后）。</summary>
	[Export(PropertyHint.Range, "0.2,5,0.1")] public float ProbeSpeedMps = 1.8f;
	/// <summary>触发性聆听/回头杀的急转速度倍率。</summary>
	[Export(PropertyHint.Range, "1,5,0.1")] public float ProbeTurnBoost = 2.5f;
	/// <summary>常态性聆听停顿下限。</summary>
	[Export(PropertyHint.Range, "0.2,10,0.1")] public float DwellMinSeconds = 0.6f;
	/// <summary>常态性聆听停顿上限。</summary>
	[Export(PropertyHint.Range, "0.2,10,0.1")] public float DwellMaxSeconds = 1.5f;
	/// <summary>触发性聆听凝视下限。</summary>
	[Export(PropertyHint.Range, "0.2,10,0.1")] public float GazeMinSeconds = 2.0f;
	/// <summary>触发性聆听凝视上限。</summary>
	[Export(PropertyHint.Range, "0.2,10,0.1")] public float GazeMaxSeconds = 3.5f;
	/// <summary>回头杀概率（每次停顿结束掷一次）。</summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float LookbackProbability = 0.12f;
	/// <summary>包络扩张率 = 假想猎物移速（无新信息时搜索圈按它长大）。</summary>
	[Export(PropertyHint.Range, "0.1,5,0.1")] public float HypoPreySpeedMps = 1.5f;
	/// <summary>搜索包络半径上限。</summary>
	[Export(PropertyHint.Range, "1,12,0.5")] public float SearchRadiusMax = 5.0f;
	/// <summary>探头结束（预算尽/反射咬合）后再次允许探头的冷却。</summary>
	[Export(PropertyHint.Range, "0,10,0.1")] public float ProbeCooldownSeconds = 2.0f;

	[ExportGroup("Bite")]
	/// <summary>咬合半径：Striking 期 Hand 到玩家胸心的判距（≈ 嘴 + 玩家胶囊）。</summary>
	[Export(PropertyHint.Range, "0.2,2,0.05")] public float BiteRadius = 0.55f;
	[Export(PropertyHint.Range, "0,8,0.1")] public float BiteShoveSpeed = 2.5f;
	[Export(PropertyHint.Range, "0.2,5,0.1")] public float BittenPromptSeconds = 1.2f;

	[ExportGroup("Debug")]
	/// <summary>调试覆盖层初值：感知两锥 + 当前探测点与连线（运行时 F3 切换）。</summary>
	[Export] public bool DebugOverlay = false;

	[ExportGroup("Camera Kick")]
	[Export(PropertyHint.Range, "0,4,0.05")] public float KickDegrees = 0.8f;
	[Export(PropertyHint.Range, "0,0.1,0.001")] public float KickOffsetMeters = 0.012f;
	[Export(PropertyHint.Range, "1,40,0.5")] public float KickFrequencyHz = 11f;
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float KickDecaySeconds = 0.4f;

	private enum HostPhase
	{
		Ambush,
		Probe,
		Engage,
	}

	/// <summary>感知视线接缝：墙层（掩码 1）射线；玩家在层 2，天然不挡自己。</summary>
	private sealed class TerrainLineOfSight : IPerceptionRaycast
	{
		private readonly RaycastTerrainQuery _terrain;

		public TerrainLineOfSight(RaycastTerrainQuery terrain) => _terrain = terrain;

		public bool LineBlocked(Vector3 from, Vector3 to) =>
			_terrain.Raycast(from, to, out _);
	}

	// 聆听节拍常量（刻意不导出：旋钮宁少勿多，敏化/累计已够调）。
	private const int ListenStillTicks = 20;   // 静止 0.5s 后的首个移动算"触发性聆听"
	private const float ListenGain = 0.05f;    // 那一下的累计折扣（灯照到=免费警告）
	private const float ProbeMinDrop = 0.8f;   // 停头距挂点的最小下探

	private readonly RaycastTerrainQuery _terrain = new();
	private BoxRoomArenaBuilder _arena = null!;
	private TentaclePlantParams _preset = null!;
	private TentaclePlantController _plant = null!;
	private TentaclePlantFormalRenderer? _formal;
	private ArenaFirstPersonPlayer _player = null!;
	private TentaclePlantArenaHud _hud = null!;
	private TentaclePlantDebugDraw _debugDraw = null!;
	private OmniLight3D _lampLight = null!;

	private Vector3 _gravityPerTick;
	private Vector3 _mountPoint;
	private double _tickAccumulator;
	private long _tick;
	private bool _fatal;

	private HostPhase _phase = HostPhase.Ambush;
	private TentaclePlantPerception _perception = null!;
	private TentaclePlantProbePlanner _planner = null!;
	private long _attackReadyTick;
	private long _probeReadyTick;
	private long _prevAttackSerial;
	private long _lastBiteSerial;
	private int _biteCount;
	private long _bittenPromptUntilTick = -1;
	private Vector3 _playerAim;
	private Vector3 _prevPlayerAim;
	private bool _playerAimInitialized;

	// 权威光束朝向（tick 域，限速回转）：探头时链身有余量、末段在重力下上翘，
	// 链末段推导的 forward 不再代表"照哪"。感知锥轴、调试锥、渲染嘴/探照灯
	// 都消费同一根方向——可视化即判定。
	private Vector3 _beamDir = Vector3.Down;
	private string _toastText = "";
	private float _toastTtl;

	// 探头调试观测（纯读，不参与任何判定）：本 tick 是否真把探测点喂给了内核，
	// 以及它的上一/本 tick 位置——画面按渲染 alpha 插值，40Hz 逻辑不抖画面。
	private bool _probeTargetActive;
	private bool _probeTargetPrevActive;
	private Vector3 _probeAimPrev;
	private Vector3 _probeAimCurr;

	// 镜头 kick（纯渲染侧化妆：多条不可通约正弦 × 冲量包络，无 RNG——RatArena 同款）。
	private float _kick;
	private float _kickTime;
	private bool _kickApplied;

	public override void _Ready()
	{
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
		System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

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
			GD.PushError($"[PLANT-ARENA] {error.Message}");
			_fatal = true;
			GetTree().Quit(2);
			return;
		}
		_arena.Build(this);

		// 挂点：房间正中天花板走面（BoxRoomArenaBuilder 净高 = RoomHeight）。
		_mountPoint = _arena.Origin + new Vector3(
			ArenaWidth * 0.5f,
			DaddyLongLegsSandbox.DaddyLongLegsMazeBuilder.RoomHeight,
			ArenaDepth * 0.5f);

		// 挂点下的暖光：既是"这盏吸顶灯是亮的"的舞台调度，也是引玩家走近的信号；
		// 亮度随 DisguiseAmount 走——揭露时灯"熄"，嘴里的灯泡只剩自发光。
		_lampLight = new OmniLight3D
		{
			Name = "LampLight",
			Position = _mountPoint - new Vector3(0f, 0.35f, 0f),
			LightColor = new Color(1f, 0.82f, 0.58f),
			LightEnergy = 2.5f,
			OmniRange = 7f,
			ShadowEnabled = true,
		};
		AddChild(_lampLight);

		SpawnPlant();
		RebuildPerception();

		_debugDraw = new TentaclePlantDebugDraw { Enabled = DebugOverlay };
		_debugDraw.Build(this);
		// 画的锥与感知器吃的是同一组 Export（ValidateExports 已放行）。
		_debugDraw.ConfigureCones(
			LockConeHalfAngleDegrees, LockConeLength,
			AwareHalfAngleDegrees, AwareRadius);

		_player = new ArenaFirstPersonPlayer { Name = "ArenaPlayer" };
		AddChild(_player);
		_player.Place(_arena.PlayerSpawn, _mountPoint);
		_player.SetActive(true);
		// 压暗玩家补光：探照灯的明暗对比是感知锥的可视化，不能被冲淡。
		_player.ConfigureFillLight(1.2f, 10f);

		_hud = new TentaclePlantArenaHud();
		_hud.Build(this);

		GD.Print($"[PLANT-ARENA] ready preset={_preset.Name} tps={HostPhysicsTps} " +
				 $"room={ArenaWidth:F0}x{ArenaDepth:F0}m " +
				 $"lock={LockConeHalfAngleDegrees:F0}°x{LockConeLength:F1}m/" +
				 $"{LockThresholdMeters:F1}wm aware={AwareHalfAngleDegrees:F0}°x" +
				 $"{AwareRadius:F1}m/{AwareThresholdMeters:F1}wm " +
				 $"probe={ProbeBudgetSeconds:F0}s H={ProbeHeadRadius:F1}m " +
				 $"stretch={StrikeStretchFactor:F2} chargeX={ProbeChargeMultiplier} " +
				 $"cooldown={AttackCooldownSeconds:F1}s bite={BiteRadius:F2}m " +
				 $"shove={BiteShoveSpeed:F1}mps dbg={(DebugOverlay ? 1 : 0)}");
	}

	/// <summary>（重）建感知器与探头规划器：固定 seed，R 重开可复现。</summary>
	private void RebuildPerception()
	{
		static float DecayFromHalfLife(float seconds) =>
			MathF.Exp(-0.6931472f / (TicksPerSecond * MathF.Max(0.01f, seconds)));

		var perceptionConfig = new TentaclePlantPerceptionConfig
		{
			LockCosHalfAngle = MathF.Cos(Mathf.DegToRad(LockConeHalfAngleDegrees)),
			LockLength = LockConeLength,
			AwareCosHalfAngle = MathF.Cos(Mathf.DegToRad(AwareHalfAngleDegrees)),
			AwareRadius = AwareRadius,
			MoveDeadzonePerTick = MoveDeadzoneMps / TicksPerSecond,
			LockThreshold = LockThresholdMeters,
			AwareThreshold = AwareThresholdMeters,
			LockStillDecay = DecayFromHalfLife(LockStillHalfLifeSeconds),
			LockOutDecay = DecayFromHalfLife(LockOutHalfLifeSeconds),
			AwareStillDecay = DecayFromHalfLife(AwareStillHalfLifeSeconds),
			AwareOutDecay = DecayFromHalfLife(AwareOutHalfLifeSeconds),
			SensitizeGainPerMeter = SensitizeGainPerMeter,
			SensitizeMax = SensitizeMax,
			SensitizeRecovery = DecayFromHalfLife(SensitizeHalfLifeSeconds),
			LockHoldTicks = (int)MathF.Ceiling(LockHoldSeconds * TicksPerSecond),
			ListenStillTicks = ListenStillTicks,
			ListenGain = ListenGain,
			BearingErrorRadians = Mathf.DegToRad(BearingErrorDegrees),
			RangeErrorFraction = RangeErrorFraction,
		};
		_perception = new TentaclePlantPerception(
			perceptionConfig, new TerrainLineOfSight(_terrain), 0x50455243);

		var probeConfig = new TentaclePlantProbeConfig
		{
			HeadRadius = ProbeHeadRadius,
			ConeLength = LockConeLength,
			StopBackoffRatio = StopBackoffRatio,
			MinDrop = ProbeMinDrop,
			SpeedPerTick = ProbeSpeedMps / TicksPerSecond,
			TurnBoost = ProbeTurnBoost,
			DwellMinTicks = (int)MathF.Ceiling(DwellMinSeconds * TicksPerSecond),
			DwellMaxTicks = (int)MathF.Ceiling(DwellMaxSeconds * TicksPerSecond),
			GazeMinTicks = (int)MathF.Ceiling(GazeMinSeconds * TicksPerSecond),
			GazeMaxTicks = (int)MathF.Ceiling(GazeMaxSeconds * TicksPerSecond),
			LookbackProbability = LookbackProbability,
			HypoPreySpeedPerTick = HypoPreySpeedMps / TicksPerSecond,
			SearchRadiusMax = SearchRadiusMax,
			BudgetTicks = ProbeBudgetSeconds * TicksPerSecond,
			StretchCost = ProbeStretchCost,
			ChainLength = _preset.Length,
		};
		_planner = new TentaclePlantProbePlanner(probeConfig, 0x50524F42);
	}

	private bool ValidateExports()
	{
		try
		{
			_preset = TentaclePlantFactory.ByName($"tentacle-plant/{DefaultPreset}");
		}
		catch (ArgumentException)
		{
			return Fail($"DefaultPreset '{DefaultPreset}' is not original/short/hunter/lurker");
		}
		if (HostPhysicsTps is < 40 or > 1000)
			return Fail($"HostPhysicsTps must be in [40,1000], got {HostPhysicsTps}");
		if (!FinitePositive(GravityMps2))
			return Fail($"GravityMps2 must be finite and positive, got {GravityMps2}");
		if (!FinitePositive(AttackCooldownSeconds))
			return Fail($"AttackCooldownSeconds must be finite and positive, got {AttackCooldownSeconds}");
		// 内核 opt-in 覆写：拉伸 + 预张紧写到本场景的预设实例上（ByName 每次新建，
		// 不污染工厂共享对象），交给内核 Validate 快速失败。
		_preset.StrikeStretchFactor = StrikeStretchFactor;
		_preset.ProbeChargeMultiplier = ProbeChargeMultiplier;
		try
		{
			_preset.Validate();
		}
		catch (ArgumentOutOfRangeException error)
		{
			return Fail($"preset override rejected by kernel Validate: {error.Message}");
		}
		// 感知/探头参数：有限正 + 交叉约束。
		if (!FinitePositive(LockConeHalfAngleDegrees) || LockConeHalfAngleDegrees >= 90f)
			return Fail($"LockConeHalfAngleDegrees must be in (0,90), got {LockConeHalfAngleDegrees}");
		if (!FinitePositive(LockConeLength))
			return Fail($"LockConeLength must be finite and positive, got {LockConeLength}");
		if (!FinitePositive(AwareHalfAngleDegrees) || AwareHalfAngleDegrees >= 90f ||
			AwareHalfAngleDegrees < LockConeHalfAngleDegrees)
			return Fail($"AwareHalfAngleDegrees must be in [{LockConeHalfAngleDegrees},90), " +
						$"got {AwareHalfAngleDegrees}");
		if (!FinitePositive(AwareRadius) || AwareRadius < LockConeLength)
			return Fail($"AwareRadius must cover LockConeLength " +
						$"({AwareRadius} vs {LockConeLength})");
		if (!float.IsFinite(MoveDeadzoneMps) || MoveDeadzoneMps < 0f)
			return Fail($"MoveDeadzoneMps must be finite and >= 0, got {MoveDeadzoneMps}");
		if (!FinitePositive(LockThresholdMeters) || !FinitePositive(AwareThresholdMeters) ||
			AwareThresholdMeters <= LockThresholdMeters)
			return Fail($"AwareThresholdMeters must exceed LockThresholdMeters " +
						$"({AwareThresholdMeters} vs {LockThresholdMeters})");
		if (!FinitePositive(LockStillHalfLifeSeconds) || !FinitePositive(LockOutHalfLifeSeconds) ||
			!FinitePositive(AwareStillHalfLifeSeconds) || !FinitePositive(AwareOutHalfLifeSeconds))
			return Fail("perception half-lives must be finite and positive");
		if (!float.IsFinite(SensitizeGainPerMeter) || SensitizeGainPerMeter < 0f ||
			!float.IsFinite(SensitizeMax) || SensitizeMax < 1f ||
			!FinitePositive(SensitizeHalfLifeSeconds))
			return Fail("sensitize params invalid (gain >= 0, max >= 1, half-life > 0)");
		if (!FinitePositive(LockHoldSeconds))
			return Fail($"LockHoldSeconds must be finite and positive, got {LockHoldSeconds}");
		if (!float.IsFinite(BearingErrorDegrees) || BearingErrorDegrees < 0f ||
			!float.IsFinite(RangeErrorFraction) || RangeErrorFraction < 0f)
			return Fail("perception noise params must be finite and >= 0");
		if (!FinitePositive(BeamTurnDegreesPerSecond))
			return Fail($"BeamTurnDegreesPerSecond must be finite and positive, " +
						$"got {BeamTurnDegreesPerSecond}");
		if (!FinitePositive(ProbeBudgetSeconds) ||
			!float.IsFinite(ProbeStretchCost) || ProbeStretchCost < 0f)
			return Fail("probe budget params invalid");
		if (!FinitePositive(ProbeHeadRadius) || ProbeHeadRadius >= _preset.Length)
			return Fail($"ProbeHeadRadius must stay below preset length " +
						$"({ProbeHeadRadius} vs {_preset.Length})");
		if (!float.IsFinite(StopBackoffRatio) || StopBackoffRatio is <= 0f or >= 1f)
			return Fail($"StopBackoffRatio must be in (0,1), got {StopBackoffRatio}");
		if (!FinitePositive(ProbeSpeedMps) || !FinitePositive(ProbeTurnBoost))
			return Fail("probe speed params must be finite and positive");
		if (!FinitePositive(DwellMinSeconds) || DwellMaxSeconds < DwellMinSeconds ||
			!FinitePositive(GazeMinSeconds) || GazeMaxSeconds < GazeMinSeconds)
			return Fail("dwell/gaze ranges invalid (max >= min > 0)");
		if (!float.IsFinite(LookbackProbability) || LookbackProbability is < 0f or > 1f)
			return Fail($"LookbackProbability must be in [0,1], got {LookbackProbability}");
		if (!FinitePositive(HypoPreySpeedMps) || !FinitePositive(SearchRadiusMax))
			return Fail("search envelope params must be finite and positive");
		if (!float.IsFinite(ProbeCooldownSeconds) || ProbeCooldownSeconds < 0f)
			return Fail($"ProbeCooldownSeconds must be finite and >= 0, got {ProbeCooldownSeconds}");
		// 咬空带提示：攻距 A = 拉伸×链长 应略小于 H + L，否则锁得到就必咬中。
		if (StrikeStretchFactor * _preset.Length >= ProbeHeadRadius + LockConeLength)
		{
			GD.Print($"[PLANT-ARENA] note: strike reach " +
					 $"{StrikeStretchFactor * _preset.Length:F1}m >= H+L " +
					 $"{ProbeHeadRadius + LockConeLength:F1}m; no whiff band at the edge");
		}
		if (!FinitePositive(BiteRadius))
			return Fail($"BiteRadius must be finite and positive, got {BiteRadius}");
		if (!float.IsFinite(BiteShoveSpeed) || BiteShoveSpeed < 0f)
			return Fail($"BiteShoveSpeed must be finite and >= 0, got {BiteShoveSpeed}");
		if (!FinitePositive(BittenPromptSeconds))
			return Fail($"BittenPromptSeconds must be finite and positive, got {BittenPromptSeconds}");
		if (!float.IsFinite(KickDegrees) || KickDegrees < 0f
			|| !float.IsFinite(KickOffsetMeters) || KickOffsetMeters < 0f)
		{
			return Fail($"kick amounts must be finite and >= 0, got " +
						$"{KickDegrees}/{KickOffsetMeters}");
		}
		if (!FinitePositive(KickFrequencyHz) || !FinitePositive(KickDecaySeconds))
			return Fail($"kick timing must be finite and positive, got " +
						$"{KickFrequencyHz}/{KickDecaySeconds}");
		// 触手全伸不能打穿地板视线之外的东西——只是提示性检查：房间净高 3.2m，
		// lurker Length 3.2m 恰好够到地面。
		if (_preset.Length > DaddyLongLegsSandbox.DaddyLongLegsMazeBuilder.RoomHeight + 0.5f)
		{
			GD.Print($"[PLANT-ARENA] note: preset length {_preset.Length:F1}m exceeds " +
					 $"room height; strikes will drag along the floor");
		}
		return true;

		static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;
		static bool Fail(string message)
		{
			GD.PushError($"[PLANT-ARENA] invalid scene configuration: {message}");
			return false;
		}
	}

	/// <summary>（重）建控制器与正式渲染件（R 重开安全）。出生即伪装。</summary>
	private void SpawnPlant()
	{
		ulong colliderId = GetNode<StaticBody3D>("Arena/ArenaCollision").GetInstanceId();
		var mount = new TentaclePlantMount(
			_mountPoint, Vector3.Down, Vector3.Right, colliderId);
		_plant = TentaclePlantFactory.CreateController(in mount, _preset, 0x414D4255534821UL);
		_plant.DisguiseIntent = true;
		_prevAttackSerial = _plant.AttackSerial;
		_lastBiteSerial = _plant.AttackSerial;

		_formal?.Clear();
		_formal = new TentaclePlantFormalRenderer(_plant, _preset.Name);
		_formal.Build(this);
		_formal.SetVisible(true);
		// 探照灯锥角 = 感知锁定锥角（可视化即设定本身）；射程盖过锥长即可。
		_formal.ConfigureSearchlight(LockConeHalfAngleDegrees, LockConeLength + 1.0f);
	}

	// ---- 固定步长循环（RatArena 同款累加器）----

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

	/// <summary>固定 tick 序：计数 → 绑定地形 → 相位喂输入 → 内核 Tick →
	/// 相位推进（读 Tick 后观测量：突刺沿、咬中判定、防御断言）。</summary>
	private void RunCoreTick()
	{
		_tick++;
		_terrain.Bind(GetWorld3D().DirectSpaceState);
		FeedPlant();
		_plant.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
		AdvancePhase();
	}

	// ---- 相位喂入（内核 Tick 之前写输入面）----

	private void FeedPlant()
	{
		// 调试观测的 tick 沿：本 tick 默认「没喂探测点」，由 FeedProbeTarget 置位。
		_probeTargetPrevActive = _probeTargetActive;
		_probeTargetActive = false;

		_playerAim = _player.EyePosition;
		if (!_playerAimInitialized)
		{
			_prevPlayerAim = _playerAim;
			_playerAimInitialized = true;
		}
		float moveAmount = (_playerAim - _prevPlayerAim).Length();
		_prevPlayerAim = _playerAim;

		// 光束朝向先于感知：按上一 tick 的相位/规划器状态选目标方向（1 tick
		// 滞后无妨），限速回转出"扫过去"的过程——触发性聆听的"急转"就是
		// 目标方向突变 + 这条回转斜坡。
		_beamDir = RotateToward(
			_beamDir,
			DesiredBeamDirection(),
			Mathf.DegToRad(BeamTurnDegreesPerSecond) / TicksPerSecond);
		_formal?.SetBeamAim(_beamDir, _phase == HostPhase.Ambush ? 0f : 1f);

		// 感知先于相位：锥轴 = 权威光束方向（tick 域，不消费渲染化妆状态），
		// 锁定锥/察觉区判定、累计与事件都在感知器内。
		TentaclePlantPerceptionEvents events = _perception.Tick(
			_plant.Hand.Pos,
			_beamDir,
			_playerAim,
			moveAmount,
			probing: _phase == HostPhase.Probe);

		switch (_phase)
		{
			case HostPhase.Ambush:
				_plant.DisguiseIntent = true;
				_plant.ProbeIntent = false;
				if (_perception.LockFresh)
				{
					// 锁定：喂真目标（最后感知点）——伪装态 ×10 充能，突袭由内核涌现。
					FeedAimTarget(hostVisible: _tick >= _attackReadyTick);
				}
				else
				{
					_plant.Target = null;
					if (events.ProbeRequested && _tick >= _probeReadyTick)
					{
						// 察觉累计过阈：放弃伪装、探头直奔最佳估计。
						_phase = HostPhase.Probe;
						_planner.Begin(
							_plant.Hand.Pos,
							_mountPoint,
							Vector3.Down,
							_perception.BestEstimate(FallbackEstimate()),
							_tick);
						GD.Print($"[PLANT-ARENA] probe begins t={_tick} " +
								 $"sens={_perception.Sensitize:F2}");
					}
				}
				break;

			case HostPhase.Probe:
				_plant.DisguiseIntent = false;
				_plant.ProbeIntent = true;
				if (_perception.LockFresh)
				{
					// 探头中锁定：预张紧 ×10 充能，~10 tick 出手。规划器冻结。
					FeedAimTarget(hostVisible: _tick >= _attackReadyTick);
				}
				else
				{
					if (events.ListenTwitch)
					{
						// 触发性聆听：光束急转照向新方位 + 长凝视，包络坍缩重铺。
						_planner.OnListen(
							_perception.BestEstimate(FallbackEstimate()), _tick);
					}
					if (_planner.TickActive(_tick))
					{
						FeedProbeTarget(_planner.ProbePoint);
					}
					else
					{
						// 预算耗尽：放弃搜索、回伪装。
						EndProbe("budget spent");
					}
				}
				break;

			case HostPhase.Engage:
				_plant.DisguiseIntent = false;
				_plant.ProbeIntent = true;
				if (_perception.LockFresh)
				{
					FeedAimTarget(hostVisible: _tick >= _attackReadyTick);
				}
				else
				{
					// 锁定过期：它只知道"置信度涨不上去"，转探头搜索。
					_phase = HostPhase.Probe;
					_planner.Begin(
						_plant.Hand.Pos,
						_mountPoint,
						Vector3.Down,
						_perception.BestEstimate(FallbackEstimate()),
						_tick);
					FeedProbeTarget(_planner.ProbePoint);
					GD.Print($"[PLANT-ARENA] lock lost -> probe t={_tick}");
				}
				break;
		}
	}

	/// <summary>探头收场：回伪装 + 压探头冷却（防边界横跳反复弹出）。</summary>
	private void EndProbe(string reason)
	{
		_phase = HostPhase.Ambush;
		_probeReadyTick = _tick + (long)MathF.Ceiling(
			ProbeCooldownSeconds * TicksPerSecond);
		_plant.ProbeIntent = false;
		_plant.DisguiseIntent = true;
		_plant.Target = null;
		GD.Print($"[PLANT-ARENA] probe ends ({reason}) t={_tick}");
	}

	/// <summary>
	/// 喂真目标：位置 = 最后感知点（非实时眼位）、速度 0——内核预测退化为
	/// 定点瞄准，"突刺瞄最后感知点"由此天然成立；极限拉扯下会咬空。
	/// </summary>
	private void FeedAimTarget(bool hostVisible)
	{
		// 恒 HostGrabbable=false：快咬弹开制——内核绝不建立抓持，
		// PositionCorrection 永远不会与玩家自驱打架；咬中判定见 AdvancePhase。
		_plant.Target = new TentaclePlantTargetSnapshot(
			PlayerStableId,
			_perception.AimPoint,
			Vector3.Zero,
			PlayerChunkRadius,
			1f,
			hostVisible,
			hostGrabbable: false);
	}

	/// <summary>
	/// 喂探测点：HostVisible=false 的合成快照——内核只把 goal 转向该点
	/// （零充能、零视线射线、wander RNG 冻结），头端张嘴缓慢跟随。
	/// </summary>
	private void FeedProbeTarget(Vector3 probePoint)
	{
		// 调试观测：新一轮探头的首 tick 没有上一位置，prev 直接取当前值免得从
		// 上一轮的残留点插值出一条飞线。
		_probeAimPrev = _probeTargetPrevActive ? _probeAimCurr : probePoint;
		_probeAimCurr = probePoint;
		_probeTargetActive = true;

		_plant.Target = new TentaclePlantTargetSnapshot(
			PlayerStableId,
			probePoint,
			Vector3.Zero,
			0.05f,
			1f,
			hostVisible: false,
			hostGrabbable: false);
	}

	/// <summary>感知零历史时的兜底估计：挂点正下方眼高处（实际不可达路径）。</summary>
	private Vector3 FallbackEstimate() =>
		new(_mountPoint.X, _playerAim.Y, _mountPoint.Z);

	/// <summary>
	/// 光束目标方向：锁定新鲜期照最后感知点（任何主动相位）、探头照规划器的
	/// 当前凝视点（假想猎物位置，非探测点——停头刻意停在它前方一截锥长处）、
	/// 伏击回落链末段推导（伪装 lerp 已把它对齐 Outward = 吸顶灯朝下）。
	/// </summary>
	private Vector3 DesiredBeamDirection()
	{
		Vector3 head = _plant.Hand.Pos;
		if (_phase != HostPhase.Ambush && _perception.LockFresh)
		{
			return SafeDirection(_perception.AimPoint - head, _beamDir);
		}
		if (_phase == HostPhase.Probe)
		{
			return SafeDirection(_planner.GazePoint - head, _beamDir);
		}
		return ComputeHeadForward();
	}

	/// <summary>把方向 from 朝 to 回转至多 maxRadians（单位向量进出）。</summary>
	private static Vector3 RotateToward(Vector3 from, Vector3 to, float maxRadians)
	{
		float angle = MathF.Acos(Mathf.Clamp(from.Dot(to), -1f, 1f));
		if (angle <= maxRadians)
		{
			return to;
		}
		Vector3 axis = from.Cross(to);
		if (axis.LengthSquared() < 1e-10f)
		{
			// 反向近共线：借任意正交轴起转。
			axis = from.Cross(Vector3.Up);
			if (axis.LengthSquared() < 1e-10f)
			{
				axis = from.Cross(Vector3.Right);
			}
		}
		return from.Rotated(axis.Normalized(), maxRadians).Normalized();
	}

	/// <summary>
	/// tick 域头部 forward：与渲染件同公式（末三段方向混合、按伪装度对齐挂点
	/// 法线）但无低通——感知行为不许依赖渲染帧率。
	/// </summary>
	private Vector3 ComputeHeadForward()
	{
		var segments = _plant.Segments;
		int count = segments.Count;
		Vector3 a = segments[count - 1].Pos - segments[count - 2].Pos;
		Vector3 b = count >= 3 ? segments[count - 2].Pos - segments[count - 3].Pos : a;
		Vector3 forward = SafeDirection(a * 0.6f + b * 0.4f, _plant.Outward);
		return SafeDirection(
			forward.Lerp(_plant.Outward,
				Mathf.Min(1f, _plant.DisguiseAmount * 1.25f)),
			forward);
	}

	private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
	{
		if (value.LengthSquared() > 1e-10f)
		{
			return value.Normalized();
		}
		return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Down;
	}

	// ---- 相位推进（内核 Tick 之后读观测量）----

	private void AdvancePhase()
	{
		// 突刺出手沿：压攻击冷却；任何相位出手都转入交战。
		if (_plant.AttackSerial != _prevAttackSerial)
		{
			_prevAttackSerial = _plant.AttackSerial;
			_attackReadyTick = _tick + Math.Max(1,
				(long)MathF.Ceiling(AttackCooldownSeconds * TicksPerSecond));
			if (_phase != HostPhase.Engage)
			{
				GD.Print($"[PLANT-ARENA] {(_phase == HostPhase.Ambush ? "ambush" : "probe")} " +
						 $"strike t={_tick} serial={_plant.AttackSerial}");
				_phase = HostPhase.Engage;
			}
		}

		// 咬中判定：Striking 期 Hand 距玩家眼位 ≤ BiteRadius，每次突刺只结算一次。
		// 注意突刺瞄的是"最后感知点"——急停侧移的玩家可以让它咬空。
		if (_plant.Phase == TentaclePlantPhase.Striking &&
			_lastBiteSerial != _plant.AttackSerial &&
			_plant.Hand.Pos.DistanceTo(_playerAim) <= BiteRadius)
		{
			_lastBiteSerial = _plant.AttackSerial;
			LandBite();
		}

		// 反射性咬合（脊髓反射，不经感知系统）：探头/交战期嘴撞到玩家直接咬，
		// 咬完立即回伪装——张开的颌内不该是安全屋。
		if (_phase != HostPhase.Ambush &&
			_plant.Phase is not (TentaclePlantPhase.Striking or TentaclePlantPhase.Holding) &&
			_tick >= _attackReadyTick &&
			_plant.Hand.Pos.DistanceTo(_playerAim) <= BiteRadius)
		{
			_attackReadyTick = _tick + Math.Max(1,
				(long)MathF.Ceiling(AttackCooldownSeconds * TicksPerSecond));
			LandBite();
			EndProbe("reflex bite");
		}

		// 防御断言：HostGrabbable=false 下内核不应建立任何抓持关系。
		if (_plant.TargetEffect.CaptureStarted || _plant.TargetEffect.Held)
		{
			GD.PushWarning("[PLANT-ARENA] unexpected capture with hostGrabbable=false; " +
						   "releasing defensively");
			_plant.ReleaseHeldTarget();
		}
	}

	/// <summary>咬中结算（象征性伤害）：计数 + 提示 + 镜头 kick + 背向推离。</summary>
	private void LandBite()
	{
		_biteCount++;
		_bittenPromptUntilTick = _tick
			+ (long)MathF.Ceiling(BittenPromptSeconds * TicksPerSecond);
		ShowToast($"BITTEN x{_biteCount}");
		_kick = 1f;
		if (BiteShoveSpeed > 0f)
		{
			Vector3 away = _playerAim - _plant.Hand.Pos;
			away.Y = 0f;
			if (away.LengthSquared() > 1e-8f)
			{
				// 必须走外部冲量通道：直写 Velocity 会被玩家「无输入即刻停」
				// 语义下一物理步一步归零（RatArena R18b 实证）。
				_player.AddImpulse(away.Normalized() * BiteShoveSpeed);
			}
		}
		GD.Print($"[PLANT-ARENA] bite lands count={_biteCount} t={_tick}");
	}

	private float HorizontalDistanceToMount(Vector3 point)
	{
		Vector3 delta = point - _mountPoint;
		delta.Y = 0f;
		return delta.Length();
	}

	private void ShowToast(string text)
	{
		_toastText = text;
		_toastTtl = 1.6f;
	}

	private void ResetRun()
	{
		_phase = HostPhase.Ambush;
		_attackReadyTick = 0;
		_probeReadyTick = 0;
		RebuildPerception();
		_biteCount = 0;
		_bittenPromptUntilTick = -1;
		_toastTtl = 0f;
		_kick = 0f;
		_kickTime = 0f;
		_player.SetCameraShake(Vector3.Zero, Vector3.Zero);
		_kickApplied = false;
		_playerAimInitialized = false;
		_probeTargetActive = false;
		_probeTargetPrevActive = false;
		_beamDir = Vector3.Down;
		SpawnPlant();
		_player.InputLocked = false;
		_player.Place(_arena.PlayerSpawn, _mountPoint);
		_player.SetActive(true); // 重新捕获鼠标（Esc 释放过也一并恢复）——RatArena 同款
		GD.Print($"[PLANT-ARENA] reset t={_tick}");
	}

	// ---- 渲染帧：kick + 渲染 + 灯光 + HUD ----

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

		// 调试覆盖层：头端取与渲染件同一 alpha 的插值位置（连线两端不打架），
		// 但锥轴取感知自己用的 tick 域光束方向——画出来的锥即判定用的那个。
		_debugDraw.Draw(
			_probeTargetActive,
			_plant.Hand.LerpPos(interpolation),
			_beamDir,
			_probeAimPrev.Lerp(_probeAimCurr, interpolation),
			_player.EyePosition);

		// 吸顶灯光随伪装程度走：揭露时熄灭，只剩嘴里灯泡的自发光。
		_lampLight.LightEnergy = Mathf.Lerp(0.15f, 2.5f, _plant.DisguiseAmount);

		UpdateHud((float)delta);
	}

	/// <summary>咬合镜头 kick：多条不可通约正弦 × 快衰减冲量包络（RatArena 同款）。</summary>
	private void UpdateCameraKick(float delta)
	{
		_kick *= MathF.Exp(-delta / MathF.Max(0.05f, KickDecaySeconds));
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

	private void UpdateHud(float delta)
	{
		_toastTtl = MathF.Max(0f, _toastTtl - delta);
		_hud.SetToast(_toastTtl > 0f ? _toastText : "");

		float cooldown = MathF.Max(0f, (_attackReadyTick - _tick) / TicksPerSecond);
		_hud.SetStatus(
			$"PLANT AMBUSH ARENA — host={_phase} plant={_plant.Phase} " +
			$"disguise={_plant.DisguiseAmount:F2} charge={_plant.AttackCharge:F2} " +
			$"cooldown={cooldown:F1}s " +
			$"dist={HorizontalDistanceToMount(_playerAim):F1}m bites={_biteCount}\n" +
			$"aware={_perception.Aware:F1}/{AwareThresholdMeters:F0} " +
			$"lock={_perception.Lock:F1}/{LockThresholdMeters:F0} " +
			$"sens={_perception.Sensitize:F2} fresh={(_perception.LockFresh ? 1 : 0)} " +
			$"budget={MathF.Max(0f, _planner.Budget) / TicksPerSecond:F1}s " +
			$"pa={_plant.ProbeAmount:F2} stretch={_plant.StretchAmount:F2}\n" +
			DebugReadoutLine() +
			"[R] restart  [F1] hud  [F3] debug  [Esc] mouse");

		if (_tick < _bittenPromptUntilTick)
			_hud.SetPrompt($"BITTEN x{_biteCount}");
		else
			_hud.SetPrompt("");
	}

	/// <summary>
	/// 调试读数（覆盖层关时为空串）：两锥几何 + 当前探测点坐标与头端到它的距离
	/// （= 伺服滞后；连线长度的数值版）。
	/// </summary>
	private string DebugReadoutLine()
	{
		if (!_debugDraw.Enabled)
		{
			return "";
		}
		string cones = $"dbg cone lock={LockConeHalfAngleDegrees:F0}°x{LockConeLength:F1}m " +
					   $"aware={AwareHalfAngleDegrees:F0}°x{AwareRadius:F1}m";
		if (!_probeTargetActive)
		{
			return $"{cones} probe=inactive (not probing)\n";
		}
		Vector3 point = _probeAimCurr;
		return $"{cones} probe=({point.X:F1},{point.Y:F1},{point.Z:F1}) " +
			   $"lag={_plant.Hand.Pos.DistanceTo(point):F2}m\n";
	}

	// ---- 输入 ----

	public override void _Input(InputEvent @event)
	{
		if (_fatal)
			return;
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
			case Key.F3:
				_debugDraw.Enabled = !_debugDraw.Enabled;
				GD.Print($"[PLANT-ARENA] debug overlay " +
						 $"{(_debugDraw.Enabled ? "on" : "off")}");
				break;
			case Key.Escape:
				Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
					? Input.MouseModeEnum.Visible
					: Input.MouseModeEnum.Captured;
				break;
		}
	}
}
