using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>宿主提供的固定安装面。工厂会归一化法线并把 TangentHint 投影到切平面。</summary>
public readonly struct TentaclePlantMount
{
    public readonly Vector3 Point;
    public readonly Vector3 OutwardNormal;
    public readonly Vector3 TangentHint;
    public readonly ulong ColliderId;

    public TentaclePlantMount(
        Vector3 point,
        Vector3 outwardNormal,
        Vector3 tangentHint,
        ulong colliderId)
    {
        Point = point;
        OutwardNormal = outwardNormal;
        TangentHint = tangentHint;
        ColliderId = colliderId;
    }

    internal TentaclePlantMount Shifted(Vector3 delta) =>
        new(Point + delta, OutwardNormal, TangentHint, ColliderId);
}

/// <summary>
/// 宿主权威目标的单 tick 快照。控制器绝不持有或修改外部实体引用；VelocityPerTick
/// 与内核 BodyChunk.Vel 同单位。
/// </summary>
public readonly struct TentaclePlantTargetSnapshot
{
    public readonly ulong StableId;
    public readonly Vector3 Position;
    public readonly Vector3 VelocityPerTick;
    public readonly float Radius;
    public readonly float Mass;
    public readonly bool HostVisible;
    public readonly bool HostGrabbable;

    public TentaclePlantTargetSnapshot(
        ulong stableId,
        Vector3 position,
        Vector3 velocityPerTick,
        float radius,
        float mass,
        bool hostVisible,
        bool hostGrabbable)
    {
        StableId = stableId;
        Position = position;
        VelocityPerTick = velocityPerTick;
        Radius = radius;
        Mass = mass;
        HostVisible = hostVisible;
        HostGrabbable = hostGrabbable;
        Validate();
    }

    internal TentaclePlantTargetSnapshot Shifted(Vector3 delta) =>
        new(StableId, Position + delta, VelocityPerTick, Radius, Mass, HostVisible, HostGrabbable);

    internal void Validate()
    {
        if (!Finite(Position) || !Finite(VelocityPerTick))
        {
            throw new ArgumentException("Target position and velocity must be finite.");
        }
        if (!float.IsFinite(Radius) || Radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Radius), Radius,
                "Target radius must be finite and non-negative.");
        }
        if (!float.IsFinite(Mass) || Mass < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Mass), Mass,
                "Target mass must be finite and non-negative.");
        }
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>
/// 本 tick 对宿主目标的请求。宿主应用 PositionCorrection/VelocityDelta 并在下一 tick
/// 回喂新快照；Released/ConsumeRequested 是事件，不代表内核擅自销毁外部实体。
/// </summary>
public readonly struct TentaclePlantTargetEffect
{
    public readonly ulong TargetId;
    public readonly bool CaptureStarted;
    public readonly bool Held;
    public readonly bool Released;
    public readonly bool ConsumeRequested;
    public readonly Vector3 PositionCorrection;
    public readonly Vector3 VelocityDelta;

    public TentaclePlantTargetEffect(
        ulong targetId,
        bool captureStarted,
        bool held,
        bool released,
        bool consumeRequested,
        Vector3 positionCorrection,
        Vector3 velocityDelta)
    {
        TargetId = targetId;
        CaptureStarted = captureStarted;
        Held = held;
        Released = released;
        ConsumeRequested = consumeRequested;
        PositionCorrection = positionCorrection;
        VelocityDelta = velocityDelta;
    }
}

public enum TentaclePlantPhase
{
    Wandering,
    Tracking,
    Windup,
    Striking,
    Recovering,
    Holding,
}

/// <summary>最近一次固定 tick 对宿主目标的攻击资格判定。</summary>
public enum TentaclePlantTargetStatus
{
    NoTarget,
    HostHidden,
    Held,
    StrikeLocked,
    OutOfRange,
    BehindMount,
    Occluded,
    Chargeable,
}

/// <summary>
/// RW TentaclePlant 思路的独立 3D 后端。它与蜥蜴控制器平行，只共享 Body/ITerrainQuery；
/// 目标和抓持结果走纯值宿主契约，触手动力学由 <see cref="TentacleChain"/> 独占。
/// </summary>
public sealed class TentaclePlantController
{
    public TentaclePlantParams Params { get; }
    public TentaclePlantMount Mount { get; private set; }
    public Vector3 Outward { get; private set; }
    public Vector3 Tangent { get; private set; }
    public Vector3 Bitangent { get; private set; }

