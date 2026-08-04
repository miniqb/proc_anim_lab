using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Deer;

/// <summary>
/// Deer 独立运动后端：头 + 大幅重叠的粗躯干链 + 轻鹿角块 + 四条专属多节腿。
/// 腿只汇报连续支撑量；重力始终开启，控制器再把不均匀升力与推进分发给头/躯干。
/// 没有 locomotion 模式枚举，行进、犹豫与休息全部是连续量。
/// </summary>
public sealed class DeerLocomotionController
{
	public readonly Body Body;
	public readonly BodyChunk Head;
	public readonly IReadOnlyList<BodyChunk> Trunk;
	public readonly BodyChunk Antler;
	private readonly List<DeerLeg> _legs = new();
	private readonly IReadOnlyList<DeerLeg> _legsView;
	public IReadOnlyList<DeerLeg> Legs => _legsView;

	public string StableId => _params.StableId;

	// —— 与其他并列后端同名同义的宿主输入 ——
	public Vector3 MoveDir;
	public float RunSpeed;
	public Vector3? MoveTarget;
	public float MoveTargetArriveRadius;
	public bool AtMoveTarget { get; private set; }

	public bool HasMoveIntent => Mathf.Clamp(RunSpeed, 0f, 1f) > _params.MoveIntentDeadzone
		&& (MoveTarget is not null ? !AtMoveTarget : MoveDir.LengthSquared() > 1e-10f);

	// —— 调试/宿主观测面 ——
	public Vector3 Forward => _forward;
	public Vector3 Up => _up;
	public Vector3 Right => _right;
	public Vector3 SupportNormal { get; private set; } = Vector3.Up;
	public Vector3 BodyCenter => ComputeBodyCenter();
	public float RawSupport { get; private set; }
	public float TotalSupport { get; private set; }
	public float ForwardPower { get; private set; }
	public float Hesitation { get; private set; }
	public float DriveScale { get; private set; }
	public float RestAmount { get; private set; }
	public int IdleTicks { get; private set; }
	public float CurrentLegReachScale { get; private set; } = 1f;
	public float DesiredBodyHeight { get; private set; }
	public float CurrentRideHeight { get; private set; }
	public float ActualBodyHeight => CurrentRideHeight;
	public bool HasAheadFloor { get; private set; }
	public Vector3 AheadFloorPoint { get; private set; }
	public Vector3 AheadFloorNormal { get; private set; } = Vector3.Up;
	public bool HasCurrentFloor { get; private set; }
	public Vector3 CurrentFloorPoint { get; private set; }
	public int FloorMissTicks { get; private set; }
	public int PlantedLegCount { get; private set; }
	public int LegsGripping => PlantedLegCount;
	public int StallTicks { get; private set; }
	public int VoluntaryReleaseSerial { get; private set; }
	public float BodyDragImpulse { get; private set; }
	/// <summary>
	/// 身体质量中心相对踩实足端中心的支撑面内偏移。球链没有可观测的轴向自转自由度，
	/// 因而这里衡量的是整体倾覆/侧倾，而不是虚构一个刚体 roll 姿态。
	/// </summary>
	public Vector3 BalanceOffset { get; private set; }

	/// <summary>质量中心投影到真实足端凸包边界的有符号距离；正值在内，负值在外。</summary>
	public float SupportMargin { get; private set; }

	/// <summary>质量中心相对支撑中心的等价倾角（度）。</summary>
	public float LeanDegrees { get; private set; }
	public float ToppleOffset { get; private set; }
	public float SupportHalfWidth { get; private set; }
	public float SupportHalfLength { get; private set; }
	/// <summary>
	/// 本实例自出生以来，任一腿对连续没有 AttachedAtTip 的历史最长 tick 数。
	/// PairAirborneTicks 才是当前连续值；Teleport/Launch 清当前值但保留这个诊断高水位。
	/// </summary>
	public int MaxPairAirborneRun { get; private set; }
	public IReadOnlyList<int> PairAirborneTicks => _pairAirborneTicks;
	public MoveTargetKind LastMoveTargetKind { get; private set; }
	public Vector3 LastMoveTarget { get; private set; }

	// 专项 smoke 的消融开关；正式预设一律保持 true。它们只切断对应机制，不另开行为模式。
	public bool EnableSupport = true;
	public bool EnablePairInterlock = true;
	public bool EnableHesitation = true;
	public bool EnableStepRelease = true;
	public bool EnableBalanceRecovery = true;
	public bool EnableAntlerPosture = true;
	public bool EnableAnatomicalBend = true;

	private readonly record struct SupportSnapshot(
		int Planted,
		float Raw,
		float Target,
		Vector3 WeightedNormal,
		Vector3 BalanceOffset,
		float LateralOffset,
		float HalfWidth,
		float HalfLength,
		float Margin,
		bool HasArea,
		float LeanDegrees);

	private readonly record struct SupportPoint(float X, float Y, int LegIndex);

	// 内核边界只允许 Godot.Vector3/Mathf。支撑凸包所需的二维运算保持为纯 float，
	// 不把场景/GUI 常用的 Godot.Vector2 引入可移植程序集。
	private readonly record struct PlanarPoint(float X, float Y)
	{
		public static PlanarPoint Zero => new(0f, 0f);
		public float LengthSquared() => X * X + Y * Y;
		public float Dot(PlanarPoint other) => X * other.X + Y * other.Y;
		public float DistanceSquaredTo(PlanarPoint other) => (other - this).LengthSquared();
		public static PlanarPoint operator +(PlanarPoint a, PlanarPoint b) =>
			new(a.X + b.X, a.Y + b.Y);
		public static PlanarPoint operator -(PlanarPoint a, PlanarPoint b) =>
			new(a.X - b.X, a.Y - b.Y);
		public static PlanarPoint operator *(PlanarPoint value, float scale) =>
			new(value.X * scale, value.Y * scale);
	}

	private readonly DeerParams _params;
	private readonly float[] _supportWeights;
	private readonly float[] _driveWeights;
	private readonly float[] _postureWeights;
	// 腿拓扑在出生后固定；支撑凸包每 tick 复用实例私有 scratch，避免稳态 GC。
	private readonly SupportPoint[] _supportPointScratch;
	private readonly SupportPoint[] _uniqueSupportPointScratch;
	private readonly SupportPoint[] _supportHullScratch;
	private readonly int[] _pairAirborneTicks = new int[2];
	private Vector3 _forward;
	private Vector3 _up = Vector3.Up;
	private Vector3 _right;
	private bool _hasSmoothedFloor;
	private float _smoothedFloorHeight;
	private Vector3 _worldUp = Vector3.Up;
	private Vector3 _previousCenter;
	private bool _hasPreviousCenter;

