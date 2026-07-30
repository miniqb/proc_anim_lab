using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

public enum CentipedeLeadEnd
{
    Start,
    End,
}

public enum CentipedeMoveTargetKind
{
    None,
    Surface,
    Corner,
    External,
}

/// <summary>单个蜈蚣身体节的出生配置与运行时局部支撑观察面。</summary>
public sealed class CentipedeSegment
{
    public readonly int Index;
    public readonly BodyChunk Chunk;
    public readonly List<CentipedeLeg> Legs = new();
    private readonly CentipedeSegmentParams _runtimeParams;
    /// <summary>出生参数的防御性副本；修改返回对象不会改变已装配控制器。</summary>
    public CentipedeSegmentParams Params => _runtimeParams.Copy();
    internal CentipedeSegmentParams RuntimeParams => _runtimeParams;

    public Vector3 SupportPoint;
    public Vector3 SupportNormal = Vector3.Up;
    public Vector3 Forward = Vector3.Right;
    public Vector3 Side = Vector3.Back;
    public Vector3 TargetCenter;
    public ulong ColliderId;
    public float SupportConfidence;
    public bool Supported => SupportConfidence >= 0.25f;

    internal CentipedeSegment(int index, BodyChunk chunk, CentipedeSegmentParams p)
    {
        Index = index;
        Chunk = chunk;
        _runtimeParams = p.Copy();
        SupportPoint = chunk.Pos - Vector3.Up * chunk.Radius;
        TargetCenter = chunk.Pos;
    }
}

/// <summary>可视化/宿主观察用的表面路径采样；控制器只按固定列表顺序读写。</summary>
public readonly struct CentipedeSurfaceSample
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly ulong ColliderId;
    /// <summary>从路径列表起点累计的表面弧长。列表在任一端延伸后会整体重建该值。</summary>
    public readonly float ArcLength;

    public CentipedeSurfaceSample(Vector3 point, Vector3 normal, ulong colliderId,
        float arcLength = 0f)
    {
        Point = point;
        Normal = normal;
        ColliderId = colliderId;
        ArcLength = arcLength;
    }
}

/// <summary>
/// 任意节 3D 蜈蚣控制器。身体沿带法线的双向表面路径运动；每节拥有独立支撑系，
/// 因而链体可以同时跨在地面、斜坡、墙和天花板上。腿抓取真实地形点并调制局部支撑，
/// 但主要贴面/推进由表面路径伺服承担，避免几十条腿形成过约束。
/// </summary>
public sealed class CentipedeLocomotionController
{
    public readonly Body Body;
    public readonly List<CentipedeSegment> Segments = new();
    public readonly List<CentipedeLeg> Legs = new();
    private readonly List<CentipedeSurfaceSample> _surfaceTrail = new();
    public IReadOnlyList<CentipedeSurfaceSample> SurfaceTrail => _surfaceTrail;

