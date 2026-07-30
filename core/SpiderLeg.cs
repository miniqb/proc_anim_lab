using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>最近一次完整摆腿由哪类事件触发；只读输出供宿主调试与逐腿回归使用。</summary>
public enum SpiderStepReason
{
	None,
	Scheduled,
	ReachRecovery,
	Forced,
}

/// <summary>
/// 蜘蛛腿的独立 plant-and-trail 足端。足端是唯一参与运动/碰撞的粒子；膝点由足端姿态
/// 派生的解析式两段 IK 得到，不承力、不碰撞。它与 <see cref="Limb"/> 并列，既不复用
/// 蜥蜴步态状态，也不把腿的反力写回身体——蜘蛛控制器按真实抓地比例统一驱动身体。
///
/// 落脚搜索没有地面/墙面/天花板模式：直接射线、当前支撑面、上次抓握面、世界向下和
/// 局部扇形射线按固定顺序产生候选，统一选择离期望落点最近且腿长可达的真实命中。
/// </summary>
public sealed class SpiderLeg
{
	// —— 足端粒子态（Vel = 米/tick；LastPos 只供渲染插值与扫掠碰撞）——
	public Vector3 Pos;
	public Vector3 LastPos;
	public Vector3 Vel;
	public readonly float Radius;

	/// <summary>这条腿挂载的身体节。</summary>
	public readonly BodyChunk Anchor;

	/// <summary>左右侧：-1 左 / +1 右。</summary>
	public readonly int Side;

	/// <summary>腿对在品种表中的稳定序号；只用于确定性弯折回退与调试。</summary>
	public readonly int PairIndex;

	/// <summary>交替步态相位组。相位调度由 SpiderLocomotionController 统一完成。</summary>
	public int PhaseGroup;

	/// <summary>同一腿对的另一条腿，装配后由工厂互联。</summary>
	public SpiderLeg? Pair;

	// —— 根部与两段 IK 姿态输出 ——
	/// <summary>
	/// 根部相对身体锚点的局部偏移：X=沿 forward，Y=沿 supportNormal，
	/// Z=沿 right（right = forward × supportNormal）。
	/// </summary>
	public Vector3 RootOffset;

	/// <summary>在 RootOffset.Z 之外，按 <see cref="Side"/> 加到 right 轴上的左右对称偏移。</summary>
	public float RootLateralOffset;

	public Vector3 RootPos;
	public Vector3 LastRootPos;
	public Vector3 KneePos;
	public Vector3 LastKneePos;

	/// <summary>
	/// 从「根部→足端」轴指向膝盖弯折侧的单位方向。它是方向而非世界坐标点；
	/// 换面时每 tick 低通并保持同半球，避免膝盖瞬间翻到腿的另一侧。
	/// </summary>
	public Vector3 BendPole;

	// —— plant-and-trail 状态 ——
	/// <summary>当前足端追逐的世界点；有真实抓握时为射线命中的地形表面点。</summary>
	public Vector3 HuntPos;

	/// <summary>false=抬腿摆向下一步；true=正在找地形或踩住真实落点。</summary>
	public bool ReachingForTerrain = true;

	/// <summary>HuntPos 是否由本 tick/既有可达地形命中背书；空中搜索目标永远是 false。</summary>
	public bool HasGrip { get; private set; }

	/// <summary>本 tick 追逐积分是否到达 HuntPos 的吸附距离。</summary>
	public bool ReachedSnapPosition;

	/// <summary>真实落点的表面法线；任意朝向都允许成为蜘蛛支撑面。</summary>
	public Vector3 GripNormal = Vector3.Up;

	/// <summary>连续踩实真实落点的 tick 数。</summary>
	public int GripCounter;

	/// <summary>足端球本 tick 是否碰到地形。</summary>
	public bool TerrainContact;

	public bool Gripping => GripCounter >= Math.Max(1, GripDelay);

	/// <summary>
	/// 足端已经到达真实目标面并开始确认抓握；它尚未满足关重力/推进所需的 Gripping，
	/// 但可在步态仲裁里承担短暂支撑，避免四条腿都等待 GripDelay 后才允许下一条换步。
	/// </summary>
	internal bool PlantedForGait => ReachingForTerrain
		&& HasGrip
		&& TargetSurfaceContact
		&& GripCounter > 0;

	// —— 出生配置（工厂装配后运行时不再回读 breed resource）——
	/// <summary>
	/// 该腿对在身体平面中的名义扇形角（弧度）：0=正侧向，正值朝身体前方，
	/// 负值朝身体后方；左右腿始终由 Side 镜像，不会因负角跨到身体内侧。
	/// </summary>
	public float FanAngle = Mathf.DegToRad(55f);

	public float UpperLength = 0.3f;
	public float LowerLength = 0.32f;

	/// <summary>足端最大可达距离占上下腿总长的比例；小于 1 保证始终留有可见弯折。</summary>
	public float MaxReachFactor = 0.96f;

	public float HuntSpeed = 0.14f;
	public float Quickness = 0.65f;
	public int GripDelay = 3;

	/// <summary>步幅阈值 ∈[0,1]；越大，身体走过足端更远后才主动换步。</summary>
	public float StepLength = 0.65f;

	/// <summary>
	/// 足端沿前向落后腿根达到多少 MaxReach 后主动换步；-1 沿用 StepLength 的旧映射。
	/// 它只控制“何时抬腿”，不会缩短摆动目标和下一落点的前伸距离。
	/// </summary>
	public float TrailReleaseRatio = -1f;

	/// <summary>
	/// 下一 AEP 在抬腿瞬间相对 RootPos 的局部前向坐标，占 MaxReach 的比例。
	/// 横向/支撑向坐标沿用当前足端工作区；负值让后腿保留在后向工作区。
	/// </summary>
	public float TouchdownLeadRatio;

	/// <summary>false 保留逐 tick 静态扇形名义目标；true 在摆动期冻结世界 AEP。</summary>
	public bool UseExplicitTouchdownLead;

	/// <summary>静态扇形工作区中心额外沿身体前向偏置的无量纲权重。</summary>
	public float ForwardStepBias;

	/// <summary>摆动足端离开支撑面的抬起比例。</summary>
	public float LiftFeet = 0.25f;

	/// <summary>期望落点朝支撑面的偏置。</summary>
	public float FeetDown = 0.45f;

	/// <summary>名义落脚方向额外朝本侧展开的无量纲权重。</summary>
	public float PairLateral = 0.25f;

	/// <summary>在腿长搜索范围之外追加的射线冗余（米），不是投影射线的总长度。</summary>
	public float ProbeReach = 0.08f;