	internal DeerLocomotionController(
		Body body,
		BodyChunk head,
		IReadOnlyList<BodyChunk> trunk,
		BodyChunk antler,
		DeerParams parameters,
		Vector3 initialForward)
	{
		Body = body;
		Head = head;
		Trunk = trunk;
		Antler = antler;
		_params = parameters;
		_legsView = _legs.AsReadOnly();
		MoveTargetArriveRadius = parameters.ArriveRadius;

		_forward = NormalizeOr(initialForward, Vector3.Forward);
		_right = NormalizeOr(_forward.Cross(_up), Vector3.Right);
		DesiredBodyHeight = parameters.PreferredBodyHeight;
		CurrentRideHeight = parameters.PreferredBodyHeight;

		_supportWeights = new float[trunk.Count + 1];
		_driveWeights = new float[trunk.Count + 1];
		_postureWeights = new float[trunk.Count + 1];
		int legCapacity = parameters.LegSlots.Length;
		_supportPointScratch = new SupportPoint[legCapacity];
		_uniqueSupportPointScratch = new SupportPoint[legCapacity];
		_supportHullScratch = new SupportPoint[legCapacity * 2];
		_supportWeights[0] = 1.3f;
		_driveWeights[0] = 1f;
		_postureWeights[0] = 1f;
		for (int i = 0; i < trunk.Count; i++)
		{
			DeerBodySegmentParams spec = parameters.TrunkSegments[i];
			_supportWeights[i + 1] = spec.SupportWeight;
			_driveWeights[i + 1] = spec.DriveWeight;
			_postureWeights[i + 1] = spec.PostureWeight;
		}
	}

	internal void AddLeg(DeerLeg leg) => _legs.Add(leg);

	internal void LinkLegPairs()
	{
		if (_legs.Count != _supportPointScratch.Length)
		{
			throw new InvalidOperationException(
				$"Deer leg topology changed during assembly: expected {_supportPointScratch.Length}, got {_legs.Count}.");
		}
		for (int i = 0; i < _legs.Count; i++)
		{
			DeerLeg mate = null!;
			for (int j = 0; j < _legs.Count; j++)
			{
				if (i != j && _legs[j].PairIndex == _legs[i].PairIndex)
				{
					mate = _legs[j];
					break;
				}
			}
			_legs[i].Mate = mate ?? throw new InvalidOperationException(
				$"Deer leg {i} has no mate in pair {_legs[i].PairIndex}.");
		}
	}

	/// <summary>
	/// 固定序：直喂目标导出 → 旧抓点受力前复验 → 连续休息/地板预看 → 当前合法支撑产生
	/// 升力/推进 → 姿态力 → Body（重力恒开）→ 四条段链 → 统一换步 → 单次支撑发布。
	/// 支撑和法线低通每 tick 恰好推进一次，所有 tie 均按腿索引确定。
	/// </summary>
	public void Tick(in TickContext ctx)
	{
		Vector3 worldUp = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? -ctx.GravityPerTick.Normalized()
			: Vector3.Up;
		_worldUp = worldUp;
		bool derivedMove = MoveTarget is not null;
		if (MoveTarget is { } fedTarget)
		{
			DeriveMoveFromTarget(fedTarget, worldUp);
		}
		else
		{
			AtMoveTarget = false;
		}

		Vector3 effectiveMove = EffectiveMove(worldUp);
		UpdateRestAmount(effectiveMove);
		UpdateLegReachLimits();
		UpdateFrame(effectiveMove, worldUp);
		ProbeGround(ctx.Terrain, effectiveMove, worldUp);

		float standableDot = Mathf.Cos(Mathf.DegToRad(_params.MaxStandableSlopeDegrees));
		foreach (DeerLeg leg in _legs)
		{
			leg.BeginControllerTick();
			leg.ValidateGripBeforeBody(
				ctx.Terrain, worldUp, standableDot, _params.TerrainClearance);
		}
		SupportSnapshot forceSnapshot = MeasureSupport(worldUp);
		float forceSupport = EnableSupport
			? Mathf.Min(TotalSupport, forceSnapshot.Target)
			: 0f;
		ApplyBodyForces(ctx, effectiveMove, worldUp, forceSupport, forceSnapshot.Planted);
		ApplyBalanceRecovery(forceSnapshot, worldUp);
		ApplyPostureForces();

		// Deer 与蜥蜴路线的硬分界：抓稳也绝不关重力。
		Body.GravityScale = 1f;
		Body.Tick(ctx);

		TickLegs(ctx, effectiveMove, worldUp);
		ResolvePairAirborneEmergency(ctx.Terrain);
		SupportSnapshot finalSnapshot = MeasureSupport(worldUp);
		if (UpdateStepCycle(ctx.Terrain, effectiveMove, finalSnapshot))
		{
			finalSnapshot = MeasureSupport(worldUp);
		}
		CommitSupport(finalSnapshot, worldUp);
		UpdateHesitation(effectiveMove);
		UpdatePairAirborneMetrics();
		UpdateMotionMetrics(effectiveMove);

		if (derivedMove)
		{
			MoveDir = Vector3.Zero;
		}
	}

	/// <summary>地形与鹿一起 rebase：速度、抓点、冷却、支撑与步态相位全部保留。</summary>
	public void Shift(Vector3 delta)
	{
		EnsureFinite(delta, nameof(delta));
		Body.Shift(delta);
		foreach (DeerLeg leg in _legs)
		{
			leg.Shift(delta);
		}
		if (MoveTarget is { } target)
		{
			MoveTarget = target + delta;
		}
		LastMoveTarget += delta;
		AheadFloorPoint += delta;
		CurrentFloorPoint += delta;
		if (_hasSmoothedFloor)
		{
			// 缓存的是沿 Up 的世界标量；rebase 后也必须同步。
			_smoothedFloorHeight += delta.Dot(_worldUp);
		}
		if (_hasPreviousCenter)
		{
			_previousCenter += delta;
		}
	}

	/// <summary>
	/// 地形不随鹿移动的瞬移：平移后腿全松并清除旧地板/直喂目标记忆；强制退出休息，
	/// 让新位置从完整腿长和活动站高重新建立支撑。
	/// </summary>
	public void Teleport(Vector3 delta)
	{
		Shift(delta);
		foreach (DeerLeg leg in _legs)
		{
			leg.ForceRelease(keepCooldown: false);
		}
		ResetSupportState();
		MoveTarget = null;
		MoveDir = Vector3.Zero;
		AtMoveTarget = false;
		LastMoveTargetKind = MoveTargetKind.None;
		_hasSmoothedFloor = false;
		_smoothedFloorHeight = 0f;
		HasAheadFloor = false;
		HasCurrentFloor = false;
		FloorMissTicks = 0;
		Hesitation = 0f;
		SupportNormal = _worldUp;
		_up = _worldUp;
		_right = NormalizeOr(_forward.Cross(_up), _right);
		AheadFloorPoint = Vector3.Zero;
		CurrentFloorPoint = Vector3.Zero;
		AheadFloorNormal = _worldUp;
		WakeFromRest(resetRideHeight: true);
		_hasPreviousCenter = false;
	}

	/// <summary>
	/// 全身统一冲量；腿立即松开并强制退出休息，重力保持开启，随后仍由同一找地/支撑循环恢复。
	/// CurrentRideHeight 保留发射瞬间连续值，但腿可达与目标站高立即回到活动值。
	/// </summary>
	public void Launch(Vector3 velocityPerTick)
	{
		EnsureFinite(velocityPerTick, nameof(velocityPerTick));
		foreach (BodyChunk chunk in Body.Chunks)
		{
			chunk.Vel += velocityPerTick;
		}
		foreach (DeerLeg leg in _legs)
		{
			leg.AddVelocity(velocityPerTick);
			leg.ForceRelease(keepCooldown: true);
		}
		ResetSupportState();
		WakeFromRest(resetRideHeight: false);
		Hesitation = 0f;
		AtMoveTarget = false;
		LastMoveTargetKind = MoveTargetKind.None;
		_hasSmoothedFloor = false;
		HasAheadFloor = false;
		HasCurrentFloor = false;
		FloorMissTicks = 0;
		_hasPreviousCenter = false;
	}