    public readonly Body Body;
    public readonly BodyChunk Root;
    public readonly BodyChunk Hand;
    public readonly TentacleChain Chain;

    public IReadOnlyList<TentacleSegmentState> Segments => Chain.Segments;
    public TentaclePlantTargetSnapshot? Target
    {
        get => _target;
        set
        {
            if (value is { } snapshot)
            {
                snapshot.Validate();
            }
            _target = value;
        }
    }
    public TentaclePlantTargetEffect TargetEffect { get; private set; }
    public TentaclePlantPhase Phase { get; private set; } = TentaclePlantPhase.Wandering;
    public int PhaseTick { get; private set; }
    public float AttackCharge { get; private set; }
    public float CanGrab { get; private set; }
    public float Extension { get; private set; } = 1f;
    public long AttackSerial { get; private set; }
    public ulong? HeldTargetId { get; private set; }
    public Vector3 WanderGoal { get; private set; }
    public IReadOnlyList<Vector3> GuidePoints => Chain.GuidePoints;
    public int BacktrackFrom => Chain.BacktrackFrom;
    public int TickQueryCount { get; private set; }
    public int PeakQueryCount { get; private set; }
    public TentaclePlantTargetStatus TargetStatus { get; private set; } =
        TentaclePlantTargetStatus.NoTarget;

    private readonly TentaclePlantParams _parameters;
    private readonly ulong _initialSeed;
    private readonly CountingTerrainQuery _countingTerrain = new();
    private TentaclePlantTargetSnapshot? _target;
    private ulong _randomState;
    private Vector3 _attackDirection;
    private int _chargeUnits;
    private int _strikeTicksRemaining;
    private int _strikeTick;
    private int _grabWindowTicksRemaining;
    private int _retractTicksElapsed;
    private int _consumeTicks;
    private bool _consumeSent;
    private ulong? _pendingReleaseId;
    private ulong? _captureSuppressedId;

    internal TentaclePlantController(
        TentaclePlantMount mount,
        TentaclePlantParams parameters,
        ulong stableSeed,
        Body body,
        BodyChunk root,
        BodyChunk hand,
        TentacleChain chain)
    {
        _parameters = parameters.Snapshot();
        Params = parameters.Snapshot();
        _initialSeed = MixSeed(stableSeed);
        _randomState = _initialSeed;
        Body = body;
        Root = root;
        Hand = hand;
        Chain = chain;
        ApplyMountFrame(mount);
        WanderGoal = InitialWanderGoal();
        _attackDirection = Outward;
    }

    /// <summary>
    /// 固定 tick 入口。顺序：Body → chain → 感知/attack → 下一 tick 力 →
    /// root/hand/tip 耦合 → 宿主抓持效果。
    /// </summary>
    public void Tick(in TickContext ctx)
    {
        TargetEffect = default;
        bool releasedThisTick = ReleaseMissingOrChangedTarget();
        UpdateCaptureSuppression();
        if (_pendingReleaseId is { } releasedId)
        {
            TargetEffect = new TentaclePlantTargetEffect(
                releasedId, false, false, true, false, Vector3.Zero, Vector3.Zero);
            _pendingReleaseId = null;
            releasedThisTick = true;
        }

        _countingTerrain.Reset(ctx.Terrain);
        var countedContext = new TickContext(
            ctx.GravityPerTick,
            _countingTerrain,
            ctx.TickIndex);

        Vector3 handSeedOffset = Hand.Pos - Chain.Tip.Pos;
        Vector3 handLastSeedOffset = Hand.LastPos - Chain.Tip.LastPos;
        if (Chain.EnsureTerrainSafeSeed(_countingTerrain, Outward))
        {
            // 宿主可能在首 tick 前施加确定性微扰；安全展开只替换出生基线，
            // 不应把合法的切向初态信息静默抹掉。
            Hand.Pos = Chain.Tip.Pos + handSeedOffset;
            Hand.LastPos = Chain.Tip.LastPos + handLastSeedOffset;
            Hand.Vel = Chain.Tip.Vel;
        }

        Body.GravityScale = 0f;
        Body.Tick(countedContext);

        float physicsExtension = HeldTargetId is not null
            ? RetractionExtension(_retractTicksElapsed + 1)
            : Extension;
        Vector3 goal = HeldTargetId is not null && Target is { } held
            ? held.Position
            : Target is { } target
                ? target.Position
                : WanderGoal;
        Chain.TickPhysics(
            countedContext,
            goal,
            Outward,
            Tangent,
            physicsExtension);

        bool visibleAttackTarget = EvaluateAttackTarget(_countingTerrain);
        ActAttack(visibleAttackTarget);
        Vector3 strikeImpulse = Phase == TentaclePlantPhase.Striking
            ? _attackDirection * _parameters.LungeImpulse
            : Vector3.Zero;
        Chain.InjectShapeForces(
            goal,
            Outward,
            physicsExtension,
            WindupAmount(),
            strikeImpulse);
        Hand.Vel += strikeImpulse;
        CoupleHandAndTip(_countingTerrain, physicsExtension);
        AnchorRoot();

        if (!releasedThisTick && HeldTargetId is null && Target is { } candidate)
        {
            TryCapture(_countingTerrain, candidate);
        }
        if (HeldTargetId is not null && Target is { } heldTarget &&
            heldTarget.StableId == HeldTargetId.Value)
        {
            ActHolding(heldTarget);
        }
        else
        {
            Extension = 1f;
            _retractTicksElapsed = 0;
            _consumeTicks = 0;
            _consumeSent = false;
            if (Target is null)
            {
                UpdateWanderGoal(_countingTerrain);
            }
        }

        TickQueryCount = _countingTerrain.Count;
        PeakQueryCount = Math.Max(PeakQueryCount, TickQueryCount);
    }