	/// <summary>
	/// 射线候选持续未踩到其真实目标面时，最多保留多少 tick。到期会在同 tick 排除旧点
	/// 重搜，避免“候选可达、足端路径却被另一面挡住”时永久占着 HasGrip。
	/// </summary>
	public int AcquisitionTimeoutTicks = 30;

	public float AirFriction = 0.72f;
	public float SurfaceFriction = 0.45f;

	/// <summary>RunSpeed 小于等于此值时不主动抬起已抓稳的脚。</summary>
	public const float MoveIntentDeadzone = 0.1f;
	public const float DefaultMaxReachFactor = 0.96f;

	private const float Skin = 0.02f;
	private const float OverlapPad = 0.025f;
	private const float TargetNormalDotThreshold = 0.7f;
	private const float PoleSmoothing = 0.2f;
	private const float SideCandidateSlackRatio = 0.4f;
	private const float SameSurfaceNormalDot = 0.85f;
	private const float StanceWidthRecovery = 0.6f;
	private const int MinimumSwingTicks = 3;
	private const int MaximumSwingTicks = 8;

	/// <summary>
	/// 名义扇形周围的固定补充采样。顺序也是等距候选的稳定优先级，禁止改为随机采样。
	/// </summary>
	private static readonly float[] LocalFanSamples =
	{
		Mathf.DegToRad(-14f),
		Mathf.DegToRad(14f),
		Mathf.DegToRad(-28f),
		Mathf.DegToRad(28f),
	};

	private Vector3 _lastGripNormal = Vector3.Up;
	private Vector3 _frameForward = Vector3.Left;
	private Vector3 _frameUp = Vector3.Up;
	private Vector3 _frameRight = Vector3.Back;
	private int _swingTicks;
	private int _acquisitionTicks;
	private Vector3 _swingLandingGoal;
	private Vector3 _swingHuntTarget;
	private bool _hasFrozenSwingTarget;
	private float _maxTargetNormalDot = -1f;
	private bool _hasFrame;
	private bool _hasSolvedIk;
	private Vector3 _lastIkAxis = Vector3.Forward;

	/// <summary>当前摆动期已经持续的 tick 数；抓地/到达新落点时为 0，供回归与调试只读。</summary>
	public int SwingTicks => _swingTicks;

	/// <summary>完整摆腿的单调序号；初始找地与未踩实候选重搜不计入。</summary>
	public int StepSerial { get; private set; }

	/// <summary>旧抓点超过真实可达环后进入完整恢复摆腿的累计次数。</summary>
	public int EmergencyStepCount { get; private set; }

	/// <summary>射线选中一个新真实落点的单调序号，包含出生找地与未踩实候选重搜。</summary>
	public int LandingSerial { get; private set; }

	public SpiderStepReason LastStepReason { get; private set; }

	/// <summary>当前摆动是否持有不会随腿根移动的世界落脚计划。</summary>
	public bool HasFrozenSwingTarget => _hasFrozenSwingTarget;

	/// <summary>当前冻结的地形表面期望点；仅在 HasFrozenSwingTarget 时参与未来 tick。</summary>
	public Vector3 SwingLandingGoal => _swingLandingGoal;

	/// <summary>当前冻结的抬腿追逐点；仅在 HasFrozenSwingTarget 时参与未来 tick。</summary>
	public Vector3 SwingHuntTarget => _swingHuntTarget;

	/// <summary>
	/// 当前局部工作区中，名义落点朝本侧展开的距离占 MaxReach 的比例。
	/// 它由扇角、PairLateral、FeetDown、ForwardStepBias 与 StepLength 共同派生，
	/// 供调试/回归区分“左右相等”与“两侧同时塌到身体附近”。
	/// </summary>
	public float NominalLateralReachRatio
	{
		get
		{
			float nominalReach = Mathf.Lerp(
				0.68f, 0.82f, Mathf.Clamp(StepLength, 0f, 1f));
			float outward =
				DesiredReachDirection().Dot(_frameRight * Side);
			return nominalReach * Mathf.Max(0f, outward);
		}
	}

	/// <summary>当前射线候选连续没有得到目标面真实接触背书的 tick 数。</summary>
	public int AcquisitionTicks => _acquisitionTicks;

	/// <summary>
	/// 本 tick 至少一个真实接触法线与 GripNormal 同向，且足端确实贴近 HuntPos。
	/// TerrainContact 可能只是撞到另一面；只有此值才允许累加 GripCounter。
	/// </summary>
	public bool TargetSurfaceContact { get; private set; }

	/// <summary>最近一次真实抓点法线；没有当前抓点时仍作为下一轮固定候选之一。</summary>
	public Vector3 LastGripNormal => _lastGripNormal;

	internal Vector3 FrameForward => _frameForward;
	internal Vector3 FrameUp => _frameUp;
	internal Vector3 FrameRight => _frameRight;
	internal bool HasFrame => _hasFrame;
	internal bool HasSolvedIk => _hasSolvedIk;
	public Vector3 LastIkAxis => _lastIkAxis;

	internal SpiderLeg(BodyChunk anchor, Vector3 initialFoot, float footRadius, int side, int pairIndex)
	{
		Anchor = anchor;
		Pos = initialFoot;
		LastPos = initialFoot;
		Vel = Vector3.Zero;
		Radius = footRadius;
		Side = side < 0 ? -1 : 1;
		PairIndex = pairIndex;
		PhaseGroup = pairIndex & 1;
		HuntPos = initialFoot;

		RootPos = anchor.Pos;
		LastRootPos = RootPos;
		BendPole = Vector3.Right * Side;
		KneePos = initialFoot;
		LastKneePos = KneePos;
	}

	/// <summary>
	/// 工厂应用完整腿配置后完成一次出生姿态求解，避免构造器默认腿长/默认根偏移污染
	/// 第一帧插值。此入口仅供同程序集装配器使用。
	/// </summary>
	internal void InitializePose(Vector3 forward, Vector3 supportNormal)
	{
		_hasFrame = false;
		_hasSolvedIk = false;
		UpdateFrame(forward, supportNormal);
		RootPos = Anchor.Pos
			+ _frameForward * RootOffset.X
			+ _frameUp * RootOffset.Y
			+ _frameRight * (RootOffset.Z + Side * RootLateralOffset);
		LastRootPos = RootPos;
		LastPos = Pos;
		HuntPos = Pos;
		GripNormal = _frameUp;
		_lastGripNormal = _frameUp;
		_acquisitionTicks = 0;
		_swingLandingGoal = Pos;
		_swingHuntTarget = Pos;
		_hasFrozenSwingTarget = false;
		StepSerial = 0;
		EmergencyStepCount = 0;
		LandingSerial = 0;
		LastStepReason = SpiderStepReason.None;
		TargetSurfaceContact = false;
		_maxTargetNormalDot = -1f;
		BendPole = _frameRight * Side;
		SolveKneePose();
		LastKneePos = KneePos;
	}