	/// <summary>Body 由调用方先折；这里覆盖所有会改变未来 tick 的 Deer 私有状态与腿链状态。</summary>
	public void FoldDeterministicState(DeterminismHasher hasher)
	{
		ArgumentNullException.ThrowIfNull(hasher);
		hasher.Fold(MoveDir);
		hasher.Fold(RunSpeed);
		hasher.Fold(MoveTarget is not null);
		if (MoveTarget is { } target)
		{
			hasher.Fold(target);
		}
		hasher.Fold(MoveTargetArriveRadius);
		hasher.Fold(AtMoveTarget);
		hasher.Fold(EnableSupport);
		hasher.Fold(EnablePairInterlock);
		hasher.Fold(EnableHesitation);
		hasher.Fold(EnableStepRelease);
		hasher.Fold(EnableBalanceRecovery);
		hasher.Fold(EnableAntlerPosture);
		hasher.Fold(EnableAnatomicalBend);
		hasher.Fold(LastMoveTarget);
		hasher.Fold((int)LastMoveTargetKind);
		hasher.Fold(_forward);
		hasher.Fold(_up);
		hasher.Fold(_right);
		hasher.Fold(SupportNormal);
		hasher.Fold(RawSupport);
		hasher.Fold(TotalSupport);
		hasher.Fold(ForwardPower);
		hasher.Fold(Hesitation);
		hasher.Fold(DriveScale);
		hasher.Fold(RestAmount);
		hasher.Fold(IdleTicks);
		hasher.Fold(CurrentLegReachScale);
		hasher.Fold(DesiredBodyHeight);
		hasher.Fold(CurrentRideHeight);
		hasher.Fold(HasAheadFloor);
		hasher.Fold(AheadFloorPoint);
		hasher.Fold(AheadFloorNormal);
		hasher.Fold(HasCurrentFloor);
		hasher.Fold(CurrentFloorPoint);
		hasher.Fold(FloorMissTicks);
		hasher.Fold(PlantedLegCount);
		hasher.Fold(StallTicks);
		hasher.Fold(VoluntaryReleaseSerial);
		hasher.Fold(BodyDragImpulse);
		hasher.Fold(BalanceOffset);
		hasher.Fold(SupportMargin);
		hasher.Fold(LeanDegrees);
		hasher.Fold(ToppleOffset);
		hasher.Fold(SupportHalfWidth);
		hasher.Fold(SupportHalfLength);
		hasher.Fold(MaxPairAirborneRun);
		hasher.Fold(_hasSmoothedFloor);
		hasher.Fold(_smoothedFloorHeight);
		hasher.Fold(_worldUp);
		hasher.Fold(_previousCenter);
		hasher.Fold(_hasPreviousCenter);
		for (int i = 0; i < _pairAirborneTicks.Length; i++)
		{
			hasher.Fold(_pairAirborneTicks[i]);
		}
		foreach (DeerLeg leg in _legs)
		{
			leg.FoldDeterministicState(hasher);
		}
		hasher.Fold(Body.GravityScale);
		hasher.Fold(Body.AirFriction);
		hasher.Fold(Body.SurfaceFriction);
		hasher.Fold(Body.ConstraintIterations);
		hasher.Fold(Body.Skin);
		hasher.Fold(Body.SnagStretchRatio);
		hasher.Fold(Body.SnagReleaseTicks);
		hasher.Fold(Body.EnablePostCollisionStructureRecovery);
		foreach (BodyChunk chunk in Body.Chunks)
		{
			hasher.Fold(chunk.CollideWithTerrain);
			hasher.Fold(chunk.TerrainSqueeze);
			hasher.Fold(chunk.TerrainContact);
			hasher.Fold(chunk.ContactNormal);
			hasher.Fold(chunk.HadContactLastTick);
		}
		foreach (ChunkConnection connection in Body.Connections)
		{
			hasher.Fold(connection.SnagTicks);
		}
	}

	private void DeriveMoveFromTarget(Vector3 target, Vector3 worldUp)
	{
		// 与 Lizard/Spider 地面后端同义：宿主喂的是贴地可达路径点。Deer 的 CurrentRideHeight
		// 明确定义为世界重力轴上的垂直高度，因此这里也必须沿 worldUp 抬高；若沿斜坡法线，
		// 胡萝卜会同时少抬高并被侧向推离路径点。移动分量随后仍由 EffectiveMove 投影到
		// 可站立支撑面；宿主不必为 Deer 特制一套“身体中心高度”的路径点。
		Vector3 carrot = target + worldUp * CurrentRideHeight;
		Vector3 delta = carrot - BodyCenter;
		AtMoveTarget = delta.Length() <= MoveTargetArriveRadius;
		MoveDir = AtMoveTarget ? Vector3.Zero : NormalizeOr(delta, _forward);
	}

	private Vector3 EffectiveMove(Vector3 worldUp)
	{
		if (!HasMoveIntent || !Finite(MoveDir))
		{
			return Vector3.Zero;
		}
		Vector3 normal = NormalizeOr(SupportNormal, worldUp);
		Vector3 tangent = MoveDir - normal * MoveDir.Dot(normal);
		if (tangent.LengthSquared() < 1e-10f)
		{
			tangent = MoveDir - worldUp * MoveDir.Dot(worldUp);
		}
		return NormalizeOr(tangent, Vector3.Zero);
	}

	private void UpdateRestAmount(Vector3 effectiveMove)
	{
		bool still = effectiveMove.LengthSquared() < 1e-10f
			|| Mathf.Clamp(RunSpeed, 0f, 1f) <= _params.RestIntentThreshold;
		IdleTicks = still ? Math.Min(IdleTicks + 1, 36000) : 0;
		bool resting = still && (AtMoveTarget || IdleTicks > _params.RestDelayTicks);
		// 原作 resting 是连续量：有合格休息目的地时 60 tick 进入，活动时 160 tick 离开。
		// 宿主未提供 AI stayStill 信号时，以无输入持续时间作为资格，不增加模式枚举。
		RestAmount = Mathf.MoveToward(RestAmount, resting ? 1f : 0f,
			resting ? 1f / 60f : 1f / 160f);
		DesiredBodyHeight = _params.PreferredBodyHeight
			* Mathf.Lerp(1f, _params.RestHeightRatio, RestAmount);
	}

	private void WakeFromRest(bool resetRideHeight)
	{
		IdleTicks = 0;
		RestAmount = 0f;
		DesiredBodyHeight = _params.PreferredBodyHeight;
		UpdateLegReachLimits();
		if (resetRideHeight)
		{
			CurrentRideHeight = DesiredBodyHeight;
		}
	}

	private void UpdateLegReachLimits()
	{
		float active = Mathf.Pow(1f - RestAmount, _params.RestLegReachExponent);
		CurrentLegReachScale = Mathf.Lerp(_params.RestLegReachRatio, 1f, active);
		foreach (DeerLeg leg in _legs)
		{
			leg.SetCurrentReachLimit(leg.MaxLength * CurrentLegReachScale);
		}
	}

