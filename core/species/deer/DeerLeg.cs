using System;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Deer;

/// <summary>
/// 鹿腿的一节独立物理粒子。它不是 <c>BodyChunk</c>，也不进入共享 <c>Body</c>：
/// 根部随躯干，段链自行积分、约束和碰撞，足端抓地只通过 Deer 控制器回传支撑。
/// </summary>
public sealed class DeerLegSegmentState
{
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public readonly float Radius;
    public readonly float Mass;
    public bool TerrainContact { get; internal set; }
    public Vector3 ContactNormal { get; internal set; } = Vector3.Up;

    internal DeerLegSegmentState(Vector3 position, float radius, float mass)
    {
        Pos = position;
        LastPos = position;
        Radius = radius;
        Mass = mass;
    }

    public Vector3 LerpPos(float timeStacker) => LastPos.Lerp(Pos, timeStacker);
}

/// <summary>
/// Deer 专属的多节腿。物理形态是 Root→Segment[0]→…→Tip 的柔性距离链，
/// 与 Lizard 的单粒子 Limb、Spider 的解析膝点及 TentaclePlant 的攻击链均无继承关系。
/// 固定序的连续射线负责选地；持久 bend pole 只为段链形态提供弱伺服，不承载抓地判定。
/// </summary>
public sealed class DeerLeg
{
    private readonly struct GripCandidate
    {
        public readonly Vector3 Point;
        public readonly Vector3 Normal;
        public readonly ulong ColliderId;
        public readonly float Cost;

        public GripCandidate(Vector3 point, Vector3 normal, ulong colliderId, float cost)
        {
            Point = point;
            Normal = normal;
            ColliderId = colliderId;
            Cost = cost;
        }
    }