	/// <summary>
	/// 推进一个固定 tick。allowStep 只许可「主动」松开已抓稳的脚；旧落点已经超出腿长时
	/// 仍会无条件作废，否则保留抓地腿下限会把脚永久吊在不可达旧点。allLegs 由控制器
	/// 提供统一签名和调试上下文；相位/最小保留腿数的仲裁归控制器，不在单腿里复制。
	/// </summary>
	public void Tick(in TickContext ctx, Vector3 forward, Vector3 supportNormal,
		float runSpeed, bool allowStep, IReadOnlyList<SpiderLeg> allLegs)
	{
		PrepareTickFrame(forward, supportNormal);
		TickPrepared(ctx, runSpeed, allowStep, allLegs);
	}

	internal void PrepareTickFrame(Vector3 forward, Vector3 supportNormal)
	{
		LastRootPos = RootPos;
		LastKneePos = KneePos;
		UpdateFrame(forward, supportNormal);
		RootPos = Anchor.Pos
			+ _frameForward * RootOffset.X
			+ _frameUp * RootOffset.Y
			+ _frameRight * (RootOffset.Z + Side * RootLateralOffset);
	}

	internal void TickPrepared(in TickContext ctx,
		float runSpeed, bool allowStep, IReadOnlyList<SpiderLeg> allLegs)
	{
		_ = allLegs;

		if (ReachingForTerrain)
		{
			if (HasGrip && !GripRemainsReachable())
			{
				// 旧实现会清 HasGrip 后在同 tick 直接 FindGrip，脚端不抬起便追向附近
				// 新点，形成用户可见的高频碎步。超过可达环现在也必须走完整摆腿。
				BeginSwing(SpiderStepReason.ReachRecovery);
			}
			else if (HasGrip && Gripping && allowStep && runSpeed > MoveIntentDeadzone
				&& ShouldBeginStep())
			{
				BeginSwing(SpiderStepReason.Scheduled);
			}
			else if (!HasGrip)
			{
				FindGrip(ctx);
			}
		}

		if (!ReachingForTerrain)
		{
			TickSwing(ctx);
		}

		IntegrateHunt(ctx);
		ConnectToRoot();
		PushOutOfTerrain(ctx);
		EnsureReachableAfterTerrain(ctx);
		UpdateTargetSurfaceContact();

		if (ReachingForTerrain && HasGrip && TargetSurfaceContact
			&& GripRemainsReachable())
		{
			// “上次抓握法线”只接受足端已经抵达且碰到地形的证据；仅被射线看见、
			// 随后又放弃的瞬态候选不能污染下一轮搜索的历史面。
			_lastGripNormal = SafeNormal(GripNormal, _frameUp);
			GripCounter++;
			_acquisitionTicks = 0;
		}
		else
		{
			GripCounter = 0;
			if (ReachingForTerrain && HasGrip)
			{
				int timeout = Math.Max(1, AcquisitionTimeoutTicks);
				_acquisitionTicks = Math.Min(timeout, _acquisitionTicks + 1);
				if (_acquisitionTicks >= timeout)
				{
					Vector3 expiredPoint = HuntPos;
					Vector3 expiredNormal = SafeNormal(GripNormal, _frameUp);
					HasGrip = false;
					ReachedSnapPosition = false;
					TargetSurfaceContact = false;
					_acquisitionTicks = 0;
					FindGrip(ctx, expiredPoint, expiredNormal);
				}
			}
			else
			{
				_acquisitionTicks = 0;
			}
		}

		SolveKneePose();
	}

	/// <summary>整体世界平移：所有位置态与插值历史同步移动，速度、抓地和弯折方向不变。</summary>
	public void Shift(Vector3 delta)
	{
		Pos += delta;
		LastPos += delta;
		HuntPos += delta;
		RootPos += delta;
		LastRootPos += delta;
		KneePos += delta;
		LastKneePos += delta;
		_swingLandingGoal += delta;
		_swingHuntTarget += delta;
	}

	/// <summary>
	/// 强制松手并进入确定性摆动期（Teleport/Launch/顶死换步）。旧抓点当场作废，
	/// 膝点与 BendPole 保留连续性，下一 tick 由新根部和足端重新解析。
	/// </summary>
	public void ForceRelease()
	{
		BeginSwing(SpiderStepReason.Forced);
		TerrainContact = false;
		_maxTargetNormalDot = -1f;
	}

	private void BeginSwing(SpiderStepReason reason)
	{
		ReachingForTerrain = false;
		HasGrip = false;
		ReachedSnapPosition = false;
		GripCounter = 0;
		_swingTicks = 0;
		_acquisitionTicks = 0;
		TargetSurfaceContact = false;
		_hasFrozenSwingTarget = false;
		_swingLandingGoal = Pos;
		_swingHuntTarget = Pos;
		HuntPos = Pos;
		LastStepReason = reason;
		if (StepSerial < int.MaxValue)
		{
			StepSerial++;
		}
		if (reason == SpiderStepReason.ReachRecovery
			&& EmergencyStepCount < int.MaxValue)
		{
			EmergencyStepCount++;
		}
	}

	private void TickSwing(in TickContext ctx)
	{
		_swingTicks++;
		bool explicitAep = UseExplicitTouchdownLead;
		if (explicitAep && !_hasFrozenSwingTarget)
		{
			CaptureSwingTarget();
		}
		if (explicitAep)
		{
			HuntPos = _swingHuntTarget;
		}
		else
		{
			Vector3 desired = DesiredReachDirection();
			float forwardReach = Mathf.Lerp(
				0.62f, 0.82f, Mathf.Clamp(StepLength, 0f, 1f));
			HuntPos = RootPos
				+ desired * (MaxReach * forwardReach)
				+ _frameUp * (MaxReach * Mathf.Max(0f, LiftFeet) * 0.35f);
		}

		float near = Mathf.Max(Radius * 3f, HuntSpeed * 1.5f);
		if (_swingTicks >= MinimumSwingTicks
			&& ((HuntPos - Pos).Length() <= near || _swingTicks >= MaximumSwingTicks))
		{
			Vector3? preferredGoal = _hasFrozenSwingTarget
				? _swingLandingGoal
				: null;
			_hasFrozenSwingTarget = false;
			ReachingForTerrain = true;
			FindGrip(ctx, preferredGoal: preferredGoal);
		}
	}

