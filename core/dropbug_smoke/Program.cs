using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DropBug;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.DropBugSmoke;

/// <summary>
/// DropBug 专项无引擎回归。地形是 AABB 盒子 + 半空间的解析房间（盒子有厚度，
/// 悬挂判据的厚度/落差探针在无引擎环境同样被走到）。全部门为真断言（退出码判定），
/// 关键机制均含消融对照：把机制关掉时对应门必须以可观测方式翻红。
/// </summary>
internal static class Program
{
    private const float TickDt = 0.025f;
    private const float GravityMps2 = 36f;
    private static readonly Vector3 GravityPerTick =
        new(0f, -GravityMps2 * TickDt * TickDt, 0f);
    private static float _maxResidualPenetration;
    private static string _currentCheck = "";
    private static string _maxPenetrationContext = "";

    // 在完整行为门人工核对后钉定；只有有意改变 DropBug 内核轨迹时才更新。
    private const ulong ExpectedHash = 0x69FEFC63E11262E7UL;

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var failures = new List<string>();

        Check("DET", CheckDeterminism, failures);
        Check("ASSEMBLY", CheckAssembly, failures);
        Check("FOOTING-ASYM", CheckFootingAsymmetry, failures);
        Check("FOOTING-GRACE", CheckFootingGrace, failures);
        Check("WALK", CheckWalk, failures);
        Check("SLOPE", CheckSlope, failures);
        Check("HOP", CheckObstacleHop, failures);
        Check("BACKWARD", CheckBackwardsWalk, failures);
        Check("HANG-VALIDATE", CheckHangValidation, failures);
        Check("HANG-ENTER", CheckHangEnter, failures);
        Check("HANG-EXIT", CheckHangExit, failures);
        Check("LAUNCH-HANG", CheckLaunchHang, failures);
        Check("DIVE", CheckDive, failures);
        Check("POUNCE", CheckPounce, failures);
        Check("STUCK", CheckStuckShake, failures);
        Check("CARRY", CheckCarry, failures);
        Check("LEGS", CheckLegs, failures);
        Check("LIFECYCLE", CheckLifecycle, failures);
        Check("QUERY", CheckQueryBudget, failures);
        Report(
            "PENETRATION",
            _maxResidualPenetration < 0.002f,
            $"maxResidual={_maxResidualPenetration:E3}m at={_maxPenetrationContext}" +
            "（门 2mm，碰撞关闭的悬挂节与抖动 tick 不计）",
            failures);