	private void UpdateFrame(Vector3 effectiveMove, Vector3 worldUp)
	{
		Vector3 desiredForward = effectiveMove;
		if (desiredForward.LengthSquared() < 1e-10f)
		{
			desiredForward = Head.Pos - Trunk[^1].Pos;
		}
		Vector3 targetUp = NormalizeOr(SupportNormal, worldUp);
		desiredForward -= targetUp * desiredForward.Dot(targetUp);
		desiredForward = NormalizeOr(desiredForward, _forward);
		_forward = BlendDirection(_forward, desiredForward,
			effectiveMove.LengthSquared() > 1e-10f ? 0.18f : 0.055f, _right);

		bool left = false;
		bool right = false;
		foreach (DeerLeg leg in _legs)
		{
			if (!leg.Gripping)
			{
				continue;
			}
			left |= leg.Side < 0;
			right |= leg.Side > 0;
		}
		float upBlend = left && right ? _params.SupportNormalBlend : 0.035f;
		_up = BlendDirection(_up, targetUp, upBlend, _forward);
		_up -= _forward * _up.Dot(_forward);
		_up = NormalizeOr(_up, targetUp);
		_right = NormalizeOr(_forward.Cross(_up), _right);
		_up = NormalizeOr(_right.Cross(_forward), targetUp);
	}

	private void ProbeGround(ITerrainQuery terrain, Vector3 effectiveMove, Vector3 worldUp)
	{
		Vector3 center = BodyCenter;
		Vector3 look = effectiveMove.LengthSquared() > 1e-10f ? effectiveMove : _forward;
		float standableDot = Mathf.Cos(Mathf.DegToRad(_params.MaxStandableSlopeDegrees));

		HasCurrentFloor = TryProbeFloor(
			terrain, center, worldUp, standableDot, out TerrainHit currentHit);
		if (HasCurrentFloor)
		{
			CurrentFloorPoint = currentHit.Point;
			CurrentRideHeight = center.Dot(worldUp) - currentHit.Point.Dot(worldUp);
		}

		Vector3 aheadOrigin = center + look * (effectiveMove.LengthSquared() > 1e-10f
			? _params.ForwardHeightProbeDistance : 0f);
		HasAheadFloor = TryProbeFloor(
			terrain, aheadOrigin, worldUp, standableDot, out TerrainHit aheadHit);
		if (HasAheadFloor)
		{
			AheadFloorPoint = aheadHit.Point;
			AheadFloorNormal = aheadHit.Normal.Normalized();
		}

		if (HasAheadFloor || HasCurrentFloor)
		{
			TerrainHit selected = HasAheadFloor ? aheadHit : currentHit;
			float floorHeight = selected.Point.Dot(worldUp);
			if (!_hasSmoothedFloor)
			{
				_smoothedFloorHeight = floorHeight;
				_hasSmoothedFloor = true;
			}
			else
			{
				_smoothedFloorHeight = Mathf.Lerp(
					_smoothedFloorHeight, floorHeight, _params.HeightBlend);
			}
			FloorMissTicks = 0;
		}
		else
		{
			FloorMissTicks++;
			if (FloorMissTicks > 20)
			{
				_hasSmoothedFloor = false;
			}
		}
	}

	private bool TryProbeFloor(
		ITerrainQuery terrain,
		Vector3 origin,
		Vector3 worldUp,
		float standableDot,
		out TerrainHit hit)
	{
		Vector3 from = origin + worldUp * Mathf.Max(0.35f, Head.Radius * 0.5f);
		Vector3 to = origin - worldUp * _params.GroundProbeDistance;
		return terrain.Raycast(from, to, out hit)
			&& Finite(hit.Point)
			&& Finite(hit.Normal)
			&& float.IsFinite(hit.Normal.LengthSquared())
			&& hit.Normal.LengthSquared() > 1e-10f
			&& hit.Normal.Normalized().Dot(worldUp) >= standableDot;
	}

	private void ApplyBodyForces(
		in TickContext ctx,
		Vector3 effectiveMove,
		Vector3 worldUp,
		float forceSupport,
		int forcePlanted)
	{
		BodyDragImpulse = 0f;
		foreach (DeerLeg leg in _legs)
		{
			BodyDragImpulse += leg.ApplyBodyDrag(_params.MaxSupportImpulse * 0.28f);
		}

		float support = EnableSupport ? Mathf.Clamp(forceSupport, 0f, 1f) : 0f;
		float liftSupport = Mathf.Clamp(support * _params.SupportLiftGain, 0f, 1f);
		float gravityMagnitude = Mathf.Abs(ctx.GravityPerTick.Dot(worldUp));
		Vector3 centerVelocity = AverageBodyVelocity();
		float heightError = _hasSmoothedFloor
			? _smoothedFloorHeight + DesiredBodyHeight - BodyCenter.Dot(worldUp)
			: 0f;
		float heightServo = heightError * _params.HeightFollowGain
			- centerVelocity.Dot(worldUp) * _params.HeightDamping;
		heightServo = Mathf.Clamp(heightServo,
			-_params.MaxSupportImpulse * 0.6f,
			_params.MaxSupportImpulse * 0.6f);
		float lift = Mathf.Clamp(gravityMagnitude + heightServo,
			0f, _params.MaxSupportImpulse) * liftSupport;

		float supportMean = PositiveMean(_supportWeights);
		for (int i = 0; i < _supportWeights.Length; i++)
		{
			BodyChunk chunk = i == 0 ? Head : Trunk[i - 1];
			float weight = supportMean > 0f ? _supportWeights[i] / supportMean : 0f;
			float damping = Mathf.Lerp(1f, 0.94f, support);
			chunk.Vel *= damping;
			// 对齐 Deer.Act 的力序：先阻尼旧速度，再注入本 tick 腿支撑升力，不能把刚算出的
			// 支撑也乘掉，否则低通支撑永远抵不过同 tick 重力，身体会靠球碰撞趴地滑行。
			chunk.Vel += worldUp * (lift * weight);
		}
		// 原作 Deer.Act 对鹿角 chunk 固定乘 0.92；本移植保留该固定阻尼，不随躯干的
		// support 阻尼档插值。
		Antler.Vel *= 0.92f;

		float gripPart = Mathf.Pow(forcePlanted / 4f, 0.8f);
		float blendDenom = _params.GripCountDriveWeight + _params.SupportDriveWeight;
		float locomotionReadiness = blendDenom <= 1e-8f ? 0f :
			(gripPart * _params.GripCountDriveWeight
			 + support * _params.SupportDriveWeight) / blendDenom;
		DriveScale = EnableHesitation
			? Mathf.Lerp(1f, _params.HesitationMinimumDrive, Hesitation)
			: 1f;
		ForwardPower = locomotionReadiness * DriveScale * Mathf.Clamp(RunSpeed, 0f, 1f);

		LastMoveTargetKind = MoveTargetKind.None;
		if (effectiveMove.LengthSquared() < 1e-10f || ForwardPower <= 0f)
		{
			return;
		}

		if (MoveTarget is { } external)
		{
			LastMoveTarget = external;
			LastMoveTargetKind = MoveTargetKind.External;
		}
		else
		{
			LastMoveTarget = HasAheadFloor
				? AheadFloorPoint + worldUp * DesiredBodyHeight
				: BodyCenter + effectiveMove * _params.ForwardHeightProbeDistance;
			LastMoveTargetKind = HasAheadFloor ? MoveTargetKind.Support : MoveTargetKind.Fallback;
		}

		float driveMean = PositiveMean(_driveWeights);
		for (int i = 0; i < _driveWeights.Length; i++)
		{
			BodyChunk chunk = i == 0 ? Head : Trunk[i - 1];
			float weight = driveMean > 0f ? _driveWeights[i] / driveMean : 0f;
			AddCappedTangentVelocity(chunk, effectiveMove,
				_params.BaseDrive * ForwardPower * weight, _params.MaxMoveSpeed, worldUp);
		}
	}