	private void CaptureSwingTarget()
	{
		float reach = MaxReach;
		Vector3 landingCenter;
		if (UseExplicitTouchdownLead)
		{
			Vector3 rootToFoot = Pos - RootPos;
			Vector3 transverse = rootToFoot
				- _frameForward * rootToFoot.Dot(_frameForward);
			Vector3 outward = _frameRight * Side;
			float lateral = transverse.Dot(outward);
			Vector3 lastGripNormal = SafeNormal(_lastGripNormal, _frameUp);
			float surfaceAlignment = lastGripNormal.Dot(_frameUp);
			if (lateral < 0f && surfaceAlignment >= SameSurfaceNormalDot)
			{
				// 急转后旧脚会短暂落在新局部坐标的另一侧。只在抬腿捕获 AEP
				// 时、且仍是同一支撑面时先镜像回本侧；多面抓握不套用后续回收。
				transverse -= outward * (2f * lateral);
				lateral = -lateral;
			}
			if (surfaceAlignment >= SameSurfaceNormalDot
				&& Pair is { } pair)
			{
				Vector3 pairGripNormal =
					SafeNormal(pair._lastGripNormal, _frameUp);
				bool sameSupportSheet =
					pairGripNormal.Dot(_frameUp) >= SameSurfaceNormalDot
					&& lastGripNormal.Dot(pairGripNormal)
						>= SameSurfaceNormalDot;
				if (sameSupportSheet)
				{
					// 显式 AEP 不能永远复制转弯内侧留下的窄工作区。同一支撑面
					// 上每次只在抬腿时朝本槽位的名义宽度软回收；植脚仍固定，
					// 不同法线的窄墙抱边/棱角抓握也不会被拉回端面。
					float nominalReach = reach * Mathf.Lerp(
						0.68f, 0.82f, Mathf.Clamp(StepLength, 0f, 1f));
					float nominalLateral = nominalReach * Mathf.Max(
						0f, DesiredReachDirection().Dot(outward));
					float recoveredLateral = Mathf.Lerp(
						lateral, nominalLateral, StanceWidthRecovery);
					transverse +=
						outward * (recoveredLateral - lateral);
				}
			}
			float lead = reach * Mathf.Clamp(TouchdownLeadRatio, -1f, 1f);
			landingCenter = RootPos + transverse + _frameForward * lead;
		}
		else
		{
			Vector3 desired = DesiredReachDirection();
			float forwardReach = Mathf.Lerp(
				0.62f, 0.82f, Mathf.Clamp(StepLength, 0f, 1f));
			landingCenter = RootPos + desired * (reach * forwardReach);
		}

		landingCenter = ClampFootCenterToReach(landingCenter);
		_swingLandingGoal = landingCenter - _frameUp * Radius;
		float lift = reach * Mathf.Max(0f, LiftFeet) * 0.35f;
		_swingHuntTarget = ClampFootCenterToReach(
			landingCenter + _frameUp * lift);
		_hasFrozenSwingTarget = true;
	}

	private Vector3 ClampFootCenterToReach(Vector3 target)
	{
		Vector3 delta = target - RootPos;
		float distance = delta.Length();
		Vector3 direction = distance > 1e-6f
			? delta / distance
			: DesiredReachDirection();
		float clamped = Mathf.Clamp(distance, MinimumReach, MaxReach);
		return RootPos + direction * clamped;
	}

	private bool ShouldBeginStep()
	{
		return StepUrgency >= 1f;
	}

	internal float StepUrgency
	{
		get
		{
		float reach = MaxReach;
		float trail = (RootPos - Pos).Dot(_frameForward);
		float trailRatio = TrailReleaseRatio >= 0f
			? Mathf.Clamp(TrailReleaseRatio, 0f, 1f)
			: Mathf.Lerp(0.18f, 0.62f, Mathf.Clamp(StepLength, 0f, 1f));
		float trailThreshold = reach * trailRatio;
			float stretchThreshold = reach * Mathf.Lerp(0.82f, 0.96f,
				Mathf.Clamp(StepLength, 0f, 1f));
			float trailUrgency = trailThreshold > 1e-6f
				? trail / trailThreshold
				: 0f;
			float stretchUrgency = stretchThreshold > 1e-6f
				? (Pos - RootPos).Length() / stretchThreshold
				: 0f;
			return Mathf.Max(0f, Mathf.Max(trailUrgency, stretchUrgency));
		}
	}

	private bool GripRemainsReachable()
	{
		Vector3 contactCenter = HuntPos + SafeNormal(GripNormal, _frameUp) * Radius;
		float reach = (contactCenter - RootPos).Length();
		float tolerance = ReachTolerance;
		return reach >= Mathf.Max(0f, MinimumReach - tolerance)
			&& reach <= MaxReach + tolerance;
	}

