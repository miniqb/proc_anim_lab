using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Spider;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.SpiderSmoke;

/// <summary>
/// 跳跃攻击 / 飞行态 / 昏迷的无引擎回归（全部 opt-in 机制——Program.Main 的既有基线哈希
/// 由这些机制不被调用时逐位不变来背书；本文件断言的是它们被调用时的契约）：
/// ① 地面扑击：规划器给出的弹道在规划 tick 上把主身体节送到瞄准点（误差门 5cm），
///    随后触地结束飞行并重新抓稳；② 天花板俯冲：起跳速度必须离面（v0·n ≥ 0），同样精确
///    到点，无地形可触时按宽限超时结束；③ 路径被墙挡住时规划必须失败；④ 飞行中 Launch
///    打断弹道；⑤ 昏迷：腿不抓地、身体落地静止、可复活；全部序列双跑 bit-exact。
/// </summary>
internal static class LeapSmoke
{
    private static readonly Vector3 Gravity = new(0f, -36f * 0.025f * 0.025f, 0f);

    private readonly record struct PounceResult(
        bool Planned,
        float ArrivalError,
        float MaxModelError,
        bool StillLeapingAtArrival,
        bool EndedByContact,
        int EndTick,
        bool Regripped,
        int RegripTick,
        ulong Hash,
        bool Finite,
        float PlanSpeed,
        int FlightTicks);

    private readonly record struct DropResult(
        bool Planned,
        float OutwardSpeed,
        float ArrivalError,
        bool EndedByTimeout,
        int EndTick,
        ulong Hash,
        bool Finite);

    private readonly record struct LimpResult(
        bool NeverGripped,
        float RestHeight,
        float RestSpeed,
        bool Revived,
        int ReviveTick,
        ulong Hash,
        bool Finite);

    public static bool Check(out string[] messages)
    {
        var lines = new List<string>();
        bool ok = true;

        PounceResult pounceA = RunFloorPounce(SpiderFactory.SmallSpider());
        PounceResult pounceB = RunFloorPounce(SpiderFactory.SmallSpider());
        PounceResult pounceLarge = RunFloorPounce(SpiderFactory.LargeSpider());
        bool pounceOk = pounceA.Planned && pounceB.Planned && pounceLarge.Planned
            && pounceA.Hash == pounceB.Hash
            && pounceA.ArrivalError <= 0.05f
            && pounceLarge.ArrivalError <= 0.08f
            && pounceA.MaxModelError <= 0.05f
            && pounceA.StillLeapingAtArrival
            && pounceA.EndedByContact && pounceLarge.EndedByContact
            && pounceA.Regripped && pounceLarge.Regripped
            && pounceA.Finite && pounceLarge.Finite;
        ok &= pounceOk;
        lines.Add(
            $"pounce ok={pounceOk} plan={pounceA.Planned}/{pounceLarge.Planned} " +
            $"ticks={pounceA.FlightTicks}/{pounceLarge.FlightTicks} " +
            $"speed={pounceA.PlanSpeed * 40f:F1}/{pounceLarge.PlanSpeed * 40f:F1}m/s " +
            $"arrival={pounceA.ArrivalError:F3}/{pounceLarge.ArrivalError:F3}m " +
            $"model={pounceA.MaxModelError:F3}m contactEnd={pounceA.EndedByContact}@{pounceA.EndTick} " +
            $"regrip={pounceA.Regripped}@{pounceA.RegripTick}/{pounceLarge.RegripTick} " +
            $"hash={pounceA.Hash:X16}/{pounceB.Hash:X16}");

        DropResult dropA = RunCeilingDrop();
        DropResult dropB = RunCeilingDrop();
        bool dropOk = dropA.Planned && dropA.Hash == dropB.Hash
            && dropA.OutwardSpeed >= 0.02f
            && dropA.ArrivalError <= 0.05f
            && dropA.EndedByTimeout
            && dropA.Finite;
        ok &= dropOk;
        lines.Add(
            $"ceiling-drop ok={dropOk} plan={dropA.Planned} outward={dropA.OutwardSpeed * 40f:F2}m/s " +
            $"arrival={dropA.ArrivalError:F3}m timeoutEnd={dropA.EndedByTimeout}@{dropA.EndTick} " +
            $"hash={dropA.Hash:X16}/{dropB.Hash:X16}");

        bool blockedOk = CheckBlockedPath(out string blockedMessage);
        ok &= blockedOk;
        lines.Add($"blocked-path ok={blockedOk} {blockedMessage}");

        bool interruptOk = CheckLaunchInterrupt(out string interruptMessage);
        ok &= interruptOk;
        lines.Add($"launch-interrupt ok={interruptOk} {interruptMessage}");

        LimpResult limpA = RunLimp();
        LimpResult limpB = RunLimp();
        bool limpOk = limpA.NeverGripped && limpA.Hash == limpB.Hash
            && limpA.RestHeight <= 0.45f && limpA.RestHeight >= 0.05f
            && limpA.RestSpeed <= 0.005f
            && limpA.Revived
            && limpA.Finite;
        ok &= limpOk;
        lines.Add(
            $"limp ok={limpOk} neverGripped={limpA.NeverGripped} restHeight={limpA.RestHeight:F3}m " +
            $"restSpeed={limpA.RestSpeed:E2} revived={limpA.Revived}@{limpA.ReviveTick} " +
            $"hash={limpA.Hash:X16}/{limpB.Hash:X16}");

        messages = lines.ToArray();
        return ok;
    }