	private void ApplyPostureForces()
	{
		BodyChunk previous = Head;
		for (int i = 0; i < Trunk.Count; i++)
		{
			BodyChunk current = Trunk[i];
			float rest = _params.TrunkSegments[i].ConnectionLengthToPrevious;
			Vector3 segmentAxis = i == 0
				? NormalizeOr(
					_up * _params.HeadPostureUpWeight
					+ _forward * _params.HeadPostureForwardWeight,
					_forward)
				: _forward;
			Vector3 desired = previous.Pos - segmentAxis * rest;
			Vector3 correction = desired - current.Pos;
			Vector3 radial = current.Pos - previous.Pos;
			if (radial.LengthSquared() > 1e-10f)
			{
				Vector3 radialDirection = radial.Normalized();
				correction -= radialDirection * correction.Dot(radialDirection);
			}
			float gain = _params.PostureStrength * _postureWeights[i + 1];
			WeightedVelocityPair(previous, current,
				correction.LimitLength(rest * 0.18f) * gain,
				_params.PostureDamping);
			previous = current;
		}

		float antlerLength = _params.HeadRadius + _params.AntlerRadius - _params.AntlerHeadOverlap;
		if (EnableAntlerPosture)
		{
			Vector3 antlerAxis = NormalizeOr(
				_up * _params.AntlerPostureUpWeight
				+ _forward * _params.AntlerPostureForwardWeight,
				_up);
			Vector3 antlerDesired = Head.Pos + antlerAxis * antlerLength;
			Vector3 antlerCorrection = (antlerDesired - Antler.Pos).LimitLength(antlerLength * 0.35f);
			// 原作鹿角连接的头端权重为零；姿态伺服也只驱动鹿角，不反拖头。
			Antler.Vel += antlerCorrection * _params.AntlerPostureStrength;
		}

		// 恒定隔节防折的 3D 等价物：只在展开跨度明显塌缩时连续推开，避免软链 180° 对折。
		for (int i = 0; i + 2 < Trunk.Count + 1; i++)
		{
			BodyChunk a = i == 0 ? Head : Trunk[i - 1];
			BodyChunk b = Trunk[i + 1];
			float span = _params.TrunkSegments[i].ConnectionLengthToPrevious
				+ _params.TrunkSegments[i + 1].ConnectionLengthToPrevious;
			float minimum = span * _params.AntiFoldSpanRatio;
			Vector3 delta = b.Pos - a.Pos;
			float distance = delta.Length();
			if (distance >= minimum)
			{
				continue;
			}
			Vector3 direction = distance > 1e-6f
				? delta / distance
				: NormalizeOr(_up.Cross(_forward), Vector3.Right) * ((i & 1) == 0 ? 1f : -1f);
			float strength = (minimum - distance) * _params.AntiFoldStrength;
			WeightedPushApart(a, b, direction, strength);
		}

	}

	private void ApplyBalanceRecovery(SupportSnapshot support, Vector3 worldUp)
	{
		if (!EnableBalanceRecovery || !support.HasArea)
		{
			return;
		}

		float lateralOverhang = Mathf.Abs(support.LateralOffset) - support.HalfWidth;
		bool outside = lateralOverhang > _params.TerrainClearance;
		float excess = Mathf.InverseLerp(
			_params.MaxLeanDegrees, 80f, Mathf.Min(support.LeanDegrees, 80f));
		if (!outside || excess <= 0f)
		{
			return;
		}

		// 球形 chunk 的纵向链没有可观测的轴向 roll 自由度；3D 新增的真实失稳量是 COM
		// plant-and-trail 本来就要求身体沿 Forward 越过旧脚后再换步，不能把前后越界误当成
		// 倾覆而反向刹停。这里只约束真正的横滚轴：质量中心横向越出左右支脚边界且侧倾超过
		// 软门时，朝最近横向边界回收；前后 pitch 由躯干姿态、腿长拖拽和换步循环约束。
		Vector3 recovery = -_right * Mathf.Sign(support.LateralOffset) * lateralOverhang;
		float footprintScale = Mathf.Max(
			Mathf.Min(support.HalfWidth, support.HalfLength), 0.1f);
		float urgency = Mathf.Max(excess,
			Mathf.Clamp(lateralOverhang / footprintScale + 0.2f, 0.2f, 1f));
		float lateralVelocity = AverageBodyVelocity().Dot(_right);
		Vector3 correction = recovery * _params.BalanceRecoveryStrength
			- _right * lateralVelocity * (_params.BalanceDamping * urgency);
		correction = correction.LimitLength(_params.MaxSupportImpulse * 0.45f)
			* urgency;
		foreach (BodyChunk chunk in Body.Chunks)
		{
			if (!ReferenceEquals(chunk, Antler))
			{
				chunk.Vel += correction;
			}
		}
	}

	private void TickLegs(in TickContext ctx, Vector3 effectiveMove, Vector3 worldUp)
	{
		float standableDot = Mathf.Cos(Mathf.DegToRad(_params.MaxStandableSlopeDegrees));
		foreach (DeerLeg leg in _legs)
		{
			Vector3 desiredDirection = leg.ComputeDesiredDirection(
				_forward, _up, _right, effectiveMove, Mathf.Clamp(RunSpeed, 0f, 1f),
				_params.FootTargetDownWeight, _params.FootTargetMoveWeight,
				_params.FootTargetSplayWeight);
			float downDot = Mathf.Max(0.35f, -desiredDirection.Dot(worldUp));
			float floorHeight = _hasSmoothedFloor
				? _smoothedFloorHeight
				: leg.Anchor.Pos.Dot(worldUp) - DesiredBodyHeight;
			float verticalDrop = Mathf.Max(
				leg.FootRadius + _params.TerrainClearance,
				leg.Anchor.Pos.Dot(worldUp) - floorHeight);
			float floorReach = verticalDrop / downDot;
			float desiredReach = Mathf.Min(
				leg.CurrentReachLimit * _params.IdealFootReachRatio,
				floorReach);
			leg.Tick(ctx, _forward, _up, _right, worldUp,
				effectiveMove, Mathf.Clamp(RunSpeed, 0f, 1f), desiredReach,
				_params.FootTargetDownWeight, _params.FootTargetMoveWeight,
				_params.FootTargetSplayWeight, standableDot, _params.TerrainClearance,
				EnableAnatomicalBend);
		}
	}

	private void ResolvePairAirborneEmergency(ITerrainQuery terrain)
	{
		if (!EnablePairInterlock)
		{
			return;
		}
		for (int pair = 0; pair < _pairAirborneTicks.Length; pair++)
		{
			DeerLeg? first = null;
			DeerLeg? second = null;
			bool confirmedGripInvalidated = false;
			foreach (DeerLeg leg in _legs)
			{
				if (leg.PairIndex != pair)
				{
					continue;
				}
				confirmedGripInvalidated |= leg.ConfirmedGripInvalidatedThisTick;
				if (leg.Gripping)
				{
					first = null;
					second = null;
					break;
				}
				if (first is null)
				{
					first = leg;
				}
				else
				{
					second = leg;
				}
			}
			if (first is null || second is null || !confirmedGripInvalidated)
			{
				continue;
			}

			DeerLeg preferred = first.CandidateConfirmCounter > second.CandidateConfirmCounter
				? first
				: first.CandidateConfirmCounter < second.CandidateConfirmCounter
					? second
					: first.Index <= second.Index ? first : second;
			DeerLeg fallback = ReferenceEquals(preferred, first) ? second : first;
			if (!preferred.TryEmergencyPairPlant(terrain))
			{
				fallback.TryEmergencyPairPlant(terrain);
			}
		}
	}