	/// <summary>
	/// 固定候选顺序：
	/// 1. 根部朝名义落点直射；
	/// 2. 当前 SupportNormal 投影；
	/// 3. 本腿上次抓握法线投影；
	/// 4. 世界向下投影；
	/// 5. 名义角两侧及反支撑方向的局部扇形直射；
	/// 6. 沿本腿左右侧的面内法线投影，让窄面外的腿自然抱住相邻侧面。
	/// 常规类别只负责产生真实 TerrainHit，并统一按离 goal 最近选取；距离相等时严格保留
	/// 先出现的候选。最后的本侧投影单独保存：它可以发现本腿一侧的横向面，也可以发现
	/// 前方的正交新面；两者都必须落在 AEP 距离余量内，横向面还只能替换旧支撑面候选。
	/// 这样窄面/凸棱仍能抱边、前腿能先够到新面，普通墙角的远侧面不会无条件抢落点。
	/// </summary>
	private void FindGrip(
		in TickContext ctx,
		Vector3? rejectedPoint = null,
		Vector3? rejectedNormal = null,
		Vector3? preferredGoal = null)
	{
		Vector3 desired = DesiredReachDirection();
		// 期望点留在可达球内部：若把它放在最大半径球面上，再沿支撑法线投影到真实
		// 表面，任何非零高度差都会让命中落到球外而被可达性检查拒绝。
		float goalReach = MaxReach * Mathf.Lerp(0.68f, 0.82f,
			Mathf.Clamp(StepLength, 0f, 1f));
		Vector3 goal = preferredGoal ?? (
			RootPos + desired * goalReach
				- _frameUp * (MaxReach * 0.2f));
		// 候选面法线不同时，直接比较 hit.Point 会把脚半径误算成横向距离。
		// 统一比较实际足端球心；冻结 AEP 由捕获时的支撑方向还原到同一语义。
		Vector3 goalCenter = goal + _frameUp * Radius;
		Vector3 worldUp = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? -ctx.GravityPerTick.Normalized()
			: Vector3.Up;

		bool found = false;
		Vector3 bestPoint = default;
		Vector3 bestNormal = _frameUp;
		float bestDistanceSq = float.MaxValue;
		bool sideFound = false;
		Vector3 sidePoint = default;
		Vector3 sideNormal = default;
		float sideDistanceSq = float.MaxValue;
		bool sideIsForwardSurface = false;

		void Consider(TerrainHit hit, bool sideCandidate = false)
		{
			if (!IsFinite(hit.Point) || !IsFinite(hit.Normal)
				|| hit.Normal.LengthSquared() < 1e-12f)
			{
				return;
			}

			Vector3 normal = hit.Normal.Normalized();
			if (rejectedPoint is { } rejected
				&& rejectedNormal is { } rejectedSurfaceNormal)
			{
				float rejectRadius = Mathf.Max(Radius * 2f, Skin);
				if ((hit.Point - rejected).LengthSquared() <= rejectRadius * rejectRadius
					&& normal.Dot(SafeNormal(rejectedSurfaceNormal, normal))
						>= TargetNormalDotThreshold)
				{
					return;
				}
			}
			Vector3 contactCenter = hit.Point + normal * Radius;
			float reach = (contactCenter - RootPos).Length();
			float tolerance = ReachTolerance;
			if (reach < Mathf.Max(0f, MinimumReach - tolerance)
				|| reach > MaxReach + tolerance)
			{
				return;
			}

			float distanceSq = (contactCenter - goalCenter).LengthSquared();
			if (sideCandidate)
			{
				Vector3 sideReferenceUp = SafeNormal(_lastGripNormal, _frameUp);
				Vector3 sideReferenceForward = _frameForward
					- sideReferenceUp * _frameForward.Dot(sideReferenceUp);
				sideReferenceForward = SafeNormal(sideReferenceForward, _frameForward);
				Vector3 sideReferenceRight = SafeNormal(
					sideReferenceForward.Cross(sideReferenceUp), _frameRight);
				Vector3 contactOffset = contactCenter - Anchor.Pos;
				bool orthogonalSurface =
					Mathf.Abs(normal.Dot(sideReferenceUp)) <= 0.5f;
				bool forwardSurface =
					Mathf.Abs(normal.Dot(sideReferenceForward)) >= 0.65f;
				bool lateralSurface = Mathf.Abs(normal.Dot(sideReferenceRight)) >= 0.75f
					&& Mathf.Abs(normal.Dot(sideReferenceUp)) <= 0.35f;
				bool onOwnSide = contactOffset.Dot(sideReferenceRight * Side) > 0f;
				if (!orthogonalSurface
					|| (!forwardSurface && (!lateralSurface || !onOwnSide))
					|| distanceSq >= sideDistanceSq - 1e-8f)
				{
					return;
				}
				sideDistanceSq = distanceSq;
				sidePoint = hit.Point;
				sideNormal = normal;
				sideIsForwardSurface = forwardSurface;
				sideFound = true;
				return;
			}

			// 1e-8m² 内视为同距，保留先到候选作为固定顺序 tie-break。
			if (distanceSq >= bestDistanceSq - 1e-8f)
			{
				return;
			}

			bestDistanceSq = distanceSq;
			bestPoint = hit.Point;
			bestNormal = normal;
			found = true;
		}

		if (ctx.Terrain.Raycast(RootPos,
				RootPos + desired * (MaxReach + ProbeReach + Radius), out TerrainHit direct))
		{
			Consider(direct);
		}

		ProjectCandidate(ctx.Terrain, goal, _frameUp, hit => Consider(hit));
		ProjectCandidate(ctx.Terrain, goal, SafeNormal(_lastGripNormal, _frameUp),
			hit => Consider(hit));
		// 外角处身体中心仍在旧墙外侧，直接从同一竖线向世界下方探测会落在墙顶之外。
		// 先沿当前支撑法线的反方向把探针移进旧面的轮廓，再用同一世界向下候选，
		// 前方腿即可先够到顶面；这是腿级几何采样，不引入墙/棱线状态。
		Vector3 worldProjectionGoal = goal - _frameUp * (MaxReach * 0.15f);
		ProjectCandidate(ctx.Terrain, worldProjectionGoal, worldUp, hit => Consider(hit));

		foreach (float offset in LocalFanSamples)
		{
			float angle = FanAngle + offset;
			Vector3 surfaceDir = _frameRight * (Side * Mathf.Cos(angle))
				+ _frameForward * Mathf.Sin(angle);
			Vector3 fanDir = surfaceDir
				+ _frameRight * (Side * PairLateral)
				- _frameUp * (0.3f * FeetDown);
			fanDir = SafeNormal(fanDir, desired);
			if (ctx.Terrain.Raycast(RootPos,
				RootPos + fanDir * (MaxReach + ProbeReach + Radius), out TerrainHit fanHit))
			{
				Consider(fanHit);
			}
		}

		// 反平行支撑面的端到端恢复：Teleport 后历史 support 仍可能是 Up，但附近只剩
		// 头顶天花板。这里仍属于固定局部扇形采样，只增加朝旧支撑法线外侧的射线；
		// 是否真是天花板完全由 TerrainHit 法线与同一可达环过滤决定，不引入地形模式。
		if (ctx.Terrain.Raycast(RootPos,
				RootPos + _frameUp * (MaxReach + ProbeReach + Radius),
				out TerrainHit oppositeSupportHit))
		{
			Consider(oppositeSupportHit);
		}
		foreach (float offset in LocalFanSamples)
		{
			float angle = FanAngle + offset;
			Vector3 surfaceDir = _frameRight * (Side * Mathf.Cos(angle))
				+ _frameForward * Mathf.Sin(angle);
			Vector3 upwardFan = SafeNormal(
				_frameUp + surfaceDir * 0.45f
					+ _frameRight * (Side * PairLateral * 0.1f),
				_frameUp);
			if (ctx.Terrain.Raycast(RootPos,
				RootPos + upwardFan * (MaxReach + ProbeReach + Radius),
				out TerrainHit upwardHit))
			{
				Consider(upwardHit);
			}
		}

		// 窄墙/柱体上，名义落点会越过当前支撑面的轮廓。沿该腿自己的横向从外向内
		// 反投影：左腿只扫左侧、右腿只扫右侧，避免跨身抓到对侧。它始终参与同一
		// 最近点比较，而不是等所有旧面候选失败；否则“勉强可达的端面点”会永久
		// 阻止更接近名义落点的侧面真实命中。
		//
		// 根部悬在旧面外侧时，直接用 goal 横扫仍可能位于凸棱前方。先把探针
		// 沿旧支撑法线压进轮廓少许，并用完整腿长向内扫描；这只改变“怎么看见”
		// 相邻面，TerrainHit 仍须通过同一可达环和后续目标面真实接触背书。
		Vector3 sideProjectionGoal = goal - _frameUp * (MaxReach * 0.25f);
		ProjectCandidate(ctx.Terrain, sideProjectionGoal, _frameRight * Side,
			hit => Consider(hit, sideCandidate: true),
			inwardReachFactor: 1f);

		if (sideFound)
		{
			Vector3 oldSupport = SafeNormal(_lastGripNormal, _frameUp);
			float slack = Mathf.Max(
				Radius * 2f, MaxReach * SideCandidateSlackRatio);
			float regularDistance = found ? Mathf.Sqrt(bestDistanceSq) : float.MaxValue;
			float sideDistance = Mathf.Sqrt(sideDistanceSq);
			bool regularIsOldSupport = found && bestNormal.Dot(oldSupport) >= 0.55f;
			bool withinLandingSlack = sideDistance <= regularDistance + slack;
			if (!found
				|| (sideIsForwardSurface && withinLandingSlack)
				|| (regularIsOldSupport && withinLandingSlack))
			{
				bestDistanceSq = sideDistanceSq;
				bestPoint = sidePoint;
				bestNormal = sideNormal;
				found = true;
			}
		}

		if (found)
		{
			HuntPos = bestPoint;
			GripNormal = bestNormal;
			HasGrip = true;
			_acquisitionTicks = 0;
			if (LandingSerial < int.MaxValue)
			{
				LandingSerial++;
			}
		}
		else
		{
			// 搜索目标可以位于空气中，但绝不冒充抓点；身体继续移动时每 tick 重新搜索。
			HuntPos = goal;
			HasGrip = false;
			_acquisitionTicks = 0;
		}
	}