        bool pass = failures.Count == 0;
        Console.WriteLine(pass
            ? "[DROPBUG-CORE-SMOKE] PASS：固定哈希、装配、不对称重力、宽限、行走/坡、越障、" +
              "倒退、悬挂收放、俯冲、蓄力扑击、卡住抖动、负重、腿驱动与生命周期均通过"
            : $"[DROPBUG-CORE-SMOKE] FAIL：{string.Join("；", failures)}");
        return pass ? 0 : 1;
    }

    private static void Check(
        string name, Func<(bool Ok, string Message)> test, List<string> failures)
    {
        try
        {
            _currentCheck = name;
            (bool ok, string message) = test();
            Report(name, ok, message, failures);
        }
        catch (Exception ex)
        {
            Report(name, false, $"{ex.GetType().Name}: {ex.Message}", failures);
        }
    }

    private static void Report(string name, bool ok, string message, List<string> failures)
    {
        Console.WriteLine($"[DROPBUG-CORE-{name}] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok)
        {
            failures.Add(name);
        }
    }

    // ================================================================ 确定性

    private readonly record struct DetResult(
        ulong Hash, bool Finite, float MaxDeviation, float MaxStuckShake,
        long Leaps, long Abandons, long Hops);

    /// <summary>钉哈希的固定路线：跨台阶巡走 → 反转 → 顶墙卡住抖动 → 折返 →
    /// 越可及扑击放弃 → 正常蓄力弹射落地 → 侧向续走。覆盖行走/越障/卡住/蓄力全路径。</summary>
    private static DetResult RunDeterminism(float perturb)
    {
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL)
            .AddBox(new Vector3(5f, 0f, -8f), new Vector3(6f, 3f, 8f), 2UL)
            .AddBox(new Vector3(2f, 0f, -8f), new Vector3(2.6f, 0.28f, 8f), 3UL);
        DropBugLocomotionController bug = NewBug(new Vector3(-2f, 0.3f, 0f), Vector3.Right);
        if (perturb != 0f)
        {
            bug.Head.Pos.X += perturb;
            bug.Head.LastPos = bug.Head.Pos;
        }
        var hasher = new DeterminismHasher();
        long tick = 0;
        float maxDev = 0f;
        float maxShake = 0f;
        for (int i = 1; i <= 900; i++)
        {
            bug.MoveTarget = null;
            bug.RunSpeed = i is > 600 and <= 700 ? 0f : 1f;
            bug.MoveDir = i switch
            {
                <= 140 => Vector3.Right,
                <= 220 => Vector3.Left,
                <= 520 => Vector3.Right, // 顶墙：卡住信号 + 抖动进哈希
                <= 600 => Vector3.Left,
                <= 700 => Vector3.Zero,
                _ => Vector3.Back,
            };
            if (i == 621)
            {
                bug.AttackTarget = new DropBugAttackTarget(
                    new Vector3(8f, 0.2f, 4f), Vector3.Zero); // 越可及 → 放弃事件进哈希
                bug.TryStartPounce();
            }
            if (i == 660)
            {
                Vector3 ahead = new(bug.Head.Pos.X - 2.2f, 0.2f, bug.Head.Pos.Z);
                bug.AttackTarget = new DropBugAttackTarget(ahead, Vector3.Zero);
                bug.TryStartPounce();
            }
            if (i == 700)
            {
                bug.AttackTarget = null;
            }
            Tick(bug, terrain, ref tick);
            bug.FoldState(hasher);
            // 抖动 tick 的位置注入会瞬时拉伸连接（下 tick 松弛收回，≙ 原作），不计入偏差门。
            if (bug.StuckShake <= 1e-3f)
            {
                maxDev = MathF.Max(maxDev, bug.Body.CurrentMaxDeviation());
            }
            maxShake = MathF.Max(maxShake, bug.StuckShake);
        }
        return new DetResult(hasher.Value, IsFinite(bug), maxDev, maxShake,
            bug.PounceLeapSerial, bug.PounceAbandonSerial, bug.HopSerial);
    }

    private static (bool, string) CheckDeterminism()
    {
        DetResult a = RunDeterminism(0f);
        DetResult b = RunDeterminism(0f);
        DetResult p = RunDeterminism(0.001f);
        bool routeCovered = a.MaxStuckShake > 0.5f && a.Leaps >= 1 && a.Abandons >= 1;
        // maxDev 门 0.12：蓄力弹射当 tick 的削速+冲量会产生一次性约束偏差（下 tick
        // 即被松弛收回），巡走段应远低于此。
        bool ok = a.Hash == b.Hash && a.Hash == ExpectedHash && p.Hash != a.Hash &&
                  a.Finite && a.MaxDeviation < 0.12f && routeCovered;
        return (ok,
            $"run1={a.Hash:X16} run2={b.Hash:X16} expected={ExpectedHash:X16} " +
            $"perturb={p.Hash:X16} finite={a.Finite} maxDev={a.MaxDeviation:F5}m " +
            $"maxShake={a.MaxStuckShake:F2} leaps={a.Leaps} abandons={a.Abandons} " +
            $"hops={a.Hops}");
    }

    // ================================================================ 装配

    private static (bool, string) CheckAssembly()
    {
        DropBugLocomotionController bug = NewBug(new Vector3(1f, 2f, 3f), Vector3.Forward);
        bool topology = bug.Body.Chunks.Count == 3 &&
                        bug.Body.Connections.Count == 3 &&
                        ReferenceEquals(bug.Body.Chunks[0], bug.Head) &&
                        ReferenceEquals(bug.Body.Chunks[1], bug.Mid) &&
                        ReferenceEquals(bug.Body.Chunks[2], bug.Tail) &&
                        bug.Body.Connections[0].ConstraintMode == ChunkConnection.Mode.Rigid &&
                        bug.Body.Connections[1].ConstraintMode == ChunkConnection.Mode.Rigid &&
                        bug.Body.Connections[2].ConstraintMode == ChunkConnection.Mode.PushOnly;
        bool dimensions = MathF.Abs(bug.Head.Radius - 0.15f) < 1e-5f &&
                          MathF.Abs(bug.Mid.Radius - 0.20f) < 1e-5f &&
                          MathF.Abs(bug.Tail.Radius - 0.15f) < 1e-5f &&
                          MathF.Abs(bug.Head.Mass - 0.32f) < 1e-5f &&
                          MathF.Abs(bug.Tail.Mass - 0.16f) < 1e-5f &&
                          MathF.Abs(bug.Body.Connections[0].RestLength - 0.30f) < 1e-5f &&
                          MathF.Abs(bug.Body.Connections[1].RestLength - 0.35f) < 1e-5f &&
                          MathF.Abs(bug.Body.Connections[2].RestLength - 0.20f) < 1e-5f;
        bool rotation = ReferenceEquals(bug.Head.RotationChunk, bug.Tail) &&
                        ReferenceEquals(bug.Mid.RotationChunk, bug.Tail) &&
                        ReferenceEquals(bug.Tail.RotationChunk, bug.Head);
        bool legs = bug.Legs.Count == 4;

        DropBugParams[] presets = DropBugFactory.AllPresets();
        bool presetSet = presets.Length == 3 &&
                         DropBugFactory.ById("dropbug/original").Id == "dropbug/original" &&
                         DropBugFactory.ById("DROPBUG/NIMBLE").Id == "dropbug/nimble" &&
                         DropBugFactory.ById("dropbug/bulky").Id == "dropbug/bulky";
        bool unknownThrows = false;
        try
        {
            DropBugFactory.ById("dropbug/unknown");
        }
        catch (ArgumentException)
        {
            unknownThrows = true;
        }

        // 出生配置必须冻结：出生后改同一张表，已出生实例的轨迹不能变化。
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL);
        DropBugParams mutable = DropBugFactory.Original();
        DropBugLocomotionController frozen = DropBugFactory.CreateController(
            new Vector3(0f, 0.3f, 0f), Vector3.Right, mutable);
        mutable.MoveForceHead = 9f;
        mutable.HeadMidLength = 9f;
        mutable.MaxMoveSpeed = 9f;
        DropBugLocomotionController reference = NewBug(new Vector3(0f, 0.3f, 0f),
            Vector3.Right);
        long frozenTick = 0;
        long referenceTick = 0;
        for (int i = 0; i < 60; i++)
        {
            frozen.MoveDir = reference.MoveDir = Vector3.Right;
            frozen.RunSpeed = reference.RunSpeed = 1f;
            Tick(frozen, terrain, ref frozenTick);
            Tick(reference, terrain, ref referenceTick);
        }
        bool birthFrozen = frozen.Head.Pos == reference.Head.Pos &&
                           frozen.Tail.Pos == reference.Tail.Pos;

        bool ok = topology && dimensions && rotation && legs && presetSet &&
                  unknownThrows && birthFrozen;
        return (ok,
            $"chunks={bug.Body.Chunks.Count} connections={bug.Body.Connections.Count} " +
            $"legs={bug.Legs.Count} presets={presets.Length} unknownThrows={unknownThrows} " +
            $"rotationPinned={rotation} birthFrozen={birthFrozen}");
    }

    // ================================================================ 支撑不对称与宽限

    /// <summary>尾段悬出台缘：站稳时前段全额抵消重力、尾段只抵消一半 → 尾巴持续下垂。
    /// 量的是「站稳之后的下垂增量」——出生坠落的历史高度差两组都有，只有不对称机制
    /// 会让尾巴在站稳后继续下沉；消融（尾段与前段同权）→ 增量归零，门翻红。</summary>
    private static (bool, string) CheckFootingAsymmetry()
    {
        float DroopGrowth(bool asymmetric)
        {
            var terrain = new BoxRoomTerrain()
                .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(1.05f, 0f, 30f), 1UL);
            DropBugLocomotionController bug = NewBug(new Vector3(0.85f, 0.3f, 0f),
                Vector3.Left);
            bug.EnableTailGravityAsymmetry = asymmetric;
            long tick = 0;
            int guard = 0;
            while (!bug.Footing && guard++ < 100)
            {
                Tick(bug, terrain, ref tick);
            }
            float tailAtFooting = bug.Tail.Pos.Y;
            for (int i = 0; i < 100; i++)
            {
                Tick(bug, terrain, ref tick);
            }
            return tailAtFooting - bug.Tail.Pos.Y;
        }

        float growth = DroopGrowth(asymmetric: true);
        float ablated = DroopGrowth(asymmetric: false);
        bool ok = growth > 0.08f && ablated < 0.03f && ablated < growth - 0.05f;
        return (ok,
            $"droopGrowth={growth:F3}m ablatedGrowth={ablated:F3}m" +
            $"（站稳后 100 tick 尾端继续下沉量，门 0.08 / 0.03）");
    }

    /// <summary>失去支撑后的宽限期（≙ IntClamp(c-3, 0, 35)）：地面消失后 Footing 仍应
    /// 维持约 8 tick；消融（立即清零）→ 首 tick 即失稳。</summary>
    private static (bool, string) CheckFootingGrace()
    {
        int GraceTicks(bool grace)
        {
            var terrain = new BoxRoomTerrain()
                .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL);
            DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f),
                Vector3.Right);
            bug.EnableFootingGrace = grace;
            long tick = 0;
            for (int i = 0; i < 120; i++)
            {
                Tick(bug, terrain, ref tick);
            }
            if (!bug.Footing)
            {
                return -1;
            }
            terrain.SetEnabled(1UL, false);
            int survived = 0;
            for (int i = 0; i < 30; i++)
            {
                Tick(bug, terrain, ref tick);
                if (!bug.Footing)
                {
                    break;
                }
                survived++;
            }
            return survived;
        }

        int normal = GraceTicks(grace: true);
        int ablated = GraceTicks(grace: false);
        bool ok = normal is >= 6 and <= 12 && ablated == 0;
        return (ok, $"graceTicks={normal}（预期 8±），ablated={ablated}（预期 0）");
    }

    // ================================================================ 行走

    private static (bool, string) CheckWalk()
    {
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 60; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        float startX = bug.Head.Pos.X;
        int headLeadTicks = 0;
        for (int i = 0; i < 400; i++)
        {
            bug.MoveDir = Vector3.Right;
            bug.RunSpeed = 1f;
            Tick(bug, terrain, ref tick);
            if (bug.Head.Pos.X > bug.Mid.Pos.X)
            {
                headLeadTicks++;
            }
        }
        float travel = bug.Head.Pos.X - startX;
        float leadFraction = headLeadTicks / 400f;

        // 失稳推进衰减（≙ !Footing → vector×0.3）：比较单 tick 注入的头部水平增速。
        // 空中无阻尼、无摩擦，多 tick 位移比较是错误物理，只有首 tick 注力干净可比。
        var empty = new BoxRoomTerrain();
        DropBugLocomotionController airborne = NewBug(new Vector3(0f, 30f, 0f),
            Vector3.Right);
        long airTick = 0;
        airborne.MoveDir = Vector3.Right;
        airborne.RunSpeed = 1f;
        Tick(airborne, empty, ref airTick);
        float airInjection = airborne.Head.Vel.X;
        DropBugLocomotionController grounded = NewBug(new Vector3(0f, 0.3f, 0f),
            Vector3.Right);
        var flat = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        long groundTick = 0;
        for (int i = 0; i < 60; i++)
        {
            Tick(grounded, flat, ref groundTick);
        }
        grounded.MoveDir = Vector3.Right;
        grounded.RunSpeed = 1f;
        Tick(grounded, flat, ref groundTick);
        float groundInjection = grounded.Head.Vel.X;
        float ratio = airInjection / MathF.Max(1e-6f, groundInjection);

        bool ok = travel > 5f && leadFraction > 0.8f && bug.Footing &&
                  ratio is > 0.2f and < 0.55f && IsFinite(bug);
        return (ok,
            $"travel={travel:F2}m/400tick headLead={leadFraction:P0} " +
            $"injection air/ground={airInjection:F4}/{groundInjection:F4} " +
            $"ratio={ratio:F3}（门 0.2~0.55，理论 0.3/0.8=0.375）");
    }

    private static (bool, string) CheckSlope()
    {
        // 18° 斜面半空间：+X 方向上坡。
        float rad = Mathf.DegToRad(18f);
        var terrain = new BoxRoomTerrain()
            .AddHalfSpace(Vector3.Zero,
                new Vector3(-MathF.Sin(rad), MathF.Cos(rad), 0f), 1UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.45f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 60; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        Vector3 start = bug.Mid.Pos;
        for (int i = 0; i < 400; i++)
        {
            bug.MoveDir = Vector3.Right;
            bug.RunSpeed = 1f;
            Tick(bug, terrain, ref tick);
        }
        float dx = bug.Mid.Pos.X - start.X;
        float dy = bug.Mid.Pos.Y - start.Y;
        bool ok = dx > 2f && dy > dx * MathF.Tan(rad) * 0.7f && IsFinite(bug);
        return (ok, $"dx={dx:F2}m dy={dy:F2}m（18° 上坡，dy 门 {dx * MathF.Tan(rad) * 0.7f:F2}）");
    }

    /// <summary>越障抬升的涌现签名是「前段落在中段后面」——实测在正向撞台阶时不出现
    /// （头恒在前），在**反转朝向障碍**时出现并点火（≙ 原作条件的字面语义）。
    /// 场景：走向台阶后反转再折返，断言点火 &gt;0、翻越台阶成功；消融点火=0，
    /// 折返用时对比一并打印。</summary>
    private static (bool, string) CheckObstacleHop()
    {
        (bool crossed, int turnTicks, long hops) RunCourse(bool hop)
        {
            var terrain = new BoxRoomTerrain()
                .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
                .AddBox(new Vector3(2f, 0f, -6f), new Vector3(10f, 0.3f, 6f), 2UL);
            DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f),
                Vector3.Right);
            bug.EnableObstacleHop = hop;
            long tick = 0;
            for (int i = 0; i < 60; i++)
            {
                Tick(bug, terrain, ref tick);
            }
            // 先离开台阶，再反身冲向台阶（反转 = 头落在中段后面的涌现来源）。
            for (int i = 0; i < 60; i++)
            {
                bug.MoveDir = Vector3.Left;
                bug.RunSpeed = 1f;
                Tick(bug, terrain, ref tick);
            }
            int turnTicks = -1;
            int headLeadRun = 0;
            bool crossed = false;
            for (int i = 0; i < 900; i++)
            {
                bug.MoveDir = Vector3.Right;
                bug.RunSpeed = 1f;
                Tick(bug, terrain, ref tick);
                if (turnTicks < 0)
                {
                    headLeadRun = (bug.Head.Pos - bug.Mid.Pos).Dot(Vector3.Right) > 0.1f
                        ? headLeadRun + 1
                        : 0;
                    if (headLeadRun >= 5)
                    {
                        turnTicks = i;
                    }
                }
                if (bug.Head.Pos.X > 3.2f && bug.Mid.Pos.X > 3.0f)
                {
                    crossed = true;
                    break;
                }
            }
            return (crossed, turnTicks, bug.HopSerial);
        }

        (bool crossedOn, int turnOn, long hopsOn) = RunCourse(hop: true);
        (bool crossedOff, int turnOff, long hopsOff) = RunCourse(hop: false);
        bool ok = crossedOn && crossedOff && hopsOn > 0 && hopsOff == 0 &&
                  turnOn >= 0 && turnOff >= 0;
        return (ok,
            $"crossed={crossedOn} turn={turnOn}tick hops={hopsOn}；" +
            $"ablated crossed={crossedOff} turn={turnOff}tick hops={hopsOff}" +
            $"（门：点火>0 且消融=0，折返时间供对照）");
    }

    // ================================================================ 倒退接近

    private static (bool, string) CheckBackwardsWalk()
    {
        (int backTicks, float tailLeadFraction, float progress) Run(bool enable)
        {
            var terrain = BackwardRoom();
            DropBugLocomotionController bug = NewBug(new Vector3(3.2f, 0.3f, 0f),
                Vector3.Left);
            bug.EnableBackwardsWalk = enable;
            long tick = 0;
            for (int i = 0; i < 60; i++)
            {
                Tick(bug, terrain, ref tick);
            }
            bool assigned = AssignCeilingAnchor(bug, terrain, new Vector3(0f, 3f, 0f),
                new Vector3(0f, 3.5f, 0f));
            if (!assigned)
            {
                return (-1, 0f, 0f);
            }
            float startX = bug.Mid.Pos.X;
            int backTicks = 0;
            int tailLead = 0;
            for (int i = 0; i < 300; i++)
            {
                bug.MoveTarget = new Vector3(0f, 0.3f, 0f);
                bug.RunSpeed = 1f;
                Tick(bug, terrain, ref tick);
                if (bug.MovingBackwards)
                {
                    backTicks++;
                    if ((bug.Tail.Pos - bug.Head.Pos).Dot(Vector3.Left) > 0f)
                    {
                        tailLead++;
                    }
                }
            }
            float fraction = backTicks > 0 ? (float)tailLead / backTicks : 0f;
            return (backTicks, fraction, startX - bug.Mid.Pos.X);
        }

        (int backTicks, float tailLead, float progress) = Run(enable: true);
        (int ablatedBack, _, float ablatedProgress) = Run(enable: false);
        bool ok = backTicks >= 50 && tailLead >= 0.7f && progress >= 1.2f &&
                  ablatedBack == 0 && ablatedProgress >= 1.2f;
        return (ok,
            $"backTicks={backTicks} tailLead={tailLead:P0} progress={progress:F2}m；" +
            $"ablated backTicks={ablatedBack} progress={ablatedProgress:F2}m");
    }

    private static BoxRoomTerrain BackwardRoom() => new BoxRoomTerrain()
        .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
        .AddBox(new Vector3(-1f, 3.2f, -1f), new Vector3(1f, 3.8f, 1f), 7UL);

    // ================================================================ 悬挂

    private static (bool, string) CheckHangValidation()
    {
        var room = new BoxRoomTerrain()
            .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
            .AddBox(new Vector3(-1f, 3.2f, -1f), new Vector3(1f, 3.8f, 1f), 7UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f), Vector3.Right);

        bool valid = room.Raycast(new Vector3(0f, 3f, 0f), new Vector3(0f, 3.5f, 0f),
                         out TerrainHit ceilingHit) &&
                     bug.TryAssignHangAnchor(in ceilingHit, room) &&
                     bug.LastHangRejection == DropBugHangRejection.None;
        bug.ClearHangAnchor();

        bool floorRejected = room.Raycast(new Vector3(0.2f, 1f, 0f),
                                 new Vector3(0.2f, -0.5f, 0f), out TerrainHit floorHit) &&
                             !bug.TryAssignHangAnchor(in floorHit, room) &&
                             bug.LastHangRejection == DropBugHangRejection.NotCeiling;

        float rad = Mathf.DegToRad(50f);
        var slantHit = new TerrainHit(new Vector3(0f, 3f, 0f),
            new Vector3(MathF.Sin(rad), -MathF.Cos(rad), 0f), 9UL);
        bool slantRejected = !bug.TryAssignHangAnchor(in slantHit, room) &&
                             bug.LastHangRejection == DropBugHangRejection.NotCeiling;

        var zeroHit = new TerrainHit(new Vector3(0f, 3f, 0f), Vector3.Zero, 9UL);
        bool zeroRejected = !bug.TryAssignHangAnchor(in zeroHit, room) &&
                            bug.LastHangRejection == DropBugHangRejection.InvalidNormal;

        var thinRoom = new BoxRoomTerrain()
            .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
            .AddBox(new Vector3(-1f, 3.2f, -1f), new Vector3(1f, 3.35f, 1f), 8UL);
        bool thinRejected = thinRoom.Raycast(new Vector3(0f, 3f, 0f),
                                new Vector3(0f, 3.3f, 0f), out TerrainHit thinHit) &&
                            !bug.TryAssignHangAnchor(in thinHit, thinRoom) &&
                            bug.LastHangRejection == DropBugHangRejection.TooThin;

        var lowRoom = new BoxRoomTerrain()
            .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
            .AddBox(new Vector3(-1f, 2.5f, -1f), new Vector3(1f, 3.1f, 1f), 8UL);
        bool lowRejected = lowRoom.Raycast(new Vector3(0f, 2.2f, 0f),
                               new Vector3(0f, 2.7f, 0f), out TerrainHit lowHit) &&
                           !bug.TryAssignHangAnchor(in lowHit, lowRoom) &&
                           bug.LastHangRejection == DropBugHangRejection.NoDropClearance;

        var blockedRoom = new BoxRoomTerrain()
            .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
            .AddBox(new Vector3(-1f, 3.2f, -1f), new Vector3(1f, 3.8f, 1f), 8UL)
            .AddBox(new Vector3(-0.3f, 2.7f, -0.3f), new Vector3(0.3f, 2.9f, 0.3f), 9UL);
        bool blockedRejected = blockedRoom.Raycast(new Vector3(0f, 3f, 0f),
                                   new Vector3(0f, 3.5f, 0f), out TerrainHit blockedHit) &&
                               !bug.TryAssignHangAnchor(in blockedHit, blockedRoom) &&
                               bug.LastHangRejection == DropBugHangRejection.NoBodyClearance;

        bool ok = valid && floorRejected && slantRejected && zeroRejected &&
                  thinRejected && lowRejected && blockedRejected;
        return (ok,
            $"valid={valid} floor={floorRejected} slant50={slantRejected} " +
            $"zero={zeroRejected} thin={thinRejected} lowDrop={lowRejected} " +
            $"blocked={blockedRejected}");
    }

    private static BoxRoomTerrain LedgeHangRoom() => new BoxRoomTerrain()
        .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
        .AddBox(new Vector3(-2f, 3.4f, -2f), new Vector3(2f, 4.0f, 2f), 2UL)
        .AddBox(new Vector3(0.8f, 0f, -0.4f), new Vector3(1.6f, 2.9f, 0.4f), 3UL);

    private static bool AssignCeilingAnchor(DropBugLocomotionController bug,
        BoxRoomTerrain terrain, Vector3 from, Vector3 to) =>
        terrain.Raycast(from, to, out TerrainHit hit) &&
        bug.TryAssignHangAnchor(in hit, terrain);

    private readonly record struct HangResult(
        int SettledTick, float Span, float Drift, float MaxChunkSpeed,
        float MaxStepDisplacement, bool CollisionOff, bool RestShrunk,
        float LegSpread, bool Finite);

    private static HangResult RunHangEnter(bool morph)
    {
        var terrain = LedgeHangRoom();
        DropBugLocomotionController bug = NewBug(new Vector3(1.2f, 3.15f, 0f),
            Vector3.Left);
        bug.EnableHangMorph = morph;
        long tick = 0;
        for (int i = 0; i < 30; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        AssignCeilingAnchor(bug, terrain, new Vector3(0f, 3.0f, 0f),
            new Vector3(0f, 3.6f, 0f));
        int settled = -1;
        float maxStep = 0f;
        for (int i = 0; i < 300; i++)
        {
            Vector3 preHead = bug.Head.Pos;
            Tick(bug, terrain, ref tick);
            maxStep = MathF.Max(maxStep, bug.Head.Pos.DistanceTo(preHead));
            if (settled < 0 && bug.Hanging)
            {
                settled = i;
            }
        }
        Vector3 meanCenter = Vector3.Zero;
        float maxSpeed = 0f;
        float drift = 0f;
        var centers = new Vector3[100];
        for (int i = 0; i < 100; i++)
        {
            Tick(bug, terrain, ref tick);
            centers[i] = (bug.Head.Pos + bug.Mid.Pos + bug.Tail.Pos) / 3f;
            meanCenter += centers[i];
            maxSpeed = MathF.Max(maxSpeed,
                MathF.Max(bug.Head.Vel.Length(),
                    MathF.Max(bug.Mid.Vel.Length(), bug.Tail.Vel.Length())));
        }
        meanCenter /= 100f;
        foreach (Vector3 c in centers)
        {
            drift = MathF.Max(drift, c.DistanceTo(meanCenter));
        }
        float span = MathF.Max(bug.Head.Pos.DistanceTo(bug.Mid.Pos),
            MathF.Max(bug.Mid.Pos.DistanceTo(bug.Tail.Pos),
                bug.Head.Pos.DistanceTo(bug.Tail.Pos)));
        bool collisionOff = !bug.Mid.CollideWithTerrain && !bug.Tail.CollideWithTerrain &&
                            bug.Head.CollideWithTerrain;
        bool restShrunk = MathF.Abs(bug.Body.Connections[0].RestLength - 0.125f) < 1e-4f &&
                          MathF.Abs(bug.Body.Connections[1].RestLength - 0.05f) < 1e-4f &&
                          MathF.Abs(bug.Body.Connections[2].RestLength) < 1e-4f;
        float legSpread = 0f;
        foreach (DropBugLeg leg in bug.Legs)
        {
            if (bug.HangAnchor is { } anchor)
            {
                legSpread = MathF.Max(legSpread, leg.Pos.DistanceTo(anchor.Point));
            }
        }
        return new HangResult(settled, span, drift, maxSpeed, maxStep, collisionOff,
            restShrunk, legSpread, IsFinite(bug));
    }

    private static (bool, string) CheckHangEnter()
    {
        HangResult normal = RunHangEnter(morph: true);
        HangResult ablated = RunHangEnter(morph: false);
        // 消融：静息长度不缩 → 身体团不到位（span 上抬、restShrunk=false），门翻红。
        // 位置 lerp 会在 tick 末留下部分压缩，消融差异以「相对余量 + 机制直接观测」判定。
        bool ok = normal.SettledTick is >= 0 and <= 200 &&
                  normal.Span < 0.22f &&
                  normal.Drift < 0.03f &&
                  normal.MaxChunkSpeed < 0.03f &&
                  normal.MaxStepDisplacement < 0.30f &&
                  normal.CollisionOff && normal.RestShrunk &&
                  normal.LegSpread < 0.6f && normal.Finite &&
                  ablated.Span > normal.Span + 0.06f && !ablated.RestShrunk;
        return (ok,
            $"settled={normal.SettledTick}tick span={normal.Span:F3}m " +
            $"drift={normal.Drift:F4}m maxVel={normal.MaxChunkSpeed:F4} " +
            $"maxStep={normal.MaxStepDisplacement:F3}m collisionOff={normal.CollisionOff} " +
            $"restShrunk={normal.RestShrunk} legSpread={normal.LegSpread:F2}m；" +
            $"ablatedSpan={ablated.Span:F3}m ablatedRestShrunk={ablated.RestShrunk}");
    }

    private static (bool, string) CheckHangExit()
    {
        // (a) 撤销锚：瞬时展开不得弹飞，落地后恢复行走。
        var terrain = LedgeHangRoom();
        DropBugLocomotionController bug = NewBug(new Vector3(1.2f, 3.15f, 0f),
            Vector3.Left);
        long tick = 0;
        for (int i = 0; i < 30; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        AssignCeilingAnchor(bug, terrain, new Vector3(0f, 3.0f, 0f),
            new Vector3(0f, 3.6f, 0f));
        for (int i = 0; i < 260 && !bug.Hanging; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        bool wasHanging = bug.Hanging;
        bug.ClearHangAnchor();
        float maxSpeed = 0f;
        float maxStep = 0f;
        int refootTick = -1;
        for (int i = 0; i < 240; i++)
        {
            Vector3 preHead = bug.Head.Pos;
            Tick(bug, terrain, ref tick);
            maxSpeed = MathF.Max(maxSpeed, bug.Head.Vel.Length());
            maxStep = MathF.Max(maxStep, bug.Head.Pos.DistanceTo(preHead));
            if (refootTick < 0 && !bug.Jumping && bug.Footing && bug.Mid.Pos.Y < 1f)
            {
                refootTick = i;
            }
        }
        bool restRestored = MathF.Abs(bug.Body.Connections[0].RestLength - 0.30f) < 1e-4f &&
                            bug.Mid.CollideWithTerrain && bug.Tail.CollideWithTerrain;
        float travelStart = bug.Head.Pos.X;
        for (int i = 0; i < 120; i++)
        {
            bug.MoveDir = Vector3.Left;
            bug.RunSpeed = 1f;
            Tick(bug, terrain, ref tick);
        }
        float travel = travelStart - bug.Head.Pos.X;

        // (b) 悬挂中 Teleport：原子清态，不弹飞。
        DropBugLocomotionController tp = NewBug(new Vector3(1.2f, 3.15f, 0f), Vector3.Left);
        var terrain2 = LedgeHangRoom();
        long tick2 = 0;
        for (int i = 0; i < 30; i++)
        {
            Tick(tp, terrain2, ref tick2);
        }
        AssignCeilingAnchor(tp, terrain2, new Vector3(0f, 3.0f, 0f),
            new Vector3(0f, 3.6f, 0f));
        for (int i = 0; i < 260 && !tp.Hanging; i++)
        {
            Tick(tp, terrain2, ref tick2);
        }
        tp.Teleport(new Vector3(-3f, -1.5f, 0f));
        bool teleportCleared = tp.HangAnchor is null && tp.HangFactor == 0f &&
                               tp.MoveTarget is null && tp.AttackTarget is null &&
                               tp.AttackCooldown == 0;
        float tpMaxSpeed = 0f;
        for (int i = 0; i < 160; i++)
        {
            Tick(tp, terrain2, ref tick2);
            tpMaxSpeed = MathF.Max(tpMaxSpeed, tp.Head.Vel.Length());
        }
        bool tpRecovered = tp.Footing && IsFinite(tp);

        bool ok = wasHanging && maxSpeed < 0.6f && maxStep < 0.45f &&
                  refootTick is >= 0 and <= 200 && restRestored && travel > 0.8f &&
                  teleportCleared && tpMaxSpeed < 0.6f && tpRecovered && IsFinite(bug);
        return (ok,
            $"wasHanging={wasHanging} maxSpeed={maxSpeed:F3} maxStep={maxStep:F3}m " +
            $"refoot={refootTick}tick restRestored={restRestored} travel={travel:F2}m " +
            $"teleportCleared={teleportCleared} tpMaxSpeed={tpMaxSpeed:F3} " +
            $"tpRecovered={tpRecovered}");
    }

    // ================================================================ 悬挂中击飞

    private readonly record struct LaunchHangResult(
        bool Engaged, float FactorAfterLaunch, float MaxFactorInWindow, float MaxDist,
        bool Escaped, bool Landed, bool AnchorKept, float EndFactor, bool Finite);

    /// <summary>悬挂 f=1 后击飞（外部评审 P1）。regrabDelay 为 -1 时用预设默认值。</summary>
    private static LaunchHangResult RunLaunchHang(int regrabDelay)
    {
        DropBugParams p = DropBugFactory.Original();
        if (regrabDelay >= 0)
        {
            p.HangRegrabDelayTicks = regrabDelay;
        }
        var terrain = LedgeHangRoom();
        DropBugLocomotionController bug = DropBugFactory.CreateController(
            new Vector3(1.2f, 3.15f, 0f), Vector3.Left, p);
        long tick = 0;
        for (int i = 0; i < 30; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        AssignCeilingAnchor(bug, terrain, new Vector3(0f, 3.0f, 0f),
            new Vector3(0f, 3.6f, 0f));
        for (int i = 0; i < 260 && !bug.Hanging; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        bool engaged = bug.Hanging;
        Vector3 engagePoint = bug.HangAnchor is { } a
            ? a.Point + a.Normal * 0.25f
            : Vector3.Zero;
        // 0.3 m/tick 斜向下（修复前无引擎实测：该量级从不离开 1m 吸附圈即被吃掉）。
        bug.Launch(new Vector3(-0.268f, -0.134f, 0f));
        float factorAfterLaunch = -1f; // 击飞后第一个 tick 末采样（贴附发生在 tick 内）
        float maxFactorInWindow = 0f;
        float maxDist = 0f;
        bool escaped = false;
        bool landed = false;
        int window = Math.Max(1, p.HangRegrabDelayTicks);
        for (int i = 0; i < 200; i++)
        {
            Tick(bug, terrain, ref tick);
            if (i == 0)
            {
                factorAfterLaunch = bug.HangFactor;
            }
            if (i < window)
            {
                maxFactorInWindow = MathF.Max(maxFactorInWindow, bug.HangFactor);
            }
            float dist = bug.Mid.Pos.DistanceTo(engagePoint);
            maxDist = MathF.Max(maxDist, dist);
            if (dist > p.HangEngageDistance)
            {
                escaped = true;
            }
            if (bug.Mid.Pos.Y < 0.6f)
            {
                landed = true;
            }
        }
        return new LaunchHangResult(engaged, factorAfterLaunch, maxFactorInWindow,
            maxDist, escaped, landed, bug.HangAnchor is not null, bug.HangFactor,
            IsFinite(bug));
    }

    private static (bool, string) CheckLaunchHang()
    {
        LaunchHangResult normal = RunLaunchHang(-1);
        // 消融（冷却归零 = 修复前行为）：下一 tick 即重贴附、从不逃逸、窗口尾部回满 →
        // 门翻红，证明冷却真在挡重贴附。
        LaunchHangResult ablated = RunLaunchHang(0);

        // 宿主显式重申意图让位：击飞后立即重指派锚，冷却必须清零。
        var terrain = LedgeHangRoom();
        DropBugLocomotionController re = NewBug(new Vector3(1.2f, 3.15f, 0f),
            Vector3.Left);
        long tick = 0;
        for (int i = 0; i < 30; i++)
        {
            Tick(re, terrain, ref tick);
        }
        AssignCeilingAnchor(re, terrain, new Vector3(0f, 3.0f, 0f),
            new Vector3(0f, 3.6f, 0f));
        for (int i = 0; i < 260 && !re.Hanging; i++)
        {
            Tick(re, terrain, ref tick);
        }
        re.Launch(new Vector3(-0.05f, 0f, 0f));
        int delayAfterLaunch = re.HangRegrabDelay;
        bool reassigned = AssignCeilingAnchor(re, terrain, new Vector3(0f, 3.0f, 0f),
            new Vector3(0f, 3.6f, 0f));
        int delayAfterAssign = re.HangRegrabDelay;

        bool ok = normal.Engaged && normal.FactorAfterLaunch == 0f &&
                  normal.MaxFactorInWindow == 0f && normal.Escaped && normal.Landed &&
                  normal.AnchorKept && normal.EndFactor == 0f && normal.Finite &&
                  ablated.Engaged && ablated.FactorAfterLaunch > 0f &&
                  !ablated.Escaped && ablated.EndFactor >= 0.99f &&
                  delayAfterLaunch == DropBugFactory.Original().HangRegrabDelayTicks &&
                  reassigned && delayAfterAssign == 0;
        return (ok,
            $"engaged={normal.Engaged} f+1={normal.FactorAfterLaunch:F3} " +
            $"fWindow={normal.MaxFactorInWindow:F3} maxDist={normal.MaxDist:F2}m " +
            $"escaped={normal.Escaped} landed={normal.Landed} " +
            $"anchorKept={normal.AnchorKept} fEnd={normal.EndFactor:F3}；" +
            $"ablated f+1={ablated.FactorAfterLaunch:F3} escaped={ablated.Escaped} " +
            $"fEnd={ablated.EndFactor:F3}；delay={delayAfterLaunch}→{delayAfterAssign}");
    }

    // ================================================================ 俯冲

    private readonly record struct DiveResult(
        bool Engaged, bool Accepted, float Closest, int FlightTicks,
        Vector3 LandingPoint, int CooldownAtLand, bool PounceBlockedDuringCooldown,
        bool PounceAllowedAfter, float HeadFirstFraction, bool Finite);

    private static DiveResult RunDive(bool steering)
    {
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
            .AddBox(new Vector3(-2f, 8.0f, -2f), new Vector3(2f, 8.6f, 2f), 2UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 7.72f, 0f), Vector3.Right);
        bug.EnableDiveSteering = steering;
        long tick = 0;
        AssignCeilingAnchor(bug, terrain, new Vector3(0f, 7.6f, 0f),
            new Vector3(0f, 8.3f, 0f));
        for (int i = 0; i < 260 && !bug.Hanging; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        bool engaged = bug.Hanging;
        Vector3 targetPoint = new(2.5f, 0.2f, 0f);
        bug.AttackTarget = new DropBugAttackTarget(targetPoint, Vector3.Zero);
        bool accepted = bug.ReleaseHangDive();
        float closest = float.MaxValue;
        int flightTicks = 0;
        int headFirst = 0;
        while (bug.Diving && flightTicks < 300)
        {
            Tick(bug, terrain, ref tick);
            flightTicks++;
            closest = MathF.Min(closest, bug.Head.Pos.DistanceTo(targetPoint));
            Vector3 velocity = bug.Head.Vel + bug.Mid.Vel;
            if (velocity.LengthSquared() > 1e-8f &&
                (bug.Head.Pos - bug.Mid.Pos).Dot(velocity.Normalized()) > 0f)
            {
                headFirst++;
            }
        }
        int cooldownAtLand = bug.AttackCooldown;
        // 冷却内蓄力必须被拒（等站稳后测，隔离 Footing 因素）。
        for (int i = 0; i < 14; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        bool blocked = bug.Footing && bug.AttackCooldown > 0 && !bug.TryStartPounce();
        for (int i = 0; i < 40; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        bool allowedAfter = bug.AttackCooldown == 0 && bug.TryStartPounce();
        bug.CancelPounce();
        return new DiveResult(engaged, accepted, closest, flightTicks,
            bug.LastDiveLandingPoint, cooldownAtLand, blocked, allowedAfter,
            flightTicks > 0 ? (float)headFirst / flightTicks : 0f, IsFinite(bug));
    }

    private static (bool, string) CheckDive()
    {
        DiveResult normal = RunDive(steering: true);
        DiveResult ablated = RunDive(steering: false);
        float landingError = new Vector3(normal.LandingPoint.X - 2.5f, 0f,
            normal.LandingPoint.Z).Length();
        bool ok = normal.Engaged && normal.Accepted &&
                  normal.Closest < 0.6f &&
                  normal.FlightTicks is > 5 and < 200 &&
                  landingError < 1.2f &&
                  normal.LandingPoint.Y < 0.8f &&
                  normal.CooldownAtLand >= 15 &&
                  normal.PounceBlockedDuringCooldown &&
                  normal.PounceAllowedAfter &&
                  normal.HeadFirstFraction > 0.6f &&
                  normal.Finite &&
                  ablated.Closest > normal.Closest + 0.15f; // 消融：空中修正关掉 → 脱靶变大
        return (ok,
            $"engaged={normal.Engaged} closest={normal.Closest:F3}m " +
            $"flight={normal.FlightTicks}tick landErr={landingError:F3}m " +
            $"landY={normal.LandingPoint.Y:F2} cooldown={normal.CooldownAtLand} " +
            $"blocked={normal.PounceBlockedDuringCooldown} after={normal.PounceAllowedAfter} " +
            $"headFirst={normal.HeadFirstFraction:P0}；ablatedClosest={ablated.Closest:F3}m");
    }

    // ================================================================ 蓄力扑击

    private static (bool, string) CheckPounce()
    {
        // 正常蓄力弹射。
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 80; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        Vector3 target = new(2.2f, 0.2f, 0f);
        bug.AttackTarget = new DropBugAttackTarget(target, Vector3.Zero);
        bool started = bug.TryStartPounce();
        Vector3 midAtStart = bug.Mid.Pos;
        Vector3 chargeDir = (target - bug.Head.Pos).Normalized();
        float initialSpan = bug.Head.Pos.DistanceTo(bug.Tail.Pos);
        float minSpan = initialSpan;
        float recoil = 0f;
        int leapTick = -1;
        float leapSpeed = 0f;
        float closest = float.MaxValue;
        int landTick = -1;
        for (int i = 1; i <= 200; i++)
        {
            bool wasCharging = bug.ChargingPounce;
            Tick(bug, terrain, ref tick);
            if (bug.ChargingPounce)
            {
                minSpan = MathF.Min(minSpan, bug.Head.Pos.DistanceTo(bug.Tail.Pos));
                recoil = MathF.Max(recoil, (midAtStart - bug.Mid.Pos).Dot(chargeDir));
            }
            if (leapTick < 0 && wasCharging && bug.Jumping)
            {
                leapTick = i;
                leapSpeed = bug.Head.Vel.Length();
            }
            if (bug.Jumping)
            {
                closest = MathF.Min(closest, bug.Head.Pos.DistanceTo(target));
            }
            if (leapTick > 0 && landTick < 0 && !bug.Jumping)
            {
                landTick = i;
            }
        }
        // 压缩签名：刚性链在地面上的蓄力压缩表现为「中段被反向力顶得后坐」（≙ 原作
        // mid −4px·charging；头尾间距因 Rigid 链只轻微屈曲，一并打印供观察）。
        bool normalOk = started && bug.PounceLeapSerial == 1 &&
                        leapTick is >= 12 and <= 20 &&
                        recoil > 0.015f &&
                        leapSpeed > 0.3f && closest < 0.5f &&
                        landTick > leapTick && IsFinite(bug);

        // 侧对目标：可及明显缩短 → 立即放弃。
        DropBugLocomotionController side = NewBug(new Vector3(0f, 0.3f, 0f), Vector3.Right);
        var terrain2 = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        long tick2 = 0;
        for (int i = 0; i < 80; i++)
        {
            Tick(side, terrain2, ref tick2);
        }
        float axisReach = side.PounceReach(Vector3.Right);
        float sideReach = side.PounceReach(Vector3.Back);
        side.AttackTarget = new DropBugAttackTarget(
            side.Mid.Pos + new Vector3(0f, 0f, 5f), Vector3.Zero);
        bool sideStarted = side.TryStartPounce();
        for (int i = 0; i < 4; i++)
        {
            Tick(side, terrain2, ref tick2);
        }
        bool sideAbandoned = sideStarted && side.PounceAbandonSerial == 1 &&
                             !side.ChargingPounce && side.PounceLeapSerial == 0;

        // 蓄力中目标逃逸 → 放弃；消融可及门 → 照样弹射（门翻红证据）。
        (long abandons, long leaps) Escape(bool gate)
        {
            DropBugLocomotionController e = NewBug(new Vector3(0f, 0.3f, 0f),
                Vector3.Right);
            var t = new BoxRoomTerrain()
                .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
            long tk = 0;
            e.EnablePounceReachGate = gate;
            for (int i = 0; i < 80; i++)
            {
                Tick(e, t, ref tk);
            }
            e.AttackTarget = new DropBugAttackTarget(new Vector3(2f, 0.2f, 0f),
                Vector3.Zero);
            e.TryStartPounce();
            for (int i = 0; i < 40; i++)
            {
                if (i == 6)
                {
                    e.AttackTarget = new DropBugAttackTarget(new Vector3(9f, 0.2f, 0f),
                        Vector3.Zero);
                }
                Tick(e, t, ref tk);
            }
            return (e.PounceAbandonSerial, e.PounceLeapSerial);
        }

        (long escapeAbandons, long escapeLeaps) = Escape(gate: true);
        (long ablatedAbandons, long ablatedLeaps) = Escape(gate: false);
        bool escapeOk = escapeAbandons == 1 && escapeLeaps == 0;
        bool gateFlips = ablatedAbandons == 0 && ablatedLeaps == 1;

        // 携带负重禁止蓄力。
        DropBugLocomotionController carry = NewBug(new Vector3(0f, 0.3f, 0f),
            Vector3.Right);
        var terrain3 = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        long tick3 = 0;
        for (int i = 0; i < 80; i++)
        {
            Tick(carry, terrain3, ref tick3);
        }
        carry.CarriedMass = 1f;
        carry.AttackTarget = new DropBugAttackTarget(new Vector3(2f, 0.2f, 0f),
            Vector3.Zero);
        bool carryBlocked = !carry.TryStartPounce();

        bool ok = normalOk && sideAbandoned && sideReach < axisReach * 0.55f &&
                  escapeOk && gateFlips && carryBlocked;
        return (ok,
            $"leapTick={leapTick} recoil={recoil:F3}m compress={initialSpan - minSpan:F3}m " +
            $"leapSpeed={leapSpeed:F3} closest={closest:F3}m land={landTick} " +
            $"reach(axis/side)={axisReach:F2}/{sideReach:F2}m sideAbandon={sideAbandoned} " +
            $"escape={escapeOk} gateFlips={gateFlips} carryBlocked={carryBlocked}");
    }

    // ================================================================ 卡住抖动

    private static (bool, string) CheckStuckShake()
    {
        (float maxSignal, float maxShake, float maxJitter) Run(bool shake)
        {
            var terrain = new BoxRoomTerrain()
                .AddBox(new Vector3(-30f, -1f, -30f), new Vector3(30f, 0f, 30f), 1UL)
                .AddBox(new Vector3(3f, 0f, -6f), new Vector3(4f, 3f, 6f), 2UL);
            DropBugLocomotionController bug = NewBug(new Vector3(1.5f, 0.3f, 0f),
                Vector3.Right);
            bug.EnableStuckShake = shake;
            long tick = 0;
            float maxSignal = 0f;
            float maxShake = 0f;
            float maxJitter = 0f;
            for (int i = 0; i < 500; i++)
            {
                bug.MoveDir = Vector3.Right;
                bug.RunSpeed = 1f;
                Vector3 pre = (bug.Head.Pos + bug.Mid.Pos + bug.Tail.Pos) / 3f;
                Tick(bug, terrain, ref tick);
                maxSignal = MathF.Max(maxSignal, bug.StuckSignal);
                maxShake = MathF.Max(maxShake, bug.StuckShake);
                if (bug.StuckShake > 0.5f)
                {
                    Vector3 post = (bug.Head.Pos + bug.Mid.Pos + bug.Tail.Pos) / 3f;
                    maxJitter = MathF.Max(maxJitter, post.DistanceTo(pre));
                }
            }
            return (maxSignal, maxShake, maxJitter);
        }

        (float signal, float shakeLevel, float jitter) = Run(shake: true);
        (float ablatedSignal, _, float ablatedJitter) = Run(shake: false);
        bool ok = signal >= 0.99f && shakeLevel >= 0.9f && jitter > 0.03f &&
                  ablatedSignal >= 0.99f && ablatedJitter < 0.02f;
        return (ok,
            $"signal={signal:F2} shake={shakeLevel:F2} jitter={jitter:F3}m；" +
            $"ablated signal={ablatedSignal:F2} jitter={ablatedJitter:F3}m");
    }

    // ================================================================ 负重

    private static (bool, string) CheckCarry()
    {
        float Travel(float mass)
        {
            var terrain = new BoxRoomTerrain()
                .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
            DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f),
                Vector3.Right);
            long tick = 0;
            for (int i = 0; i < 60; i++)
            {
                Tick(bug, terrain, ref tick);
            }
            float start = bug.Head.Pos.X;
            for (int i = 0; i < 300; i++)
            {
                bug.MoveDir = Vector3.Right;
                bug.RunSpeed = 1f;
                bug.CarriedMass = mass;
                Tick(bug, terrain, ref tick);
            }
            return bug.Head.Pos.X - start;
        }

        float unloaded = Travel(0f);
        float half = Travel(2f);
        float full = Travel(4f);
        bool ok = half < unloaded * 0.85f && full < half * 0.8f && full > 0.2f;
        return (ok, $"travel mass0/2/4 = {unloaded:F2}/{half:F2}/{full:F2}m");
    }

    // ================================================================ 表现腿

    private static (bool, string) CheckLegs()
    {
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 150; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        long stepsBefore = TotalSteps(bug);
        float cycleBefore = bug.RunCycle;
        for (int i = 0; i < 200; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        bool stationary = TotalSteps(bug) == stepsBefore && bug.RunCycle == cycleBefore;

        long walkStepsBefore = TotalSteps(bug);
        float walkCycleBefore = bug.RunCycle;
        for (int i = 0; i < 200; i++)
        {
            bug.MoveDir = Vector3.Right;
            bug.RunSpeed = 1f;
            Tick(bug, terrain, ref tick);
        }
        float fullSpeedCycle = bug.RunCycle - walkCycleBefore;
        bool everyLegSteps = true;
        foreach (DropBugLeg leg in bug.Legs)
        {
            everyLegSteps &= leg.StepSerial > 0;
        }
        bool walking = TotalSteps(bug) > walkStepsBefore && fullSpeedCycle > 1f &&
                       everyLegSteps;

        // 步频随速度：半油门 vs 全油门的 RunCycle 增速。
        float CycleRate(float speed)
        {
            DropBugLocomotionController b = NewBug(new Vector3(0f, 0.3f, 0f),
                Vector3.Right);
            var t = new BoxRoomTerrain()
                .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
            long tk = 0;
            for (int i = 0; i < 60; i++)
            {
                Tick(b, t, ref tk);
            }
            float before = b.RunCycle;
            for (int i = 0; i < 200; i++)
            {
                b.MoveDir = Vector3.Right;
                b.RunSpeed = speed;
                Tick(b, t, ref tk);
            }
            return b.RunCycle - before;
        }

        float slowCycle = CycleRate(0.45f);
        float fastCycle = CycleRate(1f);
        bool scales = fastCycle > slowCycle * 1.3f;

        // 击飞 → 腿全部离地 dangle。
        DropBugLocomotionController launched = NewBug(new Vector3(0f, 0.3f, 0f),
            Vector3.Right);
        var terrain2 = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        long tick2 = 0;
        for (int i = 0; i < 100; i++)
        {
            Tick(launched, terrain2, ref tick2);
        }
        launched.Launch(new Vector3(0.15f, 0.35f, 0f));
        bool airborneDangle = true;
        for (int i = 0; i < 8; i++)
        {
            Tick(launched, terrain2, ref tick2);
            foreach (DropBugLeg leg in launched.Legs)
            {
                airborneDangle &= !leg.Planted;
            }
        }

        bool ok = stationary && walking && scales && airborneDangle;
        return (ok,
            $"stationaryFrozen={stationary} walking(steps>0,cycle={fullSpeedCycle:F1})={walking} " +
            $"cycle slow/fast={slowCycle:F2}/{fastCycle:F2} scales={scales} " +
            $"airborneDangle={airborneDangle}");
    }

    private static long TotalSteps(DropBugLocomotionController bug)
    {
        long total = 0;
        foreach (DropBugLeg leg in bug.Legs)
        {
            total += leg.StepSerial;
        }
        return total;
    }

    // ================================================================ 生命周期

    private static (bool, string) CheckLifecycle()
    {
        var terrain = BackwardRoom();
        DropBugLocomotionController bug = NewBug(new Vector3(1.5f, 0.3f, 0f),
            Vector3.Left);
        long tick = 0;
        for (int i = 0; i < 80; i++)
        {
            bug.MoveDir = Vector3.Left;
            bug.RunSpeed = 1f;
            Tick(bug, terrain, ref tick);
        }
        bug.MoveTarget = new Vector3(-2f, 0.3f, 1f);
        bug.AttackTarget = new DropBugAttackTarget(new Vector3(1f, 0.2f, 2f),
            new Vector3(0.01f, 0f, 0f));
        AssignCeilingAnchor(bug, terrain, new Vector3(0f, 3f, 0f),
            new Vector3(0f, 3.5f, 0f));

        var oldChunks = new (Vector3 Pos, Vector3 LastPos)[3];
        for (int i = 0; i < 3; i++)
        {
            oldChunks[i] = (bug.Body.Chunks[i].Pos, bug.Body.Chunks[i].LastPos);
        }
        var oldLegs = new (Vector3 Pos, Vector3 LastPos)[bug.Legs.Count];
        for (int i = 0; i < bug.Legs.Count; i++)
        {
            oldLegs[i] = (bug.Legs[i].Pos, bug.Legs[i].LastPos);
        }
        Vector3 oldTarget = bug.MoveTarget!.Value;
        Vector3 oldAttack = bug.AttackTarget!.Value.Point;
        Vector3 oldAnchor = bug.HangAnchor!.Value.Point;
        Vector3 delta = new(512f, 64f, -256f);
        bug.Shift(delta);
        bool shiftExact = bug.MoveTarget == oldTarget + delta &&
                          bug.AttackTarget!.Value.Point == oldAttack + delta &&
                          bug.HangAnchor!.Value.Point == oldAnchor + delta;
        for (int i = 0; i < 3; i++)
        {
            shiftExact &= bug.Body.Chunks[i].Pos == oldChunks[i].Pos + delta &&
                          bug.Body.Chunks[i].LastPos == oldChunks[i].LastPos + delta;
        }
        for (int i = 0; i < bug.Legs.Count; i++)
        {
            shiftExact &= bug.Legs[i].Pos == oldLegs[i].Pos + delta &&
                          bug.Legs[i].LastPos == oldLegs[i].LastPos + delta;
        }
        // Shift 后动力学无缝：再走 30 tick 不炸。
        bool shiftContinues = true;
        for (int i = 0; i < 30; i++)
        {
            Tick(bug, new BoxRoomTerrain()
                .AddBox(new Vector3(-60f, -1f, -60f) + delta,
                    new Vector3(60f, 0f, 60f) + delta, 1UL), ref tick);
            shiftContinues &= IsFinite(bug);
        }

        // Launch：速度精确注入、MoveTarget 保留、悬挂/蓄力打断、落地恢复。
        var terrain2 = BackwardRoom();
        DropBugLocomotionController launched = NewBug(new Vector3(1.5f, 0.3f, 0f),
            Vector3.Left);
        long tick2 = 0;
        for (int i = 0; i < 80; i++)
        {
            Tick(launched, terrain2, ref tick2);
        }
        launched.MoveTarget = new Vector3(-2f, 0.3f, 0f);
        Vector3 headVel = launched.Head.Vel;
        Vector3 midVel = launched.Mid.Vel;
        Vector3 tailVel = launched.Tail.Vel;
        Vector3 impulse = new(0.1f, 0.25f, -0.05f);
        launched.Launch(impulse);
        bool launchExact = launched.Head.Vel == headVel + impulse &&
                           launched.Mid.Vel == midVel + impulse &&
                           launched.Tail.Vel == tailVel + impulse &&
                           launched.MoveTarget is not null &&
                           !launched.Footing;
        int recoverTick = -1;
        for (int i = 0; i < 200; i++)
        {
            Tick(launched, terrain2, ref tick2);
            if (recoverTick < 0 && launched.Footing)
            {
                recoverTick = i;
            }
        }
        bool launchRecovered = recoverTick is >= 0 and <= 150 && IsFinite(launched);

        bool ok = shiftExact && shiftContinues && launchExact && launchRecovered;
        return (ok,
            $"shiftExact={shiftExact} shiftContinues={shiftContinues} " +
            $"launchExact={launchExact} recover={recoverTick}tick");
    }

    // ================================================================ 查询预算

    private static (bool, string) CheckQueryBudget()
    {
        var terrain = new BoxRoomTerrain()
            .AddBox(new Vector3(-60f, -1f, -60f), new Vector3(60f, 0f, 60f), 1UL);
        DropBugLocomotionController bug = NewBug(new Vector3(0f, 0.3f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 60; i++)
        {
            Tick(bug, terrain, ref tick);
        }
        long maxRays = 0;
        long maxShapes = 0;
        long totalRays = 0;
        long totalShapes = 0;
        for (int i = 0; i < 300; i++)
        {
            bug.MoveDir = Vector3.Right;
            bug.RunSpeed = 1f;
            long raysBefore = terrain.RayCount;
            long shapesBefore = terrain.ShapeQueryCount;
            Tick(bug, terrain, ref tick);
            long rays = terrain.RayCount - raysBefore;
            long shapes = terrain.ShapeQueryCount - shapesBefore;
            maxRays = Math.Max(maxRays, rays);
            maxShapes = Math.Max(maxShapes, shapes);
            totalRays += rays;
            totalShapes += shapes;
        }
        float avgRays = totalRays / 300f;
        float avgShapes = totalShapes / 300f;
        bool ok = maxRays <= 30 && maxShapes <= 8;
        return (ok,
            $"rays avg={avgRays:F1} max={maxRays}（门 30）；" +
            $"shapes avg={avgShapes:F1} max={maxShapes}（门 8）");
    }

    // ================================================================ 基础设施

    private static DropBugLocomotionController NewBug(Vector3 origin, Vector3 forward) =>
        DropBugFactory.CreateController(origin, forward, DropBugFactory.Original());

    private static void Tick(
        DropBugLocomotionController bug, BoxRoomTerrain terrain, ref long tick)
    {
        tick++;
        bug.Tick(new TickContext(GravityPerTick, terrain, tick));
        // 卡住抖动的 pos 注入发生在本 tick 碰撞之后（≙ 原作 Act 顺序），下一 tick 的
        // 碰撞立刻收回——抖动激活期间不计 tick 末残余，其余时刻按 2mm 门统计。
        if (bug.StuckShake <= 1e-3f)
        {
            float residual = terrain.MeasureResidualPenetration(bug.Body);
            if (residual > _maxResidualPenetration)
            {
                _maxResidualPenetration = residual;
                _maxPenetrationContext = $"{_currentCheck}@tick{tick}";
            }
        }
    }

    private static bool IsFinite(DropBugLocomotionController bug)
    {
        foreach (BodyChunk chunk in bug.Body.Chunks)
        {
            if (!chunk.Pos.IsFinite() || !chunk.LastPos.IsFinite() || !chunk.Vel.IsFinite())
            {
                return false;
            }
        }
        foreach (DropBugLeg leg in bug.Legs)
        {
            if (!leg.Pos.IsFinite() || !leg.Vel.IsFinite())
            {
                return false;
            }
        }
        return bug.Forward.IsFinite() && bug.Up.IsFinite() && bug.Right.IsFinite() &&
               float.IsFinite(bug.HangFactor) && float.IsFinite(bug.PounceCharge) &&
               float.IsFinite(bug.RunCycle) && float.IsFinite(bug.StuckShake);
    }

    /// <summary>AABB 盒子 + 半空间的解析地形。语义与 RaycastTerrainQuery 对齐：
    /// 起点已在实体内 → HitFromInside（Point=起点、零法线）；SpherePenetration 给出
    /// 最深交叠的 MTD（球心在盒内也有有效方向）。</summary>
    private sealed class BoxRoomTerrain : ITerrainQuery
    {
        private sealed class Solid
        {
            public Vector3 Min;
            public Vector3 Max;
            public Vector3 PlanePoint;
            public Vector3 PlaneNormal;
            public bool IsBox;
            public ulong Id;
            public bool Enabled = true;
        }

        private readonly List<Solid> _solids = new();

        public long RayCount { get; private set; }
        public long ShapeQueryCount { get; private set; }

        public BoxRoomTerrain AddBox(Vector3 min, Vector3 max, ulong id)
        {
            _solids.Add(new Solid { Min = min, Max = max, IsBox = true, Id = id });
            return this;
        }

        /// <summary>normal 指向可通行半空间，其背面为无限厚实体。</summary>
        public BoxRoomTerrain AddHalfSpace(Vector3 point, Vector3 normal, ulong id)
        {
            _solids.Add(new Solid
            {
                PlanePoint = point,
                PlaneNormal = normal.Normalized(),
                IsBox = false,
                Id = id,
            });
            return this;
        }

        public void SetEnabled(ulong id, bool enabled)
        {
            foreach (Solid solid in _solids)
            {
                if (solid.Id == id)
                {
                    solid.Enabled = enabled;
                }
            }
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            RayCount++;
            hit = default;
            foreach (Solid solid in _solids)
            {
                if (solid.Enabled && Inside(solid, from))
                {
                    hit = new TerrainHit(from, Vector3.Zero, solid.Id);
                    return true;
                }
            }
            bool found = false;
            float bestT = float.PositiveInfinity;
            Vector3 bestNormal = Vector3.Zero;
            ulong bestId = 0;
            foreach (Solid solid in _solids)
            {
                if (!solid.Enabled)
                {
                    continue;
                }
                if (solid.IsBox)
                {
                    if (RayBox(from, to, solid, out float t, out Vector3 normal) &&
                        t < bestT)
                    {
                        found = true;
                        bestT = t;
                        bestNormal = normal;
                        bestId = solid.Id;
                    }
                }
                else
                {
                    float fromDistance = (from - solid.PlanePoint).Dot(solid.PlaneNormal);
                    float toDistance = (to - solid.PlanePoint).Dot(solid.PlaneNormal);
                    if (fromDistance < 0f || toDistance >= 0f)
                    {
                        continue;
                    }
                    float denominator = fromDistance - toDistance;
                    if (denominator <= 1e-12f)
                    {
                        continue;
                    }
                    float t = fromDistance / denominator;
                    if (t < bestT)
                    {
                        found = true;
                        bestT = t;
                        bestNormal = solid.PlaneNormal;
                        bestId = solid.Id;
                    }
                }
            }
            if (!found)
            {
                return false;
            }
            hit = new TerrainHit(from.Lerp(to, bestT), bestNormal, bestId);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            ShapeQueryCount++;
            pushDir = Vector3.Up;
            depth = 0f;
            foreach (Solid solid in _solids)
            {
                if (!solid.Enabled)
                {
                    continue;
                }
                if (!solid.IsBox)
                {
                    float candidate = radius -
                        (center - solid.PlanePoint).Dot(solid.PlaneNormal);
                    if (candidate > depth)
                    {
                        depth = candidate;
                        pushDir = solid.PlaneNormal;
                    }
                    continue;
                }
                if (Inside(solid, center))
                {
                    // 盒内：沿最浅面推出。
                    float best = float.MaxValue;
                    Vector3 dir = Vector3.Up;
                    Consider(center.X - solid.Min.X, Vector3.Left, ref best, ref dir);
                    Consider(solid.Max.X - center.X, Vector3.Right, ref best, ref dir);
                    Consider(center.Y - solid.Min.Y, Vector3.Down, ref best, ref dir);
                    Consider(solid.Max.Y - center.Y, Vector3.Up, ref best, ref dir);
                    Consider(center.Z - solid.Min.Z, Vector3.Forward, ref best, ref dir);
                    Consider(solid.Max.Z - center.Z, Vector3.Back, ref best, ref dir);
                    float candidate = radius + best;
                    if (candidate > depth)
                    {
                        depth = candidate;
                        pushDir = dir;
                    }
                }
                else
                {
                    Vector3 closest = center.Clamp(solid.Min, solid.Max);
                    Vector3 d = center - closest;
                    float distance = d.Length();
                    if (distance < radius)
                    {
                        float candidate = radius - distance;
                        if (candidate > depth)
                        {
                            depth = candidate;
                            pushDir = distance > 1e-9f ? d / distance : Vector3.Up;
                        }
                    }
                }
            }
            return depth > 0f;
        }

        /// <summary>tick 末残余穿透（米）；碰撞被关闭的悬挂节不计（有意嵌入是 RW 语义）。</summary>
        public float MeasureResidualPenetration(Body body)
        {
            float max = 0f;
            foreach (BodyChunk chunk in body.Chunks)
            {
                if (!chunk.CollideWithTerrain)
                {
                    continue;
                }
                foreach (Solid solid in _solids)
                {
                    if (!solid.Enabled)
                    {
                        continue;
                    }
                    float penetration;
                    if (!solid.IsBox)
                    {
                        penetration = chunk.TerrainRadius -
                            (chunk.Pos - solid.PlanePoint).Dot(solid.PlaneNormal);
                    }
                    else if (Inside(solid, chunk.Pos))
                    {
                        penetration = chunk.TerrainRadius + 0.001f;
                    }
                    else
                    {
                        Vector3 closest = chunk.Pos.Clamp(solid.Min, solid.Max);
                        penetration = chunk.TerrainRadius - chunk.Pos.DistanceTo(closest);
                    }
                    max = MathF.Max(max, penetration);
                }
            }
            return max;
        }

        private static void Consider(float distance, Vector3 direction, ref float best,
            ref Vector3 bestDir)
        {
            if (distance < best)
            {
                best = distance;
                bestDir = direction;
            }
        }

        private static bool Inside(Solid solid, Vector3 point) => solid.IsBox
            ? point.X > solid.Min.X && point.X < solid.Max.X &&
              point.Y > solid.Min.Y && point.Y < solid.Max.Y &&
              point.Z > solid.Min.Z && point.Z < solid.Max.Z
            : (point - solid.PlanePoint).Dot(solid.PlaneNormal) < 0f;

        private static bool RayBox(Vector3 from, Vector3 to, Solid box,
            out float tHit, out Vector3 normal)
        {
            tHit = 0f;
            normal = Vector3.Zero;
            Vector3 d = to - from;
            float tMin = 0f;
            float tMax = 1f;
            int hitAxis = -1;
            bool hitLow = false;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = Axis(from, axis);
                float dir = Axis(d, axis);
                float low = Axis(box.Min, axis);
                float high = Axis(box.Max, axis);
                if (MathF.Abs(dir) < 1e-9f)
                {
                    if (origin <= low || origin >= high)
                    {
                        return false;
                    }
                    continue;
                }
                float t1 = (low - origin) / dir;
                float t2 = (high - origin) / dir;
                bool enteredLow = t1 < t2;
                float tNear = MathF.Min(t1, t2);
                float tFar = MathF.Max(t1, t2);
                if (tNear > tMin)
                {
                    tMin = tNear;
                    hitAxis = axis;
                    hitLow = enteredLow;
                }
                tMax = MathF.Min(tMax, tFar);
                if (tMin > tMax)
                {
                    return false;
                }
            }
            if (hitAxis < 0 || tMin <= 0f || tMin > 1f)
            {
                return false;
            }
            tHit = tMin;
            normal = hitAxis switch
            {
                0 => hitLow ? Vector3.Left : Vector3.Right,
                1 => hitLow ? Vector3.Down : Vector3.Up,
                _ => hitLow ? Vector3.Forward : Vector3.Back,
            };
            return true;
        }

        private static float Axis(Vector3 v, int axis) =>
            axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    }
}