	private SupportSnapshot MeasureSupport(Vector3 worldUp)
	{
		int planted = 0;
		float raw = 0f;
		Vector3 weightedNormal = Vector3.Zero;
		Vector3 footSum = Vector3.Zero;
		float leftLateralSum = 0f;
		float rightLateralSum = 0f;
		int leftCount = 0;
		int rightCount = 0;
		SupportPoint[] supportPoints = _supportPointScratch;
		Vector3 reference = Trunk[Math.Min(1, Trunk.Count - 1)].Pos;
		Vector3 supportUp = NormalizeOr(SupportNormal, worldUp);
		if (supportUp.Dot(worldUp) <= 0f)
		{
			supportUp = worldUp;
		}
		foreach (DeerLeg leg in _legs)
		{
			if (!leg.Gripping)
			{
				leg.SupportContribution = 0f;
				continue;
			}
			planted++;
			Vector3 downLeg = leg.Tip.Pos - leg.Anchor.Pos;
			float verticality = downLeg.LengthSquared() > 1e-10f
				? downLeg.Normalized().Dot(-supportUp)
				: 0f;
			float openness = OppositeFootOpenness(leg, reference, supportUp);
			// 原作无对侧脚时以 0.8 为硬门；3D 出生/台阶恢复时同一真实脚会同时含
			// 前后与左右分量，若仍用二维硬门，四脚已落地却会在低姿态形成零支撑死锁。
			// 保留直立度单调性与对侧展开增强，但把无展开起点降到 0.20：Original 的
			// 大半径腹部降到约 0.7m 时，真实足端仍在下方却只有约 0.3 的方向余弦。
			float lower = Mathf.Lerp(0.20f, -1f,
				Mathf.Clamp(openness * _params.OppositeSideSupportBonus, 0f, 1f));
			float contribution = Mathf.InverseLerp(lower, 1f, verticality);
			contribution = Mathf.Pow(Mathf.Clamp(contribution, 0f, 1f),
				_params.SupportStraightnessExponent * 0.45f);
			leg.SupportContribution = contribution;
			raw += contribution;
			weightedNormal += leg.GripNormal * Mathf.Max(contribution, 0.05f);
			footSum += leg.GripPoint;
			float lateral = leg.GripPoint.Dot(_right);
			supportPoints[planted - 1] = new SupportPoint(
				lateral,
				leg.GripPoint.Dot(_forward),
				leg.Index);
			if (leg.Side < 0)
			{
				leftLateralSum += lateral;
				leftCount++;
			}
			else
			{
				rightLateralSum += lateral;
				rightCount++;
			}
		}

		float aggregate = Mathf.Pow(Mathf.Min(raw / 3f, 1f), 0.8f);
		float target = aggregate <= 0f
			? 0f
			: Mathf.Pow(aggregate, _params.SupportResponseExponent);
		Vector3 footCenter = planted > 0 ? footSum / planted : BodyCenter;
		Vector3 bodyCenter = MassWeightedBodyCenter();
		Vector3 balanceOffset = bodyCenter - footCenter;
		balanceOffset -= supportUp * balanceOffset.Dot(supportUp);
		float lateralOffset = balanceOffset.Dot(_right);
		float halfWidth = leftCount > 0 && rightCount > 0
			? Mathf.Abs(rightLateralSum / rightCount - leftLateralSum / leftCount) * 0.5f
			: 0f;
		float minForward = float.PositiveInfinity;
		float maxForward = float.NegativeInfinity;
		for (int i = 0; i < planted; i++)
		{
			minForward = Mathf.Min(minForward, supportPoints[i].Y);
			maxForward = Mathf.Max(maxForward, supportPoints[i].Y);
		}
		float halfLength = planted > 1 ? (maxForward - minForward) * 0.5f : 0f;

		float margin = 0f;
		bool hasArea = TrySupportPolygonCorrection(
			supportPoints, _uniqueSupportPointScratch, _supportHullScratch, planted,
			new PlanarPoint(bodyCenter.Dot(_right), bodyCenter.Dot(_forward)),
			out _, out margin);
		float leanDegrees = Mathf.RadToDeg(Mathf.Atan2(
			Mathf.Abs(lateralOffset), Mathf.Max(CurrentRideHeight, Head.Radius)));
		return new SupportSnapshot(
			planted, raw, target, weightedNormal,
			balanceOffset,
			lateralOffset, halfWidth, halfLength, margin, hasArea, leanDegrees);
	}

	private void CommitSupport(SupportSnapshot snapshot, Vector3 worldUp)
	{
		PlantedLegCount = snapshot.Planted;
		RawSupport = snapshot.Raw;
		ToppleOffset = snapshot.LateralOffset;
		BalanceOffset = snapshot.BalanceOffset;
		SupportMargin = snapshot.Margin;
		SupportHalfWidth = snapshot.HalfWidth;
		SupportHalfLength = snapshot.HalfLength;
		LeanDegrees = snapshot.LeanDegrees;
		float target = EnableSupport ? snapshot.Target : 0f;
		TotalSupport = target < TotalSupport
			? target
			: Mathf.Lerp(TotalSupport, target, _params.SupportBlend);

		if (snapshot.WeightedNormal.LengthSquared() > 1e-10f)
		{
			Vector3 candidate = snapshot.WeightedNormal.Normalized();
			if (candidate.Dot(worldUp) > 0f)
			{
				SupportNormal = BlendDirection(SupportNormal, candidate,
					_params.SupportNormalBlend, _forward);
			}
		}
		else
		{
			SupportNormal = BlendDirection(SupportNormal, worldUp, 0.045f, _forward);
		}
	}

	private float OppositeFootOpenness(DeerLeg leg, Vector3 reference, Vector3 worldUp)
	{
		Vector3 fromReference = leg.GripPoint - reference;
		fromReference -= worldUp * fromReference.Dot(worldUp);
		float best = 0f;
		foreach (DeerLeg other in _legs)
		{
			if (other == leg || !other.Gripping)
			{
				continue;
			}
			Vector3 otherOffset = other.GripPoint - reference;
			otherOffset -= worldUp * otherOffset.Dot(worldUp);
			if (fromReference.LengthSquared() < 1e-8f || otherOffset.LengthSquared() < 1e-8f)
			{
				continue;
			}
			float opposition = Mathf.Max(0f,
				-fromReference.Normalized().Dot(otherOffset.Normalized()));
			float separation = leg.GripPoint.DistanceTo(other.GripPoint);
			float distancePhase = Mathf.Sin(Mathf.Pi * Mathf.Clamp(
				separation / Mathf.Max(leg.MaxLength * 1.6f, 1e-4f), 0f, 1f));
			best = Mathf.Max(best, opposition * Mathf.Pow(Mathf.Max(0f, distancePhase), 0.3f));
		}
		return best;
	}