    private static readonly (float Forward, float Right)[] ProbeOffsets =
    {
        (0f, 0f),
        (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
        (0.70710677f, 0.70710677f), (-0.70710677f, 0.70710677f),
        (0.70710677f, -0.70710677f), (-0.70710677f, -0.70710677f),
    };

    public readonly int Index;
    public readonly BodyChunk Anchor;
    public readonly int PairIndex;
    public readonly int Side;
    public readonly DeerLegSegmentState[] Segments;
    public DeerLeg? Mate { get; internal set; }

    public readonly int SegmentCount;
    public readonly float MaxLength;
    public readonly float FootRadius;
    public readonly float ForwardSplay;
    public readonly float OutwardSplay;
    public readonly float HuntSpeed;
    public readonly float Quickness;
    public readonly int ReleaseCooldownTicks;
    public readonly int GripConfirmTicks;
    public readonly float CandidateHysteresisRatio;
    public readonly float ProbeDistance;
    public readonly float ProbeSpreadRatio;
    public readonly float BodyDragStartRatio;
    public readonly float ReachReleaseRatio;

    public Vector3 DesiredGripPoint { get; private set; }
    public Vector3 GripPoint { get; private set; }
    public Vector3 GripNormal { get; private set; } = Vector3.Up;
    public ulong GripColliderId { get; private set; }
    public Vector3 CandidatePoint { get; private set; }
    public Vector3 CandidateNormal { get; private set; } = Vector3.Up;
    public ulong CandidateColliderId { get; private set; }
    public bool HasCandidate { get; private set; }
    public bool AttachedAtTip { get; private set; }
    public bool Gripping => AttachedAtTip && GripAge >= GripConfirmTicks;
    public bool Swinging => !AttachedAtTip;
    public int GripAge { get; private set; }
    public int GripCooldown { get; private set; }
    public int CandidateConfirmCounter { get; private set; }
    public int StepSerial { get; private set; }
    public int LandingSerial { get; private set; }
    public int CandidateSwitchSerial { get; private set; }
    public int ForcedReleaseSerial { get; private set; }
    public float LastCompletedStepDistance { get; private set; }
    public Vector3 LastReleasePoint { get; private set; }
    public Vector3 LastLandingPoint { get; private set; }
    public Vector3 BendPole { get; private set; }
    public float MinimumPoleDot { get; private set; } = 1f;
    public float SupportContribution { get; internal set; }
    public float ReleaseScore { get; internal set; }
    public float CurrentReachLimit => _currentReachLimit;
    public float ReachRatio => Anchor.Pos.DistanceTo(Tip.Pos) / MaxLength;
    public bool HeldAtReachLimit => AttachedAtTip && ReachRatio >= BodyDragStartRatio;
    public DeerLegSegmentState Tip => Segments[^1];
    public float MaxConstraintError { get; private set; }

    // 只在一个 DeerLocomotionController.Tick 内使用：紧急同对保护必须由本 tick 原有的
    // 已确认支点物理失效触发，不能在出生、Launch 后或普通找地时把新候选抬成假抓地。
    internal bool ConfirmedGripInvalidatedThisTick { get; private set; }
    internal bool CandidateChangedThisTick { get; private set; }
    private bool _confirmedGripAtTickStart;

    private float _candidateCost = float.PositiveInfinity;
    private float _idealLength;
    private Vector3 _lastRootPos;
    private Vector3 _frameForward;
    private Vector3 _frameUp;
    private Vector3 _frameRight;
    private Vector3 _worldUp;
    private float _standableNormalDot;
    private float _terrainClearance;
    private float _desiredReach;
    private float _currentReachLimit;
    private bool _initializedFrame;

    /// <summary>
    /// initialFoot 是出生地表附近的足端中心。各段沿根足弦出生并加入确定性弓度，
    /// 使第一个 tick 不依赖重合点的任意法线。
    /// </summary>
    internal DeerLeg(
        int index,
        BodyChunk anchor,
        DeerLegSlotParams slot,
        Vector3 initialFoot,
        Vector3 initialForward,
        Vector3 initialUp)
    {
        Index = index;
        Anchor = anchor;
        PairIndex = slot.PairIndex;
        Side = slot.Side;
        SegmentCount = slot.SegmentCount;
        MaxLength = slot.MaxLength;
        FootRadius = slot.FootRadius;
        ForwardSplay = slot.ForwardSplay;
        OutwardSplay = slot.OutwardSplay;
        HuntSpeed = slot.HuntSpeed;
        Quickness = slot.Quickness;
        ReleaseCooldownTicks = slot.ReleaseCooldownTicks;
        GripConfirmTicks = slot.GripConfirmTicks;
        CandidateHysteresisRatio = slot.CandidateHysteresisRatio;
        ProbeDistance = slot.ProbeDistance;
        ProbeSpreadRatio = slot.ProbeSpreadRatio;
        BodyDragStartRatio = slot.BodyDragStartRatio;
        ReachReleaseRatio = slot.ReachReleaseRatio;
        _currentReachLimit = MaxLength;

        BuildFrame(initialForward, initialUp);
        Vector3 axis = initialFoot - anchor.Pos;
        float axisLength = axis.Length();
        if (axisLength < 1e-5f)
        {
            axis = -_frameUp * (MaxLength * 0.5f);
            axisLength = axis.Length();
            initialFoot = anchor.Pos + axis;
        }
        Vector3 axisDirection = axis / axisLength;
        BendPole = ComputeAnatomicalPole(axisDirection);
        _idealLength = slot.InitialLength;
        DesiredGripPoint = initialFoot;
        LastReleasePoint = initialFoot;
        LastLandingPoint = initialFoot;
        _lastRootPos = anchor.Pos;

        Segments = new DeerLegSegmentState[SegmentCount];
        for (int i = 0; i < Segments.Length; i++)
        {
            float t = (i + 1f) / Segments.Length;
            float bow = Mathf.Sin(t * Mathf.Pi) * Mathf.Min(axisLength * 0.12f, MaxLength * 0.08f);
            Vector3 position = anchor.Pos.Lerp(initialFoot, t) + BendPole * bow;
            float radius = i == Segments.Length - 1 ? FootRadius : slot.SegmentRadius;
            Segments[i] = new DeerLegSegmentState(position, radius, slot.SegmentMass);
        }
    }

    /// <summary>
    /// 身体受力前只复验上一 tick 的已附着抓点。这里不推进冷却、候选确认或段链积分；
    /// body tick 后的 <see cref="Tick"/> 仍会用更新后的腿根做第二次安全复验。
    /// </summary>
    internal void ValidateGripBeforeBody(
        ITerrainQuery terrain,
        Vector3 worldUp,
        float standableNormalDot,
        float terrainClearance)
    {
        _worldUp = NormalizeOr(worldUp, Vector3.Up);
        _standableNormalDot = standableNormalDot;
        _terrainClearance = terrainClearance;
        if (AttachedAtTip)
        {
            ValidateGrip(terrain);
        }
    }

    internal void BeginControllerTick()
    {
        ConfirmedGripInvalidatedThisTick = false;
        CandidateChangedThisTick = false;
        _confirmedGripAtTickStart = Gripping;
    }

    internal void SetCurrentReachLimit(float reachLimit)
    {
        _currentReachLimit = Mathf.Clamp(reachLimit, MaxLength * 0.2f, MaxLength);
    }

    /// <summary>
    /// 更新本腿的局部目标并完成一次段链 tick。worldUp 只来自重力；frameUp 是可站立
    /// 抓面法线的低通结果，因此坡面外撇会贴着坡旋转，而“是否可站”仍以重力竖直判断。
    /// </summary>
    internal void Tick(
        in TickContext ctx,
        Vector3 frameForward,
        Vector3 frameUp,
        Vector3 frameRight,
        Vector3 worldUp,
        Vector3 moveDirection,
        float runSpeed,
        float desiredReach,
        float downWeight,
        float moveWeight,
        float splayWeight,
        float standableNormalDot,
        float terrainClearance,
        bool enableAnatomicalBend)
    {
        Vector3 previousFrameForward = _frameForward;
        Vector3 previousFrameUp = _frameUp;
        Vector3 previousFrameRight = _frameRight;
        _frameForward = NormalizeOr(frameForward, _frameForward);
        _frameUp = NormalizeOr(frameUp, _frameUp);
        _frameRight = NormalizeOr(frameRight, _frameRight);
        _worldUp = NormalizeOr(worldUp, Vector3.Up);
        _standableNormalDot = standableNormalDot;
        _terrainClearance = terrainClearance;
        _desiredReach = Mathf.Clamp(
            desiredReach, _currentReachLimit * 0.2f, _currentReachLimit * 0.9f);

        Vector3 desiredDirection = ComputeDesiredDirection(
            _frameForward, _frameUp, _frameRight, moveDirection, runSpeed,
            downWeight, moveWeight, splayWeight);
        DesiredGripPoint = Anchor.Pos + desiredDirection * _desiredReach;

        if (GripCooldown > 0)
        {
            GripCooldown--;
        }

        if (AttachedAtTip)
        {
            ValidateGrip(ctx.Terrain);
            if (!AttachedAtTip)
            {
                UpdateCandidate(ctx.Terrain);
            }
        }
        else
        {
            UpdateCandidate(ctx.Terrain);
        }

        UpdateBendPole(
            previousFrameForward,
            previousFrameUp,
            previousFrameRight,
            enableAnatomicalBend);
        ApplySegmentForces(ctx);
        Integrate();
        RelaxChain();
        CollideSegments(ctx.Terrain);
        RelaxChainAfterCollision();
        ResolveResidualPenetration(ctx.Terrain);
        TryPlantCandidate(ctx.Terrain);

        if (AttachedAtTip)
        {
            GripAge++;
            Tip.Pos = GripCenter;
            Tip.Vel = Vector3.Zero;
            Tip.TerrainContact = true;
            Tip.ContactNormal = GripNormal;
        }
        else
        {
            GripAge = 0;
            if (enableAnatomicalBend)
            {
                ApplySwingBendVelocity();
            }
        }
        UpdateConstraintMetric();
        _lastRootPos = Anchor.Pos;
    }

    internal Vector3 ComputeDesiredDirection(
        Vector3 frameForward,
        Vector3 frameUp,
        Vector3 frameRight,
        Vector3 moveDirection,
        float runSpeed,
        float downWeight,
        float moveWeight,
        float splayWeight)
    {
        Vector3 splay = frameForward * ForwardSplay + frameRight * (Side * OutwardSplay);
        Vector3 desiredDirection = -frameUp * downWeight + splay * splayWeight;
        if (runSpeed > 0f && moveDirection.LengthSquared() > 1e-8f)
        {
            float pairLead = PairIndex == 0 ? 0.8f : 0.6f;
            desiredDirection += moveDirection.Normalized() * (moveWeight * pairLead * runSpeed);
        }
        return NormalizeOr(desiredDirection, -frameUp);
    }

    /// <summary>主动换步；控制器先做全腿评分与同对守卫，再只对获胜腿调用。</summary>
    internal bool ReleaseGrip(bool forced)
    {
        if (!AttachedAtTip)
        {
            return false;
        }
        AttachedAtTip = false;
        GripAge = 0;
        GripCooldown = ReleaseCooldownTicks;
        CandidateConfirmCounter = 0;
        HasCandidate = false;
        _candidateCost = float.PositiveInfinity;
        SupportContribution = 0f;
        LastReleasePoint = Tip.Pos;
        StepSerial++;
        if (forced)
        {
            ForcedReleaseSerial++;
        }
        return true;
    }

    /// <summary>Teleport/Launch 的无条件松脚，不把宿主生命周期记成一个主动步态事件。</summary>
    internal void ForceRelease(bool keepCooldown)
    {
        AttachedAtTip = false;
        GripAge = 0;
        GripCooldown = keepCooldown ? ReleaseCooldownTicks : 0;
        CandidateConfirmCounter = 0;
        HasCandidate = false;
        _candidateCost = float.PositiveInfinity;
        SupportContribution = 0f;
        ReleaseScore = 0f;
        GripPoint = Tip.Pos;
        GripNormal = _worldUp;
        GripColliderId = 0;
        CandidatePoint = Tip.Pos;
        CandidateNormal = _worldUp;
        CandidateColliderId = 0;
        ConfirmedGripInvalidatedThisTick = false;
        CandidateChangedThisTick = false;
        _confirmedGripAtTickStart = false;
    }

    /// <summary>
    /// 原作当前 DLL 只写 heldBackByLeg 标志却没有消费端。本项目按用户契约补齐：
    /// 进入可及极限后，腿沿根→足方向给锚点一个有上限的回拖速度。
    /// </summary>
    internal float ApplyBodyDrag(float maximumImpulse)
    {
        if (!AttachedAtTip || maximumImpulse <= 0f)
        {
            return 0f;
        }
        float amount = Mathf.Clamp(
            Mathf.InverseLerp(BodyDragStartRatio, ReachReleaseRatio, ReachRatio), 0f, 1f);
        if (amount <= 0f)
        {
            return 0f;
        }
        Vector3 direction = GripCenter - Anchor.Pos;
        if (direction.LengthSquared() < 1e-10f)
        {
            return 0f;
        }
        float impulse = maximumImpulse * amount;
        Anchor.Vel += direction.Normalized() * impulse;
        return impulse;
    }

    public void Shift(Vector3 delta)
    {
        foreach (DeerLegSegmentState segment in Segments)
        {
            segment.Pos += delta;
            segment.LastPos += delta;
        }
        DesiredGripPoint += delta;
        GripPoint += delta;
        CandidatePoint += delta;
        LastReleasePoint += delta;
        LastLandingPoint += delta;
        _lastRootPos += delta;
    }

    internal void AddVelocity(Vector3 velocityPerTick)
    {
        foreach (DeerLegSegmentState segment in Segments)
        {
            segment.Vel += velocityPerTick;
        }
    }

    internal void ClearVelocity()
    {
        foreach (DeerLegSegmentState segment in Segments)
        {
            segment.Vel = Vector3.Zero;
            segment.LastPos = segment.Pos;
        }
    }

    internal void FoldDeterministicState(DeterminismHasher hasher)
    {
        foreach (DeerLegSegmentState segment in Segments)
        {
            hasher.Fold(segment.Pos);
            hasher.Fold(segment.LastPos);
            hasher.Fold(segment.Vel);
            hasher.Fold(segment.ContactNormal);
            hasher.Fold(segment.TerrainContact);
        }
        hasher.Fold(DesiredGripPoint);
        hasher.Fold(GripPoint);
        hasher.Fold(GripNormal);
        hasher.FoldOpaqueId(GripColliderId);
        hasher.Fold(CandidatePoint);
        hasher.Fold(CandidateNormal);
        hasher.FoldOpaqueId(CandidateColliderId);
        hasher.Fold(BendPole);
        hasher.Fold(AttachedAtTip);
        hasher.Fold(HasCandidate);
        hasher.Fold(GripAge);
        hasher.Fold(GripCooldown);
        hasher.Fold(CandidateConfirmCounter);
        hasher.Fold(StepSerial);
        hasher.Fold(LandingSerial);
        hasher.Fold(CandidateSwitchSerial);
        hasher.Fold(ForcedReleaseSerial);
        hasher.Fold(SupportContribution);
        hasher.Fold(ReleaseScore);
        hasher.Fold(LastCompletedStepDistance);
        hasher.Fold(LastReleasePoint);
        hasher.Fold(LastLandingPoint);
        hasher.Fold(MinimumPoleDot);
        hasher.Fold(MaxConstraintError);
        hasher.Fold(_candidateCost);
        hasher.Fold(ConfirmedGripInvalidatedThisTick);
        hasher.Fold(CandidateChangedThisTick);
        hasher.Fold(_confirmedGripAtTickStart);
        hasher.Fold(_idealLength);
        hasher.Fold(_lastRootPos);
        hasher.Fold(_frameForward);
        hasher.Fold(_frameUp);
        hasher.Fold(_frameRight);
        hasher.Fold(_worldUp);
        hasher.Fold(_standableNormalDot);
        hasher.Fold(_terrainClearance);
        hasher.Fold(_desiredReach);
        hasher.Fold(_currentReachLimit);
        hasher.Fold(_initializedFrame);
    }

    private Vector3 GripCenter => GripPoint + GripNormal * (FootRadius + _terrainClearance);
    private Vector3 CandidateCenter => CandidatePoint + CandidateNormal * (FootRadius + _terrainClearance);

    private void UpdateCandidate(ITerrainQuery terrain)
    {
        if (GripCooldown > 0)
        {
            HasCandidate = false;
            CandidateConfirmCounter = 0;
            _candidateCost = float.PositiveInfinity;
            return;
        }

        if (HasCandidate && !CandidateStillValid(terrain))
        {
            HasCandidate = false;
            CandidateConfirmCounter = 0;
            _candidateCost = float.PositiveInfinity;
        }

        if (!FindBestCandidate(terrain, out GripCandidate best))
        {
            return;
        }

        bool replace = !HasCandidate || best.Cost < _candidateCost * (1f - CandidateHysteresisRatio);
        if (replace)
        {
            if (HasCandidate)
            {
                CandidateSwitchSerial++;
            }
            CandidatePoint = best.Point;
            CandidateNormal = best.Normal;
            CandidateColliderId = best.ColliderId;
            _candidateCost = best.Cost;
            HasCandidate = true;
            CandidateConfirmCounter = 1;
            CandidateChangedThisTick = true;
        }
        else
        {
            CandidateConfirmCounter++;
        }
    }

    private bool FindBestCandidate(ITerrainQuery terrain, out GripCandidate best)
    {
        best = default;
        bool found = false;
        float spread = _currentReachLimit * ProbeSpreadRatio;

        // 第一条沿根→理想点，保留原作“从 Base 向目标扇形射线”的遮挡语义。
        ConsiderRay(terrain, Anchor.Pos, DesiredGripPoint - _worldUp * ProbeDistance,
            ref found, ref best);

        // 其余固定顺序样本从重力上方向下投影，直接表达连续 collider 上的地板高度。
        foreach ((float forward, float right) in ProbeOffsets)
        {
            Vector3 sample = DesiredGripPoint
                + _frameForward * (forward * spread)
                + _frameRight * (right * spread);
            Vector3 from = sample + _worldUp * ProbeDistance;
            Vector3 to = sample - _worldUp * ProbeDistance;
            ConsiderRay(terrain, from, to, ref found, ref best);
        }
        return found;
    }

    private void ConsiderRay(
        ITerrainQuery terrain,
        Vector3 from,
        Vector3 to,
        ref bool found,
        ref GripCandidate best)
    {
        if (!terrain.Raycast(from, to, out TerrainHit hit)
            || !Finite(hit.Point)
            || !Finite(hit.Normal))
        {
            return;
        }
        float normalLengthSquared = hit.Normal.LengthSquared();
        if (!float.IsFinite(normalLengthSquared) || normalLengthSquared < 1e-10f)
        {
            return;
        }
        Vector3 normal = hit.Normal / Mathf.Sqrt(normalLengthSquared);
        if (!Finite(normal))
        {
            return;
        }
        if (normal.Dot(_worldUp) < _standableNormalDot)
        {
            return;
        }
        Vector3 center = hit.Point + normal * (FootRadius + _terrainClearance);
        if (!Finite(center)
            || center.DistanceTo(Anchor.Pos) > _currentReachLimit * ReachReleaseRatio
            || !PathClear(terrain, center, hit.Point, hit.ColliderId)
            || terrain.SpherePenetration(center, FootRadius,
                out Vector3 candidatePush, out float candidateDepth)
                && candidatePush.LengthSquared() > 1e-10f && candidateDepth > 1e-5f)
        {
            return;
        }
        float distanceCost = hit.Point.DistanceTo(DesiredGripPoint);
        float slopeCost = (1f - normal.Dot(_worldUp)) * (_currentReachLimit * 0.2f);
        float cost = distanceCost + slopeCost;
        if (!found || cost < best.Cost)
        {
            best = new GripCandidate(hit.Point, normal, hit.ColliderId, cost);
            found = true;
        }
    }

    private bool CandidateStillValid(ITerrainQuery terrain)
    {
        if (!Finite(CandidatePoint)
            || !Finite(CandidateNormal)
            || CandidateNormal.Dot(_worldUp) < _standableNormalDot)
        {
            return false;
        }
        Vector3 center = CandidateCenter;
        return Finite(center)
            && center.DistanceTo(Anchor.Pos) <= _currentReachLimit * ReachReleaseRatio
            && SurfaceStillPresent(terrain, CandidatePoint, CandidateNormal, CandidateColliderId)
            && PathClear(terrain, center, CandidatePoint, CandidateColliderId)
            && !HasPenetration(terrain, center, FootRadius);
    }

    private void ValidateGrip(ITerrainQuery terrain)
    {
        Vector3 center = GripCenter;
        float reachRatio = ReachRatio;
        if (!Finite(GripPoint)
            || !Finite(GripNormal)
            || !Finite(center)
            || !float.IsFinite(reachRatio)
            || reachRatio >= ReachReleaseRatio
            || !SurfaceStillPresent(terrain, GripPoint, GripNormal, GripColliderId)
            || !PathClear(terrain, center, GripPoint, GripColliderId)
            || HasPenetration(terrain, center, FootRadius))
        {
            bool wasConfirmedAtTickStart = _confirmedGripAtTickStart;
            if (ReleaseGrip(forced: true) && wasConfirmedAtTickStart)
            {
                ConfirmedGripInvalidatedThisTick = true;
            }
        }
    }

    /// <summary>
    /// 主动抬起同对另一腿前的短视预测：当前支点必须真实存在，并在未来数个按当前根速度
    /// 外推的位置上仍可达且无遮挡。它只否决主动松脚，不延迟任何物理失效释放。
    /// </summary>
    internal bool CanGuardPairStep(
        ITerrainQuery terrain,
        Vector3 predictedRootDeltaPerTick,
        int horizonTicks)
    {
        if (!Gripping
            || !SurfaceStillPresent(terrain, GripPoint, GripNormal, GripColliderId))
        {
            return false;
        }
        int horizon = Math.Clamp(horizonTicks, 1, 40);
        for (int tick = 1; tick <= horizon; tick++)
        {
            Vector3 predictedRoot = Anchor.Pos + predictedRootDeltaPerTick * tick;
            if (predictedRoot.DistanceTo(GripCenter) >= MaxLength * ReachReleaseRatio
                || !PathClearFrom(terrain, predictedRoot, GripCenter, GripPoint, GripColliderId))
            {
                return false;
            }
        }
        return true;
    }

    private bool PathClear(ITerrainQuery terrain, Vector3 center, Vector3 surfacePoint, ulong colliderId)
        => PathClearFrom(terrain, Anchor.Pos, center, surfacePoint, colliderId);

    private bool PathClearFrom(
        ITerrainQuery terrain,
        Vector3 root,
        Vector3 center,
        Vector3 surfacePoint,
        ulong colliderId)
    {
        if (!Finite(root) || !Finite(center) || !Finite(surfacePoint))
        {
            return false;
        }
        if (!terrain.Raycast(root, center, out TerrainHit obstruction))
        {
            return true;
        }
        if (!Finite(obstruction.Point)
            || !Finite(obstruction.Normal)
            || obstruction.Normal.LengthSquared() < 1e-10f)
        {
            return false;
        }
        float tolerance = Mathf.Max(FootRadius * 2.5f, 0.12f);
        bool sameSurface = obstruction.ColliderId == colliderId
            && obstruction.Point.DistanceTo(surfacePoint) <= tolerance;
        bool reachedEndpoint = obstruction.ColliderId == colliderId
            && obstruction.Point.DistanceTo(center) <= tolerance;
        return sameSurface || reachedEndpoint;
    }

    private bool SurfaceStillPresent(
        ITerrainQuery terrain,
        Vector3 point,
        Vector3 normal,
        ulong colliderId)
    {
        float normalLengthSquared = normal.LengthSquared();
        if (colliderId == 0
            || !Finite(point)
            || !Finite(normal)
            || !float.IsFinite(normalLengthSquared)
            || normalLengthSquared < 1e-10f)
        {
            return false;
        }
        normal /= Mathf.Sqrt(normalLengthSquared);
        float distance = Mathf.Max(FootRadius + _terrainClearance, 0.08f);
        Vector3 from = point + normal * (distance * 1.5f);
        Vector3 to = point - normal * distance;
        if (!terrain.Raycast(from, to, out TerrainHit hit)
            || !Finite(hit.Point)
            || !Finite(hit.Normal)
            || hit.ColliderId != colliderId)
        {
            return false;
        }
        float hitNormalLengthSquared = hit.Normal.LengthSquared();
        if (!float.IsFinite(hitNormalLengthSquared) || hitNormalLengthSquared < 1e-10f)
        {
            return false;
        }
        Vector3 hitNormal = hit.Normal / Mathf.Sqrt(hitNormalLengthSquared);
        float tolerance = Mathf.Max(FootRadius * 0.75f, _terrainClearance * 4f + 0.02f);
        return hit.Point.DistanceTo(point) <= tolerance
            && hitNormal.Dot(normal) >= 0.8f
            && hitNormal.Dot(_worldUp) >= _standableNormalDot;
    }

    private static bool HasPenetration(ITerrainQuery terrain, Vector3 center, float radius) =>
        terrain.SpherePenetration(center, radius, out Vector3 push, out float depth)
        && push.LengthSquared() > 1e-10f && depth > 1e-5f;

    private void TryPlantCandidate(ITerrainQuery terrain)
    {
        if (AttachedAtTip || !HasCandidate || GripCooldown > 0
            || CandidateConfirmCounter < GripConfirmTicks)
        {
            return;
        }
        float snapDistance = Mathf.Max(FootRadius * 2.5f, HuntSpeed * 1.5f);
        if (Tip.Pos.DistanceTo(CandidateCenter) > snapDistance
            || !CandidateStillValid(terrain))
        {
            return;
        }
        AttachedAtTip = true;
        GripPoint = CandidatePoint;
        GripNormal = CandidateNormal;
        GripColliderId = CandidateColliderId;
        Tip.Pos = GripCenter;
        Tip.Vel = Vector3.Zero;
        // 新抓点可能明显远于摆动期间收缩的 idealLength；固定根和固定足端若仍使用旧总长，
        // PBD 无解并会把差额挤到末节。落地当拍只向上扩到真实根足距，随后仍由连续目标收敛。
        _idealLength = Mathf.Max(
            _idealLength,
            Mathf.Min(MaxLength, Anchor.Pos.DistanceTo(GripCenter) * 1.01f));
        // 落脚吸附发生在本 tick 常规约束之后；若只瞬移 Tip，末链会留下一个与吸附距离
        // 等量的单 tick 断节。立即用同一固定迭代器把误差向根部传播，再做有界 MTD，
        // 使多节腿的公开 tick 末状态仍是完整物理链，而不是等下一 tick 才补洞。
        RelaxChain();
        ResolveResidualPenetration(terrain);
        HasCandidate = false;
        CandidateConfirmCounter = 0;
        _candidateCost = float.PositiveInfinity;
        GripAge = 1;
        LandingSerial++;
        LastLandingPoint = Tip.Pos;
        LastCompletedStepDistance = LastLandingPoint.DistanceTo(LastReleasePoint);
    }

    /// <summary>
    /// 同对旧支点在台阶边缘同时物理失效时，允许其中一条腿把已经完成冷却、已接近足端且
    /// 仍通过完整可达/遮挡/穿透复验的候选立即确认为支点。它不寻找新点，也不跳过冷却；
    /// 只省掉常规候选的最后确认 tick，以免真实同对互锁在离散地形边界上出现一帧假空档。
    /// </summary>
    internal bool TryEmergencyPairPlant(ITerrainQuery terrain)
    {
        if (AttachedAtTip || !HasCandidate || GripCooldown > 0)
        {
            return false;
        }
        // 只允许省掉候选确认的最后一拍；刚出现的单帧候选仍必须走完整滞回。
        if (CandidateChangedThisTick
            || CandidateConfirmCounter < Math.Max(0, GripConfirmTicks - 1))
        {
            return false;
        }
        float snapDistance = Mathf.Max(FootRadius * 2.5f, HuntSpeed * 1.5f);
        if (Tip.Pos.DistanceTo(CandidateCenter) > snapDistance
            || !CandidateStillValid(terrain))
        {
            return false;
        }
        CandidateConfirmCounter = GripConfirmTicks;
        TryPlantCandidate(terrain);
        if (!AttachedAtTip)
        {
            return false;
        }
        GripAge = GripConfirmTicks;
        return true;
    }

    private void UpdateBendPole(
        Vector3 previousFrameForward,
        Vector3 previousFrameUp,
        Vector3 previousFrameRight,
        bool enableAnatomicalBend)
    {
        // BendPole 描述的是当前可见多节链的曲率，而不是未来候选落点的曲率。摆动时若沿
        // Anchor→Candidate 建 pole，足端尚未到达时 renderer 与段链看到的是另一条根足弦，
        // 就会出现数值 pole 正确、关节却仍朝旧移动方向凹的口径错位。
        Vector3 axis = NormalizeOr(Tip.Pos - Anchor.Pos, -_frameUp);
        if (BendPole.LengthSquared() < 1e-8f)
        {
            BendPole = enableAnatomicalBend
                ? ComputeAnatomicalPole(axis)
                : ComputeLegacyBendPole(axis);
            return;
        }

        if (!enableAnatomicalBend)
        {
            // 专项消融真正保留旧路径：纵向/外撇先混成一个世界向量再投影，
            // 不做 frame transport，而且新偏好进入反半球时直接翻回旧 pole。
            // 因此消融同时能证明正交解剖构造、有向运输和摆动弓向三部分门有效。
            Vector3 legacyDesired = ComputeLegacyBendPole(axis);
            if (legacyDesired.Dot(BendPole) < 0f)
            {
                legacyDesired = -legacyDesired;
            }
            Vector3 legacy = NormalizeOr(BendPole.Lerp(legacyDesired, 0.18f), BendPole);
            legacy = StableProjectedPole(axis, legacy);
            MinimumPoleDot = Mathf.Min(MinimumPoleDot, BendPole.Dot(legacy));
            BendPole = legacy;
            return;
        }

        Vector3 desired = ComputeAnatomicalPole(axis);

        // BendPole 是解剖 frame 中的方向，不是世界空间里永久固定的一侧。先把上一拍 pole
        // 在旧 frame 的三个分量重建到新 frame，再投影到本拍根—足轴；这样身体掉头或换坡时
        // pole 会随身体连续运输，而不会因为世界方向记忆把关节长期留在身后。
        Vector3 transported = _frameForward * BendPole.Dot(previousFrameForward)
            + _frameUp * BendPole.Dot(previousFrameUp)
            + _frameRight * BendPole.Dot(previousFrameRight);
        transported = StableProjectedPole(axis, transported);

        // 真实曲率方向有正负，不能再把 antipodal 目标当成同一条无向轴。沿根—足轴所在圆
        // 以有上限的确定性角速度转向；精确 180° 时用本腿解剖外侧决定绕行方向。
        float maximumTurn = AttachedAtTip ? 0.12f : 0.22f;
        float pairSign = ForwardSplay == 0f
            ? (PairIndex == 0 ? 1f : -1f)
            : Mathf.Sign(ForwardSplay);
        Vector3 turnGuide = _frameRight * Side
            + _frameForward * pairSign * 0.35f;
        Vector3 next = RotatePoleToward(axis, transported, desired, turnGuide, maximumTurn);
        MinimumPoleDot = Mathf.Min(MinimumPoleDot, BendPole.Dot(next));
        BendPole = next;
    }

    private Vector3 RotatePoleToward(
        Vector3 axis,
        Vector3 current,
        Vector3 desired,
        Vector3 turnGuide,
        float maximumRadians)
    {
        current = StableProjectedPole(axis, current);
        desired = StableProjectedPole(axis, desired);
        float dot = Mathf.Clamp(current.Dot(desired), -1f, 1f);
        float angle = Mathf.Acos(dot);
        if (angle <= maximumRadians)
        {
            return desired;
        }

        Vector3 tangent = desired - current * dot;
        if (dot < -0.985f || tangent.LengthSquared() < 1e-8f)
        {
            Vector3 guide = StableProjectedPole(axis, turnGuide);
            tangent = guide - current * guide.Dot(current);
        }
        if (tangent.LengthSquared() < 1e-8f)
        {
            tangent = axis.Cross(current) * Side;
        }
        tangent = NormalizeOr(tangent, axis.Cross(current));
        return NormalizeOr(
            current * Mathf.Cos(maximumRadians) + tangent * Mathf.Sin(maximumRadians),
            desired);
    }

    private void ApplySegmentForces(in TickContext ctx)
    {
        Vector3 end = AttachedAtTip ? GripCenter : HasCandidate ? CandidateCenter : DesiredGripPoint;
        float endDistance = Anchor.Pos.DistanceTo(end);
        float maximumTargetLength = AttachedAtTip ? MaxLength : _currentReachLimit;
        float targetLength = Mathf.Clamp(
            Mathf.Max(endDistance * 1.03f, _desiredReach * 1.08f),
            _currentReachLimit * 0.28f,
            maximumTargetLength);
        _idealLength = Mathf.Lerp(_idealLength, targetLength, 0.05f);

        Vector3 rootVelocity = Anchor.Pos - _lastRootPos;
        Vector3 segmentGravity = ctx.GravityPerTick * 0.35f;
        float straightness = Mathf.Clamp(
            endDistance / Mathf.Max(_idealLength, 1e-4f), 0f, 1f);
        float bowAmplitude = _currentReachLimit * 0.08f * (1f - straightness * 0.75f);
        // 主候选形态保持软响应；可见摆动曲率另在全部约束完成后校正，踩实腿仍能在身体
        // 越过世界抓点时形成 plant-and-trail 的自然拖后。
        const float shapeSpeedRatio = 0.60f;
        const float shapeQuicknessRatio = 0.22f;
        Vector3 shapeAxis = NormalizeOr(end - Anchor.Pos, -_frameUp);
        Vector3 shapePole = StableProjectedPole(shapeAxis, BendPole);
        for (int i = 0; i < Segments.Length; i++)
        {
            DeerLegSegmentState segment = Segments[i];
            float t = (i + 1f) / Segments.Length;
            // 主形态仍沿未来候选弦预先展开整条链；这是长腿能在坡面/台阶上让足端及时
            // 抵达候选的运动路径。当前可见 Root→Tip 的完整 3D 弯向由 tick 末的独立通道修正。
            Vector3 target = Anchor.Pos.Lerp(end, t)
                + shapePole * (Mathf.Sin(t * Mathf.Pi) * bowAmplitude);
            Vector3 desiredVelocity = (target - segment.Pos).LimitLength(HuntSpeed * shapeSpeedRatio);
            segment.Vel = segment.Vel.Lerp(desiredVelocity, Quickness * shapeQuicknessRatio);
            segment.Vel += segmentGravity;
            segment.Vel += rootVelocity * (0.08f * (1f - t));
        }

        DeerLegSegmentState tip = Tip;
        Vector3 tipTarget = AttachedAtTip ? GripCenter : HasCandidate ? CandidateCenter : DesiredGripPoint;
        Vector3 toTarget = tipTarget - tip.Pos;
        Vector3 targetVelocity = toTarget.LimitLength(HuntSpeed);
        tip.Vel = tip.Vel.Lerp(targetVelocity, Quickness);
    }

    /// <summary>
    /// 距离松弛会把快速足端的轴向冲量沿自由链传播，尤其会压过摆动腿的可见弯曲。所有
    /// 常规约束/碰撞结束且本 tick 确认仍在摆动后，只给内段补下一拍的完整解剖 pole 平面速度。
    /// 不改当前位置和足端，因此不会扰动斜坡/台阶上的候选确认、同对腾空保护、段长误差或
    /// plant-and-trail；下一 tick 的常规积分/约束仍是唯一位置权威。
    /// </summary>
    private void ApplySwingBendVelocity()
    {
        if (AttachedAtTip || Segments.Length < 2)
        {
            return;
        }

        Vector3 visibleTip = Tip.Pos;
        Vector3 shapeAxis = NormalizeOr(visibleTip - Anchor.Pos, -_frameUp);
        float pairSign = ForwardSplay == 0f
            ? (PairIndex == 0 ? 1f : -1f)
            : Mathf.Sign(ForwardSplay);
        Vector3 longitudinalPole = StableProjectedPole(
            shapeAxis, _frameForward * pairSign);
        Vector3 shapePole = StableProjectedPole(shapeAxis, BendPole);
        float longitudinalComponent = shapePole.Dot(longitudinalPole);
        Vector3 secondary = shapePole - longitudinalPole * longitudinalComponent;
        float secondaryComponent = secondary.Length();
        Vector3 secondaryPole = secondaryComponent > 1e-5f
            ? secondary / secondaryComponent
            : Vector3.Zero;
        float visibleEndDistance = Anchor.Pos.DistanceTo(visibleTip);
        float straightness = Mathf.Clamp(
            visibleEndDistance / Mathf.Max(_idealLength, 1e-4f), 0f, 1f);
        float bowAmplitude = _currentReachLimit * 0.10f * (1f - straightness * 0.75f);

        for (int i = 0; i < Segments.Length - 1; i++)
        {
            DeerLegSegmentState segment = Segments[i];
            float t = (i + 1f) / Segments.Length;
            float curveWeight = Mathf.Sin(t * Mathf.Pi);
            Vector3 chordPoint = Anchor.Pos.Lerp(visibleTip, t);
            Vector3 actualOffset = segment.Pos - chordPoint;
            float actualLongitudinal = actualOffset.Dot(longitudinalPole);
            float wantedLongitudinal = curveWeight * bowAmplitude;
            float longitudinalSpeed = Mathf.Clamp(
                (wantedLongitudinal - actualLongitudinal) * 0.95f,
                -HuntSpeed * 0.80f * curveWeight,
                HuntSpeed * 0.80f * curveWeight);
            segment.Vel += longitudinalPole * longitudinalSpeed;

            // 纵向符号决定截图里“向前/向后凹”的主观读感；BendPole 还包含固定外撇。
            // 用更弱的第二通道把真实段链带回完整 3D pole，避免高速 compact 或长腿
            // strider 的约束反作用把内段压到 pole 另一侧。它仍不改 Tip/Pos。
            if (secondaryComponent > 1e-5f)
            {
                float actualSecondary = actualOffset.Dot(secondaryPole);
                float wantedSecondary = curveWeight * bowAmplitude * secondaryComponent;
                float secondarySpeed = Mathf.Clamp(
                    (wantedSecondary - actualSecondary) * 0.14f,
                    -HuntSpeed * 0.10f * curveWeight,
                    HuntSpeed * 0.10f * curveWeight);
                segment.Vel += secondaryPole * secondarySpeed;
            }
        }
    }

    private void Integrate()
    {
        foreach (DeerLegSegmentState segment in Segments)
        {
            segment.LastPos = segment.Pos;
            segment.Pos += segment.Vel;
            segment.Vel *= 0.58f;
            segment.TerrainContact = false;
        }
    }

    private void RelaxChain()
    {
        int iterations = Math.Max(6, SegmentCount);
        float linkLength = _idealLength / Segments.Length;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            CorrectFromFixedRoot(Anchor.Pos, Segments[0], linkLength);
            for (int i = 1; i < Segments.Length; i++)
            {
                CorrectPair(Segments[i - 1], Segments[i], linkLength);
            }
            if (AttachedAtTip)
            {
                Tip.Pos = GripCenter;
                Tip.Vel = Vector3.Zero;
            }
        }
    }