    /// <summary>整体原点平移：所有世界坐标输入、路径、随机游走目标与插值历史同步移动。</summary>
    public void Shift(Vector3 delta)
    {
        Mount = Mount.Shifted(delta);
        Body.Shift(delta);
        Chain.Shift(delta);
        WanderGoal += delta;
        if (Target is { } target)
        {
            Target = target.Shifted(delta);
        }
    }

    /// <summary>
    /// 把同一实例重新安装到另一表面。清空攻击、抓持、回退、随机年龄和插值历史。
    /// </summary>
    public void Remount(in TentaclePlantMount mount)
    {
        TentaclePlantMount canonical = TentaclePlantFactory.CanonicalizeMount(mount);
        if (HeldTargetId is { } held)
        {
            _pendingReleaseId = held;
        }
        ApplyMountFrame(canonical);
        Vector3 rootPos = canonical.Point + Outward * _parameters.RootSurfaceOffset;
        Root.Pos = Root.LastPos = rootPos;
        Root.Vel = Vector3.Zero;
        Vector3 handPos = canonical.Point + Outward * _parameters.RootRadius;
        Hand.Pos = Hand.LastPos = handPos;
        Hand.Vel = Vector3.Zero;
        Chain.Remount(Outward);

        Target = null;
        HeldTargetId = null;
        AttackCharge = 0f;
        _chargeUnits = 0;
        CanGrab = 0f;
        _grabWindowTicksRemaining = 0;
        Extension = 1f;
        _retractTicksElapsed = 0;
        AttackSerial = 0;
        _strikeTicksRemaining = 0;
        _strikeTick = 0;
        _consumeTicks = 0;
        _consumeSent = false;
        _captureSuppressedId = null;
        _randomState = _initialSeed;
        WanderGoal = InitialWanderGoal();
        _attackDirection = Outward;
        Phase = TentaclePlantPhase.Wandering;
        PhaseTick = 0;
        TargetStatus = TentaclePlantTargetStatus.NoTarget;
        PeakQueryCount = 0;
        TickQueryCount = 0;
    }

    /// <summary>显式解除当前抓持；Released 在下一次 Tick 的 TargetEffect 中发出。</summary>
    public void ReleaseHeldTarget()
    {
        if (HeldTargetId is not { } held)
        {
            return;
        }
        _pendingReleaseId = held;
        _captureSuppressedId = held;
        HeldTargetId = null;
        Extension = 1f;
        _retractTicksElapsed = 0;
        _consumeTicks = 0;
        _consumeSent = false;
        SetPhase(Target is null
            ? TentaclePlantPhase.Wandering
            : TentaclePlantPhase.Tracking);
    }