	private void UpdateHesitation(Vector3 effectiveMove)
	{
		if (!EnableHesitation || effectiveMove.LengthSquared() < 1e-10f)
		{
			Hesitation = Mathf.MoveToward(Hesitation, 0f, _params.HesitationFallPerTick);
			return;
		}
		Vector3 reference = Trunk[Math.Min(1, Trunk.Count - 1)].Pos;
		bool ahead = false;
		foreach (DeerLeg leg in _legs)
		{
			if (leg.Gripping && (leg.GripPoint - reference).Dot(effectiveMove) > 0f)
			{
				ahead = true;
				break;
			}
		}
		Hesitation = Mathf.MoveToward(Hesitation, ahead ? 0f : 1f,
			ahead ? _params.HesitationFallPerTick : _params.HesitationRisePerTick);
	}

	private bool UpdateStepCycle(
		ITerrainQuery terrain,
		Vector3 effectiveMove,
		SupportSnapshot support)
	{
		bool hasReachUrgency = false;
		bool hasRestRetraction = false;
		foreach (DeerLeg leg in _legs)
		{
			if (!leg.Gripping)
			{
				leg.ReleaseScore = float.NegativeInfinity;
				continue;
			}
			float distance = leg.GripPoint.DistanceTo(leg.DesiredGripPoint) / leg.MaxLength;
			float behind = effectiveMove.LengthSquared() > 1e-10f
				? Mathf.Max(0f, -(leg.GripPoint - leg.Anchor.Pos).Dot(effectiveMove) / leg.MaxLength)
				: 0f;
			float age = Mathf.Min(leg.GripAge / 120f, 1f);
			leg.ReleaseScore = distance * _params.ReleaseDistanceScoreWeight
				+ behind * _params.ReleaseBehindScoreWeight
				+ age * _params.ReleaseAgeScoreWeight
				- leg.SupportContribution * _params.ReleaseSupportPenaltyWeight;

			int guardTicks = leg.ReleaseCooldownTicks + leg.GripConfirmTicks + 8;
			float travelBudget = _params.MaxMoveSpeed * guardTicks * 1.15f / leg.MaxLength;
			float warningRatio = Mathf.Clamp(
				leg.ReachReleaseRatio - travelBudget, 0.45f, 0.82f);
			Vector3 predictedRootDelta = leg.Anchor.Vel
				+ effectiveMove * (_params.BaseDrive * 0.75f);
			bool losesPathWithinTwoSteps = !leg.CanGuardPairStep(
				terrain, predictedRootDelta, Math.Min(40, guardTicks * 2));
			if (leg.ReachRatio >= warningRatio || losesPathWithinTwoSteps)
			{
				// 还未物理失效，但已进入两倍完整冷却+落脚窗口；提前一轮搬走其中一只脚，
				// 让同对另一脚在真正跨过台阶遮挡或可达极限前始终留下一个真实支点。
				leg.ReleaseScore += 10f + leg.ReachRatio;
				hasReachUrgency = true;
			}
			float rootToGrip = leg.Anchor.Pos.DistanceTo(leg.GripPoint);
			if (!HasMoveIntent && RestAmount > 0f
				&& rootToGrip > leg.CurrentReachLimit * 0.98f)
			{
				// 动态 reach 不能只影响下一次寻点：完全休息时旧远脚也要逐条换到缩短后的
				// 工作区。仍只走统一 ReleaseGrip→冷却→候选→落地循环，且四脚时一次放一条；
				// 物理有效性继续用 hard MaxLength，避免同对因连续缩限同 tick 一起失效。
				leg.ReleaseScore += 20f + rootToGrip / leg.MaxLength;
				hasRestRetraction = true;
			}
		}

		bool hasReleaseWindow = support.Planted > _params.ReleaseWhenPlantedAbove
			|| (hasReachUrgency
				&& support.Planted > _params.ReachGuardReleaseWhenPlantedAbove);
		if (!EnableStepRelease || (!HasMoveIntent && !hasRestRetraction)
			|| !hasReleaseWindow
			|| support.Planted <= _params.MinimumPlantedLegs
			|| support.Raw < _params.MinimumRawSupportForRelease)
		{
			return false;
		}

		bool stalled = StallTicks >= _params.StallReleaseTicks;
		DeerLeg? winner = null;
		// 腿表出生后只读且按工厂索引顺序装配；相同评分保留先遇到者，即最低腿索引。
		foreach (DeerLeg leg in _legs)
		{
			if (!leg.Gripping)
			{
				continue;
			}
			if (EnablePairInterlock && leg.Mate is { Gripping: false })
			{
				continue;
			}
			if (EnablePairInterlock && leg.Mate is { } mate)
			{
				Vector3 predictedDelta = mate.Anchor.Vel
					+ effectiveMove * (_params.BaseDrive * 0.75f);
				// 多节腿完成“冷却→跨过工作区→确认落地”明显慢于单粒子脚。
				int swingTicks = (int)MathF.Ceiling(
					mate.MaxLength * 0.75f / MathF.Max(mate.HuntSpeed, 1e-4f));
				int guardHorizon = Math.Min(
					40, mate.ReleaseCooldownTicks + mate.GripConfirmTicks + swingTicks);
				if (!mate.CanGuardPairStep(terrain, predictedDelta, guardHorizon))
				{
					continue;
				}
			}
			if (winner is null || leg.ReleaseScore > winner.ReleaseScore + 1e-7f)
			{
				winner = leg;
			}
		}

		if (winner is not null
			&& (winner.ReleaseScore >= _params.ReleaseScoreThreshold || stalled)
			&& winner.ReleaseGrip(forced: stalled))
		{
			VoluntaryReleaseSerial++;
			StallTicks = 0;
			return true;
		}
		return false;
	}

	private void UpdatePairAirborneMetrics()
	{
		for (int pair = 0; pair < _pairAirborneTicks.Length; pair++)
		{
			bool anyPlanted = false;
			foreach (DeerLeg leg in _legs)
			{
				// AttachedAtTip 已表示足端物理落地；GripConfirmTicks 只是支撑贡献的滞回。
				// “同对不同时腾空”约束不应把正在确认的已落地足端误报为空中。
				if (leg.PairIndex == pair && leg.AttachedAtTip)
				{
					anyPlanted = true;
					break;
				}
			}
			_pairAirborneTicks[pair] = anyPlanted ? 0 : _pairAirborneTicks[pair] + 1;
			MaxPairAirborneRun = Math.Max(MaxPairAirborneRun, _pairAirborneTicks[pair]);
		}
	}

	private void UpdateMotionMetrics(Vector3 effectiveMove)
	{
		Vector3 currentCenter = BodyCenter;
		Vector3 direction = effectiveMove.LengthSquared() > 1e-10f
			? effectiveMove
			: _forward;
		float displacement = _hasPreviousCenter
			? Mathf.Abs((currentCenter - _previousCenter).Dot(direction))
			: 0f;
		StallTicks = HasMoveIntent && displacement < 0.0015f
			? StallTicks + 1
			: 0;
		_previousCenter = currentCenter;
		_hasPreviousCenter = true;
	}

	private void ResetSupportState()
	{
		RawSupport = 0f;
		TotalSupport = 0f;
		ForwardPower = 0f;
		DriveScale = 0f;
		PlantedLegCount = 0;
		BodyDragImpulse = 0f;
		BalanceOffset = Vector3.Zero;
		SupportMargin = 0f;
		LeanDegrees = 0f;
		ToppleOffset = 0f;
		SupportHalfWidth = 0f;
		SupportHalfLength = 0f;
		StallTicks = 0;
		for (int i = 0; i < _pairAirborneTicks.Length; i++)
		{
			_pairAirborneTicks[i] = 0;
		}
	}

	private Vector3 ComputeBodyCenter()
	{
		return MassWeightedBodyCenter();
	}