	private void ProjectCandidate(ITerrainQuery terrain, Vector3 goal, Vector3 normal,
		Action<TerrainHit> consider, float inwardReachFactor = 0.75f)
	{
		Vector3 n = SafeNormal(normal, _frameUp);
		// ProbeReach 是「腿长之外的冗余」，不是整根射线长度。向面一侧至少覆盖大半
		// 条腿，才能从最大可达球内部的 goal 穿过地面/墙面；命中后仍由 MaxReach 做
		// 真实可达性过滤，不会因为探针变长而造出远处假抓点。
		float outward = Mathf.Max(ProbeReach + Radius * 2f, Radius * 2f + Skin);
		float inward = MaxReach * Mathf.Max(0.75f, inwardReachFactor)
			+ Mathf.Max(0f, ProbeReach);
		Vector3 from = goal + n * outward;
		Vector3 to = goal - n * inward;
		if (terrain.Raycast(from, to, out TerrainHit hit))
		{
			consider(hit);
		}
	}

	private Vector3 DesiredReachDirection()
	{
		Vector3 surfaceDir = _frameRight * (Side * Mathf.Cos(FanAngle))
			+ _frameForward * Mathf.Sin(FanAngle);
		Vector3 desired = surfaceDir
			+ _frameForward * Mathf.Max(0f, ForwardStepBias)
			+ _frameRight * (Side * PairLateral)
			- _frameUp * (0.3f * FeetDown);
		return SafeNormal(desired, _frameForward);
	}

	/// <summary>足端追 HuntPos 积分；根部约束与最终地形解算由调用者按固定顺序随后执行。</summary>
	private void IntegrateHunt(in TickContext ctx)
	{
		_ = ctx;
		float huntSpeed = Mathf.Max(0.001f, HuntSpeed) + Anchor.Vel.Length();
		LastPos = Pos;
		Vector3 toTarget = HuntPos - Pos;
		if (toTarget.Length() < huntSpeed)
		{
			Vel = toTarget;
			ReachedSnapPosition = true;
		}
		else
		{
			Vector3 desiredVel = toTarget.Normalized() * huntSpeed;
			Vel = Vel.Lerp(desiredVel, Mathf.Clamp(Quickness, 0f, 1f));
			ReachedSnapPosition = false;
		}

		Pos += Vel;
		Vel *= AirFriction;
	}

	/// <summary>
	/// 足端只被根部单侧拉住，不反推身体。上限小于上下腿总长，确保 IK 不会完全拉直；
	/// 下限处理上下腿不等长时的几何不可解区。
	/// </summary>
	private void ConnectToRoot()
	{
		Vector3 delta = Pos - RootPos;
		float distance = delta.Length();
		float maxReach = MaxReach;
		float minReach = MinimumReach;

		if (distance > maxReach)
		{
			Vector3 correction = delta / distance * (distance - maxReach);
			Pos -= correction;
			Vel -= correction;
		}
		else if (distance < minReach)
		{
			Vector3 dir = distance > 1e-6f
				? delta / distance
				: DesiredReachDirection();
			Vector3 correction = dir * (minReach - distance);
			Pos += correction;
			Vel += correction;
		}
	}

	private void PushOutOfTerrain(in TickContext ctx)
	{
		TerrainContact = false;
		TargetSurfaceContact = false;
		_maxTargetNormalDot = -1f;

		Vector3 motion = Pos - LastPos;
		float motionLength = motion.Length();
		if (motionLength > 1e-6f)
		{
			Vector3 direction = motion / motionLength;
			if (ctx.Terrain.Raycast(LastPos, Pos + direction * Radius, out TerrainHit sweep)
				&& SphereTerrain.Resolve(sweep, Radius, SurfaceFriction,
					ref Pos, ref Vel, out Vector3 sweepNormal))
			{
				RecordTerrainContact(sweepNormal);
			}
		}

		if (ctx.Terrain.Raycast(Pos, Pos - _frameUp * (Radius + Skin), out TerrainHit support)
			&& SphereTerrain.Resolve(support, Radius, SurfaceFriction,
				ref Pos, ref Vel, out Vector3 supportNormal))
		{
			RecordTerrainContact(supportNormal);
		}

		Vector3 gripNormal = SafeNormal(GripNormal, _frameUp);
		if (gripNormal.Dot(_frameUp) < 0.999f
			&& ctx.Terrain.Raycast(Pos, Pos - gripNormal * (Radius + Skin), out TerrainHit grip)
			&& SphereTerrain.Resolve(grip, Radius, SurfaceFriction,
				ref Pos, ref Vel, out Vector3 gripContactNormal))
		{
			RecordTerrainContact(gripContactNormal);
		}

		if (ctx.Terrain.SpherePenetration(Pos, Radius, out Vector3 pushDir, out float depth))
		{
			Pos += pushDir * depth;
			SphereTerrain.RespondVelocity(pushDir, SurfaceFriction, ref Vel);
			RecordTerrainContact(pushDir);
		}
	}