    private bool EvaluateAttackTarget(ITerrainQuery terrain)
    {
        if (Target is not { } target)
        {
            TargetStatus = TentaclePlantTargetStatus.NoTarget;
            return false;
        }
        if (!target.HostVisible)
        {
            TargetStatus = TentaclePlantTargetStatus.HostHidden;
            return false;
        }
        if (HeldTargetId is not null)
        {
            TargetStatus = TentaclePlantTargetStatus.Held;
            return false;
        }
        if (_strikeTicksRemaining > 0)
        {
            TargetStatus = TentaclePlantTargetStatus.StrikeLocked;
            return false;
        }

        // Target 是有体积的宿主实体。攻击长度球与 mount 前半空间都按球体相交
        // 判定，避免猎物表面仍可达、但中心刚越过边界时永久只 Tracking 不充能。
        float targetRadius = Math.Max(0f, target.Radius);
        Vector3 fromRoot = target.Position - Root.Pos;
        float reach = _parameters.Length + targetRadius;
        if (fromRoot.LengthSquared() > reach * reach)
        {
            TargetStatus = TentaclePlantTargetStatus.OutOfRange;
            return false;
        }
        float mountForward = (target.Position - Mount.Point).Dot(Outward);
        if (mountForward < -targetRadius)
        {
            TargetStatus = TentaclePlantTargetStatus.BehindMount;
            return false;
        }
        if (DistanceSquaredToAttackEnvelope(target.Position, mountForward) >
            targetRadius * targetRadius)
        {
            // 长度球与洞外半空间的接缝：目标球可能分别碰到两者，却没有同一个
            // 体积点落在二者交集内。此时仍属于攻击包络外。
            TargetStatus = TentaclePlantTargetStatus.OutOfRange;
            return false;
        }

        bool visible = CanSee(terrain, Hand.Pos, target);
        int count = Chain.Segments.Length;
        visible |= CanSee(terrain, Chain.Segments[count / 4].Pos, target);
        visible |= CanSee(terrain, Chain.Segments[count / 2].Pos, target);
        visible |= CanSee(terrain, Chain.Segments[(count * 3) / 4].Pos, target);
        if (visible)
        {
            float leadTicks = Hand.Pos.DistanceTo(target.Position) *
                _parameters.PredictionTicksPerMeter;
            Vector3 predicted = target.Position + target.VelocityPerTick * leadTicks;
            _attackDirection = SafeDirection(predicted - Hand.Pos, Outward);
        }
        TargetStatus = visible
            ? TentaclePlantTargetStatus.Chargeable
            : TentaclePlantTargetStatus.Occluded;
        return visible;
    }

    private float DistanceSquaredToAttackEnvelope(
        Vector3 point,
        float mountForward)
    {
        Vector3 fromRoot = point - Root.Pos;
        float rootDistance = fromRoot.Length();
        if (mountForward >= 0f)
        {
            float radialExcess = Math.Max(0f, rootDistance - _parameters.Length);
            return radialExcess * radialExcess;
        }

        // 先试长度球上的径向最近点；它若仍在洞外半空间，就是整个球冠的最近点。
        if (rootDistance > _parameters.Length && rootDistance > 1e-6f)
        {
            Vector3 sphereProjection = Root.Pos +
                fromRoot * (_parameters.Length / rootDistance);
            if ((sphereProjection - Mount.Point).Dot(Outward) >= 0f)
            {
                float radialExcess = rootDistance - _parameters.Length;
                return radialExcess * radialExcess;
            }
        }

        // 否则半空间约束生效，最近点在安装平面与长度球相交的圆盘上。
        float rootForward = (Root.Pos - Mount.Point).Dot(Outward);
        float planeRadiusSquared = _parameters.Length * _parameters.Length -
                                   rootForward * rootForward;
        if (planeRadiusSquared <= 0f)
        {
            float radialExcess = Math.Max(0f, rootDistance - _parameters.Length);
            return radialExcess * radialExcess;
        }
        Vector3 pointOnPlane = point - Outward * mountForward;
        Vector3 rootOnPlane = Root.Pos - Outward * rootForward;
        float tangentDistance = pointOnPlane.DistanceTo(rootOnPlane);
        float tangentExcess = Math.Max(
            0f,
            tangentDistance - Mathf.Sqrt(planeRadiusSquared));
        return mountForward * mountForward + tangentExcess * tangentExcess;
    }