    private void RelaxChainAfterCollision()
    {
        float linkLength = _idealLength / Segments.Length;
        CorrectFromFixedRoot(Anchor.Pos, Segments[0], linkLength);
        for (int i = 1; i < Segments.Length; i++)
        {
            CorrectPair(Segments[i - 1], Segments[i], linkLength);
        }
        if (AttachedAtTip)
        {
            Tip.Pos = GripCenter;
            Tip.Vel = Vector3.Zero;
        }
    }

    private static void CorrectFromFixedRoot(Vector3 root, DeerLegSegmentState segment, float length)
    {
        Vector3 delta = segment.Pos - root;
        float distance = delta.Length();
        Vector3 direction = distance < 1e-6f ? Vector3.Down : delta / distance;
        Vector3 correction = direction * (distance - length);
        segment.Pos -= correction;
        segment.Vel -= correction;
    }

    private static void CorrectPair(DeerLegSegmentState a, DeerLegSegmentState b, float length)
    {
        Vector3 delta = b.Pos - a.Pos;
        float distance = delta.Length();
        Vector3 direction = distance < 1e-6f ? Vector3.Down : delta / distance;
        Vector3 correction = direction * (distance - length);
        float totalMass = a.Mass + b.Mass;
        float moveA = totalMass > 1e-6f ? b.Mass / totalMass : 0.5f;
        float moveB = 1f - moveA;
        a.Pos += correction * moveA;
        a.Vel += correction * moveA;
        b.Pos -= correction * moveB;
        b.Vel -= correction * moveB;
    }