    private CentipedeLeadEnd _requestedLeadEnd = CentipedeLeadEnd.Start;
    /// <summary>
    /// 宿主指定的领航端输入。控制器不会根据 MoveDir 或 MoveTarget 自动改写它；
    /// 新值在下一次 Tick 的确定性边界生效。
    /// </summary>
    public CentipedeLeadEnd RequestedLeadEnd
    {
        get => _requestedLeadEnd;
        set => _requestedLeadEnd = value switch
        {
            CentipedeLeadEnd.Start => value,
            CentipedeLeadEnd.End => value,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value,
                "Centipede lead end must be Start or End."),
        };
    }
    /// <summary>本 tick 已应用的领航端；写入请使用 RequestedLeadEnd。</summary>
    public CentipedeLeadEnd LeadEnd { get; private set; } = CentipedeLeadEnd.Start;
    /// <summary>领航端本 tick 未能把表面路径继续延伸；供宿主/回归观测换面预算。</summary>
    public bool LeadSurfaceBlocked => _leadSurfaceBlocked;
    public BodyChunk LeadChunk => LeadEnd == CentipedeLeadEnd.Start
        ? Segments[0].Chunk : Segments[^1].Chunk;
    public int SupportedSegmentCount { get; private set; }
    public float SupportRatio => Segments.Count == 0 ? 0f
        : SupportedSegmentCount / (float)Segments.Count;
    public bool DeterministicStateIsFinite
    {
        get
        {
            if (!MoveDir.IsFinite() || !LastMoveTarget.IsFinite()
                || !_derivedMoveDir.IsFinite()
                || !_startLeadSurfaceTangent.IsFinite()
                || !_endLeadSurfaceTangent.IsFinite()
                || !_startLeadSurfaceNormal.IsFinite()
                || !_endLeadSurfaceNormal.IsFinite()
                || !float.IsFinite(RunSpeed) || !float.IsFinite(_trailAdvanceRemainder)
                || !float.IsFinite(_gaitClock)
                || MoveTarget is { } moveTarget && !moveTarget.IsFinite())
            {
                return false;
            }
            foreach (CentipedeSegment segment in Segments)
            {
                if (!segment.Chunk.Pos.IsFinite() || !segment.Chunk.LastPos.IsFinite()
                    || !segment.Chunk.Vel.IsFinite() || !segment.SupportPoint.IsFinite()
                    || !segment.SupportNormal.IsFinite() || !segment.Forward.IsFinite()
                    || !segment.Side.IsFinite() || !segment.TargetCenter.IsFinite()
                    || !float.IsFinite(segment.SupportConfidence))
                {
                    return false;
                }
            }
            foreach (CentipedeLeg leg in Legs)
            {
                if (!leg.DeterministicStateIsFinite)
                {
                    return false;
                }
            }
            foreach (CentipedeSurfaceSample sample in _surfaceTrail)
            {
                if (!sample.Point.IsFinite() || !sample.Normal.IsFinite()
                    || !float.IsFinite(sample.ArcLength))
                {
                    return false;
                }
            }
            return true;
        }
    }

    public Vector3 MoveDir;
    public float RunSpeed;
    public Vector3? MoveTarget;
    public bool AtMoveTarget { get; private set; }
    public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
        && (MoveTarget is not null ? !AtMoveTarget : MoveDir.LengthSquared() > 1e-10f);
    public Vector3 LastMoveTarget { get; private set; }
    public CentipedeMoveTargetKind LastMoveTargetKind { get; private set; }

    public float BaseSpeed = 0.045f;
    public float MaxMoveSpeed = 0.09f;
    public float MoveIntentDeadzone = 0.1f;
    public float SurfaceClearance = 0.015f;
    public float SurfaceProbeDistance = 0.45f;
    public float SurfaceServo = 0.22f;
    public float SurfaceDamping = 0.35f;
    public float SupportBlend = 0.25f;
    public float TrailSampleSpacing = 0.04f;
    public int CornerProbeSteps = 6;
    public float GaitFrequency = 0.075f;
    public float GaitWavelength = 0.9f;
    public float StanceFraction = 0.65f;
    public float SelfAvoidanceStrength = 0.18f;
    public float SelfAvoidanceCellSize = 0.5f;
    public int MaxSelfAvoidancePairsPerSegment = 12;
    public int MaxSelfAvoidanceCandidatesPerSegment = 96;
    public float ArriveRadius = 0.35f;

    private readonly float[] _segmentArcFromStart;
    private readonly float _maxSegmentRadius;
    private readonly List<CentipedeSurfaceSample> _preparedSurfaceTransition = new();
    private bool _surfaceInitialized;
    private bool _leadSurfaceBlocked;
    private int _leadSurfaceBlockedTicks;
    private float _trailAdvanceRemainder;
    private float _gaitClock;
    private Vector3 _derivedMoveDir;
    // 两端分别保存“作为 leader 时朝身体外延伸”的有向切线。切线同时记录它所处
    // 表面的法线，下一面输入投影退化时才能做确定性的平行运输，而不是重新猜 world-up。
    private Vector3 _startLeadSurfaceTangent;
    private Vector3 _endLeadSurfaceTangent;
    private Vector3 _startLeadSurfaceNormal = Vector3.Up;
    private Vector3 _endLeadSurfaceNormal = Vector3.Up;
    private bool _hasStartLeadSurfaceTangent;
    private bool _hasEndLeadSurfaceTangent;

    public CentipedeLocomotionController(Body body,
        IReadOnlyList<CentipedeSegmentParams> segmentParams)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(segmentParams);
        if (segmentParams.Count < 2 || body.Chunks.Count != segmentParams.Count)
        {
            throw new ArgumentException(
                "Centipede requires at least two body chunks and exactly one parameter snapshot per chunk.");
        }
        Body = body;
        Body.GravityScale = 1f;
        Body.AirFriction = 0.94f;
        Body.SurfaceFriction = 0.48f;
        // 长链跨锐角时碰撞可能在松弛后重新拉开某一环。蜈蚣把“深断链”定义为
        // 超过节距 10%，并在 20 tick 预算内走 Body 既有的确定性、落点校验释放。
        Body.SnagStretchRatio = 0.1f;
        // 留出 MTD 在释放 tick 后把球推到相邻面的最多数 tick 余量，宿主观测的单环
        // 连续违反仍严格落在 20 tick 硬门内。
        Body.SnagReleaseTicks = 16;

        for (int i = 0; i < segmentParams.Count; i++)
        {
            var segment = new CentipedeSegment(i, body.Chunks[i], segmentParams[i]);
            Segments.Add(segment);
            _maxSegmentRadius = Mathf.Max(_maxSegmentRadius, segment.Chunk.Radius);
        }
        _segmentArcFromStart = new float[Segments.Count];
        for (int i = 1; i < _segmentArcFromStart.Length; i++)
        {
            _segmentArcFromStart[i] = _segmentArcFromStart[i - 1]
                + segmentParams[i - 1].LinkLengthToNext;
        }
        BuildLegs();
    }

    public void Tick(in TickContext ctx)
    {
        Vector3 worldUp = ctx.GravityPerTick.LengthSquared() > 1e-10f
            ? -ctx.GravityPerTick.Normalized() : Vector3.Up;
        ApplyRequestedLeadEnd();
        DeriveMoveIntent();
        if (_leadSurfaceBlocked && ++_leadSurfaceBlockedTicks >= 8)
        {
            // 连续探不到下一面时丢弃旧路径，让已经交还重力的身体从真实新位置重捕获；
            // 否则它落到下层地面后仍会被旧墙角 TargetCenter 拉回空气。
            _surfaceInitialized = false;
            _leadSurfaceBlockedTicks = 0;
        }
        EnsureSurfaceTrail(ctx, worldUp);

        Vector3 desiredMove = EffectiveMoveDirection();
        if (_surfaceInitialized && HasMoveIntent && desiredMove.LengthSquared() > 1e-10f)
        {
            AdvanceLeadSurface(ctx, desiredMove.Normalized(), worldUp);
        }
        else
        {
            LastMoveTargetKind = CentipedeMoveTargetKind.None;
        }

        UpdateSegmentTargets(worldUp);
        ApplySelfAvoidance();
        ApplySegmentForces(ctx);
        Body.Tick(ctx);
        ResolveResidualBodyPenetration(ctx.Terrain);
        TickLegs(ctx);
        UpdateSupportObservations();

        if (MoveTarget is not null)
        {
            MoveDir = Vector3.Zero;
            _derivedMoveDir = Vector3.Zero;
        }
    }

    public void Shift(Vector3 delta)
    {
        Body.Shift(delta);
        foreach (CentipedeLeg leg in Legs)
        {
            leg.Shift(delta);
        }
        for (int i = 0; i < Segments.Count; i++)
        {
            Segments[i].SupportPoint += delta;
            Segments[i].TargetCenter += delta;
        }
        for (int i = 0; i < _surfaceTrail.Count; i++)
        {
            CentipedeSurfaceSample s = _surfaceTrail[i];
            _surfaceTrail[i] = new CentipedeSurfaceSample(
                s.Point + delta, s.Normal, s.ColliderId, s.ArcLength);
        }
        if (MoveTarget is { } target)
        {
            MoveTarget = target + delta;
        }
        LastMoveTarget += delta;
    }

    public void Teleport(Vector3 delta)
    {
        Shift(delta);
        InvalidateSurfaceState();
        MoveTarget = null;
        MoveDir = Vector3.Zero;
        _derivedMoveDir = Vector3.Zero;
        AtMoveTarget = false;
        LastMoveTargetKind = CentipedeMoveTargetKind.None;
    }

    public void Launch(Vector3 velocityPerTick)
    {
        foreach (BodyChunk chunk in Body.Chunks)
        {
            chunk.Vel += velocityPerTick;
        }
        InvalidateSurfaceState();
    }

    /// <summary>
    /// 将控制器全部可演化状态折入哈希；Body 由宿主先折，随后调用本方法。
    /// 配置常量不重复进入每 tick 哈希，但表面路径、局部支撑与腿的隐式步态门全部覆盖。
    /// </summary>
    public void FoldDeterministicState(DeterminismHasher hasher)
    {
        hasher.Fold(MoveDir);
        hasher.Fold(RunSpeed);
        hasher.Fold(MoveTarget is not null);
        if (MoveTarget is { } moveTarget)
        {
            hasher.Fold(moveTarget);
        }
        hasher.Fold(AtMoveTarget);
        hasher.Fold(LastMoveTarget);
        hasher.Fold((int)LastMoveTargetKind);
        hasher.Fold((int)LeadEnd);
        hasher.Fold((int)RequestedLeadEnd);
        hasher.Fold(SupportedSegmentCount);
        hasher.Fold(_surfaceInitialized);
        hasher.Fold(_leadSurfaceBlocked);
        hasher.Fold(_leadSurfaceBlockedTicks);
        hasher.Fold(_trailAdvanceRemainder);
        hasher.Fold(_gaitClock);
        hasher.Fold(_derivedMoveDir);
        hasher.Fold(_hasStartLeadSurfaceTangent);
        hasher.Fold(_startLeadSurfaceTangent);
        hasher.Fold(_startLeadSurfaceNormal);
        hasher.Fold(_hasEndLeadSurfaceTangent);
        hasher.Fold(_endLeadSurfaceTangent);
        hasher.Fold(_endLeadSurfaceNormal);

        hasher.Fold(Segments.Count);
        foreach (CentipedeSegment segment in Segments)
        {
            hasher.Fold(segment.SupportPoint);
            hasher.Fold(segment.SupportNormal);
            hasher.Fold(segment.Forward);
            hasher.Fold(segment.Side);
            hasher.Fold(segment.TargetCenter);
            hasher.FoldOpaqueId(segment.ColliderId);
            hasher.Fold(segment.SupportConfidence);
        }
        hasher.Fold(Legs.Count);
        foreach (CentipedeLeg leg in Legs)
        {
            leg.FoldDeterministicState(hasher);
        }
        hasher.Fold(_surfaceTrail.Count);
        foreach (CentipedeSurfaceSample sample in _surfaceTrail)
        {
            hasher.Fold(sample.Point);
            hasher.Fold(sample.Normal);
            hasher.FoldOpaqueId(sample.ColliderId);
            hasher.Fold(sample.ArcLength);
        }
    }

    private void BuildLegs()
    {
        foreach (CentipedeSegment segment in Segments)
        {
            CentipedeSegmentParams p = segment.RuntimeParams;
            for (int pair = 0; pair < p.LegPairs; pair++)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    Vector3 initial = segment.Chunk.Pos
                        + Vector3.Back * (side * p.LegLateral * (1f + pair * 0.18f))
                        - Vector3.Up * (p.LegLength * 0.65f);
                    var leg = new CentipedeLeg(segment, p, side, pair, initial);
                    segment.Legs.Add(leg);
                    Legs.Add(leg);
                }
            }
        }
    }

    private void DeriveMoveIntent()
    {
        if (MoveTarget is not { } target)
        {
            AtMoveTarget = false;
            _derivedMoveDir = Vector3.Zero;
            return;
        }
        Vector3 delta = target - LeadChunk.Pos;
        AtMoveTarget = delta.Length() <= ArriveRadius;
        _derivedMoveDir = AtMoveTarget || delta.LengthSquared() < 1e-10f
            ? Vector3.Zero : delta.Normalized();
    }

    private Vector3 EffectiveMoveDirection() =>
        MoveTarget is not null ? _derivedMoveDir
        : MoveDir.LengthSquared() < 1e-10f ? Vector3.Zero : MoveDir.Normalized();

    private void ApplyRequestedLeadEnd()
    {
        if (LeadEnd == RequestedLeadEnd)
        {
            return;
        }
        LeadEnd = RequestedLeadEnd;
        _leadSurfaceBlocked = false;
        _leadSurfaceBlockedTicks = 0;
        // 非活动端会随着裁剪而改变其路径端点，旧切线不能直接套到新端点。切换时从
        // 当前路径几何重新播种；之后该端再独立跨 tick 持久化。
        ClearLeadSurfaceTangent(LeadEnd);
        TrySeedLeadSurfaceTangentFromTrail(LeadEnd);
    }

    private void EnsureSurfaceTrail(in TickContext ctx, Vector3 worldUp)
    {
        // Launch/高处出生后第一次初始化可能完全探不到面。这样的临时空气路径不能
        // 永久阻止重捕获；只要还没有任何真实 collider，就每 tick 重新探测，落地当 tick
        // 后的下一步便会把整条路径重新钉到地形。
        if (_surfaceInitialized)
        {
            return;
        }
        _surfaceTrail.Clear();
        _preparedSurfaceTransition.Clear();
        ClearAllLeadSurfaceTangents();
        for (int i = 0; i < Segments.Count; i++)
        {
            BodyChunk chunk = Segments[i].Chunk;
            Vector3 normal = chunk.TerrainContact && chunk.ContactNormal.LengthSquared() > 1e-10f
                ? chunk.ContactNormal.Normalized() : worldUp;
            Vector3 point = chunk.Pos - normal * (chunk.Radius + SurfaceClearance);
            ulong collider = 0;
            if (ctx.Terrain.Raycast(chunk.Pos + normal * 0.1f,
                chunk.Pos - normal * (chunk.Radius + SurfaceProbeDistance), out TerrainHit hit)
                && ValidSurfaceHit(hit))
            {
                point = hit.Point;
                normal = hit.Normal.Normalized();
                collider = hit.ColliderId;
            }
            _surfaceTrail.Add(new CentipedeSurfaceSample(point, normal, collider));
        }
        RebuildArcLengths();
        _surfaceInitialized = HasAllRealSurfaceSamples();
        if (_surfaceInitialized)
        {
            TrySeedLeadSurfaceTangentFromTrail(CentipedeLeadEnd.Start);
            TrySeedLeadSurfaceTangentFromTrail(CentipedeLeadEnd.End);
        }
    }

    private bool HasAllRealSurfaceSamples()
    {
        if (_surfaceTrail.Count != Segments.Count)
        {
            return false;
        }
        foreach (CentipedeSurfaceSample sample in _surfaceTrail)
        {
            if (sample.ColliderId == 0)
            {
                return false;
            }
        }
        return true;
    }

    private void AdvanceLeadSurface(in TickContext ctx, Vector3 intent, Vector3 worldUp)
    {
        if (_surfaceTrail.Count == 0)
        {
            return;
        }
        int leadIndex = LeadEnd == CentipedeLeadEnd.Start ? 0 : _surfaceTrail.Count - 1;
        CentipedeSurfaceSample lead = _surfaceTrail[leadIndex];
        if (!TryResolveLeadSurfaceTangent(intent, lead, out Vector3 tangent))
        {
            _trailAdvanceRemainder = 0f;
            _leadSurfaceBlocked = true;
            return;
        }

        float advance = BaseSpeed * Mathf.Clamp(RunSpeed, 0f, 1f);
        _trailAdvanceRemainder += advance;
        float step = Mathf.Max(0.01f, TrailSampleSpacing);
        bool extended = false;
        while (_trailAdvanceRemainder >= step)
        {
            leadIndex = LeadEnd == CentipedeLeadEnd.Start ? 0 : _surfaceTrail.Count - 1;
            lead = _surfaceTrail[leadIndex];
            if (!TryFindNextSurface(ctx, lead, tangent, step, worldUp,
                    out CentipedeSurfaceSample next, out bool corner))
            {
                _trailAdvanceRemainder = 0f;
                _leadSurfaceBlocked = true;
                break;
            }
            if (LeadEnd == CentipedeLeadEnd.Start)
            {
                foreach (CentipedeSurfaceSample sample in _preparedSurfaceTransition)
                {
                    _surfaceTrail.Insert(0, sample);
                }
            }
            else
            {
                _surfaceTrail.AddRange(_preparedSurfaceTransition);
            }
            _preparedSurfaceTransition.Clear();
            LastMoveTargetKind = MoveTarget is not null
                ? CentipedeMoveTargetKind.External
                : corner ? CentipedeMoveTargetKind.Corner : CentipedeMoveTargetKind.Surface;
            LastMoveTarget = next.Point + next.Normal * (LeadChunk.Radius + SurfaceClearance);
            tangent = TransportTangent(tangent, lead.Normal, next.Normal);
            SetLeadSurfaceTangent(LeadEnd, tangent, next.Normal);
            _trailAdvanceRemainder -= step;
            extended = true;
            _leadSurfaceBlocked = false;
            _leadSurfaceBlockedTicks = 0;
        }
        if (!extended && LastMoveTargetKind == CentipedeMoveTargetKind.None)
        {
            LastMoveTarget = LeadChunk.Pos;
        }
        TrimTrail();
        RebuildArcLengths();
    }

    private bool TryFindNextSurface(in TickContext ctx, CentipedeSurfaceSample current,
        Vector3 tangent, float step, Vector3 worldUp,
        out CentipedeSurfaceSample next, out bool corner)
    {
        corner = false;
        Vector3 normal = SafeNormal(current.Normal, worldUp);
        Vector3 intended = current.Point + tangent * step;
        float probe = Mathf.Max(SurfaceProbeDistance, LeadChunk.Radius * 2f);

        if (ctx.Terrain.Raycast(intended + normal * probe * 0.45f,
            intended - normal * probe, out TerrainHit support)
            && ValidSurfaceHit(support))
        {
            Vector3 newNormal = support.Normal.Normalized();
            corner = newNormal.Dot(normal) < 0.94f;
            var candidate = new CentipedeSurfaceSample(
                support.Point, newNormal, support.ColliderId);
            if (AcceptSurfaceCandidate(ctx, current, candidate, tangent, step, out next))
            {
                return true;
            }
            // 仍能打到旧支撑面、但该面的球心位置已被前方墙/棱边占据，是内角换面
            // 的典型前一帧。不能在这里提前 false，必须继续固定顺序的前向/扇形探测。
        }

        Vector3 center = current.Point + normal * (LeadChunk.Radius + SurfaceClearance);
        if (ctx.Terrain.Raycast(center, center + tangent * (probe + step),
            out TerrainHit inner) && ValidSurfaceHit(inner))
        {
            var candidate = new CentipedeSurfaceSample(
                inner.Point, inner.Normal.Normalized(), inner.ColliderId);
            if (AcceptSurfaceCandidate(ctx, current, candidate, tangent, step, out next))
            {
                corner = true;
                return true;
            }
        }

        int steps = Mathf.Max(2, CornerProbeSteps);
        Vector3 origin = intended + normal * (LeadChunk.Radius + SurfaceClearance)
            - normal * (step * 0.5f);
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 rayDir = (-normal).Lerp(-tangent, t);
            if (rayDir.LengthSquared() < 1e-10f)
            {
                continue;
            }
            rayDir = rayDir.Normalized();
            Vector3 fanOrigin = origin + tangent * (step * t) - normal * (step * t);
            if (ctx.Terrain.Raycast(fanOrigin, fanOrigin + rayDir * (probe * 1.5f),
                out TerrainHit outer) && ValidSurfaceHit(outer))
            {
                var candidate = new CentipedeSurfaceSample(outer.Point,
                    outer.Normal.Normalized(), outer.ColliderId);
                if (AcceptSurfaceCandidate(ctx, current, candidate, tangent, step, out next))
                {
                    corner = true;
                    return true;
                }
            }
        }

        next = default;
        return false;
    }

    private bool AcceptSurfaceCandidate(in TickContext ctx,
        in CentipedeSurfaceSample current, in CentipedeSurfaceSample candidate,
        Vector3 travelTangent, float step, out CentipedeSurfaceSample first)
    {
        first = default;
        _preparedSurfaceTransition.Clear();
        float radius = LeadChunk.Radius;
        float chord = current.Point.DistanceTo(candidate.Point);
        float maxChord = Mathf.Max(step * 3f, SurfaceProbeDistance);
        if (chord > maxChord)
        {
            return false;
        }
        if (current.ColliderId != 0 && candidate.ColliderId != 0
            && current.ColliderId != candidate.ColliderId
            && chord > Mathf.Max(step * 1.75f, radius * 1.5f))
        {
            return false;
        }
        if (!FeasibleCenter(ctx, candidate, radius))
        {
            return false;
        }
        Vector3 fromNormal = SafeNormal(current.Normal, Vector3.Up);
        Vector3 toNormal = SafeNormal(candidate.Normal, fromNormal);
        if (fromNormal.Dot(toNormal) < -0.5f)
        {
            return false;
        }
        Vector3 forward = travelTangent - fromNormal * travelTangent.Dot(fromNormal);
        if (forward.LengthSquared() < 1e-8f)
        {
            return false;
        }
        forward = forward.Normalized();
        if (!HasForwardSurfaceProgress(current, candidate, forward, step, radius)
            || IsNearRecentLeadTrail(candidate, step, radius))
        {
            return false;
        }

        float arc = SurfaceArcStep(current, candidate);
        int pieces = Mathf.Clamp(Mathf.CeilToInt(arc / Mathf.Max(0.01f, step)),
            1, Mathf.Max(2, CornerProbeSteps * 2));
        var transition = new List<CentipedeSurfaceSample>(pieces);
        for (int i = 1; i <= pieces; i++)
        {
            float t = i / (float)pieces;
            Vector3 normal = SafeNormal(fromNormal.Slerp(toNormal, t), toNormal);
            Vector3 point = current.Point.Lerp(candidate.Point, t);
            if (fromNormal.Dot(toNormal) < 0.9999f
                && !TryMakeTransitionCenterFeasible(ctx, normal, radius, ref point))
            {
                return false;
            }
            var sample = new CentipedeSurfaceSample(
                point,
                normal,
                t < 0.5f ? current.ColliderId : candidate.ColliderId);
            transition.Add(sample);
        }
        CentipedeSurfaceSample resolved = transition[^1];
        if (!HasForwardSurfaceProgress(current, resolved, forward, step, radius)
            || IsNearRecentLeadTrail(resolved, step, radius))
        {
            return false;
        }
        _preparedSurfaceTransition.AddRange(transition);
        first = resolved;
        return true;
    }

    private bool HasForwardSurfaceProgress(in CentipedeSurfaceSample current,
        in CentipedeSurfaceSample candidate, Vector3 forward, float step, float radius)
    {
        Vector3 fromNormal = SafeNormal(current.Normal, Vector3.Up);
        Vector3 toNormal = SafeNormal(candidate.Normal, fromNormal);
        Vector3 nextForward = TransportTangent(forward, fromNormal, toNormal);
        Vector3 currentCenter = current.Point
            + fromNormal * (radius + SurfaceClearance);
        Vector3 candidateCenter = candidate.Point
            + toNormal * (radius + SurfaceClearance);
        Vector3 centerDelta = candidateCenter - currentCenter;
        Vector3 supportDelta = candidate.Point - current.Point;
        float minimum = Mathf.Max(0.0025f, step * 0.06f);
        float normalDot = fromNormal.Dot(toNormal);

        // 同一平面必须产生实际位移；法线换面则允许内角在同一可行球心原地换系，
        // 但这段不会再被 SurfaceArcStep 记成一截虚构身体长度。
        if (centerDelta.LengthSquared() < minimum * minimum && normalDot > 0.995f)
        {
            return false;
        }

        Vector3 expected = forward + nextForward;
        expected = SafeNormal(expected, forward);
        float centerProgress = centerDelta.Dot(expected);
        float supportProgress = supportDelta.Dot(forward);
        float backwardTolerance = Mathf.Max(0.004f, step * 0.25f);
        if (centerProgress < -backwardTolerance
            && supportProgress < -backwardTolerance)
        {
            return false;
        }
        // 法线基本没变却往 leader 身后落点，是旧表面回射或 hairpin，不是前进。
        return normalDot <= 0.98f || centerProgress >= minimum;
    }

    private bool IsNearRecentLeadTrail(in CentipedeSurfaceSample candidate,
        float step, float radius)
    {
        if (_surfaceTrail.Count < 3)
        {
            return false;
        }
        Vector3 normal = SafeNormal(candidate.Normal, Vector3.Up);
        Vector3 center = candidate.Point + normal * (radius + SurfaceClearance);
        float ignoreArc = Mathf.Max(step * 1.5f, 0.025f);
        float recentArc = Mathf.Max(radius * 4f, step * 12f);
        float near = Mathf.Max(0.0125f, step * 0.6f);
        float walked = 0f;

        if (LeadEnd == CentipedeLeadEnd.Start)
        {
            for (int i = 1; i < _surfaceTrail.Count; i++)
            {
                walked += SurfaceArcStep(_surfaceTrail[i - 1], _surfaceTrail[i]);
                if (walked > recentArc)
                {
                    break;
                }
                if (walked < ignoreArc)
                {
                    continue;
                }
                CentipedeSurfaceSample prior = _surfaceTrail[i];
                Vector3 priorCenter = prior.Point
                    + SafeNormal(prior.Normal, normal) * (radius + SurfaceClearance);
                if (center.DistanceSquaredTo(priorCenter) < near * near)
                {
                    return true;
                }
            }
        }
        else
        {
            for (int i = _surfaceTrail.Count - 2; i >= 0; i--)
            {
                walked += SurfaceArcStep(_surfaceTrail[i], _surfaceTrail[i + 1]);
                if (walked > recentArc)
                {
                    break;
                }
                if (walked < ignoreArc)
                {
                    continue;
                }
                CentipedeSurfaceSample prior = _surfaceTrail[i];
                Vector3 priorCenter = prior.Point
                    + SafeNormal(prior.Normal, normal) * (radius + SurfaceClearance);
                if (center.DistanceSquaredTo(priorCenter) < near * near)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryMakeTransitionCenterFeasible(in TickContext ctx,
        Vector3 normal, float radius, ref Vector3 point)
    {
        float offset = radius + SurfaceClearance;
        Vector3 center = point + normal * offset;
        for (int iteration = 0; iteration < 4; iteration++)
        {
            if (!ctx.Terrain.SpherePenetration(center, radius,
                    out Vector3 push, out float depth)
                || depth <= 0.0001f)
            {
                point = center - normal * offset;
                return true;
            }
            if (push.LengthSquared() < 1e-10f || !push.IsFinite()
                || !float.IsFinite(depth))
            {
                return false;
            }
            center += push.Normalized() * (depth + 0.0001f);
        }
        if (ctx.Terrain.SpherePenetration(center, radius,
                out _, out float remainingDepth)
            && remainingDepth > 0.002f)
        {
            return false;
        }
        point = center - normal * offset;
        return true;
    }

    private bool FeasibleCenter(in TickContext ctx, in CentipedeSurfaceSample sample, float radius)
    {
        Vector3 center = sample.Point + sample.Normal * (radius + SurfaceClearance);
        if (!ctx.Terrain.SpherePenetration(center, radius, out Vector3 push, out float depth))
        {
            return true;
        }
        return depth <= 0.002f
            && (push.LengthSquared() <= 1e-10f || push.Normalized().Dot(sample.Normal) > 0.7f);
    }

    private static bool ValidSurfaceHit(in TerrainHit hit) =>
        hit.Normal.LengthSquared() > 1e-10f && hit.Point.IsFinite() && hit.Normal.IsFinite();

    private void TrimTrail()
    {
        float retain = TotalBodyLength() + Mathf.Max(0.5f, SurfaceProbeDistance * 2f);
        while (_surfaceTrail.Count > 2 && TrailLength() > retain)
        {
            if (LeadEnd == CentipedeLeadEnd.Start)
            {
                _surfaceTrail.RemoveAt(_surfaceTrail.Count - 1);
            }
            else
            {
                _surfaceTrail.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// 重建列表起点到每个采样的累计球心路径长。外角的共点命中会因法线旋转产生
    /// 球心位移；内角也可能在同一可行球心换法线，后者不能凭法线角度虚构弧长。
    /// 过角已拆成小段，逐段球心弦长是稳定且不会在同一角落累计假路径的近似。
    /// </summary>
    private void RebuildArcLengths()
    {
        if (_surfaceTrail.Count == 0)
        {
            return;
        }
        CentipedeSurfaceSample first = _surfaceTrail[0];
        _surfaceTrail[0] = new CentipedeSurfaceSample(
            first.Point, first.Normal, first.ColliderId, 0f);
        float arc = 0f;
        for (int i = 1; i < _surfaceTrail.Count; i++)
        {
            CentipedeSurfaceSample previous = _surfaceTrail[i - 1];
            CentipedeSurfaceSample current = _surfaceTrail[i];
            arc += SurfaceArcStep(previous, current);
            _surfaceTrail[i] = new CentipedeSurfaceSample(
                current.Point, current.Normal, current.ColliderId, arc);
        }
    }

    private float SurfaceArcStep(in CentipedeSurfaceSample a,
        in CentipedeSurfaceSample b)
    {
        Vector3 normalA = SafeNormal(a.Normal, Vector3.Up);
        Vector3 normalB = SafeNormal(b.Normal, normalA);
        float offset = _maxSegmentRadius + SurfaceClearance;
        Vector3 centerA = a.Point + normalA * offset;
        Vector3 centerB = b.Point + normalB * offset;
        return centerA.DistanceTo(centerB);
    }

    private void UpdateSegmentTargets(Vector3 worldUp)
    {
        float distance = 0f;
        int count = Segments.Count;
        for (int order = 0; order < count; order++)
        {
            int index = LeadEnd == CentipedeLeadEnd.Start ? order : count - 1 - order;
            CentipedeSegment segment = Segments[index];
            SampleTrailFromLead(distance, worldUp,
                out CentipedeSurfaceSample sample,
                out Vector3 tangent);
            segment.SupportPoint = sample.Point;
            segment.ColliderId = sample.ColliderId;
            Vector3 targetNormal = SafeNormal(sample.Normal, segment.SupportNormal);
            Vector3 blended = segment.SupportNormal.Lerp(targetNormal, SupportBlend);
            segment.SupportNormal = SafeNormal(blended, targetNormal);
            Vector3 planarForward = tangent
                - segment.SupportNormal * tangent.Dot(segment.SupportNormal);
            segment.Forward = SafeNormal(planarForward, segment.Forward);
            Vector3 transportedSide = segment.Forward.Cross(segment.SupportNormal);
            if (transportedSide.LengthSquared() < 1e-8f)
            {
                transportedSide = segment.Side;
            }
            if (transportedSide.Dot(segment.Side) < 0f)
            {
                transportedSide = -transportedSide;
            }
            segment.Side = SafeNormal(transportedSide, Vector3.Back);
            segment.TargetCenter = sample.Point
                + segment.SupportNormal * (segment.Chunk.Radius + SurfaceClearance);

            if (order < count - 1)
            {
                distance += LinkLengthBetweenLeadOrders(order);
            }
        }
    }

    private float LinkLengthBetweenLeadOrders(int order)
    {
        if (LeadEnd == CentipedeLeadEnd.Start)
        {
            return Segments[order].RuntimeParams.LinkLengthToNext;
        }
        int followerIndex = Segments.Count - 2 - order;
        return Segments[Mathf.Max(0, followerIndex)].RuntimeParams.LinkLengthToNext;
    }

    private void SampleTrailFromLead(float targetDistance, Vector3 fallbackNormal,
        out CentipedeSurfaceSample sample, out Vector3 tangent)
    {
        if (_surfaceTrail.Count == 0)
        {
            sample = new CentipedeSurfaceSample(LeadChunk.Pos, fallbackNormal, 0);
            tangent = EffectiveMoveDirection();
            return;
        }
        float total = _surfaceTrail[^1].ArcLength;
        float targetArc = LeadEnd == CentipedeLeadEnd.Start
            ? Mathf.Clamp(targetDistance, 0f, total)
            : Mathf.Clamp(total - targetDistance, 0f, total);
        int low = 0;
        int high = _surfaceTrail.Count - 1;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (_surfaceTrail[middle].ArcLength <= targetArc)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        CentipedeSurfaceSample left = _surfaceTrail[low];
        CentipedeSurfaceSample right = _surfaceTrail[Mathf.Min(high, _surfaceTrail.Count - 1)];
        float interval = right.ArcLength - left.ArcLength;
        float t = interval <= 1e-8f ? 0f : (targetArc - left.ArcLength) / interval;
        Vector3 normal = SafeNormal(left.Normal.Slerp(right.Normal, t), left.Normal);
        sample = new CentipedeSurfaceSample(left.Point.Lerp(right.Point, t), normal,
            t < 0.5f ? left.ColliderId : right.ColliderId, targetArc);

        Vector3 listDelta = right.Point - left.Point;
        Vector3 listDirection = listDelta.LengthSquared() > 1e-10f
            ? listDelta.Normalized() : Vector3.Zero;
        if (listDirection.LengthSquared() < 1e-8f
            && left.Normal.Dot(right.Normal) < 0.9999f)
        {
            Vector3 axis = left.Normal.Cross(right.Normal);
            // axis×normal 是法线沿 left→right 的圆弧导数；旧的 normal×axis
            // 恰为反号，会让共点外角的局部 Forward 指回刚离开的表面。
            listDirection = SafeNormal(axis.Cross(normal), EffectiveMoveDirection());
        }
        tangent = LeadEnd == CentipedeLeadEnd.Start ? -listDirection : listDirection;
        if (tangent.LengthSquared() < 1e-8f)
        {
            tangent = SafeNormal(EffectiveMoveDirection(),
                Segments[LeadEnd == CentipedeLeadEnd.Start ? 0 : ^1].Forward);
        }
        tangent -= normal * tangent.Dot(normal);
        tangent = SafeNormal(tangent, EffectiveMoveDirection());
    }

    private void ApplySegmentForces(in TickContext ctx)
    {
        foreach (CentipedeSegment segment in Segments)
        {
            int gripping = 0;
            foreach (CentipedeLeg leg in segment.Legs)
            {
                if (leg.Gripping)
                {
                    gripping++;
                }
            }
            float legSupport = segment.Legs.Count == 0 ? 0f
                : Mathf.Clamp(gripping / (float)Mathf.Max(1, segment.Legs.Count / 2), 0f, 1f);
            bool blockedLead = _leadSurfaceBlocked
                && segment.Index == (LeadEnd == CentipedeLeadEnd.Start ? 0 : Segments.Count - 1);
            float trailSupport = segment.ColliderId != 0 && !blockedLead ? 0.45f : 0f;
            float targetConfidence = Mathf.Max(trailSupport, legSupport);
            segment.SupportConfidence = Mathf.Lerp(segment.SupportConfidence,
                targetConfidence, SupportBlend);

            BodyChunk chunk = segment.Chunk;
            Vector3 error = segment.TargetCenter - chunk.Pos;
            float adhesion = segment.RuntimeParams.AdhesionWeight * segment.SupportConfidence;
            chunk.Vel += error * (SurfaceServo * adhesion);
            float normalVelocity = chunk.Vel.Dot(segment.SupportNormal);
            chunk.Vel -= segment.SupportNormal * normalVelocity
                * (SurfaceDamping * segment.SupportConfidence);
            chunk.Vel -= ctx.GravityPerTick * segment.SupportConfidence;

            if (HasMoveIntent)
            {
                float traction = segment.SupportConfidence;
                Vector3 desired = segment.Forward
                    * (BaseSpeed * Mathf.Clamp(RunSpeed, 0f, 1f)
                       * segment.RuntimeParams.DriveWeight * traction);
                Vector3 along = segment.Forward * chunk.Vel.Dot(segment.Forward);
                chunk.Vel += (desired - along) * 0.35f;
            }
            if (chunk.Vel.LengthSquared() > MaxMoveSpeed * MaxMoveSpeed)
            {
                chunk.Vel = chunk.Vel.Normalized() * MaxMoveSpeed;
            }
        }
    }

    private void TickLegs(in TickContext ctx)
    {
        if (HasMoveIntent)
        {
            _gaitClock += Mathf.Clamp(RunSpeed, 0f, 1f);
        }
        int count = Segments.Count;
        for (int order = 0; order < count; order++)
        {
            int index = LeadEnd == CentipedeLeadEnd.Start ? order : count - 1 - order;
            CentipedeSegment segment = Segments[index];
            foreach (CentipedeLeg leg in segment.Legs)
            {
                float pairOffset = leg.PairIndex * 0.25f;
                float sideOffset = leg.Side > 0 ? 0.5f : 0f;
                float phase = HasMoveIntent
                    ? _gaitClock * GaitFrequency
                      - _segmentArcFromStart[index] / Mathf.Max(0.05f, GaitWavelength)
                      + pairOffset + sideOffset
                    : 0f;
                leg.Tick(ctx, phase, StanceFraction, RunSpeed);
            }
        }
    }

    private void UpdateSupportObservations()
    {
        int supported = 0;
        foreach (CentipedeSegment segment in Segments)
        {
            if (segment.Supported)
            {
                supported++;
            }
        }
        SupportedSegmentCount = supported;
    }

    /// <summary>
    /// Godot 的 GetRestInfo 一次只返回一个重叠面；长体停在墙顶/梁底角时，Body 的单次
    /// MTD 可能离开第一面却进入第二面。蜈蚣专属固定迭代补齐共同可行区，不改变共享 Body
    /// 的历史顺序与蜥蜴哈希。
    /// </summary>
    private void ResolveResidualBodyPenetration(ITerrainQuery terrain)
    {
        foreach (BodyChunk chunk in Body.Chunks)
        {
            for (int iteration = 0; iteration < 4; iteration++)
            {
                if (!terrain.SpherePenetration(chunk.Pos, chunk.TerrainRadius,
                        out Vector3 pushDir, out float depth)
                    || pushDir.LengthSquared() < 1e-10f || depth <= 0f)
                {
                    break;
                }
                Vector3 normal = pushDir.Normalized();
                chunk.Pos += normal * depth;
                SphereTerrain.RespondVelocity(normal, Body.SurfaceFriction, ref chunk.Vel);
                chunk.TerrainContact = true;
                chunk.ContactNormal = normal;
                chunk.ContactManifold.Add(normal);
            }
        }
    }

    private void ApplySelfAvoidance()
    {
        if (SelfAvoidanceStrength <= 0f || SelfAvoidanceCellSize <= 0f)
        {
            return;
        }
        // 至少覆盖最大排斥直径，固定 27 邻格才不会因调用侧给了过小 bucket 而漏碰撞。
        float cellSize = Mathf.Max(SelfAvoidanceCellSize, _maxSegmentRadius * 1.8f);
        int pairCap = Mathf.Max(1, MaxSelfAvoidancePairsPerSegment);
        int candidateCap = Mathf.Max(pairCap, MaxSelfAvoidanceCandidatesPerSegment);
        var buckets = new Dictionary<(int X, int Y, int Z), List<int>>();
        for (int i = 0; i < Segments.Count; i++)
        {
            Vector3 p = Segments[i].Chunk.Pos;
            var key = ((int)Mathf.Floor(p.X / cellSize),
                (int)Mathf.Floor(p.Y / cellSize),
                (int)Mathf.Floor(p.Z / cellSize));
            int pairs = 0;
            int candidates = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var neighbor = (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz);
                if (!buckets.TryGetValue(neighbor, out List<int>? others))
                {
                    continue;
                }
                foreach (int j in others)
                {
                    if (++candidates > candidateCap || pairs >= pairCap)
                    {
                        break;
                    }
                    if (i - j <= 2)
                    {
                        continue;
                    }
                    BodyChunk a = Segments[j].Chunk;
                    BodyChunk b = Segments[i].Chunk;
                    Vector3 delta = b.Pos - a.Pos;
                    float minimum = (a.Radius + b.Radius) * 0.9f;
                    float distance = delta.Length();
                    if (distance >= minimum)
                    {
                        continue;
                    }
                    Vector3 direction = distance < 1e-8f
                        ? DeterministicSeparationAxis(i, j)
                        : delta / distance;
                    float push = Mathf.Min(0.03f, (minimum - distance) * SelfAvoidanceStrength);
                    a.Vel -= direction * push;
                    b.Vel += direction * push;
                    pairs++;
                }
            }
            if (!buckets.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>();
                buckets.Add(key, bucket);
            }
            bucket.Add(i);
        }
    }

    private void InvalidateSurfaceState()
    {
        _surfaceTrail.Clear();
        _preparedSurfaceTransition.Clear();
        ClearAllLeadSurfaceTangents();
        _surfaceInitialized = false;
        _leadSurfaceBlocked = false;
        _leadSurfaceBlockedTicks = 0;
        _trailAdvanceRemainder = 0f;
        SupportedSegmentCount = 0;
        foreach (CentipedeSegment segment in Segments)
        {
            segment.SupportConfidence = 0f;
            segment.ColliderId = 0;
        }
        foreach (CentipedeLeg leg in Legs)
        {
            leg.ForceRelease();
        }
    }

    private float TotalBodyLength()
    {
        float total = 0f;
        for (int i = 0; i < Segments.Count - 1; i++)
        {
            total += Segments[i].RuntimeParams.LinkLengthToNext;
        }
        return total;
    }

    private float TrailLength()
    {
        float total = 0f;
        for (int i = 1; i < _surfaceTrail.Count; i++)
        {
            total += SurfaceArcStep(_surfaceTrail[i - 1], _surfaceTrail[i]);
        }
        return total;
    }

    private bool TryResolveLeadSurfaceTangent(Vector3 intent,
        in CentipedeSurfaceSample lead, out Vector3 tangent)
    {
        Vector3 normal = SafeNormal(lead.Normal, Vector3.Up);
        Vector3 projectedInput = intent - normal * intent.Dot(normal);
        // 小于约 0.6° 的表面投影视为退化：墙面法线的浮点微扰不应把已经运输到
        // “向下”的切线重置成极小但归一化后的任意方向。
        if (projectedInput.LengthSquared() >= 1e-4f)
        {
            tangent = projectedInput.Normalized();
            SetLeadSurfaceTangent(LeadEnd, tangent, normal);
            return true;
        }

        if (!TryGetLeadSurfaceTangent(LeadEnd,
                out Vector3 savedTangent, out Vector3 savedNormal))
        {
            TrySeedLeadSurfaceTangentFromTrail(LeadEnd);
        }
        if (!TryGetLeadSurfaceTangent(LeadEnd, out savedTangent, out savedNormal))
        {
            tangent = Vector3.Zero;
            return false;
        }

        tangent = TransportTangent(savedTangent, savedNormal, normal);
        tangent -= normal * tangent.Dot(normal);
        if (tangent.LengthSquared() < 1e-8f)
        {
            tangent = Vector3.Zero;
            return false;
        }
        tangent = tangent.Normalized();
        SetLeadSurfaceTangent(LeadEnd, tangent, normal);
        return true;
    }

    private bool TrySeedLeadSurfaceTangentFromTrail(CentipedeLeadEnd end)
    {
        if (_surfaceTrail.Count < 2)
        {
            return false;
        }
        int endpoint = end == CentipedeLeadEnd.Start ? 0 : _surfaceTrail.Count - 1;
        int neighbor = end == CentipedeLeadEnd.Start ? 1 : _surfaceTrail.Count - 2;
        CentipedeSurfaceSample current = _surfaceTrail[endpoint];
        CentipedeSurfaceSample adjacent = _surfaceTrail[neighbor];
        Vector3 normal = SafeNormal(current.Normal, Vector3.Up);
        Vector3 outward = current.Point - adjacent.Point;
        outward -= normal * outward.Dot(normal);

        if (outward.LengthSquared() < 1e-8f
            && current.Normal.Dot(adjacent.Normal) < 0.9999f)
        {
            CentipedeSurfaceSample left = end == CentipedeLeadEnd.Start
                ? current : adjacent;
            CentipedeSurfaceSample right = end == CentipedeLeadEnd.Start
                ? adjacent : current;
            Vector3 axis = SafeNormal(left.Normal, normal)
                .Cross(SafeNormal(right.Normal, normal));
            Vector3 listDirection = axis.Cross(normal);
            outward = end == CentipedeLeadEnd.Start
                ? -listDirection : listDirection;
            outward -= normal * outward.Dot(normal);
        }
        if (outward.LengthSquared() < 1e-8f)
        {
            int chunkIndex = end == CentipedeLeadEnd.Start ? 0 : Segments.Count - 1;
            int adjacentChunkIndex = end == CentipedeLeadEnd.Start ? 1 : Segments.Count - 2;
            outward = Segments[chunkIndex].Chunk.Pos
                - Segments[adjacentChunkIndex].Chunk.Pos;
            outward -= normal * outward.Dot(normal);
        }
        if (outward.LengthSquared() < 1e-8f)
        {
            return false;
        }
        SetLeadSurfaceTangent(end, outward.Normalized(), normal);
        return true;
    }

    private bool TryGetLeadSurfaceTangent(CentipedeLeadEnd end,
        out Vector3 tangent, out Vector3 normal)
    {
        if (end == CentipedeLeadEnd.Start)
        {
            tangent = _startLeadSurfaceTangent;
            normal = _startLeadSurfaceNormal;
            return _hasStartLeadSurfaceTangent;
        }
        tangent = _endLeadSurfaceTangent;
        normal = _endLeadSurfaceNormal;
        return _hasEndLeadSurfaceTangent;
    }

    private void SetLeadSurfaceTangent(CentipedeLeadEnd end,
        Vector3 tangent, Vector3 normal)
    {
        normal = SafeNormal(normal, Vector3.Up);
        tangent -= normal * tangent.Dot(normal);
        if (tangent.LengthSquared() < 1e-8f)
        {
            ClearLeadSurfaceTangent(end);
            return;
        }
        tangent = tangent.Normalized();
        if (end == CentipedeLeadEnd.Start)
        {
            _startLeadSurfaceTangent = tangent;
            _startLeadSurfaceNormal = normal;
            _hasStartLeadSurfaceTangent = true;
        }
        else
        {
            _endLeadSurfaceTangent = tangent;
            _endLeadSurfaceNormal = normal;
            _hasEndLeadSurfaceTangent = true;
        }
    }

    private void ClearLeadSurfaceTangent(CentipedeLeadEnd end)
    {
        if (end == CentipedeLeadEnd.Start)
        {
            _startLeadSurfaceTangent = Vector3.Zero;
            _startLeadSurfaceNormal = Vector3.Up;
            _hasStartLeadSurfaceTangent = false;
        }
        else
        {
            _endLeadSurfaceTangent = Vector3.Zero;
            _endLeadSurfaceNormal = Vector3.Up;
            _hasEndLeadSurfaceTangent = false;
        }
    }

    private void ClearAllLeadSurfaceTangents()
    {
        ClearLeadSurfaceTangent(CentipedeLeadEnd.Start);
        ClearLeadSurfaceTangent(CentipedeLeadEnd.End);
    }

    private static Vector3 TransportTangent(Vector3 tangent, Vector3 oldNormal,
        Vector3 normal)
    {
        Vector3 projected = tangent - normal * tangent.Dot(normal);
        if (projected.LengthSquared() > 1e-10f)
        {
            return projected.Normalized();
        }
        Vector3 axis = SafeNormal(oldNormal, Vector3.Up)
            .Cross(SafeNormal(normal, oldNormal));
        if (axis.LengthSquared() > 1e-10f)
        {
            Vector3 turned = SafeNormal(normal, oldNormal).Cross(axis);
            Vector3 oldTurned = SafeNormal(oldNormal, Vector3.Up).Cross(axis);
            if (oldTurned.Dot(tangent) < 0f)
            {
                turned = -turned;
            }
            return SafeNormal(turned, tangent);
        }
        return SafeNormal(tangent, Vector3.Right);
    }

    private static Vector3 SafeDir(Vector3 from, Vector3 to, Vector3 fallback)
    {
        Vector3 delta = to - from;
        return SafeNormal(delta, fallback);
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Right;
    }

    private static Vector3 DeterministicSeparationAxis(int i, int j)
    {
        int selector = (i * 73856093 ^ j * 19349663) & 3;
        return selector switch
        {
            0 => Vector3.Right,
            1 => Vector3.Up,
            2 => Vector3.Back,
            _ => new Vector3(1f, 1f, 0f).Normalized(),
        };
    }
}