	private Vector3 MassWeightedBodyCenter()
	{
		Vector3 weighted = Vector3.Zero;
		float mass = 0f;
		foreach (BodyChunk chunk in Body.Chunks)
		{
			weighted += chunk.Pos * chunk.Mass;
			mass += chunk.Mass;
		}
		return mass > 1e-8f ? weighted / mass : Vector3.Zero;
	}

	private Vector3 AverageBodyVelocity()
	{
		Vector3 weighted = Vector3.Zero;
		float mass = 0f;
		foreach (BodyChunk chunk in Body.Chunks)
		{
			weighted += chunk.Vel * chunk.Mass;
			mass += chunk.Mass;
		}
		return mass > 1e-8f ? weighted / mass : Vector3.Zero;
	}

	/// <summary>
	/// 在持久 Right/Forward 支撑平面上构造至多四点的确定性凸包，并返回 COM 投影到
	/// 凸包的最短修正。margin 正值表示在内，负值表示在外；退化成线/点时不伪造面积。
	/// </summary>
	private static bool TrySupportPolygonCorrection(
		SupportPoint[] points,
		SupportPoint[] unique,
		SupportPoint[] hull,
		int count,
		PlanarPoint projectedCenter,
		out PlanarPoint correction,
		out float margin)
	{
		correction = PlanarPoint.Zero;
		margin = 0f;
		if (count < 3)
		{
			return false;
		}

		// 四腿固定小集合用插入排序，显式以腿索引打破完全重合的 tie。
		for (int i = 1; i < count; i++)
		{
			SupportPoint value = points[i];
			int j = i - 1;
			while (j >= 0 && CompareSupportPoint(points[j], value) > 0)
			{
				points[j + 1] = points[j];
				j--;
			}
			points[j + 1] = value;
		}

		int uniqueCount = 0;
		for (int i = 0; i < count; i++)
		{
			if (uniqueCount == 0
				|| Mathf.Abs(points[i].X - unique[uniqueCount - 1].X) > 1e-6f
				|| Mathf.Abs(points[i].Y - unique[uniqueCount - 1].Y) > 1e-6f)
			{
				unique[uniqueCount++] = points[i];
			}
		}
		if (uniqueCount < 3)
		{
			return false;
		}

		int hullCount = 0;
		for (int i = 0; i < uniqueCount; i++)
		{
			while (hullCount >= 2
				&& SupportCross(hull[hullCount - 2], hull[hullCount - 1], unique[i]) <= 1e-7f)
			{
				hullCount--;
			}
			hull[hullCount++] = unique[i];
		}
		int lowerCount = hullCount;
		for (int i = uniqueCount - 2; i >= 0; i--)
		{
			while (hullCount > lowerCount
				&& SupportCross(hull[hullCount - 2], hull[hullCount - 1], unique[i]) <= 1e-7f)
			{
				hullCount--;
			}
			hull[hullCount++] = unique[i];
		}
		hullCount--;
		if (hullCount < 3)
		{
			return false;
		}

		bool inside = true;
		float closestDistanceSquared = float.PositiveInfinity;
		PlanarPoint closest = projectedCenter;
		for (int i = 0; i < hullCount; i++)
		{
			PlanarPoint a = new(hull[i].X, hull[i].Y);
			PlanarPoint b = new(hull[(i + 1) % hullCount].X, hull[(i + 1) % hullCount].Y);
			PlanarPoint edge = b - a;
			PlanarPoint toCenter = projectedCenter - a;
			float cross = edge.X * toCenter.Y - edge.Y * toCenter.X;
			inside &= cross >= -1e-6f;
			float edgeLengthSquared = edge.LengthSquared();
			float phase = edgeLengthSquared > 1e-10f
				? Mathf.Clamp(toCenter.Dot(edge) / edgeLengthSquared, 0f, 1f)
				: 0f;
			PlanarPoint candidate = a + edge * phase;
			float distanceSquared = projectedCenter.DistanceSquaredTo(candidate);
			if (distanceSquared < closestDistanceSquared)
			{
				closestDistanceSquared = distanceSquared;
				closest = candidate;
			}
		}

		float distance = Mathf.Sqrt(Mathf.Max(closestDistanceSquared, 0f));
		margin = inside ? distance : -distance;
		correction = inside ? PlanarPoint.Zero : closest - projectedCenter;
		return true;
	}

	private static int CompareSupportPoint(SupportPoint a, SupportPoint b)
	{
		int x = a.X.CompareTo(b.X);
		if (x != 0)
		{
			return x;
		}
		int y = a.Y.CompareTo(b.Y);
		return y != 0 ? y : a.LegIndex.CompareTo(b.LegIndex);
	}

	private static float SupportCross(SupportPoint a, SupportPoint b, SupportPoint c) =>
		(b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

	private static float PositiveMean(float[] values)
	{
		float sum = 0f;
		int count = 0;
		foreach (float value in values)
		{
			if (value > 0f)
			{
				sum += value;
				count++;
			}
		}
		return count == 0 ? 0f : sum / count;
	}

	private static void AddCappedTangentVelocity(
		BodyChunk chunk,
		Vector3 direction,
		float impulse,
		float maximum,
		Vector3 worldUp)
	{
		// headroom = maximum - dot(reject(Vel, worldUp), direction)：已有 world-up 速度
		// 不参与度量；clamp 仍缩放整个 drive direction。它不是最终 3D 速度硬上限。
		Vector3 tangent = chunk.Vel - worldUp * chunk.Vel.Dot(worldUp);
		float along = tangent.Dot(direction);
		float add = Mathf.Min(impulse, Mathf.Max(0f, maximum - along));
		chunk.Vel += direction * add;
	}

	private static void WeightedVelocityPair(
		BodyChunk a,
		BodyChunk b,
		Vector3 correction,
		float damping)
	{
		float weightA = b.Mass / (a.Mass + b.Mass);
		a.Vel -= correction * weightA;
		b.Vel += correction * (1f - weightA);
		Vector3 relative = b.Vel - a.Vel;
		a.Vel += relative * (weightA * damping * 0.05f);
		b.Vel -= relative * ((1f - weightA) * damping * 0.05f);
	}

	private static void WeightedPushApart(
		BodyChunk a,
		BodyChunk b,
		Vector3 direction,
		float strength)
	{
		float weightA = b.Mass / (a.Mass + b.Mass);
		a.Vel -= direction * (strength * weightA);
		b.Vel += direction * (strength * (1f - weightA));
	}

	private static Vector3 BlendDirection(
		Vector3 current,
		Vector3 desired,
		float amount,
		Vector3 antipodalAxis)
	{
		current = NormalizeOr(current, desired);
		desired = NormalizeOr(desired, current);
		if (current.Dot(desired) < -0.985f)
		{
			Vector3 axis = antipodalAxis - current * antipodalAxis.Dot(current);
			axis = NormalizeOr(axis, Vector3.Up.Cross(current));
			desired = NormalizeOr(current.Lerp(axis, 0.08f), axis);
		}
		return NormalizeOr(current.Lerp(desired, Mathf.Clamp(amount, 0f, 1f)), desired);
	}

	private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
		value.LengthSquared() > 1e-10f ? value.Normalized() : fallback;

	private static bool Finite(Vector3 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

	private static void EnsureFinite(Vector3 value, string parameter)
	{
		if (!Finite(value))
		{
			throw new ArgumentOutOfRangeException(parameter, value, $"{parameter} must be finite.");
		}
	}
}