    private bool CanSee(
        ITerrainQuery terrain,
        Vector3 origin,
        in TentaclePlantTargetSnapshot target)
    {
        if (!terrain.Raycast(origin, target.Position, out TerrainHit hit))
        {
            return true;
        }
        float tolerance = Math.Max(0f, target.Radius);
        return hit.Point.DistanceSquaredTo(target.Position) <= tolerance * tolerance;
    }

    private void ActAttack(bool canCharge)
    {
        if (HeldTargetId is not null)
        {
            TargetStatus = TentaclePlantTargetStatus.Held;
            AttackCharge = 0f;
            _chargeUnits = 0;
            _strikeTicksRemaining = 0;
            DecayGrabWindow();
            SetPhase(TentaclePlantPhase.Holding);
            return;
        }

        if (_strikeTicksRemaining > 0)
        {
            TargetStatus = TentaclePlantTargetStatus.StrikeLocked;
            _strikeTick++;
            _strikeTicksRemaining--;
            AttackCharge = 1f + _strikeTick / (float)_parameters.LungeTicks;
            _grabWindowTicksRemaining = _parameters.GrabWindowTicks;
            CanGrab = 1f;
            SetPhase(TentaclePlantPhase.Striking);
            if (_strikeTicksRemaining == 0)
            {
                AttackCharge = 0f;
                _chargeUnits = 0;
                _strikeTick = 0;
            }
            return;
        }

        DecayGrabWindow();
        int maximumChargeUnits = _parameters.ChargeTicks * 2;
        if (canCharge)
        {
            _chargeUnits = Math.Min(maximumChargeUnits, _chargeUnits + 2);
        }
        else
        {
            _chargeUnits = Math.Max(0, _chargeUnits - 1);
        }
        AttackCharge = _chargeUnits / (float)maximumChargeUnits;

        // charge 达满的同一 tick 就是第一帧扑击；不多等一个逻辑步。
        if (_chargeUnits >= maximumChargeUnits)
        {
            _strikeTicksRemaining = _parameters.LungeTicks;
            _strikeTick = 0;
            AttackSerial++;
            ActAttack(canCharge);
            return;
        }

        if (CanGrab > 0f)
        {
            SetPhase(TentaclePlantPhase.Recovering);
        }
        else if (AttackCharge >= _parameters.WindupStart)
        {
            SetPhase(TentaclePlantPhase.Windup);
        }
        else if (Target is not null)
        {
            SetPhase(TentaclePlantPhase.Tracking);
        }
        else
        {
            SetPhase(TentaclePlantPhase.Wandering);
        }
    }

    private void DecayGrabWindow()
    {
        if (_grabWindowTicksRemaining > 0)
        {
            _grabWindowTicksRemaining--;
        }
        CanGrab = _grabWindowTicksRemaining /
                  (float)_parameters.GrabWindowTicks;
    }

    private float WindupAmount()
    {
        if (AttackCharge < _parameters.WindupStart || AttackCharge >= 1f)
        {
            return 0f;
        }
        return Mathf.InverseLerp(_parameters.WindupStart, 1f, AttackCharge);
    }

    private void TryCapture(
        ITerrainQuery terrain,
        in TentaclePlantTargetSnapshot target)
    {
        if (!target.HostGrabbable || _captureSuppressedId == target.StableId)
        {
            return;
        }
        float grabRadius = (Phase == TentaclePlantPhase.Striking || CanGrab > 0f)
            ? _parameters.StrikeGrabRadius
            : _parameters.TipRadius;
        float combinedRadius = grabRadius + Math.Max(0f, target.Radius);
        float distanceSquared = DistancePointToSegmentSquared(
            target.Position,
            Hand.LastPos,
            Hand.Pos);
        if (distanceSquared > combinedRadius * combinedRadius)
        {
            distanceSquared = DistancePointToSegmentSquared(
                target.Position,
                Chain.Tip.LastPos,
                Chain.Tip.Pos);
            if (distanceSquared > combinedRadius * combinedRadius)
            {
                return;
            }
        }

        float relativeSpeed = (target.VelocityPerTick - Hand.Vel).Length();
        if (CanGrab <= 0f && relativeSpeed > _parameters.LowRelativeGrabSpeed)
        {
            return;
        }
        // 抓取扫掠允许攻击球比物理 tip 更大，但不能把这段额外半径当成穿墙许可。
        // 只有手端到目标球的真实地形视线成立时才建立宿主抓持关系。
        if (!CanSee(terrain, Hand.Pos, target))
        {
            return;
        }

        HeldTargetId = target.StableId;
        TargetStatus = TentaclePlantTargetStatus.Held;
        AttackCharge = 0f;
        _chargeUnits = 0;
        _strikeTicksRemaining = 0;
        _strikeTick = 0;
        Extension = 1f;
        _retractTicksElapsed = 0;
        _consumeTicks = 0;
        _consumeSent = false;
        SetPhase(TentaclePlantPhase.Holding);
        TargetEffect = new TentaclePlantTargetEffect(
            target.StableId,
            true,
            true,
            false,
            false,
            Vector3.Zero,
            Vector3.Zero);
    }