	private void RecordTerrainContact(Vector3 normal)
	{
		if (!IsFinite(normal) || normal.LengthSquared() < 1e-12f)
		{
			return;
		}
		TerrainContact = true;
		Vector3 targetNormal = SafeNormal(GripNormal, _frameUp);
		_maxTargetNormalDot = Mathf.Max(
			_maxTargetNormalDot, normal.Normalized().Dot(targetNormal));
	}

	private void UpdateTargetSurfaceContact()
	{
		TargetSurfaceContact = false;
		if (!ReachingForTerrain || !HasGrip || !TerrainContact
			|| !OverlappingHuntPos())
		{
			return;
		}
		TargetSurfaceContact = _maxTargetNormalDot >= TargetNormalDotThreshold;
	}

	/// <summary>
	/// 地形 MTD 优先保证足端不穿透；若它把足端推出可达球，则旧抓点已经与腿长冲突，
	/// 必须松手。先做固定次数的「可达球投影→MTD」交替投影；若局部几何确实不可行，
	/// 把足端放到根部沿支撑外法线的确定性空中休息点，绝不以穿透换取假抓地。
	/// </summary>
	private void EnsureReachableAfterTerrain(in TickContext ctx)
	{
		float tolerance = ReachTolerance;
		float initialDistance = (Pos - RootPos).Length();
		if (initialDistance >= MinimumReach - tolerance
			&& initialDistance <= MaxReach + tolerance)
		{
			return;
		}

		// 已经在摆动时，MTD 可能连续数 tick 把脚推出可达环；继续同一次摆动，
		// 不重置冻结 AEP/计时，也不把一个恢复 episode 重复计成许多紧急步。
		if (ReachingForTerrain)
		{
			BeginSwing(SpiderStepReason.ReachRecovery);
		}
		for (int iteration = 0; iteration < 4; iteration++)
		{
			Vector3 delta = Pos - RootPos;
			float distance = delta.Length();
			if (distance > MaxReach && distance > 1e-6f)
			{
				Vector3 correction = delta / distance * (distance - MaxReach);
				Pos -= correction;
				Vel -= correction;
			}
			else if (distance < MinimumReach)
			{
				Vector3 direction = distance > 1e-6f
					? delta / distance
					: DesiredReachDirection();
				Vector3 correction = direction * (MinimumReach - distance);
				Pos += correction;
				Vel += correction;
			}
			if (ctx.Terrain.SpherePenetration(Pos, Radius,
					out Vector3 pushDir, out float depth))
			{
				Pos += pushDir * depth;
				SphereTerrain.RespondVelocity(pushDir, SurfaceFriction, ref Vel);
				RecordTerrainContact(pushDir);
			}
			float solvedDistance = (Pos - RootPos).Length();
			if (solvedDistance >= MinimumReach - tolerance
				&& solvedDistance <= MaxReach + tolerance
				&& !ctx.Terrain.SpherePenetration(Pos, Radius, out _, out _))
			{
				HuntPos = Pos;
				return;
			}
		}

		float restDistance = (MinimumReach + MaxReach) * 0.5f;
		Vector3 away = SafeNormal(_frameUp + SafeNormal(GripNormal, _frameUp), _frameUp);
		ITerrainQuery terrain = ctx.Terrain;
		bool TryRest(Vector3 direction, out Vector3 point)
		{
			point = RootPos + SafeNormal(direction, away) * restDistance;
			return !terrain.SpherePenetration(point, Radius, out _, out _);
		}

		bool foundRest = TryRest(away, out Vector3 candidate)
			|| TryRest(_frameUp, out candidate)
			|| TryRest(SafeNormal(GripNormal, _frameUp), out candidate)
			|| TryRest(_frameUp + _frameRight * Side, out candidate);
		if (!foundRest)
		{
			// 极端嵌入：仍以不穿透优先，下一 tick 身体 MTD 会继续恢复根部可行域。
			candidate = RootPos + away * restDistance;
			if (ctx.Terrain.SpherePenetration(candidate, Radius,
					out Vector3 finalPush, out float finalDepth))
			{
				candidate += finalPush * finalDepth;
			}
		}
		Pos = candidate;
		Vel = Anchor.Vel;
		HuntPos = Pos;
		ReachedSnapPosition = false;
		TerrainContact = false;
		TargetSurfaceContact = false;
		_maxTargetNormalDot = -1f;
	}

	/// <summary>
	/// 两球交线式两段 IK。KneePos 每 tick 都严格满足 UpperLength/LowerLength；
	/// 平滑的是 BendPole，而非事后 Lerp 膝点，因此换面连续性不会破坏骨段长度。
	/// </summary>
	private void SolveKneePose()
	{
		float upper = Mathf.Max(UpperLength, 1e-4f);
		float lower = Mathf.Max(LowerLength, 1e-4f);
		Vector3 rootToFoot = Pos - RootPos;
		float distance = rootToFoot.Length();
		Vector3 axis = distance > 1e-6f
			? rootToFoot / distance
			: SafeNormal(DesiredReachDirection(), _frameForward);

		float solvedDistance = Mathf.Clamp(
			distance, MinimumReach, TheoreticalMaximumReach);

		float slotSign = ((PairIndex & 1) == 0 ? 1f : -1f) * Side;
		Vector3 targetPole = _frameRight * Side
			+ _frameForward * (0.16f * slotSign);
		targetPole = RejectFromAxis(targetPole, axis);
		if (targetPole.LengthSquared() < 1e-10f)
		{
			// 腿轴与侧向共线时，用腿槽位决定沿支撑法线的稳定回退侧。
			targetPole = RejectFromAxis(_frameUp * slotSign, axis);
		}
		targetPole = SafeNormal(targetPole, StablePerpendicular(axis));

		Vector3 pole;
		if (!_hasSolvedIk)
		{
			pole = targetPole;
		}
		else
		{
			Vector3 projectedPrevious = RejectFromAxis(BendPole, axis);
			Vector3 previous = projectedPrevious.LengthSquared() > 2.5e-3f
				? projectedPrevious.Normalized()
				: ParallelTransportPole(
					BendPole, _lastIkAxis, axis, targetPole, slotSign);
			Vector3 poleTarget = targetPole;
			if (previous.Dot(targetPole) < -0.95f)
			{
				// 精确反向时直接 Nlerp 会在零向量两侧锁死。按左右侧×腿槽位选择
				// 唯一 90° 中继方向，连续绕回真正的 outward target，而不是把 target
				// 永久取反后把膝锁到身体内侧。
				poleTarget = SafeNormal(axis.Cross(targetPole) * slotSign,
					StablePerpendicular(axis));
			}
			pole = SafeNormal(previous.Lerp(poleTarget, PoleSmoothing), previous);
		}
		BendPole = pole;

		float along = (upper * upper - lower * lower + solvedDistance * solvedDistance)
			/ (2f * solvedDistance);
		float heightSq = Mathf.Max(0f, upper * upper - along * along);
		float height = Mathf.Sqrt(heightSq);
		KneePos = RootPos + axis * along + pole * height;
		_lastIkAxis = axis;
		_hasSolvedIk = true;
	}

