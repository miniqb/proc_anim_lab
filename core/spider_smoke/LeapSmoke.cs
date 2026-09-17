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
///    打断弹道；⑤ 昏迷：腿不抓地、身体落地静止、可复活；⑥ 飞行姿态对准：身体轴/支撑法线
///    在截止 tick 前等角速度摆正、到点前正面朝目标、主节弹道不受扰；⑦ 墙/顶被击落：
///    Launch(noGripTicks) 保持期内腿不抓、身体离面落到地板再站稳（noGripTicks=0 消融钉住旧症状：
///    墙上被打只下滑半米就被腿抓回去）；⑧ 昏迷翻身：落地后背朝地、腿蜷向天；
///    全部序列双跑 bit-exact。
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
        int Deadline,
        float UpDotBeforeArrival,
        float MaxUpStepDeg,
        ulong Hash,
        bool Finite);

    private readonly record struct LimpResult(
        bool NeverGripped,
        float RestHeight,
        float RestSpeed,
        bool Revived,
        int ReviveTick,
        float BellyUpDot,
        int FeetAboveCenter,
        int LegCount,
        ulong Hash,
        bool Finite);

    private readonly record struct AlignResult(
        bool Planned,
        float InitialAngleDeg,
        int Deadline,
        int FlightTicks,
        float AxisDotAtDeadline,
        float UpDotAtDeadline,
        float MaxAxisStepDeg,
        float ArrivalError,
        float MaxModelError,
        bool EndedByContact,
        ulong Hash,
        bool Finite);

    private readonly record struct KnockOffResult(
        bool Hung,
        bool Released,
        int RegripTick,
        bool OnSurfaceAfter,
        bool OnFloorAfter,
        float MaxDrop,
        float MaxNormalStepDeg,
        bool Fell,
        float FinalUpDot,
        bool FinalFooted,
        int FinalGrips,
        float FinalDrop,
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
            && dropA.UpDotBeforeArrival >= 0.995f
            && dropA.MaxUpStepDeg <= Mathf.RadToDeg(0.45f) + 0.5f
            && dropA.Finite;
        ok &= dropOk;
        lines.Add(
            $"ceiling-drop ok={dropOk} plan={dropA.Planned} outward={dropA.OutwardSpeed * 40f:F2}m/s " +
            $"arrival={dropA.ArrivalError:F3}m timeoutEnd={dropA.EndedByTimeout}@{dropA.EndTick} " +
            $"roll: deadline={dropA.Deadline} upDotBeforeArrival={dropA.UpDotBeforeArrival:F3} " +
            $"maxStep={dropA.MaxUpStepDeg:F1}deg " +
            $"hash={dropA.Hash:X16}/{dropB.Hash:X16}");

        AlignResult alignA = RunAlignedPounce();
        AlignResult alignB = RunAlignedPounce();
        bool alignOk = alignA.Planned && alignA.Hash == alignB.Hash
            && alignA.InitialAngleDeg >= 90f
            && alignA.AxisDotAtDeadline >= 0.995f
            && alignA.UpDotAtDeadline >= 0.995f
            && alignA.MaxAxisStepDeg <= alignA.InitialAngleDeg / Mathf.Max(1, alignA.Deadline) + 1f
            && alignA.MaxAxisStepDeg <= Mathf.RadToDeg(0.45f) + 0.5f
            && alignA.ArrivalError <= 0.05f
            && alignA.MaxModelError <= 0.05f
            && alignA.EndedByContact
            && alignA.Finite;
        ok &= alignOk;
        lines.Add(
            $"flight-align ok={alignOk} plan={alignA.Planned} initial={alignA.InitialAngleDeg:F0}deg " +
            $"deadline={alignA.Deadline}/{alignA.FlightTicks}ticks axisDot={alignA.AxisDotAtDeadline:F3} " +
            $"upDot={alignA.UpDotAtDeadline:F3} maxStep={alignA.MaxAxisStepDeg:F1}deg " +
            $"arrival={alignA.ArrivalError:F3}m model={alignA.MaxModelError:F3}m " +
            $"contactEnd={alignA.EndedByContact} hash={alignA.Hash:X16}/{alignB.Hash:X16}");

        KnockOffResult wallA = RunKnockOff(ceiling: false, noGripTicks: 12, bounce: 0.35f);
        KnockOffResult wallB = RunKnockOff(ceiling: false, noGripTicks: 12, bounce: 0.35f);
        KnockOffResult wallOld = RunKnockOff(ceiling: false, noGripTicks: 0, bounce: 0f);
        KnockOffResult ceilingA = RunKnockOff(ceiling: true, noGripTicks: 12, bounce: 0.35f);
        bool knockOk = wallA.Hung && wallA.Hash == wallB.Hash
            && wallA.Released && wallA.Fell && wallA.OnFloorAfter && !wallA.OnSurfaceAfter
            && wallA.MaxNormalStepDeg <= Mathf.RadToDeg(0.12f) + 0.5f
            && wallA.Finite
            && ceilingA.Hung && ceilingA.Released && ceilingA.Fell && ceilingA.OnFloorAfter
            && ceilingA.MaxNormalStepDeg <= Mathf.RadToDeg(0.12f) + 0.5f
            && ceilingA.Finite
            && wallOld.Hung && !wallOld.Fell && wallOld.OnSurfaceAfter; // 消融：旧路径（无反弹无保持期）被腿抓回墙
        ok &= knockOk;
        lines.Add(
            $"knock-off ok={knockOk} wall: hung={wallA.Hung} released={wallA.Released} fell={wallA.Fell} " +
            $"drop={wallA.MaxDrop:F2}m floor={wallA.OnFloorAfter}@{wallA.RegripTick} " +
            $"final(upDot={wallA.FinalUpDot:F2} footed={wallA.FinalFooted} grips={wallA.FinalGrips} " +
            $"drop={wallA.FinalDrop:F2}m) normalStep={wallA.MaxNormalStepDeg:F1}deg | ceiling: fell={ceilingA.Fell} " +
            $"drop={ceilingA.MaxDrop:F2}m floor={ceilingA.OnFloorAfter}@{ceilingA.RegripTick} " +
            $"normalStep={ceilingA.MaxNormalStepDeg:F1}deg | old-path ablation(noGrip=0,bounce=0): fell={wallOld.Fell} " +
            $"drop={wallOld.MaxDrop:F2}m backOnWall={wallOld.OnSurfaceAfter}@{wallOld.RegripTick} " +
            $"hash={wallA.Hash:X16}/{wallB.Hash:X16}");

        LimpResult limpA = RunLimp(bellyUp: false);
        LimpResult limpB = RunLimp(bellyUp: false);
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

        LimpResult flipA = RunLimp(bellyUp: true);
        LimpResult flipB = RunLimp(bellyUp: true);
        bool flipOk = flipA.NeverGripped && flipA.Hash == flipB.Hash
            && flipA.Hash != limpA.Hash
            && flipA.BellyUpDot >= 0.995f
            && flipA.FeetAboveCenter == flipA.LegCount
            && flipA.RestHeight <= 0.45f && flipA.RestHeight >= 0.05f
            && flipA.RestSpeed <= 0.005f
            && flipA.Revived
            && flipA.Finite;
        ok &= flipOk;
        lines.Add(
            $"limp-belly-up ok={flipOk} bellyUpDot={flipA.BellyUpDot:F3} " +
            $"feetAbove={flipA.FeetAboveCenter}/{flipA.LegCount} restHeight={flipA.RestHeight:F3}m " +
            $"restSpeed={flipA.RestSpeed:E2} revived={flipA.Revived}@{flipA.ReviveTick} " +
            $"hash={flipA.Hash:X16}/{flipB.Hash:X16}");

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
            return new DropResult(false, 0f, float.PositiveInfinity, false, -1, 0, -2f, 0f, 0UL, false);
        }

        // 猎物在正下方偏一点：起跳速度必须朝下离面（俯冲），不能先向上跳进天花板。
        Vector3 target = spider.Primary.Pos + new Vector3(0.4f, -2.5f, -0.2f);
        SpiderLeapRequest request = Request(spider, target, terrain);
        bool planned = SpiderLeapPlanner.TryPlan(request, terrain, out SpiderLeapPlan plan);
        if (!planned)
        {
            return new DropResult(false, 0f, float.PositiveInfinity, false, -1, 0, -2f, 0f, 0UL, false);
        }
        float outward = plan.LaunchVelocity.Dot(spider.SupportNormal);
        spider.BeginLeap(plan.LaunchVelocity, plan.FlightTicks);
        int deadline = Mathf.Max(1, Mathf.RoundToInt(plan.FlightTicks * spider.LeapAlignFraction));
        float arrivalError = float.PositiveInfinity;
        bool endedByTimeout = false;
        int endTick = -1;
        bool finite = true;
        Vector3 lastUp = spider.SupportNormal;
        float maxUpStep = 0f;
        float upDotBeforeArrival = -2f;
        int graceLimit = plan.FlightTicks + spider.LeapGraceTicks + 2;
        for (int n = 1; n <= graceLimit; n++)
        {
            tick++;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.Fold(spider.Leaping);
            finite &= spider.Primary.Pos.IsFinite();
            if (spider.Leaping)
            {
                // 顶面俯冲：支撑法线要从世界下翻到世界上（180° 滚转），到点前完成且逐 tick 不瞬切。
                maxUpStep = Mathf.Max(maxUpStep, AngleDeg(lastUp, spider.SupportNormal));
                lastUp = spider.SupportNormal;
            }
            if (n == plan.FlightTicks - 1)
            {
                upDotBeforeArrival = spider.SupportNormal.Dot(Vector3.Up);
            }
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
            deadline, upDotBeforeArrival, maxUpStep, hasher.Value, finite);
    }

    private static Vector3 ChainAxis(SpiderLocomotionController spider)
    {
        Vector3 axis = spider.Primary.Pos - spider.Rear.Pos;
        return axis.LengthSquared() > 1e-12f ? axis.Normalized() : Vector3.Forward;
    }

    private static float AngleDeg(Vector3 a, Vector3 b) =>
        Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f)));

    /// <summary>目标在身后侧方（出生朝向 −X，目标在 +X/+Z 象限，身体轴要偏航 ≈140°）：验证截止式
    /// 对准——身体轴在截止 tick 已指向目标水平方向、逐 tick 转角等于「初始角/截止 tick」（等角
    /// 速度，不瞬切），且绕主节的刚体旋转不扰动主节弹道（到点误差、模型误差门与地面扑击同）。</summary>
    private static AlignResult RunAlignedPounce()
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        var hasher = new DeterminismHasher();
        long tick = Settle(spider, terrain, 0, 400);

        Vector3 target = spider.Primary.Pos + new Vector3(2.5f, 0.7f, 2.0f);
        Vector3 facing = new Vector3(2.5f, 0f, 2.0f).Normalized();
        SpiderLeapRequest request = Request(spider, target, terrain);
        if (!SpiderLeapPlanner.TryPlan(request, terrain, out SpiderLeapPlan plan))
        {
            return new AlignResult(false, 0f, 0, 0, -2f, -2f, 0f, float.PositiveInfinity,
                float.PositiveInfinity, false, 0UL, false);
        }

        Vector3 from = spider.Primary.Pos;
        Vector3 lastAxis = ChainAxis(spider);
        float initialAngle = AngleDeg(lastAxis, facing);
        spider.BeginLeap(plan.LaunchVelocity, plan.FlightTicks, facing);
        int deadline = Mathf.Max(1, Mathf.RoundToInt(plan.FlightTicks * spider.LeapAlignFraction));
        float axisDotAtDeadline = -2f;
        float upDotAtDeadline = -2f;
        float maxAxisStep = 0f;
        float arrivalError = float.PositiveInfinity;
        float maxModelError = 0f;
        bool endedByContact = false;
        bool finite = true;
        for (int n = 1; n <= plan.FlightTicks + spider.LeapGraceTicks; n++)
        {
            tick++;
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.Fold(spider.Leaping);
            finite &= spider.Primary.Pos.IsFinite() && spider.Rear.Pos.IsFinite();
            if (spider.Leaping)
            {
                Vector3 axis = ChainAxis(spider);
                maxAxisStep = Mathf.Max(maxAxisStep, AngleDeg(lastAxis, axis));
                lastAxis = axis;
                if (n <= plan.FlightTicks)
                {
                    Vector3 model = SpiderLeapPlanner.PositionAfter(
                        from, plan.LaunchVelocity, Gravity, spider.AirborneAirFriction, n);
                    maxModelError = Mathf.Max(maxModelError, (spider.Primary.Pos - model).Length());
                }
                if (n == deadline)
                {
                    axisDotAtDeadline = axis.Dot(facing);
                    upDotAtDeadline = spider.SupportNormal.Dot(Vector3.Up);
                }
            }
            if (n == plan.FlightTicks)
            {
                arrivalError = (spider.Primary.Pos - target).Length();
            }
            if (!spider.Leaping && n > 1)
            {
                endedByContact = spider.LeapEndedByContact;
                break;
            }
        }
        return new AlignResult(true, initialAngle, deadline, plan.FlightTicks, axisDotAtDeadline,
            upDotAtDeadline, maxAxisStep, arrivalError, maxModelError, endedByContact,
            hasher.Value, finite);
    }

    /// <summary>墙/顶上被击中：墙在 z ≤ −0.42（法线 +Z）或顶在 y ≥ 0.42（法线 −Y），地板远在 5m 之下；
    /// 零重力挂上表面后开重力站稳，再按竞技场默认注入冲量（枪向 6.5m/s 打进表面 + 向上 2.2m/s，
    /// 打进表面的分量按 bounce 反射为离面反弹）。noGripTicks=12 + bounce=0.35：腿保持不抓、身体离面
    /// 下落、落到地板再站稳；旧路径消融（noGripTicks=0、bounce=0）：墙上只下滑半米就被腿抓回去
    /// ——用户看到的「墙上打不掉」。</summary>
    private static KnockOffResult RunKnockOff(bool ceiling, int noGripTicks, float bounce)
    {
        var terrain = new BoxTerrain();
        Vector3 normal;
        if (ceiling)
        {
            terrain.Add(new Vector3(-50f, 0.42f, -50f), new Vector3(50f, 3f, 50f));
            normal = Vector3.Down;
        }
        else
        {
            terrain.Add(new Vector3(-50f, -50f, -3f), new Vector3(50f, 50f, -0.42f));
            normal = Vector3.Back;
        }
        terrain.Add(new Vector3(-50f, -6f, -50f), new Vector3(50f, -5f, 50f));
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            Vector3.Zero, SpiderFactory.SmallSpider());
        var hasher = new DeterminismHasher();
        spider.Teleport(Vector3.Zero);
        long tick = 0;
        for (; tick < 240; tick++)
        {
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Vector3.Zero, terrain, tick + 1));
        }
        tick = Settle(spider, terrain, tick, 120);
        bool hung = spider.SupportNormal.Dot(normal) > 0.8f
            && spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity;
        if (!hung)
        {
            return new KnockOffResult(false, false, -1, false, false, 0f, 0f, false, 0f, false, 0, 0f, 0UL, false);
        }

        // 竞技场 HandleHit 同式：枪向打进表面 + 向上，打进表面的分量按 (1 + bounce) 反射为离面反弹。
        Vector3 impulse = -normal * 6.5f + Vector3.Up * 2.2f;
        float into = -impulse.Dot(normal);
        if (bounce > 0f)
        {
            impulse += normal * (into * (1f + bounce));
        }
        impulse *= 0.025f;
        float startY = spider.Primary.Pos.Y;
        spider.Launch(impulse, noGripTicks);
        bool released = false;
        int regripTick = -1;
        float maxDrop = 0f;
        float maxNormalStep = 0f;
        bool fell = false;
        bool finite = true;
        bool footed = false;
        Vector3 lastNormal = spider.SupportNormal;
        for (int n = 1; n <= 160; n++)
        {
            tick++;
            bool inHold = spider.LaunchNoGripTicks > 0;
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Gravity, terrain, tick));
            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.Fold(spider.ApplyGravity);
            hasher.Fold(spider.LaunchNoGripTicks);
            finite &= spider.Primary.Pos.IsFinite();
            released |= spider.LegsGripping == 0;
            maxDrop = Mathf.Max(maxDrop, startY - spider.Primary.Pos.Y);
            fell |= spider.Primary.Pos.Y < startY - 1.5f;
            if (inHold)
            {
                // 保持期内翻正必须是等角速度（不瞬切）；期满后由常规支撑低通接手，不在此门内。
                maxNormalStep = Mathf.Max(maxNormalStep, AngleDeg(lastNormal, spider.SupportNormal));
            }
            lastNormal = spider.SupportNormal;
            footed = spider.LegsGripping >= spider.MinGroundedLegs && !spider.ApplyGravity;
            if (released && regripTick < 0 && footed)
            {
                regripTick = n;
            }
        }
        bool onSurfaceAfter = footed && spider.SupportNormal.Dot(normal) > 0.8f;
        bool onFloorAfter = footed && spider.SupportNormal.Dot(Vector3.Up) > 0.8f
            && spider.Primary.Pos.Y < startY - 4f;
        return new KnockOffResult(true, released, regripTick, onSurfaceAfter, onFloorAfter, maxDrop,
            maxNormalStep, fell, spider.SupportNormal.Dot(Vector3.Up), footed, spider.LegsGripping,
            startY - spider.Primary.Pos.Y, hasher.Value, finite);
    }

    private static LimpResult RunLimp(bool bellyUp)
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        var hasher = new DeterminismHasher();
        long tick = Settle(spider, terrain, 0, 400);
        spider.LimpBellyUp = bellyUp;
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
        float bellyUpDot = spider.SupportNormal.Dot(Vector3.Down);
        int feetAbove = 0;
        foreach (SpiderLeg leg in spider.Legs)
        {
            feetAbove += leg.Pos.Y > leg.Anchor.Pos.Y ? 1 : 0;
        }

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
            bellyUpDot, feetAbove, spider.Legs.Count, hasher.Value, finite);
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
