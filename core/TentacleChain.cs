using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 拟态草触手的独立粒子。它不是 <see cref="Limb"/>：没有 plant-and-trail、抓足或步态状态。
/// Vel 是米/tick；LastPos 只供插值与运动扫掠。
/// </summary>
public sealed class TentacleSegmentState
{
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public readonly float Radius;
    public readonly float Mass;
    public bool TerrainContact { get; internal set; }
    public Vector3 ContactNormal { get; internal set; } = Vector3.Up;

    internal TentacleSegmentState(Vector3 position, float radius, float mass)
    {
        Pos = position;
        LastPos = position;
        Radius = radius;
        Mass = mass;
    }

    public Vector3 LerpPos(float timeStacker) => LastPos.Lerp(Pos, timeStacker);
}

/// <summary>
/// RW Tentacle/TentacleChunk 的 3D、无引擎版本：只抗拉的多段链、按弧长追引导线、
/// 球体扫掠/MTD、轻量自避，以及不能穿墙的确定性回卷恢复。
/// </summary>
public sealed class TentacleChain
{
    private const int BlockedStateUnknown = -2;

    private static readonly (float A, float B)[] DetourDirections =
    {
        (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
        (0.70710677f, 0.70710677f), (-0.70710677f, 0.70710677f),
        (0.70710677f, -0.70710677f), (-0.70710677f, -0.70710677f),
    };

    public readonly BodyChunk Anchor;
    public readonly TentacleSegmentState[] Segments;

    /// <summary>仅公开真实折点；直线为 0，绕障最多 2。根与最终目标是隐含端点。</summary>
    public IReadOnlyList<Vector3> GuidePoints => _guideBends;
    public int BacktrackFrom { get; private set; } = -1;

    private readonly TentaclePlantParams _parameters;
    private readonly List<Vector3> _guidePoints = new(4);
    private readonly List<Vector3> _guideBends = new(2);
    private readonly List<Vector3> _lastSafeGuide = new(4);
    private Vector3 _lastGuideTarget;
    private int _guideAge;
    private int _sameBlockedTicks;
    private int _lastBlockedFrom = -1;
    private int _rebuildTicks;
    private int _routingQueries;
    private bool _guideBlocked;

    internal TentacleChain(
        BodyChunk anchor,
        TentaclePlantParams parameters,
        Vector3 outward)
    {
        Anchor = anchor;
        _parameters = parameters;
        Segments = new TentacleSegmentState[parameters.SegmentCount];
        float spawnLength = parameters.Length * parameters.SpawnExtension;
        for (int i = 0; i < Segments.Length; i++)
        {
            float t = (i + 1f) / Segments.Length;
            float radius = Mathf.Lerp(
                parameters.RootRadius,
                parameters.TipRadius,
                i / (float)(Segments.Length - 1));
            float mass = Mathf.Lerp(
                parameters.RootMass,
                parameters.TipMass,
                i / (float)(Segments.Length - 1));
            Segments[i] = new TentacleSegmentState(
                anchor.Pos + outward * (spawnLength * t),
                radius,
                mass);
        }
        Vector3 initialGoal = anchor.Pos + outward * parameters.WanderCenterDistance;
        SetStraightGuide(initialGoal);
        CopyGuide(_guidePoints, _lastSafeGuide);
        _lastGuideTarget = initialGoal;
    }

    /// <summary>
    /// 消费上个 tick 已注入的速度，依次更新路径、约束、碰撞与堵塞恢复。
    /// 当 tick 的感知/attack 结果不会倒灌进这次积分。
    /// </summary>
    internal void TickPhysics(
        in TickContext ctx,
        Vector3 goal,
        Vector3 outward,
        Vector3 tangent,
        float extension)
    {
        _routingQueries = 0;
        bool rebuildingAtTickStart = _rebuildTicks > 0;
        _guideAge++;
        bool movedGoal = goal.DistanceSquaredTo(_lastGuideTarget) >=
            _parameters.GuideTargetMoveThreshold * _parameters.GuideTargetMoveThreshold;
        if (_rebuildTicks > 0)
        {
            CopyGuide(_lastSafeGuide, _guidePoints);
            SyncPublicGuide();
        }
        else if (_guideAge >= _parameters.GuideRefreshTicks || movedGoal)
        {
            RefreshGuide(ctx.Terrain, goal, outward, tangent);
            _guideAge = 0;
            _lastGuideTarget = goal;
        }

        extension = Mathf.Clamp(extension, 0f, 1f);
        float effectiveLength = _parameters.Length *
            Mathf.Lerp(_parameters.RetractedLengthFraction, 1f, extension);

        DampSegments();
        ApplySelfAvoidance(outward, tangent);
        Integrate();
        for (int i = 0; i < _parameters.ConstraintIterations; i++)
        {
            RelaxRope(effectiveLength);
        }
        CollideIntegratedMotion(ctx);
        DetectBlockedSuffix(ctx.Terrain, outward);
        if (rebuildingAtTickStart && _rebuildTicks > 0 && --_rebuildTicks == 0)
        {
            int stillBlocked = FindBlockedSuffix(ctx.Terrain, outward);
            if (stillBlocked >= 0)
            {
                BacktrackFrom = stillBlocked;
                _lastBlockedFrom = stillBlocked;
                _rebuildTicks = _parameters.RebuildTicks;
            }
            else if (stillBlocked == -1)
            {
                BacktrackFrom = -1;
                _lastBlockedFrom = -1;
            }
            else
            {
                // 没有查询预算就不能宣称已经脱困；下一 tick 再复查。
                _rebuildTicks = 1;
            }
        }
    }

    /// <summary>
    /// 在本 tick 物理解算完成后注入形态力，留给下一 tick 消费。
    /// 这使 charge/windup/strike 的固定序不依赖控制器内部调用细节。
    /// </summary>
    internal void InjectShapeForces(
        Vector3 goal,
        Vector3 outward,
        float extension,
        float windupAmount,
        Vector3 strikeImpulse)
    {
        extension = Mathf.Clamp(extension, 0f, 1f);
        float effectiveLength = _parameters.Length *
            Mathf.Lerp(_parameters.RetractedLengthFraction, 1f, extension);
        ApplyServoForces(goal, outward, effectiveLength, windupAmount);
        Tip.Vel += strikeImpulse;
    }

    public void Shift(Vector3 delta)
    {
        foreach (TentacleSegmentState segment in Segments)
        {
            segment.Pos += delta;
            segment.LastPos += delta;
        }
        ShiftGuide(_guidePoints, delta);
        ShiftGuide(_lastSafeGuide, delta);
        SyncPublicGuide();
        _lastGuideTarget += delta;
    }

    internal void Remount(Vector3 outward)
    {
        float spawnLength = _parameters.Length * _parameters.SpawnExtension;
        for (int i = 0; i < Segments.Length; i++)
        {
            float t = (i + 1f) / Segments.Length;
            Vector3 position = Anchor.Pos + outward * (spawnLength * t);
            Segments[i].Pos = position;
            Segments[i].LastPos = position;
            Segments[i].Vel = Vector3.Zero;
            Segments[i].TerrainContact = false;
            Segments[i].ContactNormal = outward;
        }
        Vector3 initialGoal = Anchor.Pos + outward * _parameters.WanderCenterDistance;
        SetStraightGuide(initialGoal);
        CopyGuide(_guidePoints, _lastSafeGuide);
        _lastGuideTarget = initialGoal;
        _guideAge = 0;
        _sameBlockedTicks = 0;
        _lastBlockedFrom = -1;
        _rebuildTicks = 0;
        _guideBlocked = false;
        BacktrackFrom = -1;
    }

    private void DampSegments()
    {
        foreach (TentacleSegmentState segment in Segments)
        {
            segment.Vel *= _parameters.SegmentDamping;
        }
    }

    private void ApplyServoForces(
        Vector3 goal,
        Vector3 outward,
        float effectiveLength,
        float windupAmount)
    {
        Vector3 recoilPoint = (Tip.Pos + Anchor.Pos +
            outward * _parameters.WanderCenterDistance) * 0.5f;
        float desiredGuideSpan = Math.Min(effectiveLength, CurrentGuideLength());
        for (int i = 0; i < Segments.Length; i++)
        {
            TentacleSegmentState segment = Segments[i];
            float t = (i + 1f) / Segments.Length;
            Vector3 guideTarget = PointAlongGuide(desiredGuideSpan * t);
            float guideGain = i == Segments.Length - 1
                ? _parameters.TipGoalAttraction
                : _parameters.InnerGoalAttraction;
            segment.Vel += Toward(segment.Pos, guideTarget, guideGain);
            segment.Vel += Toward(
                segment.Pos,
                PointAlongGuide(CurrentGuideLength() * t),
                _parameters.GuideAttraction);
            if (_guideBlocked)
            {
                // 找不到满足粗根净空的局部路线时，PullOnly 绳约束本身不会把
                // 已伸出的细梢压回安全停点；显式沿已背书前缀回收，避免它凭
                // 较小半径继续钻过整株无法通过的窄缝。
                segment.Vel += Toward(
                    segment.Pos,
                    guideTarget,
                    _parameters.BacktrackSpeed * Mathf.Lerp(0.35f, 1f, t));
            }

            if (windupAmount > 0f)
            {
                segment.Vel += SafeDirection(recoilPoint - goal, -outward) *
                    (_parameters.WindupPull * windupAmount);
            }

            if (BacktrackFrom >= 0 && i >= BacktrackFrom)
            {
                Vector3 previous = i == 0 ? Anchor.Pos : Segments[i - 1].Pos;
                segment.Vel += Toward(segment.Pos, previous, _parameters.BacktrackSpeed);
            }
            segment.Vel += outward * (_parameters.OutwardRootForce * (1f - t));
        }

        // 原作的隔一节互推：给绳链一个有机曲率，但不新增刚性连接。
        for (int i = 2; i < Segments.Length; i++)
        {
            Vector3 dir = SafeDirection(Segments[i].Pos - Segments[i - 2].Pos, outward);
            Segments[i].Vel += dir * _parameters.ShapeSeparationForce;
            Segments[i - 2].Vel -= dir * _parameters.ShapeSeparationForce;
        }
    }

    private void RelaxRope(float effectiveLength)
    {
        float linkLength = effectiveLength / Segments.Length;
        TentacleSegmentState first = Segments[0];
        CorrectStretch(Anchor.Pos, first, linkLength, previous: null);
        for (int i = 1; i < Segments.Length; i++)
        {
            CorrectStretch(Segments[i - 1].Pos, Segments[i], linkLength, Segments[i - 1]);
        }
    }

    private static void CorrectStretch(
        Vector3 previousPosition,
        TentacleSegmentState current,
        float linkLength,
        TentacleSegmentState? previous)
    {
        Vector3 delta = current.Pos - previousPosition;
        float distance = delta.Length();
        if (distance <= linkLength || distance <= 1e-6f)
        {
            return;
        }
        Vector3 correction = delta / distance * (distance - linkLength);
        if (previous is null)
        {
            current.Pos -= correction;
            current.Vel -= correction;
        }
        else
        {
            correction *= 0.5f;
            current.Pos -= correction;
            current.Vel -= correction;
            previous.Pos += correction;
            previous.Vel += correction;
        }
    }

    private void ApplySelfAvoidance(Vector3 outward, Vector3 tangent)
    {
        Vector3 bitangent = SafeDirection(
            outward.Cross(tangent),
            TentaclePlantFactory.StablePerpendicular(outward));
        for (int i = 0; i < Segments.Length; i++)
        {
            for (int j = i + 2; j < Segments.Length; j++)
            {
                TentacleSegmentState a = Segments[i];
                TentacleSegmentState b = Segments[j];
                Vector3 delta = b.Pos - a.Pos;
                float distance = delta.Length();
                float minimum = a.Radius + b.Radius + _parameters.SelfAvoidancePadding;
                if (distance >= minimum)
                {
                    continue;
                }
                Vector3 direction = distance > 1e-6f
                    ? delta / distance
                    : StablePairDirection(i, j, tangent, bitangent, outward);
                float push = (minimum - distance) * _parameters.SelfAvoidanceStrength;
                a.Vel -= direction * push;
                b.Vel += direction * push;
            }
        }
    }

    private void Integrate()
    {
        foreach (TentacleSegmentState segment in Segments)
        {
            segment.Vel = segment.Vel.LimitLength(_parameters.SegmentVelocityCap);
            segment.LastPos = segment.Pos;
            segment.Pos += segment.Vel;
            segment.TerrainContact = false;
        }
    }

    private void CollideIntegratedMotion(in TickContext ctx)
    {
        foreach (TentacleSegmentState segment in Segments)
        {
            Vector3 motion = segment.Pos - segment.LastPos;
            float motionLength = motion.Length();
            if (motionLength > 1e-6f &&
                ctx.Terrain.Raycast(
                    segment.LastPos,
                    segment.Pos + motion / motionLength * segment.Radius,
                    out TerrainHit hit) &&
                SphereTerrain.Resolve(
                    hit,
                    segment.Radius,
                    0.35f,
                    ref segment.Pos,
                    ref segment.Vel,
                    out Vector3 normal))
            {
                segment.TerrainContact = true;
                segment.ContactNormal = normal;
                // 全方向偏转：去掉撞入法线的分量，保留沿面滑动。
                float into = segment.Vel.Dot(normal);
                if (into < 0f)
                {
                    segment.Vel -= normal * into;
                }
            }

            if (ctx.Terrain.SpherePenetration(
                    segment.Pos,
                    segment.Radius,
                    out Vector3 pushDir,
                    out float depth))
            {
                segment.Pos += pushDir * depth;
                SphereTerrain.RespondVelocity(pushDir, 0.35f, ref segment.Vel);
                segment.TerrainContact = true;
                segment.ContactNormal = pushDir;
            }
        }
    }

    private void DetectBlockedSuffix(ITerrainQuery terrain, Vector3 outward)
    {
        if (_rebuildTicks > 0)
        {
            return;
        }
        int blocked = FindBlockedSuffix(terrain, outward);

        if (blocked == BlockedStateUnknown)
        {
            return;
        }
        if (blocked == -1)
        {
            _sameBlockedTicks = 0;
            _lastBlockedFrom = -1;
            CopyGuide(_guidePoints, _lastSafeGuide);
            return;
        }

        if (_lastBlockedFrom == blocked)
        {
            _sameBlockedTicks++;
        }
        else
        {
            _lastBlockedFrom = blocked;
            _sameBlockedTicks = 1;
        }
        if (_sameBlockedTicks >= _parameters.BlockedSuffixTicks)
        {
            CopyGuide(_lastSafeGuide, _guidePoints);
            SyncPublicGuide();
            BacktrackFrom = blocked;
            _rebuildTicks = _parameters.RebuildTicks;
            _sameBlockedTicks = 0;
        }
    }

    private int FindBlockedSuffix(ITerrainQuery terrain, Vector3 outward)
    {
        // Anchor 本身按设计埋在安装面里；首条可见链从粗根球刚退出表面的位置开始检查。
        Vector3 previous = Anchor.Pos + outward *
            Math.Max(0f, _parameters.RootRadius - _parameters.RootSurfaceOffset);
        float previousRadius = _parameters.RootRadius;
        for (int i = 0; i < Segments.Length; i++)
        {
            TentacleSegmentState segment = Segments[i];
            float clearance = Math.Max(previousRadius, segment.Radius);
            if (!TryRouteSegmentClear(
                    terrain,
                    previous,
                    segment.Pos,
                    clearance,
                    segment.Radius + 0.02f,
                    out bool clear,
                    out _))
            {
                return BlockedStateUnknown;
            }
            if (!clear)
            {
                return i;
            }
            previous = segment.Pos;
            previousRadius = segment.Radius;
        }
        return -1;
    }

    private void RefreshGuide(
        ITerrainQuery terrain,
        Vector3 goal,
        Vector3 outward,
        Vector3 tangentHint)
    {
        _routingQueries = 0;
        Vector3 start = Anchor.Pos + outward *
            (_parameters.RootRadius + _parameters.GuideSurfaceOffset);
        if (!TryRouteSegmentClear(
                terrain,
                start,
                goal,
                _parameters.GuideClearanceRadius,
                0f,
                out bool directClear,
                out TerrainHit firstHit))
        {
            return;
        }
        if (directClear)
        {
            _guideBlocked = false;
            SetGuide(start, goal);
            return;
        }
        Vector3 targetDirection = SafeDirection(goal - start, outward);
        if (firstHit.Normal.LengthSquared() <= 1e-10f)
        {
            SetBlockedGuide(start, firstHit.Point, targetDirection);
            return;
        }

        if (TryFindDetour(
                terrain,
                start,
                goal,
                firstHit,
                targetDirection,
                tangentHint,
                out Vector3 firstBend,
                out TerrainHit secondHit,
                out bool reachesGoal))
        {
            if (reachesGoal)
            {
                _guideBlocked = false;
                SetGuide(start, firstBend, goal);
                return;
            }

            if (TryFindDetour(
                    terrain,
                    firstBend,
                    goal,
                secondHit,
                    targetDirection,
                    tangentHint,
                out Vector3 secondBend,
                out _,
                out bool secondReachesGoal) &&
                secondReachesGoal &&
                TryRouteSegmentClear(
                    terrain,
                    start,
                    firstBend,
                    _parameters.GuideClearanceRadius,
                    0f,
                    out bool firstLegClear,
                    out _) &&
                firstLegClear)
            {
                _guideBlocked = false;
                SetGuide(start, firstBend, secondBend, goal);
                return;
            }
        }
        // 净空探针确认直线不可行、局部候选又全部失败时，不能继续沿旧的
        // 直达 carrot 把细梢硬塞进窄缝。首个命中之前的前缀已经由
        // TryRouteSegmentClear 背书，故把导引终点退到命中点前方作为安全停点；
        // 后续刷新会在地形或目标改变后重新尝试展开。
        SetBlockedGuide(start, firstHit.Point, targetDirection);
    }

    private void SetBlockedGuide(
        Vector3 start,
        Vector3 obstructionPoint,
        Vector3 targetDirection)
    {
        float hitProgress = Math.Max(
            0f,
            (obstructionPoint - start).Dot(targetDirection));
        float stopProgress = Math.Max(
            0f,
            hitProgress -
            _parameters.GuideClearanceRadius -
            _parameters.GuideSurfaceOffset);
        _guideBlocked = true;
        SetGuide(start, start + targetDirection * stopProgress);
    }

    private bool TryFindDetour(
        ITerrainQuery terrain,
        Vector3 start,
        Vector3 goal,
        in TerrainHit obstacle,
        Vector3 targetDirection,
        Vector3 tangentHint,
        out Vector3 bend,
        out TerrainHit nextObstacle,
        out bool reachesGoal)
    {
        bend = default;
        nextObstacle = default;
        reachesGoal = false;
        Vector3 normal = obstacle.Normal.Normalized();
        Vector3 tangentA = tangentHint - normal * tangentHint.Dot(normal);
        if (tangentA.LengthSquared() <= 1e-10f)
        {
            tangentA = TentaclePlantFactory.StablePerpendicular(normal);
        }
        tangentA = tangentA.Normalized();
        Vector3 tangentB = SafeDirection(normal.Cross(tangentA),
            TentaclePlantFactory.StablePerpendicular(normal));

        foreach ((float a, float b) in DetourDirections)
        {
            Vector3 lateral = tangentA * a + tangentB * b;
            Vector3 candidate = obstacle.Point +
                normal * _parameters.GuideSurfaceOffset +
                lateral * _parameters.GuideDetourDistance;
            float progress = (candidate - start).Dot(targetDirection);
            if (progress <= _parameters.GuideForwardProgressEpsilon)
            {
                continue;
            }
            if (!TryRoutePenetration(
                    terrain,
                    candidate,
                    _parameters.GuideClearanceRadius,
                    out bool penetrates,
                    out _,
                    out _))
            {
                break;
            }
            if (penetrates)
            {
                continue;
            }
            if (!TryRouteSegmentClear(
                    terrain,
                    start,
                    candidate,
                    _parameters.GuideClearanceRadius,
                    0f,
                    out bool approachClear,
                    out _))
            {
                break;
            }
            if (!approachClear)
            {
                continue;
            }
            if (!TryRouteSegmentClear(
                    terrain,
                    candidate,
                    goal,
                    _parameters.GuideClearanceRadius,
                    0f,
                    out bool goalClear,
                    out TerrainHit hit))
            {
                break;
            }
            if (goalClear)
            {
                bend = candidate;
                reachesGoal = true;
                return true;
            }
            if (hit.Normal.LengthSquared() > 1e-10f &&
                hit.Point.DistanceSquaredTo(candidate) >
                _parameters.GuideForwardProgressEpsilon *
                _parameters.GuideForwardProgressEpsilon)
            {
                bend = candidate;
                nextObstacle = hit;
                return true;
            }
        }
        return false;
    }

    private bool TryRouteSegmentClear(
        ITerrainQuery terrain,
        Vector3 from,
        Vector3 to,
        float clearanceRadius,
        float endTouchTolerance,
        out bool clear,
        out TerrainHit obstruction)
    {
        clear = false;
        obstruction = default;
        if (!TryRouteRaycast(terrain, from, to, out bool didHit, out TerrainHit hit))
        {
            return false;
        }
        if (didHit &&
            (endTouchTolerance <= 0f ||
             hit.Point.DistanceSquaredTo(to) >
             endTouchTolerance * endTouchTolerance))
        {
            obstruction = hit;
            return true;
        }

        float length = from.DistanceTo(to);
        int sampleCount = Math.Max(
            1,
            Mathf.CeilToInt(length / Math.Max(clearanceRadius * 1.5f, 0.05f)));
        for (int i = 1; i < sampleCount; i++)
        {
            Vector3 sample = from.Lerp(to, i / (float)sampleCount);
            if (!TryRoutePenetration(
                    terrain,
                    sample,
                    clearanceRadius,
                    out bool penetrates,
                    out Vector3 pushDir,
                    out float depth))
            {
                return false;
            }
            if (penetrates)
            {
                Vector3 normal = SafeDirection(pushDir, Vector3.Up);
                obstruction = new TerrainHit(
                    sample - normal * Math.Max(0f, clearanceRadius - depth),
                    normal,
                    0UL);
                return true;
            }
        }
        clear = true;
        return true;
    }

    private bool TryRouteRaycast(
        ITerrainQuery terrain,
        Vector3 from,
        Vector3 to,
        out bool didHit,
        out TerrainHit hit)
    {
        if ((to - from).LengthSquared() <= 1e-10f)
        {
            didHit = false;
            hit = default;
            return true;
        }
        if (_routingQueries >= _parameters.RoutingQueryBudget)
        {
            didHit = false;
            hit = default;
            return false;
        }
        _routingQueries++;
        didHit = terrain.Raycast(from, to, out hit);
        return true;
    }

    private bool TryRoutePenetration(
        ITerrainQuery terrain,
        Vector3 point,
        float radius,
        out bool penetrates,
        out Vector3 pushDir,
        out float depth)
    {
        if (_routingQueries >= _parameters.RoutingQueryBudget)
        {
            penetrates = false;
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
        _routingQueries++;
        penetrates = terrain.SpherePenetration(point, radius, out pushDir, out depth);
        return true;
    }

    private Vector3 PointAlongGuide(float distance)
    {
        if (_guidePoints.Count == 0)
        {
            return Anchor.Pos;
        }
        if (_guidePoints.Count == 1)
        {
            return _guidePoints[0];
        }
        float remaining = Mathf.Max(0f, distance);
        for (int i = 1; i < _guidePoints.Count; i++)
        {
            Vector3 a = _guidePoints[i - 1];
            Vector3 b = _guidePoints[i];
            float length = a.DistanceTo(b);
            if (remaining <= length && length > 1e-6f)
            {
                return a.Lerp(b, remaining / length);
            }
            remaining -= length;
        }
        return _guidePoints[^1];
    }

    private float CurrentGuideLength()
    {
        float total = 0f;
        for (int i = 1; i < _guidePoints.Count; i++)
        {
            total += _guidePoints[i - 1].DistanceTo(_guidePoints[i]);
        }
        return total;
    }

    private void SetStraightGuide(Vector3 goal) => SetGuide(Anchor.Pos, goal);

    private void SetGuide(params Vector3[] points)
    {
        _guidePoints.Clear();
        _guidePoints.AddRange(points);
        SyncPublicGuide();
    }

    private void SyncPublicGuide()
    {
        _guideBends.Clear();
        for (int i = 1; i < _guidePoints.Count - 1 && _guideBends.Count < 2; i++)
        {
            _guideBends.Add(_guidePoints[i]);
        }
    }

    private static void CopyGuide(List<Vector3> source, List<Vector3> destination)
    {
        destination.Clear();
        destination.AddRange(source);
    }

    private static void ShiftGuide(List<Vector3> guide, Vector3 delta)
    {
        for (int i = 0; i < guide.Count; i++)
        {
            guide[i] += delta;
        }
    }

    private static Vector3 Toward(Vector3 from, Vector3 to, float maxImpulse)
    {
        Vector3 delta = to - from;
        if (delta.LengthSquared() <= 1e-10f || maxImpulse <= 0f)
        {
            return Vector3.Zero;
        }
        return delta.LimitLength(0.5f) / 0.5f * maxImpulse;
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Up;
    }

    private static Vector3 StablePairDirection(
        int a,
        int b,
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 outward)
    {
        uint bits = unchecked((uint)(a * 73856093 ^ b * 19349663));
        float x = ((bits & 1023u) / 511.5f) - 1f;
        float y = (((bits >> 10) & 1023u) / 511.5f) - 1f;
        float z = (((bits >> 20) & 1023u) / 511.5f) - 1f;
        return SafeDirection(
            tangent * x + bitangent * y + outward * z,
            tangent);
    }

    public TentacleSegmentState Tip => Segments[^1];
}