	private Vector3 ParallelTransportPole(Vector3 pole, Vector3 oldAxis, Vector3 newAxis,
		Vector3 fallback, float slotSign)
	{
		oldAxis = SafeNormal(oldAxis, newAxis);
		newAxis = SafeNormal(newAxis, oldAxis);
		float cosine = Mathf.Clamp(oldAxis.Dot(newAxis), -1f, 1f);
		Vector3 cross = oldAxis.Cross(newAxis);
		float sine = cross.Length();
		Vector3 transported;
		if (sine > 1e-5f)
		{
			Vector3 rotationAxis = cross / sine;
			transported = pole * cosine
				+ rotationAxis.Cross(pole) * sine
				+ rotationAxis * rotationAxis.Dot(pole) * (1f - cosine);
		}
		else if (cosine >= 0f)
		{
			transported = pole;
		}
		else
		{
			Vector3 rotationAxis = RejectFromAxis(
				_frameUp * slotSign + _frameRight * Side, oldAxis);
			rotationAxis = SafeNormal(rotationAxis, StablePerpendicular(oldAxis));
			transported = -pole + rotationAxis * (2f * rotationAxis.Dot(pole));
		}

		transported = RejectFromAxis(transported, newAxis);
		return SafeNormal(transported, fallback);
	}

	private void UpdateFrame(Vector3 forward, Vector3 supportNormal)
	{
		Vector3 up = SafeNormal(supportNormal, _hasFrame ? _frameUp : Vector3.Up);
		Vector3 tangent = forward - up * forward.Dot(up);
		if (tangent.LengthSquared() < 1e-10f && _hasFrame)
		{
			tangent = _frameForward - up * _frameForward.Dot(up);
		}
		if (tangent.LengthSquared() < 1e-10f)
		{
			Vector3 fallback = Mathf.Abs(up.Dot(Vector3.Left)) < 0.9f
				? Vector3.Left
				: Vector3.Forward;
			tangent = fallback - up * fallback.Dot(up);
		}

		_frameForward = tangent.Normalized();
		_frameUp = up;
		_frameRight = SafeNormal(_frameForward.Cross(_frameUp),
			_hasFrame ? _frameRight : Vector3.Right);
		_hasFrame = true;
	}

	private Vector3 StablePerpendicular(Vector3 axis)
	{
		Vector3 side = RejectFromAxis(_frameRight * Side, axis);
		if (side.LengthSquared() >= 1e-10f)
		{
			return side.Normalized();
		}
		float slotSign = ((PairIndex & 1) == 0 ? 1f : -1f) * Side;
		Vector3 slot = _frameUp * slotSign;
		slot = RejectFromAxis(slot, axis);
		if (slot.LengthSquared() >= 1e-10f)
		{
			return slot.Normalized();
		}
		Vector3 fallback = Mathf.Abs(axis.Dot(Vector3.Up)) < 0.9f
			? Vector3.Up
			: Vector3.Right;
		return RejectFromAxis(fallback * Side, axis).Normalized();
	}

	private static Vector3 RejectFromAxis(Vector3 value, Vector3 axis)
	{
		return value - axis * value.Dot(axis);
	}

	private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
	{
		if (IsFinite(value) && value.LengthSquared() >= 1e-12f)
		{
			return value.Normalized();
		}
		if (IsFinite(fallback) && fallback.LengthSquared() >= 1e-12f)
		{
			return fallback.Normalized();
		}
		return Vector3.Up;
	}

	private static bool IsFinite(Vector3 value)
	{
		return float.IsFinite(value.X)
			&& float.IsFinite(value.Y)
			&& float.IsFinite(value.Z);
	}

	private bool OverlappingHuntPos()
	{
		return (HuntPos - Pos).Length() <= Radius + OverlapPad;
	}

	internal static void CalculateReachBounds(
		float upperLength,
		float lowerLength,
		float maxReachFactor,
		out float minimumReach,
		out float maximumReach,
		out float theoreticalMaximumReach)
	{
		float upper = Mathf.Max(upperLength, 1e-4f);
		float lower = Mathf.Max(lowerLength, 1e-4f);
		float margin = Mathf.Max(1e-6f, Mathf.Min(upper, lower) * 0.01f);
		minimumReach = Mathf.Abs(upper - lower) + margin;
		theoreticalMaximumReach = upper + lower - margin;
		float factor = Mathf.Clamp(maxReachFactor, 0.5f, 0.995f);
		maximumReach = Mathf.Clamp(
			(upper + lower) * factor,
			minimumReach + margin,
			theoreticalMaximumReach);
	}

	/// <summary>两段腿长度差形成的不可达内半径；足端始终被约束在此值之外。</summary>
	public float MinimumReach
	{
		get
		{
			CalculateReachBounds(
				UpperLength, LowerLength, MaxReachFactor,
				out float minimumReach, out _, out _);
			return minimumReach;
		}
	}

	/// <summary>解析 IK 的理论外边界；比上下腿总长少一个与短骨段相对的稳定 margin。</summary>
	public float TheoreticalMaximumReach
	{
		get
		{
			CalculateReachBounds(
				UpperLength, LowerLength, MaxReachFactor,
				out _, out _, out float theoreticalMaximumReach);
			return theoreticalMaximumReach;
		}
	}

	public float MaxReach
	{
		get
		{
			CalculateReachBounds(
				UpperLength, LowerLength, MaxReachFactor,
				out _, out float maximumReach, out _);
			return maximumReach;
		}
	}

	private float ReachTolerance
	{
		get
		{
			float upper = Mathf.Max(UpperLength, 1e-4f);
			float lower = Mathf.Max(LowerLength, 1e-4f);
			return Mathf.Max(1e-8f, (upper + lower) * 1e-5f);
		}
	}

	public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
	public Vector3 LerpRoot(float t) => LastRootPos.Lerp(RootPos, t);
	public Vector3 LerpKnee(float t) => LastKneePos.Lerp(KneePos, t);
}