    private static SpiderLeapRequest Request(SpiderLocomotionController spider, Vector3 target,
        ITerrainQuery terrain, float minOutward = 0.02f)
    {
        _ = terrain;
        return new SpiderLeapRequest(
            from: spider.Primary.Pos,
            target: target,
            targetVelocity: Vector3.Zero,
            gravityPerTick: Gravity,
            airFriction: spider.AirborneAirFriction,
            surfaceNormal: spider.SupportNormal,
            minOutwardSpeed: minOutward,
            preferredSpeed: 0.22f,
            minTicks: 6,
            maxTicks: 60,
            maxSpeed: 0.6f,
            maxRise: 1.6f,
            clearanceRadius: spider.Primary.Radius);
    }

    private static long Settle(SpiderLocomotionController spider, ITerrainQuery terrain,
        long tick, long budget)
    {
        long deadline = tick + budget;
        while (tick < deadline)
        {
            tick++;
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            if (spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity)
            {
                break;
            }
        }
        return tick;
    }

    private static PounceResult RunFloorPounce(SpiderBreedParams breed)
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), breed);
        var hasher = new DeterminismHasher();
        long tick = Settle(spider, terrain, 0, 400);

        Vector3 target = spider.Primary.Pos + new Vector3(3.0f, 0.7f, 0.4f);
        SpiderLeapRequest request = Request(spider, target, terrain);
        bool planned = SpiderLeapPlanner.TryPlan(request, terrain, out SpiderLeapPlan plan);
        if (!planned)
        {
            return new PounceResult(false, float.PositiveInfinity, float.PositiveInfinity,
                false, false, -1, false, -1, 0UL, false, 0f, 0);
        }

        Vector3 from = spider.Primary.Pos;
        spider.BeginLeap(plan.LaunchVelocity, plan.FlightTicks);
        float arrivalError = float.PositiveInfinity;
        float maxModelError = 0f;
        bool stillLeaping = false;
        bool endedByContact = false;
        int endTick = -1;
        bool regripped = false;
        int regripTick = -1;
        bool finite = true;
        long start = tick;
        for (int n = 1; n <= 400; n++)
        {
            tick++;
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.Fold(spider.Leaping);
            hasher.Fold(spider.LeapTicks);
            finite &= spider.Primary.Pos.IsFinite() && spider.Rear.Pos.IsFinite();
            if (spider.Leaping && n <= plan.FlightTicks)
            {
                Vector3 model = SpiderLeapPlanner.PositionAfter(
                    from, plan.LaunchVelocity, Gravity, spider.AirborneAirFriction, n);
                maxModelError = Mathf.Max(maxModelError, (spider.Primary.Pos - model).Length());
            }
            if (n == plan.FlightTicks)
            {
                arrivalError = (spider.Primary.Pos - target).Length();
                stillLeaping = spider.Leaping && spider.LeapTicks == plan.FlightTicks;
            }
            if (endTick < 0 && !spider.Leaping && n > 1)
            {
                endTick = n;
                endedByContact = spider.LeapEndedByContact;
            }
            if (endTick >= 0 && !regripped
                && spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity)
            {
                regripped = true;
                regripTick = (int)(tick - start);
                break;
            }
        }
        return new PounceResult(true, arrivalError, maxModelError, stillLeaping,
            endedByContact, endTick, regripped, regripTick, hasher.Value, finite,
            plan.Speed, plan.FlightTicks);
    }

    private static DropResult RunCeilingDrop()
    {
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            Vector3.Zero, SpiderFactory.SmallSpider());
        var terrain = new CeilingOnlyTerrain(spider.Primary.Pos.Y + 0.42f);
        var hasher = new DeterminismHasher();
        // 与 Program.RunDirectCeilingReacquire 同法：先零重力挂上天花板，再开重力
        //（抓稳后 GravityScale=0，身体不掉）。
        spider.Teleport(Vector3.Zero);
        long tick = 0;
        for (; tick < 240; tick++)
        {
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Vector3.Zero, terrain, tick + 1));
        }
        tick = Settle(spider, terrain, tick, 120);
        bool hanging = spider.SupportNormal.Dot(Vector3.Down) > 0.8f
            && spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity;
        if (!hanging)
        {
            return new DropResult(false, 0f, float.PositiveInfinity, false, -1, 0UL, false);
        }

        // 猎物在正下方偏一点：起跳速度必须朝下离面（俯冲），不能先向上跳进天花板。
        Vector3 target = spider.Primary.Pos + new Vector3(0.4f, -2.5f, -0.2f);
        SpiderLeapRequest request = Request(spider, target, terrain);
        bool planned = SpiderLeapPlanner.TryPlan(request, terrain, out SpiderLeapPlan plan);
        if (!planned)
        {
            return new DropResult(false, 0f, float.PositiveInfinity, false, -1, 0UL, false);
        }
        float outward = plan.LaunchVelocity.Dot(spider.SupportNormal);
        spider.BeginLeap(plan.LaunchVelocity, plan.FlightTicks);
        float arrivalError = float.PositiveInfinity;
        bool endedByTimeout = false;
        int endTick = -1;
        bool finite = true;
        int graceLimit = plan.FlightTicks + spider.LeapGraceTicks + 2;
        for (int n = 1; n <= graceLimit; n++)
        {
            tick++;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.Fold(spider.Leaping);
            finite &= spider.Primary.Pos.IsFinite();
            if (n == plan.FlightTicks)
            {
                arrivalError = (spider.Primary.Pos - target).Length();
            }
            if (endTick < 0 && !spider.Leaping)
            {
                endTick = n;
                endedByTimeout = !spider.LeapEndedByContact;
            }
        }
        return new DropResult(true, outward, arrivalError, endedByTimeout, endTick,
            hasher.Value, finite);
    }

    private static bool CheckBlockedPath(out string message)
    {
        var open = new BoxTerrain();
        open.Add(new Vector3(-20f, -1f, -20f), new Vector3(20f, 0f, 20f)); // 地板
        var blocked = new BoxTerrain();
        blocked.Add(new Vector3(-20f, -1f, -20f), new Vector3(20f, 0f, 20f));
        blocked.Add(new Vector3(1.4f, 0f, -3f), new Vector3(1.6f, 3f, 3f)); // 立在路中间的墙

        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        long tick = Settle(spider, open, 0, 400);
        bool settled = spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity;
        Vector3 target = spider.Primary.Pos + new Vector3(3.0f, 0.7f, 0f);
        bool openPlanned = SpiderLeapPlanner.TryPlan(Request(spider, target, open), open, out SpiderLeapPlan openPlan);
        bool blockedPlanned = SpiderLeapPlanner.TryPlan(Request(spider, target, blocked), blocked, out _);
        // 无地形扫掠（terrain=null）时同一请求必须可解——证明失败来自扫掠而非约束。
        bool unsweptPlanned = SpiderLeapPlanner.TryPlan(Request(spider, target, blocked), null, out _);
        _ = tick;
        message = $"settled={settled} open={openPlanned}(ticks={openPlan.FlightTicks}) " +
                  $"blocked={blockedPlanned} unswept={unsweptPlanned}";
        return settled && openPlanned && !blockedPlanned && unsweptPlanned;
    }

    private static bool CheckLaunchInterrupt(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        long tick = Settle(spider, terrain, 0, 400);
        Vector3 target = spider.Primary.Pos + new Vector3(3.0f, 0.7f, 0f);
        if (!SpiderLeapPlanner.TryPlan(Request(spider, target, terrain), terrain, out SpiderLeapPlan plan))
        {
            message = "plan failed";
            return false;
        }
        spider.BeginLeap(plan.LaunchVelocity, plan.FlightTicks);
        for (int n = 0; n < 4; n++)
        {
            tick++;
            spider.Tick(new TickContext(Gravity, terrain, tick));
        }
        bool leapingBefore = spider.Leaping;
        Vector3 velBefore = spider.Primary.Vel;
        var impulse = new Vector3(-0.1f, 0.05f, 0f);
        spider.Launch(impulse);
        bool interrupted = leapingBefore && !spider.Leaping && !spider.LeapEndedByContact
            && (spider.Primary.Vel - (velBefore + impulse)).Length() <= 1e-6f
            && spider.ApplyGravity;
        bool regripped = false;
        for (int n = 0; n < 300 && !regripped; n++)
        {
            tick++;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            regripped = spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity;
        }
        message = $"interrupted={interrupted} regripped={regripped}";
        return interrupted && regripped;
    }

    private static LimpResult RunLimp()
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        var hasher = new DeterminismHasher();
        long tick = Settle(spider, terrain, 0, 400);
        spider.Conscious = false;
        bool neverGripped = true;
        bool finite = true;
        for (int n = 0; n < 240; n++)
        {
            tick++;
            spider.MoveDir = Vector3.Right;
            spider.RunSpeed = 1f; // 昏迷必须无视移动意图
            spider.Tick(new TickContext(Gravity, terrain, tick));
            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            neverGripped &= spider.LegsGripping == 0 && spider.ApplyGravity;
            foreach (SpiderLeg leg in spider.Legs)
            {
                neverGripped &= leg.GripCounter == 0 && !leg.HasGrip;
            }
            finite &= spider.Primary.Pos.IsFinite() && spider.Rear.Pos.IsFinite();
        }
        float restHeight = spider.Primary.Pos.Y;
        float restSpeed = Mathf.Max(spider.Primary.Vel.Length(), spider.Rear.Vel.Length());

        spider.Conscious = true;
        bool revived = false;
        int reviveTick = -1;
        for (int n = 0; n < 300 && !revived; n++)
        {
            tick++;
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            revived = spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity;
            reviveTick = n;
        }
        return new LimpResult(neverGripped, restHeight, restSpeed, revived, reviveTick,
            hasher.Value, finite);
    }

    /// <summary>只有天花板（y ≥ ceilingY 为实体）的半空间；下方无限空。</summary>
    private sealed class CeilingOnlyTerrain : ITerrainQuery
    {
        private readonly float _ceilingY;

        public CeilingOnlyTerrain(float ceilingY)
        {
            _ceilingY = ceilingY;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (from.Y > _ceilingY)
            {
                hit = new TerrainHit(from, Vector3.Zero, 2UL);
                return true;
            }
            if (to.Y <= _ceilingY)
            {
                return false;
            }
            float t = (_ceilingY - from.Y) / (to.Y - from.Y);
            hit = new TerrainHit(from.Lerp(to, t), Vector3.Down, 2UL);
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            pushDir = Vector3.Down;
            depth = center.Y + radius - _ceilingY;
            return depth > 0f;
        }
    }

    /// <summary>轴对齐盒子集合的解析地形（射线 slab 求交 + 球体最近点穿透）。</summary>
    private sealed class BoxTerrain : ITerrainQuery
    {
        private readonly List<(Vector3 Min, Vector3 Max)> _boxes = new();

        public void Add(Vector3 min, Vector3 max) => _boxes.Add((min, max));

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            float bestT = float.PositiveInfinity;
            Vector3 dir = to - from;
            foreach ((Vector3 min, Vector3 max) in _boxes)
            {
                if (Inside(from, min, max))
                {
                    hit = new TerrainHit(from, Vector3.Zero, 7UL);
                    return true;
                }
                float tMin = 0f;
                float tMax = 1f;
                int hitAxis = -1;
                float hitSign = 0f;
                for (int axis = 0; axis < 3; axis++)
                {
                    float o = from[axis];
                    float d = dir[axis];
                    float lo = min[axis];
                    float hi = max[axis];
                    if (Mathf.Abs(d) < 1e-9f)
                    {
                        if (o < lo || o > hi)
                        {
                            tMin = float.PositiveInfinity;
                            break;
                        }
                        continue;
                    }
                    float t1 = (lo - o) / d;
                    float t2 = (hi - o) / d;
                    float sign = -1f;
                    if (t1 > t2)
                    {
                        (t1, t2) = (t2, t1);
                        sign = 1f;
                    }
                    if (t1 > tMin)
                    {
                        tMin = t1;
                        hitAxis = axis;
                        hitSign = sign;
                    }
                    tMax = Mathf.Min(tMax, t2);
                    if (tMin > tMax)
                    {
                        tMin = float.PositiveInfinity;
                        break;
                    }
                }
                if (float.IsPositiveInfinity(tMin) || hitAxis < 0 || tMin >= bestT)
                {
                    continue;
                }
                bestT = tMin;
                Vector3 normal = Vector3.Zero;
                normal[hitAxis] = hitSign;
                hit = new TerrainHit(from + dir * tMin, normal, 7UL);
            }
            return !float.IsPositiveInfinity(bestT);
        }

        public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            pushDir = Vector3.Up;
            depth = 0f;
            foreach ((Vector3 min, Vector3 max) in _boxes)
            {
                Vector3 closest = new(
                    Mathf.Clamp(center.X, min.X, max.X),
                    Mathf.Clamp(center.Y, min.Y, max.Y),
                    Mathf.Clamp(center.Z, min.Z, max.Z));
                Vector3 away = center - closest;
                float dist = away.Length();
                float candidateDepth;
                Vector3 candidateDir;
                if (dist > 1e-6f)
                {
                    candidateDepth = radius - dist;
                    candidateDir = away / dist;
                }
                else
                {
                    // 球心在盒内：沿最薄的面推出。
                    float best = float.PositiveInfinity;
                    candidateDir = Vector3.Up;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float toMin = center[axis] - min[axis];
                        float toMax = max[axis] - center[axis];
                        if (toMin < best)
                        {
                            best = toMin;
                            candidateDir = Vector3.Zero;
                            candidateDir[axis] = -1f;
                        }
                        if (toMax < best)
                        {
                            best = toMax;
                            candidateDir = Vector3.Zero;
                            candidateDir[axis] = 1f;
                        }
                    }
                    candidateDepth = best + radius;
                }
                if (candidateDepth > depth)
                {
                    depth = candidateDepth;
                    pushDir = candidateDir;
                }
            }
            return depth > 0f;
        }

        private static bool Inside(Vector3 p, Vector3 min, Vector3 max) =>
            p.X > min.X && p.X < max.X && p.Y > min.Y && p.Y < max.Y && p.Z > min.Z && p.Z < max.Z;
    }
}