    private void CollideSegments(ITerrainQuery terrain)
    {
        for (int i = 0; i < Segments.Length; i++)
        {
            DeerLegSegmentState segment = Segments[i];
            if (AttachedAtTip && i == Segments.Length - 1)
            {
                continue;
            }
            Vector3 motion = segment.Pos - segment.LastPos;
            float length = motion.Length();
            if (length > 1e-6f
                && terrain.Raycast(segment.LastPos, segment.Pos + motion / length * segment.Radius,
                    out TerrainHit sweep)
                && SphereTerrain.Resolve(sweep, segment.Radius, 0.42f,
                    ref segment.Pos, ref segment.Vel, out Vector3 sweepNormal))
            {
                segment.TerrainContact = true;
                segment.ContactNormal = sweepNormal;
            }
            if (terrain.SpherePenetration(segment.Pos, segment.Radius,
                out Vector3 pushDirection, out float depth)
                && pushDirection.LengthSquared() > 1e-10f && depth > 0f)
            {
                Vector3 normal = pushDirection.Normalized();
                segment.Pos += normal * depth;
                SphereTerrain.RespondVelocity(normal, 0.42f, ref segment.Vel);
                segment.TerrainContact = true;
                segment.ContactNormal = normal;
            }
        }
    }

    /// <summary>
    /// 距离约束可能把刚被 MTD 推出的中间段重新带回凸角。固定两轮“MTD→单轮约束”，
    /// 最后再做一次纯 MTD，保证公开 tick 末先满足不穿透；残余链误差由下一 tick 松弛吸收。
    /// </summary>
    private void ResolveResidualPenetration(ITerrainQuery terrain)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            ResolvePenetrationsOnly(terrain);
            if (pass == 0)
            {
                RelaxChainAfterCollision();
            }
        }
        ResolvePenetrationsOnly(terrain);
    }

    private void ResolvePenetrationsOnly(ITerrainQuery terrain)
    {
        for (int i = 0; i < Segments.Length; i++)
        {
            if (AttachedAtTip && i == Segments.Length - 1)
            {
                continue;
            }
            DeerLegSegmentState segment = Segments[i];
            if (!terrain.SpherePenetration(segment.Pos, segment.Radius,
                    out Vector3 pushDirection, out float depth)
                || pushDirection.LengthSquared() <= 1e-10f || depth <= 0f)
            {
                continue;
            }
            Vector3 normal = pushDirection.Normalized();
            segment.Pos += normal * depth;
            SphereTerrain.RespondVelocity(normal, 0.42f, ref segment.Vel);
            segment.TerrainContact = true;
            segment.ContactNormal = normal;
        }
    }

    private void UpdateConstraintMetric()
    {
        float linkLength = _idealLength / Segments.Length;
        float maximum = Mathf.Abs(Anchor.Pos.DistanceTo(Segments[0].Pos) - linkLength);
        for (int i = 1; i < Segments.Length; i++)
        {
            maximum = Mathf.Max(maximum,
                Mathf.Abs(Segments[i - 1].Pos.DistanceTo(Segments[i].Pos) - linkLength));
        }
        MaxConstraintError = maximum;
    }

    private void BuildFrame(Vector3 forward, Vector3 up)
    {
        _frameUp = NormalizeOr(up, Vector3.Up);
        Vector3 projectedForward = forward - _frameUp * forward.Dot(_frameUp);
        _frameForward = NormalizeOr(projectedForward, Vector3.Right);
        _frameRight = NormalizeOr(_frameForward.Cross(_frameUp), Vector3.Back);
        _worldUp = _frameUp;
        _initializedFrame = true;
    }

    private Vector3 StableProjectedPole(Vector3 axis, Vector3 preferred)
    {
        Vector3 projected = preferred - axis * preferred.Dot(axis);
        if (projected.LengthSquared() < 1e-8f && _initializedFrame)
        {
            projected = _frameRight * Side;
            projected -= axis * projected.Dot(axis);
        }
        if (projected.LengthSquared() < 1e-8f)
        {
            Vector3 alternate = Mathf.Abs(axis.Dot(Vector3.Up)) < 0.9f
                ? Vector3.Up
                : Vector3.Right;
            projected = alternate - axis * alternate.Dot(axis);
        }
        return NormalizeOr(projected, Vector3.Back * Side);
    }

    /// <summary>
    /// 在根—足轴的法平面内显式构造“前/后腿纵向弯曲 + 本腿向外弯曲”正交基。
    /// 不能先把两种偏好混成一个世界向量再整体投影：当根足弦本身同时向前、向外倾斜时，
    /// 那种投影会让一侧腿的纵向分量反号，即使 pole 对混合目标的 dot 仍为 1。
    /// </summary>
    private Vector3 ComputeAnatomicalPole(Vector3 axis)
    {
        axis = NormalizeOr(axis, -_frameUp);
        float pairSign = ForwardSplay == 0f
            ? (PairIndex == 0 ? 1f : -1f)
            : Mathf.Sign(ForwardSplay);
        Vector3 pairForward = _frameForward * pairSign;
        Vector3 longitudinal = pairForward - axis * pairForward.Dot(axis);
        Vector3 outwardWorld = _frameRight * Side;
        Vector3 outwardProjected = outwardWorld - axis * outwardWorld.Dot(axis);

        if (longitudinal.LengthSquared() < 1e-8f)
        {
            return StableProjectedPole(axis, outwardProjected + _frameUp * 0.12f);
        }
        longitudinal = longitudinal.Normalized();

        // lateral 与纵向在 pole 平面内正交，并翻到本腿的解剖外侧；因此给 lateral 加权
        // 永远不会把前/后腿对的 longitudinal 符号冲掉。
        Vector3 lateral = outwardProjected
            - longitudinal * outwardProjected.Dot(longitudinal);
        if (lateral.LengthSquared() < 1e-8f)
        {
            lateral = axis.Cross(longitudinal);
        }
        lateral = NormalizeOr(lateral, axis.Cross(longitudinal));
        if (lateral.Dot(outwardWorld) < 0f)
        {
            lateral = -lateral;
        }

        float longitudinalWeight = Mathf.Max(Mathf.Abs(ForwardSplay), 0.35f);
        float lateralWeight = Mathf.Max(OutwardSplay, 0.35f);
        Vector3 upProjected = _frameUp - axis * _frameUp.Dot(axis);
        Vector3 desired = longitudinal * longitudinalWeight
            + lateral * lateralWeight
            + upProjected * 0.12f;

        // 上向弱偏置或极端根足轴可能压低某一解剖分量；以正的小余量恢复符号，
        // 但不把 pole 硬钉成单一纵向/横向轴。
        float longitudinalDot = desired.Dot(longitudinal);
        if (longitudinalDot < 0.08f)
        {
            desired += longitudinal * (0.08f - longitudinalDot);
        }
        if (outwardProjected.LengthSquared() > 1e-8f)
        {
            Vector3 outward = outwardProjected.Normalized();
            float outwardDot = desired.Dot(outward);
            float lateralEffect = lateral.Dot(outward);
            if (outwardDot < 0.08f && lateralEffect > 1e-5f)
            {
                desired += lateral * ((0.08f - outwardDot) / lateralEffect);
            }
        }
        return NormalizeOr(desired, longitudinal);
    }

    private Vector3 ComputeLegacyBendPole(Vector3 axis)
    {
        Vector3 mixedPreference = _frameForward * ForwardSplay
            + _frameRight * (Side * OutwardSplay)
            + _frameUp * 0.12f;
        return StableProjectedPole(axis, mixedPreference);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f ? value.Normalized() : fallback;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