    private void ActHolding(in TentaclePlantTargetSnapshot target)
    {
        // 捕获建立在本 tick 耦合之后，先保持完整长度；以后每个 Held tick
        // Chain 使用同一档预计算 Extension 解算，随后在这里提交公开标量。
        if (!TargetEffect.CaptureStarted)
        {
            _retractTicksElapsed = Math.Min(
                _parameters.RetractTicks,
                _retractTicksElapsed + 1);
            Extension = RetractionExtension(_retractTicksElapsed);
        }

        Vector3 error = Hand.Pos - target.Position;
        float targetMass = Math.Max(0.01f, target.Mass);
        float totalMass = targetMass + _parameters.CarryHandMass;
        float targetShare = _parameters.CarryHandMass / totalMass;
        float handShare = targetMass / totalMass;
        Vector3 weightedTargetCorrection = error * targetShare;
        // Held 是刚性手端关系：链的扎根投影可能拒绝 Hand 应承担的质量份额，
        // 因而最终位置误差必须完整交回宿主，不能让重目标与手端逐 tick 分离。
        Vector3 positionCorrection = error;
        Vector3 handCorrection = -error * handShare;
        Vector3 handVelocityBeforeCorrection = Hand.Vel;
        Hand.Vel += handCorrection;
        Chain.Tip.Vel += handCorrection;
        Hand.Vel = Hand.Vel.LimitLength(_parameters.SegmentVelocityCap);
        Chain.Tip.Vel = Chain.Tip.Vel.LimitLength(_parameters.SegmentVelocityCap);

        Vector3 velocityDelta =
            (handVelocityBeforeCorrection - target.VelocityPerTick) *
            _parameters.CarryVelocityGain +
            weightedTargetCorrection;
        velocityDelta += (Root.Pos - target.Position).LimitLength(1f) *
            (_parameters.CarryRootPull * (1f - Extension));

        bool consume = false;
        if (Extension <= 0f)
        {
            _consumeTicks++;
            if (!_consumeSent &&
                // PositionCorrection 会把宿主目标落到 Hand.Pos；吞入门必须检查
                // 这个同 tick 最终位置，不能先看旧快照再把目标从洞口拉出去。
                (Hand.Pos.DistanceTo(Root.Pos) <= _parameters.ConsumeDistance ||
                 _consumeTicks >= _parameters.ConsumeForceTicks))
            {
                consume = true;
                _consumeSent = true;
            }
        }
        else
        {
            _consumeTicks = 0;
        }

        bool captureStarted = TargetEffect.CaptureStarted;
        TargetEffect = new TentaclePlantTargetEffect(
            target.StableId,
            captureStarted,
            true,
            false,
            consume,
            positionCorrection,
            velocityDelta);
    }

    private float RetractionExtension(int elapsedTicks) =>
        1f - Math.Min(_parameters.RetractTicks, elapsedTicks) /
        (float)_parameters.RetractTicks;

    private bool ReleaseMissingOrChangedTarget()
    {
        if (HeldTargetId is not { } held)
        {
            return false;
        }
        if (Target is { } target && target.StableId == held)
        {
            return false;
        }
        _pendingReleaseId = held;
        HeldTargetId = null;
        Extension = 1f;
        _retractTicksElapsed = 0;
        _consumeTicks = 0;
        _consumeSent = false;
        return true;
    }

    private void UpdateCaptureSuppression()
    {
        if (_captureSuppressedId is not { } suppressed)
        {
            return;
        }
        if (Target is not { } target || target.StableId != suppressed)
        {
            _captureSuppressedId = null;
            return;
        }

        float radius = _parameters.StrikeGrabRadius + Math.Max(0f, target.Radius);
        bool touchingHand = DistancePointToSegmentSquared(
            target.Position,
            Hand.LastPos,
            Hand.Pos) <= radius * radius;
        bool touchingTip = DistancePointToSegmentSquared(
            target.Position,
            Chain.Tip.LastPos,
            Chain.Tip.Pos) <= radius * radius;
        if (!touchingHand && !touchingTip)
        {
            _captureSuppressedId = null;
        }
    }

    private void CoupleHandAndTip(ITerrainQuery terrain, float extension)
    {
        Vector3 safeTipPosition = Chain.Tip.Pos;
        if (Chain.BacktrackFrom >= 0)
        {
            // 原作回卷时以触手梢为权威，避免 body proxy 把堵塞后缀重新拖向墙内。
            Hand.Pos = Chain.Tip.Pos;
            Hand.Vel = Chain.Tip.Vel;
        }
        else
        {
            Vector3 delta = Chain.Tip.Pos - Hand.Pos;
            Vector3 halfCorrection = delta * 0.5f;
            Hand.Pos += halfCorrection;
            Hand.Vel += halfCorrection;
            Chain.Tip.Pos -= halfCorrection;
            Chain.Tip.Vel -= halfCorrection;
        }

        Vector3 position = Hand.Pos;
        float maximumReach = _parameters.Length * 2f * Mathf.Clamp(extension, 0f, 1f);
        Vector3 fromRoot = position - Root.Pos;
        float rootDistance = fromRoot.Length();
        if (rootDistance > maximumReach && rootDistance > 1e-6f)
        {
            Vector3 tetherCorrection = fromRoot / rootDistance * (rootDistance - maximumReach);
            position -= tetherCorrection;
            Hand.Pos -= tetherCorrection;
            Chain.Tip.Pos -= tetherCorrection;
            Hand.Vel -= tetherCorrection;
            Chain.Tip.Vel -= tetherCorrection;
        }

        Vector3 velocity = (Hand.Vel + Chain.Tip.Vel) * 0.5f;
        Vector3 velocityBeforeTerrain = velocity;

        Vector3 motion = position - Chain.Tip.LastPos;
        float motionLength = motion.Length();
        if (motionLength > 1e-6f &&
            terrain.Raycast(
                Chain.Tip.LastPos,
                position + motion / motionLength * Chain.Tip.Radius,
                out TerrainHit hit) &&
            SphereTerrain.Resolve(
                hit,
                Chain.Tip.Radius,
                0.35f,
                ref position,
                ref velocity,
                out Vector3 normal))
        {
            Chain.Tip.TerrainContact = true;
            Chain.Tip.ContactNormal = normal;
        }
        if (terrain.SpherePenetration(
                position,
                Chain.Tip.Radius,
                out Vector3 pushDir,
                out float depth))
        {
            position += pushDir * depth;
            SphereTerrain.RespondVelocity(pushDir, 0.35f, ref velocity);
            Chain.Tip.TerrainContact = true;
            Chain.Tip.ContactNormal = pushDir;
        }

        Vector3 terrainVelocityDelta = velocity - velocityBeforeTerrain;
        Hand.Vel += terrainVelocityDelta;
        Chain.Tip.Vel += terrainVelocityDelta;
        Hand.Pos = Chain.Tip.Pos = position;
        Hand.Vel = Hand.Vel.LimitLength(_parameters.SegmentVelocityCap);
        Chain.Tip.Vel = Chain.Tip.Vel.LimitLength(_parameters.SegmentVelocityCap);
        Chain.ConstrainTipAfterCoupling(terrain, extension, safeTipPosition);
        Hand.Pos = Chain.Tip.Pos;
    }

    private void AnchorRoot()
    {
        Vector3 position = Mount.Point + Outward * _parameters.RootSurfaceOffset;
        Root.Pos = position;
        Root.LastPos = position;
        Root.Vel = Vector3.Zero;
    }

    private void UpdateWanderGoal(ITerrainQuery terrain)
    {
        Vector3 center = Root.Pos + Outward * _parameters.WanderCenterDistance;
        Vector3 randomDirection = RandomUnitVector();
        Vector3 candidate = WanderGoal +
            randomDirection * _parameters.WanderStep +
            RandomUnitVector() * _parameters.WanderJitter;
        Vector3 fromCenter = candidate - center;
        if (fromCenter.Length() > _parameters.WanderRadius)
        {
            candidate = center + fromCenter.Normalized() * _parameters.WanderRadius;
        }

        float currentClearance = ClearanceScore(terrain, WanderGoal);
        float candidateClearance = ClearanceScore(terrain, candidate);
        bool candidateFree = !terrain.SpherePenetration(
            candidate,
            _parameters.GuideClearanceRadius,
            out _,
            out _);
        if (candidateFree &&
            (candidateClearance >= currentClearance ||
             currentClearance < _parameters.WanderProbeDistance))
        {
            WanderGoal = candidate;
        }

        if (NextFloat() < 1f / _parameters.WanderResetMeanTicks &&
            (Hand.Pos.DistanceTo(WanderGoal) > _parameters.WanderGoalCatchupDistance ||
             currentClearance < _parameters.GuideClearanceRadius))
        {
            WanderGoal = Hand.Pos;
        }
    }

    private float ClearanceScore(ITerrainQuery terrain, Vector3 point)
    {
        float best = _parameters.WanderClearanceDistance;
        AccumulateClearance(terrain, point, Outward, ref best);
        AccumulateClearance(terrain, point, -Outward, ref best);
        AccumulateClearance(terrain, point, Tangent, ref best);
        AccumulateClearance(terrain, point, -Tangent, ref best);
        AccumulateClearance(terrain, point, Bitangent, ref best);
        AccumulateClearance(terrain, point, -Bitangent, ref best);
        return best;
    }

    private void AccumulateClearance(
        ITerrainQuery terrain,
        Vector3 point,
        Vector3 direction,
        ref float best)
    {
        if (terrain.Raycast(
                point,
                point + direction * _parameters.WanderClearanceDistance,
                out TerrainHit hit))
        {
            best = Math.Min(best, point.DistanceTo(hit.Point));
        }
    }

    private Vector3 InitialWanderGoal()
    {
        float distance = Mathf.Lerp(
            _parameters.WanderCenterDistance,
            _parameters.Length,
            NextFloat());
        return Root.Pos + Outward * distance;
    }

    private void ApplyMountFrame(in TentaclePlantMount mount)
    {
        Mount = mount;
        Outward = mount.OutwardNormal;
        Tangent = mount.TangentHint;
        Bitangent = SafeDirection(Outward.Cross(Tangent),
            TentaclePlantFactory.StablePerpendicular(Outward));
        Tangent = SafeDirection(Bitangent.Cross(Outward), Tangent);
    }

    private void SetPhase(TentaclePlantPhase phase)
    {
        if (Phase == phase)
        {
            PhaseTick++;
        }
        else
        {
            Phase = phase;
            PhaseTick = 0;
        }
    }

    private float NextFloat()
    {
        ulong x = _randomState;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _randomState = x;
        ulong bits = x * 2685821657736338717UL;
        return (bits >> 40) * (1f / 16777216f);
    }

    private Vector3 RandomUnitVector()
    {
        float z = NextFloat() * 2f - 1f;
        float angle = NextFloat() * Mathf.Tau;
        float radial = Mathf.Sqrt(Math.Max(0f, 1f - z * z));
        // local z 沿 Outward，xy 覆盖两个切向轴。
        return Tangent * (Mathf.Cos(angle) * radial) +
               Bitangent * (Mathf.Sin(angle) * radial) +
               Outward * z;
    }

    private static ulong MixSeed(ulong seed)
    {
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0UL ? 0xA0761D6478BD642FUL : z;
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Up;
    }

    private static float DistancePointToSegmentSquared(
        Vector3 point,
        Vector3 a,
        Vector3 b)
    {
        Vector3 ab = b - a;
        float denominator = ab.LengthSquared();
        if (denominator <= 1e-10f)
        {
            return point.DistanceSquaredTo(a);
        }
        float t = Mathf.Clamp((point - a).Dot(ab) / denominator, 0f, 1f);
        return point.DistanceSquaredTo(a + ab * t);
    }

    private sealed class CountingTerrainQuery : ITerrainQuery
    {
        private ITerrainQuery? _inner;
        public int Count { get; private set; }

        public void Reset(ITerrainQuery inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Count = 0;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            Count++;
            return _inner!.Raycast(from, to, out hit);
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            Count++;
            return _inner!.SpherePenetration(center, radius, out pushDir, out depth);
        }
    }
}
