using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Godot;

namespace ProcAnim.Core.Smoke;

/// <summary>
/// 无引擎冒烟回归：内核完全脱离 Godot 运行时跑在纯 .NET 进程里（M5「与引擎解耦」
/// 的实证），也是回迁后目标仓库最快的内核回归入口。
/// 纯解析平面地形 + default 品种平地巡走（前半直行、后半 45° 转向）。
/// 断言：双跑 bit-exact、哈希对基线（防「确定但错误」）、里程过阈、终态约束收敛、
/// 无 NaN、嵌入恢复、Shift 连续性、Launch 恢复、MoveTarget 直喂契约、
/// 墙顶顶死姿态稳定、内核程序集引擎边界干净（TypeRef 扫描）。
/// </summary>
internal static class Program
{
    private const int Ticks = 1000;

    /// <summary>与沙盒一致：40 tick/s、重力 36 m/s²（≙ RW 0.9px/tick²）。</summary>
    private const float TickDt = 0.025f;
    private const float GravityMps2 = 36f;

    /// <summary>基线哈希 = 内核行为的指纹。**只有有意改物理时才允许更新**（与 CLAUDE.md §5
    /// 的哈希表同步改）——只比双跑一致会漏掉「确定但错误」的行为漂移。</summary>
    private const ulong ExpectedHash = 0xAAA0E4963668E5DCUL;

    /// <summary>人形（scavenger 品种）平地巡走基线哈希（折叠序 chunks → legs → arms）。
    /// 与 ExpectedHash 同规则：有意改人形内核 = 同一提交更新此值与 run_matrix.sh 的人形行。</summary>
    private const ulong HumanoidExpectedHash = 0x900982675F381F26UL;

    private static int Main()
    {
        // 输出统一不变文化：逗号小数 locale 会破坏下游脚本对数值行的解析（与沙盒同一约定）。
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        (ulong Hash, float Walk, float Grip, float RaysPerTick, bool Nan, float EndDev) a = Run();
        (ulong Hash, float Walk, float Grip, float RaysPerTick, bool Nan, float EndDev) b = Run();
        bool boundaryOk = CheckEngineBoundary(out string boundaryMsg);
        bool embedOk = CheckEmbedRecovery(out string embedMsg);
        bool shiftOk = CheckShiftContinuity(out string shiftMsg);
        bool launchOk = CheckLaunchRecovery(out string launchMsg);
        bool carrotOk = CheckExternalTarget(out string carrotMsg);
        bool rotationOk = CheckRotationTopology(out string rotationMsg);
        bool recoveryOk = CheckRecoveryInvariants(out string recoveryMsg);
        bool wallPoseOk = CheckWallPoseStability(out string wallPoseMsg);
        bool heavyMountOk = CheckHeavyWallMount(out string heavyMountMsg);
        (ulong Hash, float Walk, float Upright, long Plants, float RaysPerTick, bool Nan, float EndDev) ha = RunHumanoid();
        (ulong Hash, float Walk, float Upright, long Plants, float RaysPerTick, bool Nan, float EndDev) hb = RunHumanoid();
        bool humanoidStandOk = CheckHumanoidStand(out string humanoidStandMsg);
        bool humanoidStunOk = CheckHumanoidStun(out string humanoidStunMsg);
        bool humanoidActOk = CheckHumanoidAct(out string humanoidActMsg);
        bool humanoidShiftOk = CheckHumanoidShift(out string humanoidShiftMsg);
        bool humanoidGravityOk = CheckHumanoidGravityGate(out string humanoidGravityMsg);
        bool humanoidHandsOk = CheckHumanoidHandSpread(out string humanoidHandsMsg);
        bool humanoidGaitOk = CheckHumanoidGait(out string humanoidGaitMsg);

        Console.WriteLine($"[CORE-DET] ticks={Ticks} run1={a.Hash:X16} run2={b.Hash:X16} expected={ExpectedHash:X16}");
        Console.WriteLine($"[CORE-METRIC] walkDistance={a.Walk:F2}m avgLegsGripping={a.Grip:F2}/4 " +
                          $"raysPerTick={a.RaysPerTick:F1} endDev={a.EndDev:F4}m nan={a.Nan}");
        Console.WriteLine($"[CORE-BOUNDARY] {boundaryMsg}");
        Console.WriteLine($"[CORE-EMBED] {embedMsg}");
        Console.WriteLine($"[CORE-SHIFT] {shiftMsg}");
        Console.WriteLine($"[CORE-LAUNCH] {launchMsg}");
        Console.WriteLine($"[CORE-CARROT] {carrotMsg}");
        Console.WriteLine($"[CORE-ROTATION] {rotationMsg}");
        Console.WriteLine($"[CORE-RECOVERY] {recoveryMsg}");
        Console.WriteLine($"[CORE-WALL-POSE] {wallPoseMsg}");
        Console.WriteLine($"[CORE-HEAVY-MOUNT] {heavyMountMsg}");
        Console.WriteLine($"[CORE-HUMANOID-DET] ticks={Ticks} run1={ha.Hash:X16} run2={hb.Hash:X16} " +
                          $"expected={HumanoidExpectedHash:X16} walk={ha.Walk:F2}m meanUpright={ha.Upright:F2} " +
                          $"plants={ha.Plants} raysPerTick={ha.RaysPerTick:F1} endDev={ha.EndDev:F4}m nan={ha.Nan}");
        Console.WriteLine($"[CORE-HUMANOID-STAND] {humanoidStandMsg}");
        Console.WriteLine($"[CORE-HUMANOID-STUN] {humanoidStunMsg}");
        Console.WriteLine($"[CORE-HUMANOID-ACT] {humanoidActMsg}");
        Console.WriteLine($"[CORE-HUMANOID-SHIFT] {humanoidShiftMsg}");
        Console.WriteLine($"[CORE-HUMANOID-GRAVITY] {humanoidGravityMsg}");
        Console.WriteLine($"[CORE-HUMANOID-HANDS] {humanoidHandsMsg}");
        Console.WriteLine($"[CORE-HUMANOID-GAIT] {humanoidGaitMsg}");

        var reasons = new List<string>();
        if (a.Hash != b.Hash)
        {
            reasons.Add("双跑哈希不一致");
        }
        if (a.Hash != ExpectedHash)
        {
            reasons.Add("哈希偏离基线（有意改内核请同步更新 ExpectedHash 与 CLAUDE.md §5）");
        }
        if (a.Walk <= 15f)
        {
            reasons.Add($"行走里程不足（{a.Walk:F2}m）");
        }
        if (a.Nan)
        {
            reasons.Add("状态出现 NaN/Inf");
        }
        if (a.EndDev >= 0.05f)
        {
            reasons.Add($"终态约束偏差过大（{a.EndDev:F4}m，碰撞后量取）");
        }
        if (!boundaryOk)
        {
            reasons.Add("内核程序集越出引擎边界");
        }
        if (!embedOk)
        {
            reasons.Add("嵌入恢复失败（出生在地形内没有被推出）");
        }
        if (!shiftOk)
        {
            reasons.Add("Shift 后步态中断或平移不完备（rebase 契约被破坏）");
        }
        if (!launchOk)
        {
            reasons.Add("Launch 后未进坠落态或未恢复步态（击飞契约被破坏）");
        }
        if (!carrotOk)
        {
            reasons.Add("MoveTarget 直喂契约被破坏（到达/观测/取消/Teleport 语义不一致）");
        }
        if (!rotationOk)
        {
            reasons.Add("RotationChunk 拓扑被破坏（连接互绑/工厂脊柱钉定不变量不成立）");
        }
        if (!recoveryOk)
        {
            reasons.Add("深卡角恢复边界失效（强度外推/接触可行锥/候选穿透）");
        }
        if (!wallPoseOk)
        {
            reasons.Add("墙顶顶死时髋部侧摆失稳或未回到头后方");
        }
        if (!heavyMountOk)
        {
            reasons.Add("heavy 上墙前段未及时沿墙、链尾未按期收敛，或停驶时被吸向墙");
        }
        if (ha.Hash != hb.Hash)
        {
            reasons.Add("人形双跑哈希不一致");
        }
        if (ha.Hash != HumanoidExpectedHash)
        {
            reasons.Add("人形哈希偏离基线（有意改人形内核请同步更新 HumanoidExpectedHash 与矩阵人形行）");
        }
        if (ha.Walk <= 30f)
        {
            reasons.Add($"人形行走里程不足（{ha.Walk:F2}m）");
        }
        if (ha.Upright <= 0.6f)
        {
            reasons.Add($"人形行走平均直立度不足（{ha.Upright:F2}）");
        }
        if (ha.Plants <= 40)
        {
            reasons.Add($"人形撑地锁点过少（{ha.Plants}——knuckle-walk 没在跑）");
        }
        if (ha.Nan)
        {
            reasons.Add("人形状态出现 NaN/Inf");
        }
        if (ha.EndDev >= 0.05f)
        {
            reasons.Add($"人形终态约束偏差过大（{ha.EndDev:F4}m）");
        }
        if (!humanoidStandOk)
        {
            reasons.Add("人形站立/摔倒爬起失效（失重悬停或站立力偶回正被破坏）");
        }
        if (!humanoidStunOk)
        {
            reasons.Add("人形昏迷瘫倒/苏醒爬起失效（Conscious 开关语义被破坏）");
        }
        if (!humanoidActOk)
        {
            reasons.Add("人形手臂动作契约失效（指向/持物/蓄力停驶/出手语义不一致）");
        }
        if (!humanoidShiftOk)
        {
            reasons.Add("人形 Shift/Teleport/Launch 完备性被破坏");
        }
        if (!humanoidGravityOk)
        {
            reasons.Add("人形重力开关边界失守（陡壁可爬/贴墙接触被计成撑地）");
        }
        if (!humanoidHandsOk)
        {
            reasons.Add("人形双手撑地重合（扇扫两手分离失效——目标点侧向错开/俯仰偏置被破坏）");
        }
        if (!humanoidGaitOk)
        {
            reasons.Add("人形步态退化（高频碎步/双悬空/触地不足——前瞻点释放或支撑保序被破坏）");
        }

        bool pass = reasons.Count == 0;
        Console.WriteLine(pass
            ? "[CORE-SMOKE] PASS：双跑 bit-exact、哈希对基线、约束收敛、边界干净、无 NaN"
            : $"[CORE-SMOKE] FAIL：{string.Join("；", reasons)}");
        return pass ? 0 : 1;
    }

    private static (ulong, float, float, float, bool, float) Run()
    {
        var terrain = new PlaneTerrainQuery(0f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(0f, 0.6f, 0f), BodyFactory.Default());
        var hasher = new DeterminismHasher();
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);

        float walk = 0f;
        long gripSum = 0;
        Vector3 lastHead = controller.Head.Pos;
        for (long tick = 1; tick <= Ticks; tick++)
        {
            // 脚本化路线：把「直行步态」与「转弯换步」都纳入哈希。
            controller.MoveDir = tick <= Ticks / 2
                ? new Vector3(1f, 0f, 0f)
                : new Vector3(1f, 0f, 1f).Normalized();
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, tick));

            hasher.FoldBody(controller.Body);
            hasher.FoldLimbs(controller.Limbs);

            Vector3 step = controller.Head.Pos - lastHead;
            step.Y = 0f;
            walk += step.Length();
            lastHead = controller.Head.Pos;
            gripSum += controller.LegsGripping;
        }

        bool nan = false;
        foreach (BodyChunk c in controller.Body.Chunks)
        {
            nan |= !c.Pos.IsFinite();
        }
        foreach (Limb l in controller.Limbs)
        {
            nan |= !l.Pos.IsFinite();
        }
        return (hasher.Value, walk, (float)gripSum / Ticks, (float)terrain.RayCount / Ticks,
            nan, controller.Body.CurrentMaxDeviation());
    }

    /// <summary>
    /// 嵌入恢复：出生在地板下，SpherePenetration 的 MTD 必须在数 tick 内把全身推出
    /// （外部评审 P1-3：旧版 HitFromInside 回退 LastPos + 清速 = 出生嵌入永久冻结）。
    /// </summary>
    private static bool CheckEmbedRecovery(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(0f, -0.1f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        for (long tick = 1; tick <= 50; tick++)
        {
            controller.MoveDir = Vector3.Zero;
            controller.RunSpeed = 0f;
            controller.Tick(new TickContext(gravityPerTick, terrain, tick));
        }
        float minY = float.MaxValue;
        foreach (BodyChunk c in controller.Body.Chunks)
        {
            minY = Math.Min(minY, c.Pos.Y);
        }
        message = $"出生 y=-0.1 嵌入地板，50 tick 后最低 chunk y={minY:F3}（须 ≥ 0）";
        return minY > -1e-3f;
    }

    /// <summary>
    /// rebase 完备性 + 连续性：行走中整体 Shift(+512,0,+512)（浮点原点重置的契约入口）。
    /// ① 逐字段精确断言：每个带世界坐标的量（chunk Pos/LastPos、limb Pos/LastPos/HuntPos、
    /// MoveTarget/LastMoveTarget）必须恰好移动 delta——漏平移 LastPos/HuntPos 在无限平面上
    /// 不会立刻炸，光靠续走断言测不出（终审 C12）。② 直喂目标随世界平移后仍能续走，
    /// 且里程过阈、无 NaN、约束收敛。
    /// </summary>
    private static bool CheckShiftContinuity(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(0f, 0.6f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        long t = 0;
        for (int i = 0; i < 300; i++)
        {
            t++;
            controller.MoveDir = new Vector3(1f, 0f, 0f);
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, t));
        }

        controller.MoveTarget = new Vector3(controller.Head.Pos.X + 100f, 0f, controller.Head.Pos.Z + 10f);
        controller.RunSpeed = 1f;
        t++;
        controller.Tick(new TickContext(gravityPerTick, terrain, t));

        var delta = new Vector3(512f, 0f, 512f);
        var prevChunks = new (Vector3 Pos, Vector3 LastPos)[controller.Body.Chunks.Count];
        for (int i = 0; i < prevChunks.Length; i++)
        {
            prevChunks[i] = (controller.Body.Chunks[i].Pos, controller.Body.Chunks[i].LastPos);
        }
        var prevLimbs = new (Vector3 Pos, Vector3 LastPos, Vector3 HuntPos)[controller.Limbs.Count];
        for (int i = 0; i < prevLimbs.Length; i++)
        {
            prevLimbs[i] = (controller.Limbs[i].Pos, controller.Limbs[i].LastPos, controller.Limbs[i].HuntPos);
        }
        Vector3 prevMoveTarget = controller.MoveTarget!.Value;
        Vector3 prevLastMoveTarget = controller.LastMoveTarget;
        MoveTargetKind prevTargetKind = controller.LastMoveTargetKind;

        controller.Shift(delta);
        bool exact = true;
        for (int i = 0; i < prevChunks.Length; i++)
        {
            exact &= controller.Body.Chunks[i].Pos == prevChunks[i].Pos + delta
                && controller.Body.Chunks[i].LastPos == prevChunks[i].LastPos + delta;
        }
        for (int i = 0; i < prevLimbs.Length; i++)
        {
            exact &= controller.Limbs[i].Pos == prevLimbs[i].Pos + delta
                && controller.Limbs[i].LastPos == prevLimbs[i].LastPos + delta
                && controller.Limbs[i].HuntPos == prevLimbs[i].HuntPos + delta;
        }
        exact &= controller.MoveTarget == prevMoveTarget + delta
            && controller.LastMoveTarget == prevLastMoveTarget + delta
            && controller.LastMoveTargetKind == prevTargetKind;

        Vector3 start = controller.Head.Pos;
        for (int i = 0; i < 300; i++)
        {
            t++;
            controller.MoveDir = new Vector3(1f, 0f, 0f);
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, t));
        }
        Vector3 d = controller.Head.Pos - start;
        d.Y = 0f;
        bool nan = !controller.Head.Pos.IsFinite();
        float dev = controller.Body.CurrentMaxDeviation();
        message = $"Shift(+512,0,+512) 含直喂目标逐字段精确={exact}，" +
                  $"续走 {d.Length():F2}m，endDev={dev:F4}m";
        return exact && d.Length() > 15f && !nan && dev < 0.05f;
    }

    /// <summary>
    /// Launch 恢复：行走中被抛掷（全腿松手+站稳清零）→ 当 tick 后必须处于坠落态，
    /// 落地后必须重新关重力并继续行走——击飞契约的机器可查覆盖（--yank 是它的场景版）。
    /// </summary>
    private static bool CheckLaunchRecovery(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(0f, 0.6f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        long t = 0;
        for (int i = 0; i < 300; i++)
        {
            t++;
            controller.MoveDir = new Vector3(1f, 0f, 0f);
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, t));
        }
        // 恢复控制器生命周期：Launch 必须立刻撤销临时缩碰撞体与 post-contact 门控。
        controller.Hips.TerrainSqueeze = 0.1f;
        controller.Body.EnablePostCollisionStructureRecovery = true;
        controller.Launch(new Vector3(0.1f, 0.4f, 0.15f));
        bool recoveryReset = controller.Hips.TerrainSqueeze == 1f
            && !controller.Body.EnablePostCollisionStructureRecovery;
        t++;
        controller.Tick(new TickContext(gravityPerTick, terrain, t));
        bool airborne = controller.ApplyGravity;
        Vector3 start = controller.Head.Pos;
        for (int i = 0; i < 500; i++)
        {
            t++;
            controller.MoveDir = new Vector3(1f, 0f, 0f);
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, t));
        }
        bool regained = !controller.ApplyGravity;
        Vector3 d = controller.Head.Pos - start;
        d.Y = 0f;
        bool nan = !controller.Head.Pos.IsFinite();
        message = $"Launch 恢复状态清零={recoveryReset}，坠落={airborne}，" +
                  $"500 tick 后回归步态={regained}，续走 {d.Length():F2}m";
        return recoveryReset && airborne && regained && d.Length() > 10f && !nan;
    }

    /// <summary>
    /// MoveTarget 直喂契约（路线2，≙ RW 寻路器喂路径格）：平地依次喂 3 个贴地路径点，
    /// AtMoveTarget 到达即换点。断言：① 全部点按序到达（不悬停、不过冲震荡）
    /// ② 直喂期间胡萝卜分支恒为 External（射线构造被旁路），Tick 后 HasMoveIntent 仍如实
    /// ③ 中途清 null 且油门保持 1 后意图归零（派生 MoveDir 不残留）④ Teleport 作废旧路径点。
    /// </summary>
    private static bool CheckExternalTarget(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(0f, 0.6f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        var route = new[] { new Vector3(3f, 0f, 0f), new Vector3(5f, 0f, 2f), new Vector3(2f, 0f, 4f) };

        int reached = 0;
        long arriveTick = 0;
        bool alwaysExternal = true;
        bool intentObservable = true;
        long t = 0;
        for (int i = 0; i < 1500 && reached < route.Length; i++)
        {
            t++;
            controller.MoveTarget = route[reached];
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, t));
            if (controller.LastMoveTargetKind is not (MoveTargetKind.External or MoveTargetKind.None))
            {
                alwaysExternal = false;
            }
            if (!controller.AtMoveTarget && !controller.HasMoveIntent)
            {
                intentObservable = false;
            }
            if (controller.AtMoveTarget)
            {
                reached++;
                arriveTick = t;
            }
        }

        // 不在到点 tick 上取消，且故意保留油门：若派生 MoveDir 跨 tick 残留，
        // 清 null 后会立刻掉回方向驱动继续走，旧断言把 RunSpeed 同时清零因此测不出来。
        controller.MoveTarget = controller.Head.Pos + new Vector3(10f, -controller.Head.Pos.Y, 0f);
        controller.RunSpeed = 1f;
        t++;
        controller.Tick(new TickContext(gravityPerTick, terrain, t));
        bool activeBeforeCancel = controller.HasMoveIntent
            && controller.LastMoveTargetKind == MoveTargetKind.External
            && controller.MoveDir == Vector3.Zero;
        controller.MoveTarget = null;
        t++;
        controller.Tick(new TickContext(gravityPerTick, terrain, t));
        bool cleared = !controller.AtMoveTarget && !controller.HasMoveIntent
            && controller.LastMoveTargetKind == MoveTargetKind.None
            && controller.MoveDir == Vector3.Zero;

        controller.MoveTarget = controller.Head.Pos + new Vector3(10f, -controller.Head.Pos.Y, 0f);
        t++;
        controller.Tick(new TickContext(gravityPerTick, terrain, t));
        controller.Teleport(new Vector3(2f, 0f, 0f));
        bool teleportCleared = controller.MoveTarget is null
            && !controller.AtMoveTarget
            && controller.LastMoveTargetKind == MoveTargetKind.None;
        bool nan = !controller.Head.Pos.IsFinite();

        message = $"直喂 {route.Length} 路径点：到达 {reached}（末点 tick={arriveTick}），" +
                  $"分支恒 External={alwaysExternal}，意图观测={intentObservable}，" +
                  $"中途取消前有效={activeBeforeCancel}，清 null 后停={cleared}，" +
                  $"Teleport 清目标={teleportCleared}";
        return reached == route.Length && alwaysExternal && intentObservable
            && activeBeforeCancel && cleared && teleportCleared && !nan;
    }

    /// <summary>
    /// RotationChunk 拓扑断言：① ChunkConnection 构造副作用——两端互绑、后建覆盖
    /// （≙ RW BodyChunkConnection 无条件互设）；② 工厂钉定不变量——全品种装配后
    /// 头参照髋、中段参照后一节、髋参照头（3 节脊柱时 ≙ RW Lizard 最终指向
    /// 头→髋/中→髋/髋→头），尾链保持自然拓扑。腿的步进方向由锚点 Rotation 导出
    /// （LizardLocomotionController.TickLimbs），指向错 =
    /// 全身步态漂——这道结构断言把「装配顺序变动悄悄改朝向」挡在回归里。
    /// </summary>
    private static bool CheckRotationTopology(out string message)
    {
        var c1 = new BodyChunk(Vector3.Zero, 0.1f, 1f);
        var c2 = new BodyChunk(new Vector3(1f, 0f, 0f), 0.1f, 1f);
        var c3 = new BodyChunk(new Vector3(0f, 1f, 0f), 0.1f, 1f);
        _ = new ChunkConnection(c1, c2, 1f, 0.5f);
        bool bind = c1.RotationChunk == c2 && c2.RotationChunk == c1;
        _ = new ChunkConnection(c1, c3, 1f, 0.5f);
        bool overwrite = c1.RotationChunk == c3 && c3.RotationChunk == c1 && c2.RotationChunk == c1;
        // 退化语义照抄 RW：近重合 → 零向量（Unity normalized 原语义，kEpsilon=1e-5——
        // 精确重合与 1e-6~1e-5 噪声带都必须归零，带内放大成单位向量是近折叠不稳的来源）、
        // 阈值之上正常归一化、无参照 → Up。
        var c4 = new BodyChunk(Vector3.Zero, 0.1f, 1f);
        _ = new ChunkConnection(c1, c4, 1f, 0.5f);
        var near = new BodyChunk(new Vector3(5e-6f, 0f, 0f), 0.1f, 1f);
        var above = new BodyChunk(new Vector3(1e-4f, 0f, 0f), 0.1f, 1f);
        near.RotationChunk = c1;
        above.RotationChunk = c1;
        bool degenerate = c4.Rotation == Vector3.Zero
            && near.Rotation == Vector3.Zero
            && above.Rotation != Vector3.Zero
            && new BodyChunk(Vector3.Zero, 0.1f, 1f).Rotation == Vector3.Up;

        bool pinned = true;
        // 四预设 + 合成 4 节脊柱（预设最多 3 节，spine≥4 才区分「中段→后一节」与「统一指髋」——
        // 后者会把中段朝向退化成跨关节长弦）。合成品种只装配不 tick，零哈希影响。
        var breeds = new List<BreedParams>(BodyFactory.AllBreeds())
        {
            new() { Name = "spine4", SpineSegments = 4, LegPairs = 3 },
        };
        foreach (BreedParams p in breeds)
        {
            LizardLocomotionController w = BodyFactory.CreateLizardController(new Vector3(0f, 0.6f, 0f), p);
            int spine = Math.Max(2, p.SpineSegments);
            List<BodyChunk> chunks = w.Body.Chunks;
            pinned &= w.Head.RotationChunk == w.Hips; // 头 → 髋：长基线全身轴
            for (int i = 1; i < spine - 1; i++)
            {
                pinned &= chunks[i].RotationChunk == chunks[i + 1]; // 中段 → 后一节：本段轴
            }
            pinned &= w.Hips.RotationChunk == w.Head;
            // 尾链自然拓扑精确断言（弱化成非空 = 同义反复，互绑保证恒非空）：
            // 段 i 参照段 i+1（朝身体，后建覆盖的结果）、尾尖参照前一段。
            for (int i = spine; i < chunks.Count - 1; i++)
            {
                pinned &= chunks[i].RotationChunk == chunks[i + 1];
            }
            if (chunks.Count > spine)
            {
                pinned &= chunks[^1].RotationChunk == chunks[^2];
            }
        }
        message = $"连接互绑={bind}，后建覆盖={overwrite}，退化语义={degenerate}，" +
                  $"脊柱钉定+尾链拓扑（四预设+合成 spine4）={pinned}";
        return bind && overwrite && degenerate && pinned;
    }

    /// <summary>heavy 上墙弓起回归（2026-07 取证轮）：三节脊柱链尾无驱动、长硬尾经尾根
    /// WeightA 机械回拉（纯化消融：只解耦尾根与整条摘尾逐项同值——Footing 记账贡献为零）、
    /// 后腿超出 JointDist 可及圈被逐 tick 全拒、抓稳关重力让悬臂力中性——四者叠加时
    /// 髋部曾悬在墙外最长 1.5s（后腿重抓 +59 tick）慢慢才被爬行拖平。场景 = 概率扫描出的
    /// 最坏种子（spawn -3.0、接近偏角 10°，弓起深浅取决于撞墙步态相位）。后续用户实测又
    /// 暴露「内部已近 180°、整条却水平伸出」的假修复：交接期允许前段沿墙、后段留地形成
    /// 约 90°~140° 的 L/斜线，不能再拿全身内角当唯一美观指标。双场景断言：
    /// ① 10°/0° 两种接近：Head 真接触墙后前段 15 tick 内连续两 tick 进入沿墙 30°；
    ///   真抓墙瞬间分别约束前段沿墙/法向，交接内部角不得低于 90°；随后后腿重抓 ≤45 tick、
    ///   脊柱角回到 ≥160° ≤40 tick、此后不得再塌回 &lt;140°；② RunSpeed=0.25 仍能有限、
    ///   按比例地完成上墙；③ 上墙后停驶（mount+15 切零输入）：终态仍挂墙、脊柱角 ≥165° 且
    ///   停驶期髋离墙 ≥0.35m——钉死「朝最近墙面吸」类实现（停驶时无推进力抵消，会把停在
    ///   近直姿态的身体主动折进墙角：实测 174°→135°、髋吸到 0.30m 贴死，用户实测回归）。</summary>
    private static bool CheckHeavyWallMount(out string message)
    {
        (int Mount, int FrontAlignDelta, float FrontUpAtMount, float FrontNormalAtMount,
            int RegripDelta, int AngleSettleDelta, float MinAngle, float MinAngleAfterSettle,
            float MinAngleFinalWindow, float FinalAngle, bool Finite) angled = RunHeavyMount(
                spawnX: -3f, yawDegrees: 10f, runSpeed: 1f, maxTicks: 400);
        (int Mount, int FrontAlignDelta, float FrontUpAtMount, float FrontNormalAtMount,
            int RegripDelta, int AngleSettleDelta, float MinAngle, float MinAngleAfterSettle,
            float MinAngleFinalWindow, float FinalAngle, bool Finite) straight = RunHeavyMount(
                spawnX: -3f, yawDegrees: 0f, runSpeed: 1f, maxTicks: 400);
        (int Mount, int FrontAlignDelta, float FrontUpAtMount, float FrontNormalAtMount,
            int RegripDelta, int AngleSettleDelta, float MinAngle, float MinAngleAfterSettle,
            float MinAngleFinalWindow, float FinalAngle, bool Finite) lowThrottle = RunHeavyMount(
                spawnX: -3f, yawDegrees: 10f, runSpeed: 0.25f, maxTicks: 400);
        (int Mount, float FinalAngle, float MinWallGap, float FinalHeadHeight,
            int FinalGripRun, bool FinalAttached, bool Finite) stopped =
            RunHeavyMountStopped(stopAfterMount: 15);

        bool angledOk = angled.Finite && angled.Mount > 0
            && angled.FrontAlignDelta is >= 0 and <= 15
            && angled.FrontUpAtMount >= 0.5f
            && angled.FrontNormalAtMount <= 0.75f
            && angled.RegripDelta is >= 0 and <= 45
            && angled.AngleSettleDelta is >= 0 and <= 40
            && angled.MinAngle >= 90f
            && angled.MinAngleAfterSettle >= 140f;
        bool straightOk = straight.Finite && straight.Mount > 0
            && straight.FrontAlignDelta is >= 0 and <= 15
            && straight.FrontUpAtMount >= 0.55f
            && straight.FrontNormalAtMount <= 0.82f
            && straight.RegripDelta is >= 0 and <= 45
            && straight.AngleSettleDelta is >= 0 and <= 40
            && straight.MinAngle >= 90f
            && straight.MinAngleAfterSettle >= 140f;
        bool lowThrottleOk = lowThrottle.Finite && lowThrottle.Mount > 0
            && lowThrottle.FrontAlignDelta is >= 0 and <= 40
            && lowThrottle.RegripDelta is >= 0 and <= 120
            && lowThrottle.MinAngle >= 90f;
        bool stoppedOk = stopped.Finite && stopped.Mount > 0
            && stopped.FinalAngle >= 165f
            && stopped.MinWallGap >= 0.35f
            && stopped.FinalHeadHeight >= 0.8f
            && stopped.FinalGripRun >= 20
            && stopped.FinalAttached;

        int sweepMounted = 0, sweepAligned = 0, sweepRegripped = 0;
        int sweepMountPose = 0, sweepAngles = 0, sweepStable = 0;
        int maxSweepAlign = -1, maxSweepRegrip = -1;
        float maxSweepNormal = 0f, minSweepAngle = 181f;
        bool sweepOk = true;
        for (int spawnIndex = 0; spawnIndex < 11; spawnIndex++)
        {
            float spawnX = -2.4f - spawnIndex * 0.2f;
            for (int yawIndex = 0; yawIndex < 4; yawIndex++)
            {
                var sample = RunHeavyMount(
                    spawnX, yawDegrees: yawIndex * 10f, runSpeed: 1f, maxTicks: 400);
                if (sample.Mount > 0)
                {
                    sweepMounted++;
                }
                if (sample.FrontAlignDelta is >= 0 and <= 15)
                {
                    sweepAligned++;
                }
                if (sample.RegripDelta is >= 0 and <= 45)
                {
                    sweepRegripped++;
                }
                if (sample.FrontNormalAtMount <= 0.93f)
                {
                    sweepMountPose++;
                }
                if (sample.MinAngle >= 90f)
                {
                    sweepAngles++;
                }
                if (sample.MinAngleFinalWindow >= 165f)
                {
                    sweepStable++;
                }
                maxSweepAlign = Math.Max(maxSweepAlign, sample.FrontAlignDelta);
                maxSweepRegrip = Math.Max(maxSweepRegrip, sample.RegripDelta);
                maxSweepNormal = Mathf.Max(maxSweepNormal, sample.FrontNormalAtMount);
                minSweepAngle = Mathf.Min(minSweepAngle, sample.MinAngle);
                sweepOk &= sample.Finite && sample.Mount > 0
                    && sample.FrontAlignDelta is >= 0 and <= 15
                    && sample.FrontNormalAtMount <= 0.93f
                    && sample.RegripDelta is >= 0 and <= 45
                    && sample.MinAngle >= 90f
                    && sample.MinAngleFinalWindow >= 165f;
            }
        }

        message = $"10°：上墙 t{angled.Mount}，前段沿墙Δ={angled.FrontAlignDelta}tick（≤15），" +
                  $"上墙瞬间沿墙/法向={angled.FrontUpAtMount:F2}/{angled.FrontNormalAtMount:F2}" +
                  $"（≥0.50/≤0.75），" +
                  $"重抓Δ={angled.RegripDelta}，最小/恢复后角={angled.MinAngle:F0}/{angled.MinAngleAfterSettle:F0}°；" +
                  $"0°：沿墙Δ={straight.FrontAlignDelta}tick（≤15），上墙瞬间沿墙/法向=" +
                  $"{straight.FrontUpAtMount:F2}/{straight.FrontNormalAtMount:F2}，" +
                  $"重抓Δ={straight.RegripDelta}，最小角={straight.MinAngle:F0}°；" +
                  $"低油门0.25：上墙 t{lowThrottle.Mount}，沿墙Δ={lowThrottle.FrontAlignDelta}，" +
                  $"重抓Δ={lowThrottle.RegripDelta}；停驶：终态角={stopped.FinalAngle:F0}°，" +
                  $"头高={stopped.FinalHeadHeight:F2}m，髋离墙最小={stopped.MinWallGap:F2}m，" +
                  $"连续挂墙={stopped.FinalGripRun}tick；44扫：mount/沿墙/重抓/瞬时姿态/角/终稳=" +
                  $"{sweepMounted}/{sweepAligned}/{sweepRegripped}/{sweepMountPose}/" +
                  $"{sweepAngles}/{sweepStable}（/44），" +
                  $"max沿墙Δ/重抓Δ={maxSweepAlign}/{maxSweepRegrip}，" +
                  $"max法向={maxSweepNormal:F2}，min角={minSweepAngle:F0}°";
        return angledOk && straightOk && lowThrottleOk && stoppedOk && sweepOk;
    }

    private static (int Mount, int FrontAlignDelta, float FrontUpAtMount,
        float FrontNormalAtMount, int RegripDelta, int AngleSettleDelta,
        float MinAngle, float MinAngleAfterSettle, float MinAngleFinalWindow,
        float FinalAngle, bool Finite) RunHeavyMount(
            float spawnX, float yawDegrees, float runSpeed, int maxTicks)
    {
        var terrain = new WallPoseTerrain(floorY: 0f, wallX: -5f, ceilingY: 100f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(spawnX, 0.4f, 0f), BodyFactory.Heavy());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        float yaw = Mathf.DegToRad(yawDegrees);
        Vector3 move = new(-Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));

        int frontContact = -1, frontAlignCandidate = -1, frontAlign = -1;
        int mount = -1, regrip = -1, angleSettle = -1;
        float frontUpAtMount = -1f, frontNormalAtMount = 99f;
        float minAngle = 181f, minAngleAfterSettle = 181f;
        float minAngleFinalWindow = 181f, finalAngle = 180f;
        bool finite = true;
        for (long tick = 1; tick <= maxTicks; tick++)
        {
            controller.MoveDir = move;
            controller.RunSpeed = runSpeed;
            controller.Tick(new TickContext(gravityPerTick, terrain, tick));

            bool frontWallContact = controller.Head.TerrainContact
                && controller.Head.ContactNormal.X > 0.7f;
            bool frontWall = false, rearWall = false;
            foreach (Limb limb in controller.Limbs)
            {
                if (limb.GripNormal.X <= 0.7f)
                {
                    continue;
                }
                if (limb.Anchor == controller.Head)
                {
                    frontWallContact |= limb.GripCounter > 0;
                    frontWall |= limb.Gripping;
                }
                else if (limb.Gripping)
                {
                    rearWall = true;
                }
            }
            float angle = SpineAngleDeg(controller);
            finalAngle = angle;
            if (tick > maxTicks - 20)
            {
                minAngleFinalWindow = Mathf.Min(minAngleFinalWindow, angle);
            }
            Vector3 frontAxis = controller.Head.Pos - controller.SpineFollower.Pos;
            Vector3 wallClimb = new(0f, Mathf.Cos(yaw), Mathf.Sin(yaw));
            float frontAngle = frontAxis.LengthSquared() < 1e-10f
                ? 180f
                : Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(
                    frontAxis.Normalized().Dot(wallClimb), -1f, 1f)));
            if (frontContact < 0 && frontWallContact)
            {
                frontContact = (int)tick;
            }
            if (frontContact > 0 && frontAlign < 0)
            {
                if (frontWallContact && frontAngle <= 30f)
                {
                    if (frontAlignCandidate < 0)
                    {
                        frontAlignCandidate = (int)tick;
                    }
                    else if (tick == frontAlignCandidate + 1)
                    {
                        frontAlign = frontAlignCandidate;
                    }
                }
                else
                {
                    frontAlignCandidate = -1;
                }
            }
            if (mount < 0 && frontWall)
            {
                mount = (int)tick;
                frontUpAtMount = frontAxis.LengthSquared() < 1e-10f
                    ? -1f
                    : frontAxis.Normalized().Dot(wallClimb);
                Vector3 frontRear = controller.SpineFollower.Pos - controller.Head.Pos;
                frontNormalAtMount = frontRear.LengthSquared() < 1e-10f
                    ? 1f
                    : Mathf.Abs(frontRear.Normalized().Dot(Vector3.Right));
            }
            if (mount > 0)
            {
                minAngle = Mathf.Min(minAngle, angle);
                if (regrip < 0 && rearWall)
                {
                    regrip = (int)tick;
                }
                if (angleSettle < 0 && minAngle < 160f && angle >= 160f)
                {
                    angleSettle = (int)tick;
                }
                if (angleSettle > 0 && tick > angleSettle)
                {
                    minAngleAfterSettle = Mathf.Min(minAngleAfterSettle, angle);
                }
            }
            finite &= controller.Head.Pos.IsFinite() && controller.Hips.Pos.IsFinite();
        }
        int settleDelta = minAngle >= 160f
            ? 0
            : angleSettle < 0 || mount < 0 ? -1 : angleSettle - mount;
        return (mount,
            frontAlign < 0 || frontContact < 0 ? -1 : frontAlign - frontContact,
            frontUpAtMount,
            frontNormalAtMount,
            regrip < 0 || mount < 0 ? -1 : regrip - mount,
            settleDelta,
            minAngle,
            minAngleAfterSettle,
            minAngleFinalWindow,
            finalAngle, finite);
    }

    private static (int, float, float, float, int, bool, bool) RunHeavyMountStopped(int stopAfterMount)
    {
        var terrain = new WallPoseTerrain(floorY: 0f, wallX: -5f, ceilingY: 100f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(-3f, 0.4f, 0f), BodyFactory.Heavy());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        float yaw = Mathf.DegToRad(10f);
        Vector3 move = new(-Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));

        int mount = -1;
        float minWallGap = 99f, finalAngle = 0f, finalHeadHeight = 0f;
        int finalGripRun = 0;
        bool finalAttached = false, finite = true;
        for (long tick = 1; tick <= 700; tick++)
        {
            bool stop = mount > 0 && tick > mount + stopAfterMount;
            controller.MoveDir = stop ? Vector3.Zero : move;
            controller.RunSpeed = stop ? 0f : 1f;
            controller.Tick(new TickContext(gravityPerTick, terrain, tick));

            if (mount < 0)
            {
                foreach (Limb limb in controller.Limbs)
                {
                    if (limb.Anchor == controller.Head && limb.Gripping && limb.GripNormal.X > 0.7f)
                    {
                        mount = (int)tick;
                        break;
                    }
                }
            }
            if (stop)
            {
                minWallGap = Mathf.Min(minWallGap, controller.Hips.Pos.X - (-5f));
            }
            finalAngle = SpineAngleDeg(controller);
            finalHeadHeight = controller.Head.Pos.Y;
            finalAttached = false;
            foreach (Limb limb in controller.Limbs)
            {
                finalAttached |= limb.Gripping && limb.GripNormal.X > 0.7f;
            }
            finalGripRun = finalAttached ? finalGripRun + 1 : 0;
            finite &= controller.Head.Pos.IsFinite() && controller.Hips.Pos.IsFinite();
        }
        return (mount, finalAngle, minWallGap, finalHeadHeight,
            finalGripRun, finalAttached, finite);
    }

    private static float SpineAngleDeg(LizardLocomotionController controller)
    {
        Vector3 toHead = controller.Head.Pos - controller.SpineFollower.Pos;
        Vector3 toHips = controller.Hips.Pos - controller.SpineFollower.Pos;
        if (toHead.LengthSquared() < 1e-10f || toHips.LengthSquared() < 1e-10f)
        {
            return 0f;
        }
        return Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(
            toHead.Normalized().Dot(toHips.Normalized()), -1f, 1f)));
    }

    /// <summary>wall-pose 回归：墙+天花板把头顶死后，旧拖尾点会把 1cm 髋部侧扰动
    /// 在约 40 tick 内放大到约 80°，并让身体持续横向滑移；无扰动对照也会把髋冻结在
    /// 头侧。新拖尾点必须让两种场景都回到头后方。平面对称巡走测不到这个失稳，必须
    /// 显式保留侧向种子。</summary>
    private static bool CheckWallPoseStability(out string message)
    {
        (float MaxSide, float FinalSide, float FinalRelY, float Drift, bool Finite) seeded =
            RunWallPose(seedSidePerturbation: true);
        (float MaxSide, float FinalSide, float FinalRelY, float Drift, bool Finite) symmetric =
            RunWallPose(seedSidePerturbation: false);

        bool seededStable = seeded.Finite
            && seeded.MaxSide <= 10f
            && seeded.FinalSide <= 5f
            && seeded.FinalRelY <= -0.15f
            && seeded.Drift <= 0.10f;
        bool symmetricStable = symmetric.Finite
            && symmetric.MaxSide <= 5f
            && symmetric.FinalSide <= 5f
            && symmetric.FinalRelY <= -0.15f
            && symmetric.Drift <= 0.05f;

        message = $"1cm侧扰 max/final={seeded.MaxSide:F1}/{seeded.FinalSide:F1}deg，" +
                  $"relY={seeded.FinalRelY:F3}m，横漂={seeded.Drift:F3}m，稳定={seededStable}；" +
                  $"无扰动 max/final={symmetric.MaxSide:F1}/{symmetric.FinalSide:F1}deg，" +
                  $"relY={symmetric.FinalRelY:F3}m，稳定={symmetricStable}";
        return seededStable && symmetricStable;
    }

    private static (float MaxSide, float FinalSide, float FinalRelY, float Drift, bool Finite)
        RunWallPose(bool seedSidePerturbation)
    {
        var terrain = new WallPoseTerrain(floorY: 0f, wallX: -5f, ceilingY: 3f);
        LizardLocomotionController controller = BodyFactory.CreateLizardController(
            new Vector3(-4f, 0.6f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);

        float maxSide = 0f;
        float finalSide = 0f;
        float finalRelY = 0f;
        float headZAtPerturbation = 0f;
        bool finite = true;
        for (long tick = 1; tick <= 900; tick++)
        {
            controller.MoveDir = Vector3.Left;
            controller.RunSpeed = 1f;
            if (tick == 260)
            {
                headZAtPerturbation = controller.Head.Pos.Z;
                if (seedSidePerturbation)
                {
                    controller.Hips.Pos.Z += 0.01f;
                }
            }
            controller.Tick(new TickContext(gravityPerTick, terrain, tick));

            Vector3 relative = controller.Hips.Pos - controller.Head.Pos;
            float side = Mathf.RadToDeg(MathF.Atan2(MathF.Abs(relative.Z), -relative.Y));
            if (tick >= 260)
            {
                maxSide = Mathf.Max(maxSide, side);
            }
            finalSide = side;
            finalRelY = relative.Y;
            finite &= controller.Head.Pos.IsFinite() && controller.Head.Vel.IsFinite()
                && controller.Hips.Pos.IsFinite() && controller.Hips.Vel.IsFinite()
                && float.IsFinite(side);
        }

        float drift = Mathf.Abs(controller.Head.Pos.Z - headZAtPerturbation);
        return (maxSide, finalSide, finalRelY, drift, finite);
    }

    /// <summary>恢复控制器的极端边界：InverseLerp 必须饱和；非正交接触投影必须同时满足
    /// 全部半空间；候选位置不得写进未收集到的邻面；持续 650 tick 的人工卡角也不得让
    /// squeeze 越界或恢复速度随计数爆炸。正常路线永远到不了 600 tick，必须定向造状态。</summary>
    private static bool CheckRecoveryInvariants(out string message)
    {
        bool ramp = LizardLocomotionController.SaturatedInverseLerp(5f, 20f, -100f) == 0f
            && LizardLocomotionController.SaturatedInverseLerp(5f, 20f, 100f) == 1f;

        var manifold = new ContactManifold3D();
        Vector3 n0 = Vector3.Right;
        Vector3 n1 = new(-0.5f, 0.8660254f, 0f);
        manifold.Add(n0);
        manifold.Add(n1);
        Vector3 projected = manifold.ProjectDisplacement(new Vector3(-1f, -1f, 0f));
        bool cone = projected.Dot(n0) >= -1e-5f && projected.Dot(n1) >= -1e-5f;

        var floor = new PlaneTerrainQuery(0f);
        var probe = new BodyChunk(new Vector3(0f, 0.2f, 0f), 0.2f, 1f);
        Vector3 rejected = Body.FeasibleTerrainCorrection(probe, Vector3.Down * 0.05f, floor);
        bool terrainSafe = rejected == Vector3.Zero;

        BreedParams footingBreed = BodyFactory.Heavy();
        footingBreed.TailSegments = 0;
        LizardLocomotionController footingController = BodyFactory.CreateLizardController(new Vector3(0f, 10f, 0f), footingBreed);
        footingController.BaseSpeed = 0f;
        var emptyTerrain = new PlaneTerrainQuery(-100f);
        for (long tick = 1; tick <= 20; tick++)
        {
            foreach (BodyChunk chunk in footingController.Body.Chunks)
            {
                chunk.TerrainContact = false;
            }
            footingController.Hips.TerrainContact = true;
            footingController.Tick(new TickContext(Vector3.Zero, emptyTerrain, tick));
        }
        int footingBeforeProbe = footingController.FootingCounter;
        foreach (BodyChunk chunk in footingController.Body.Chunks)
        {
            chunk.TerrainContact = false;
        }
        footingController.Hips.TerrainSqueeze = 0.05f;
        float expectedProbeLength = footingController.Hips.TerrainRadius + footingController.NearTerrainRange;
        float oldProbeLength = footingController.Hips.Radius + footingController.NearTerrainRange;
        var annulusTerrain = new FirstRayLengthGateTerrain(
            (expectedProbeLength + oldProbeLength) * 0.5f);
        footingController.Tick(new TickContext(Vector3.Zero, annulusTerrain, 21));
        bool footingProbe = footingBeforeProbe >= footingController.RegainFootingTicks
            && footingController.FootingCounter == 0
            && Mathf.Abs(annulusTerrain.FirstRayLength - expectedProbeLength) < 1e-6f;

        BreedParams breed = BodyFactory.Heavy();
        breed.TailSegments = 0;
        LizardLocomotionController controller = BodyFactory.CreateLizardController(new Vector3(0f, 10f, 0f), breed);
        controller.BaseSpeed = 0.0001f;
        controller.MaxMoveSpeed = 0.0001f;
        controller.StallSpeed = 1f;
        foreach (ChunkConnection conn in controller.Body.Connections)
        {
            if (conn.SoftOnly)
            {
                conn.Elasticity = 0f;
            }
        }
        float rearLink = controller.HeadLinkLength;
        foreach (ChunkConnection conn in controller.Body.Connections)
        {
            bool followerToHips = (conn.A == controller.SpineFollower && conn.B == controller.Hips)
                || (conn.B == controller.SpineFollower && conn.A == controller.Hips);
            if (followerToHips && !conn.SoftOnly)
            {
                rearLink = conn.RestLength;
                break;
            }
        }

        var emptyFloor = new PlaneTerrainQuery(-100f);
        float peakSpeed = 0f;
        for (long tick = 1; tick <= 650; tick++)
        {
            Vector3 origin = new(0f, 10f, 0f);
            controller.SpineFollower.Pos = origin;
            controller.Head.Pos = origin + Vector3.Right * controller.HeadLinkLength;
            controller.Hips.Pos = origin + Vector3.Right * rearLink;
            foreach (BodyChunk chunk in controller.Body.Chunks)
            {
                chunk.LastPos = chunk.Pos;
                chunk.Vel = Vector3.Zero;
                chunk.TerrainContact = false;
            }
            // Body.Collide 会把这次手工接触转存为 HadContactLastTick；人工几何保持 0° V 折叠。
            controller.Hips.TerrainContact = true;
            controller.Hips.HadContactLastTick = true;
            controller.MoveDir = Vector3.Right;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(Vector3.Zero, emptyFloor, tick));
            foreach (BodyChunk chunk in controller.Body.Chunks)
            {
                peakSpeed = Mathf.Max(peakSpeed, chunk.Vel.Length());
            }
        }
        bool deepBounded = controller.SpineCornerStuckTicks == 600
            && controller.MaxSpineCornerStuckTicks == 600
            && controller.Hips.TerrainSqueeze >= 0.05f
            && controller.Hips.TerrainSqueeze <= 1f
            && peakSpeed < 0.001f
            && controller.StraightenOutNeeded is >= 0f and <= 1f;

        message = $"ramp饱和={ramp}，非正交可行锥={cone}，候选穿透拒绝={terrainSafe}，" +
                  $"squeeze近地探针={footingProbe}（len={annulusTerrain.FirstRayLength:F3}），" +
                  $"650tick卡角 bounded={deepBounded}（stuck={controller.SpineCornerStuckTicks}, " +
                  $"squeeze={controller.Hips.TerrainSqueeze:F2}, peakVel={peakSpeed:F6}）";
        return ramp && cone && terrainSafe && footingProbe && deepBounded;
    }

    /// <summary>只对第一个射线按长度门控命中：把地形放在 TerrainRadius 与正式 Radius
    /// 两条近地探针之间，钉死 squeeze 期间 UpdateFooting 不得使用旧碰撞壳。</summary>
    private sealed class FirstRayLengthGateTerrain : ITerrainQuery
    {
        private readonly float _minHitLength;
        private bool _sampled;

        public float FirstRayLength { get; private set; } = -1f;

        public FirstRayLengthGateTerrain(float minHitLength)
        {
            _minHitLength = minHitLength;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (_sampled)
            {
                return false;
            }
            _sampled = true;
            FirstRayLength = (to - from).Length();
            if (FirstRayLength < _minHitLength)
            {
                return false;
            }
            hit = new TerrainHit(to, Vector3.Up, 1UL);
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir,
            out float depth)
        {
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }

    /// <summary>wall-pose 专用解析地形：地板 y≤floor、墙 x≤wall、天花板 y≥ceiling
    /// 三个半空间的并集。提供 Raycast 的 HitFromInside 零法线与球体 MTD，复现墙顶内角。</summary>
    private sealed class WallPoseTerrain : ITerrainQuery
    {
        private readonly float _floorY;
        private readonly float _wallX;
        private readonly float _ceilingY;

        public WallPoseTerrain(float floorY, float wallX, float ceilingY)
        {
            _floorY = floorY;
            _wallX = wallX;
            _ceilingY = ceilingY;
        }

        private bool Inside(Vector3 point) =>
            point.Y < _floorY || point.X < _wallX || point.Y > _ceilingY;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (Inside(from))
            {
                hit = new TerrainHit(from, Vector3.Zero, 1UL);
                return true;
            }

            float best = float.MaxValue;
            if (to.Y < _floorY)
            {
                float t = (from.Y - _floorY) / (from.Y - to.Y);
                if (t is >= 0f and <= 1f && t < best)
                {
                    best = t;
                    hit = new TerrainHit(from.Lerp(to, t), Vector3.Up, 1UL);
                }
            }
            if (to.Y > _ceilingY)
            {
                float t = (_ceilingY - from.Y) / (to.Y - from.Y);
                if (t is >= 0f and <= 1f && t < best)
                {
                    best = t;
                    hit = new TerrainHit(from.Lerp(to, t), Vector3.Down, 2UL);
                }
            }
            if (to.X < _wallX)
            {
                float t = (_wallX - from.X) / (to.X - from.X);
                if (t is >= 0f and <= 1f && t < best)
                {
                    best = t;
                    hit = new TerrainHit(from.Lerp(to, t), Vector3.Right, 3UL);
                }
            }
            return best != float.MaxValue;
        }

        public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir,
            out float depth)
        {
            pushDir = Vector3.Up;
            depth = _floorY - (center.Y - radius);

            float wallDepth = radius - (center.X - _wallX);
            if (wallDepth > depth)
            {
                pushDir = Vector3.Right;
                depth = wallDepth;
            }

            float ceilingDepth = center.Y + radius - _ceilingY;
            if (ceilingDepth > depth)
            {
                pushDir = Vector3.Down;
                depth = ceilingDepth;
            }
            return depth > 0f;
        }
    }

    /// <summary>
    /// 内核程序集的引擎边界扫描：除数学允许清单外出现任何 Godot.* 类型引用即 FAIL。
    /// GodotSharp 包里 Node/GD/PhysicsServer3D 全部编译期可达——「不引 Godot.NET.Sdk」
    /// 只挡住了场景树源生成器，真正的强制靠这道 TypeRef 扫描（离线、秒级，回迁后照跑）。
    /// </summary>
    private static bool CheckEngineBoundary(out string message)
    {
        var allowed = new HashSet<string> { "Vector3", "Mathf" };
        using FileStream fs = File.OpenRead(typeof(Body).Assembly.Location);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var offenders = new List<string>();
        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference tr = md.GetTypeReference(handle);
            string ns = md.GetString(tr.Namespace);
            if (!ns.StartsWith("Godot", StringComparison.Ordinal))
            {
                continue;
            }
            string name = md.GetString(tr.Name);
            if (ns == "Godot" && allowed.Contains(name))
            {
                continue;
            }
            offenders.Add($"{ns}.{name}");
        }
        message = offenders.Count == 0
            ? $"ProcAnim.Core 引擎类型引用 = 允许清单 [{string.Join(", ", allowed)}]，边界干净"
            : $"越界引用: {string.Join(", ", offenders)}";
        return offenders.Count == 0;
    }

    // ================= 人形（HumanoidLocomotionController）断言 =================

    private static HumanoidLocomotionController MakeHumanoid(Vector3 origin) =>
        BodyFactory.CreateHumanoidController(origin, BodyFactory.Scavenger());

    private static Vector3 HumanoidGravity() => new(0f, -GravityMps2 * TickDt * TickDt, 0f);

    private static void SettleHumanoid(HumanoidLocomotionController c, PlaneTerrainQuery terrain,
        ref long tick, int ticks)
    {
        Vector3 g = HumanoidGravity();
        for (int i = 0; i < ticks; i++)
        {
            tick++;
            c.Tick(new TickContext(g, terrain, tick));
        }
    }

    /// <summary>人形基线路线（与蜥蜴 Run 同构）：scavenger 平地巡走，前半直行后半 45° 转向。
    /// 直行把 knuckle-walk 的撑点节奏、转向把 Facing/扇扫换向都纳入哈希。</summary>
    private static (ulong, float, float, long, float, bool, float) RunHumanoid()
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        var hasher = new DeterminismHasher();
        Vector3 g = HumanoidGravity();

        float walk = 0f;
        float uprightSum = 0f;
        Vector3 lastChest = c.Chest.Pos;
        for (long tick = 1; tick <= Ticks; tick++)
        {
            c.MoveDir = tick <= Ticks / 2
                ? new Vector3(1f, 0f, 0f)
                : new Vector3(1f, 0f, 1f).Normalized();
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));

            hasher.FoldBody(c.Body);
            hasher.FoldLimbs(c.Legs);
            hasher.FoldArms(c.Arms);

            Vector3 step = c.Chest.Pos - lastChest;
            step.Y = 0f;
            walk += step.Length();
            lastChest = c.Chest.Pos;
            uprightSum += c.Uprightness;
        }

        bool nan = false;
        foreach (BodyChunk chunk in c.Body.Chunks)
        {
            nan |= !chunk.Pos.IsFinite();
        }
        foreach (Limb leg in c.Legs)
        {
            nan |= !leg.Pos.IsFinite();
        }
        foreach (Arm arm in c.Arms)
        {
            nan |= !arm.Pos.IsFinite();
        }
        return (hasher.Value, walk, uprightSum / Ticks, c.KnucklePlants,
            (float)terrain.RayCount / Ticks, nan, c.Body.CurrentMaxDeviation());
    }

    /// <summary>
    /// 站立与摔倒爬起：① 落地站定后必须是「失重悬停」姿态（重力关、髋悬在 RideHeight 附近、
    /// 头被伺服顶在胸上方）；② 大冲量击飞必须真摔（直立度跌穿 0.5、重力回归）再**自然爬起**
    /// （限时回正 ≥0.85）——零 knockdown 状态机的机器可查证明。
    /// </summary>
    private static bool CheckHumanoidStand(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        long tick = 0;
        SettleHumanoid(c, terrain, ref tick, 300);

        bool hoverOk = !c.ApplyGravity && c.Grounded
            && Mathf.Abs(c.Hips.Pos.Y - c.HipRideHeight) < 0.1f;
        bool standOk = c.Uprightness >= 0.95f;
        bool headOk = c.Head.Pos.Y - c.Chest.Pos.Y >= 0.3f;

        c.Launch(new Vector3(0.4f, 0.3f, 0f));
        float minUpright = 1f;
        bool sawGravity = false;
        int recoverAfter = -1;
        for (int i = 1; i <= 300; i++)
        {
            tick++;
            c.Tick(new TickContext(HumanoidGravity(), terrain, tick));
            minUpright = Mathf.Min(minUpright, c.Uprightness);
            sawGravity |= c.ApplyGravity;
            if (recoverAfter < 0 && i > 20 && c.Uprightness >= 0.85f && c.Grounded)
            {
                recoverAfter = i;
            }
        }

        bool tumbled = minUpright < 0.5f;
        bool recovered = recoverAfter > 0;
        message = $"悬停={hoverOk}（hipsY={c.HipRideHeight:F2}±0.1，重力关），站直={standOk}，" +
                  $"头伺服={headOk}；击飞后 minUpright={minUpright:F2}（须 <0.5），坠落态={sawGravity}，" +
                  $"爬起={recoverAfter}tick（须 ≤300）";
        return hoverOk && standOk && headOk && tumbled && sawGravity && recovered;
    }

    /// <summary>昏迷瘫倒/苏醒爬起（≙ RW !Consious ⇒ Act 不跑）：昏迷 + 轻推 → 重力回归、
    /// 直立度跌穿 0.5、髋落地；苏醒 → 限时回正、恢复悬停。轻推是必要的——完全对称的
    /// 直立杆是确定性的不稳定平衡点，没有扰动永远不倒（内核零随机，没有噪声帮它倒）。</summary>
    private static bool CheckHumanoidStun(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        long tick = 0;
        SettleHumanoid(c, terrain, ref tick, 300);

        // 昏迷即弃掷（契约 §4.2）：置昏后同一帧（未过 Tick）释放也必须拿不到出手向量。
        c.StartThrowCharge(new Vector3(1f, 0f, 0f));
        SettleHumanoid(c, terrain, ref tick, 5);
        c.Conscious = false;
        bool unconsciousReleaseNull = c.ReleaseThrow() is null && c.ThrowChargeTicks == 0;

        c.Launch(new Vector3(0.1f, 0f, 0.03f));
        float minUpright = 1f;
        float minHipsY = float.MaxValue;
        for (int i = 0; i < 200; i++)
        {
            tick++;
            c.Tick(new TickContext(HumanoidGravity(), terrain, tick));
            minUpright = Mathf.Min(minUpright, c.Uprightness);
            minHipsY = Mathf.Min(minHipsY, c.Hips.Pos.Y);
        }
        bool collapsed = minUpright < 0.5f && c.ApplyGravity;
        bool hipsDown = minHipsY < c.HipRideHeight;

        c.Conscious = true;
        int recoverAfter = -1;
        for (int i = 1; i <= 300; i++)
        {
            tick++;
            c.Tick(new TickContext(HumanoidGravity(), terrain, tick));
            if (recoverAfter < 0 && c.Uprightness >= 0.85f && !c.ApplyGravity)
            {
                recoverAfter = i;
            }
        }
        message = $"昏迷即弃掷（同帧释放=null）={unconsciousReleaseNull}；" +
                  $"昏迷+轻推 minUpright={minUpright:F2}（须 <0.5），重力回归={collapsed}，" +
                  $"髋落地={hipsDown}；苏醒回正={recoverAfter}tick（须 ≤300），终态 upright={c.Uprightness:F2}";
        return unconsciousReleaseNull && collapsed && hipsDown && recoverAfter > 0;
    }

    /// <summary>手臂非移动三件的契约：指向收敛（主手落到指向射线上）、持物到位（携带位 +
    /// HuntRelative 模式）、蓄力强制停驶（水平速度归零、主手拉到身后上方）、出手返回值
    /// （速度大小=ThrowSpeed、方向=蓄力方向；未蓄力/二次释放返回 null）。</summary>
    private static bool CheckHumanoidAct(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        long tick = 0;

        Vector3? idleRelease = c.ReleaseThrow();
        bool nullWhenUncharged = idleRelease is null;

        SettleHumanoid(c, terrain, ref tick, 300);

        // —— 指向 ——
        var pt = new Vector3(3f, 1.5f, 0f);
        c.PointTarget = pt;
        float bestAngleDeg = 999f;
        float bestReach = 0f;
        for (int i = 0; i < 100; i++)
        {
            tick++;
            c.Tick(new TickContext(HumanoidGravity(), terrain, tick));
            Vector3 want = (pt - c.Chest.Pos).Normalized();
            Vector3 arm = c.Arms[0].Pos - c.Chest.Pos;
            if (arm.LengthSquared() > 1e-8f)
            {
                float angle = Mathf.RadToDeg(want.AngleTo(arm.Normalized()));
                if (angle < bestAngleDeg)
                {
                    bestAngleDeg = angle;
                    bestReach = arm.Length();
                }
            }
        }
        bool pointOk = bestAngleDeg < 10f && bestReach >= c.Arms[0].ArmLength * 0.6f;
        c.PointTarget = null;

        // —— 持物 ——
        c.Carrying = true;
        SettleHumanoid(c, terrain, ref tick, 80);
        bool carryMode = c.Arms[0].Mode == Arm.ArmMode.HuntRelativePosition;
        float carryDist = (c.Arms[0].Pos - c.Arms[0].HuntPos).Length();
        bool carryOk = carryMode && carryDist < 0.2f
            && (c.MainHandPos - c.Chest.Pos).Length() < c.Arms[0].ArmLength;
        c.Carrying = false;

        // —— 蓄力（行进中触发，验证强制停驶）——
        Vector3 g = HumanoidGravity();
        for (int i = 0; i < 100; i++)
        {
            tick++;
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));
        }
        Vector3 chargeDir = new Vector3(1f, 0.2f, 0f).Normalized();
        bool started = c.StartThrowCharge(chargeDir);
        for (int i = 0; i < 60; i++)
        {
            tick++;
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));
        }
        Vector3 chestVel = c.Chest.Vel;
        chestVel.Y = 0f;
        bool stoppedWhileCharging = chestVel.Length() < 0.005f;
        Vector3 handRel = c.Arms[0].Pos - c.Chest.Pos;
        bool handCharged = handRel.Dot(c.Facing) < 0f && handRel.Y > 0.2f;

        // —— 出手 ——
        Vector3? thrown = c.ReleaseThrow();
        Vector3? doubleRelease = c.ReleaseThrow();
        bool throwOk = thrown is { } v
            && Mathf.Abs(v.Length() - c.ThrowSpeed) < 1e-4f
            && v.Normalized().Dot(chargeDir) >= 0.999f
            && doubleRelease is null;

        message = $"未蓄力释放=null:{nullWhenUncharged}；指向最小夹角={bestAngleDeg:F1}°/伸展={bestReach:F2}m（{pointOk}）；" +
                  $"持物 mode={carryMode}/到位差={carryDist:F2}m（{carryOk}）；蓄力停驶 |v|={chestVel.Length():F4}（{stoppedWhileCharging}）、" +
                  $"手在身后上方={handCharged}；出手向量契约={throwOk}";
        return nullWhenUncharged && pointOk && carryOk && started && stoppedWhileCharging && handCharged && throwOk;
    }

    /// <summary>
    /// 重力开关边界（评审修复轮固化，两条 HIGH 的机器可查复现）：
    /// ① 65° 陡壁不可爬——撑地探针法线门槛曾是 0.3（72.5° 以内都算地面），与滑墙的 0.5
    /// 墙判据之间留了 60°~72.5° 争议带：陡面被重力开关当地面 → 失重 + 髋伺服钉面 +
    /// 前置探地节节抬高 = 全速爬升 49m 永久悬挂。修后陡面探针被拒 → 重力回归，
    /// 人形在坡脚停驶，永不出现「悬在高处还失重」。
    /// ② 贴竖墙的 chunk 接触不是撑地——contact 项曾不看接触法线，击飞撞墙 + 持续推墙时
    /// 胸-墙接触 5 tick 攒满 GroundedCounter，半空反复失重成粘滞滑降。修后接触按
    /// 「可站立地面」法线过滤，贴墙下落全程重力在拽。
    /// </summary>
    private static bool CheckHumanoidGravityGate(out string message)
    {
        // —— 场景 ①：地板 + 65° 陡坡（坡脚 x=3，面法线朝 -X 上方）——
        float slopeRad = Mathf.DegToRad(65f);
        var slopeNormal = new Vector3(-Mathf.Sin(slopeRad), Mathf.Cos(slopeRad), 0f);
        var slopeTerrain = new HalfSpaceTerrain(
            (Vector3.Up, 0f),
            (slopeNormal, slopeNormal.X * 3f));
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        Vector3 g = HumanoidGravity();
        float maxHipsY = 0f;
        bool weightlessWhileHigh = false;
        for (long tick = 1; tick <= 800; tick++)
        {
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, slopeTerrain, tick));
            maxHipsY = Mathf.Max(maxHipsY, c.Hips.Pos.Y);
            weightlessWhileHigh |= !c.ApplyGravity && c.Hips.Pos.Y > 1f;
        }
        bool slopeOk = !weightlessWhileHigh && maxHipsY < 1f;

        // —— 场景 ②：地板 + x=4 竖墙，击飞撞墙 + 持续推墙 ——
        var wallTerrain = new HalfSpaceTerrain(
            (Vector3.Up, 0f),
            (Vector3.Left, -4f));
        HumanoidLocomotionController w = MakeHumanoid(new Vector3(2.5f, 0.5f, 0f));
        long t2 = 0;
        for (int i = 0; i < 200; i++)
        {
            t2++;
            w.Tick(new TickContext(g, wallTerrain, t2));
        }
        w.Launch(new Vector3(0.3f, 0.75f, 0f));
        bool weightlessMidair = false;
        float maxAirHips = 0f;
        for (int i = 0; i < 250; i++)
        {
            t2++;
            w.MoveDir = new Vector3(1f, 0f, 0f);
            w.RunSpeed = 1f;
            w.Tick(new TickContext(g, wallTerrain, t2));
            if (!w.ApplyGravity && w.Hips.Pos.Y > w.HipRideHeight + 0.5f)
            {
                weightlessMidair = true;
                maxAirHips = Mathf.Max(maxAirHips, w.Hips.Pos.Y);
            }
        }
        bool wallOk = !weightlessMidair && w.Hips.Pos.Y < 0.6f && w.Grounded;

        message = $"65°坡：maxHipsY={maxHipsY:F2}m（须 <1，不可爬）、高处失重={weightlessWhileHigh}（须 False）；" +
                  $"贴墙击飞：半空失重={weightlessMidair}（须 False，max={maxAirHips:F2}m）、" +
                  $"落地站稳={w.Grounded}（hipsY={w.Hips.Pos.Y:F2}）";
        return slopeOk && wallOk;
    }

    /// <summary>双足步态特征（回归钉，用户白盒实测暴露的高频蹭步）：蜥蜴 oldestGrip 错开在两腿
    /// 拓扑下退化成「对脚一落地本脚即松」——步频锁死在对腿落地延迟（5 tick/步）、步幅仅腿长
    /// 0.4 倍、释放时脚还在髋前 +0.21、brute 触地 3 tick 永远达不到 GripDelay=4 的 Gripping 判定。
    /// 修复 = Limb.LookaheadTicks 前瞻点释放（≙ ScavengerLeg）+ 支撑保序 guard。断言钉住：
    /// 步幅/周期/触地/释放位置/抓地占比/零双悬空 + brute 的 Gripping 占比。</summary>
    private static bool CheckHumanoidGait(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        long tick = 0;
        SettleHumanoid(c, terrain, ref tick, 200);
        var gDir = new Vector3(1f, 0f, 0f);
        var lastPlant = new Vector3[2];
        var lastPlantT = new long[2] { -1, -1 };
        var gripStart = new long[2] { -1, -1 };
        var prevGc = new int[2];
        int steps = 0, contacts = 0, releases = 0, bothAir = 0, gripTicks = 0;
        float strideSum = 0f, releaseAdvSum = 0f;
        long periodSum = 0, contactSum = 0;
        for (int i = 0; i < 800; i++)
        {
            tick++;
            c.MoveDir = gDir;
            c.RunSpeed = 1f;
            c.Tick(new TickContext(HumanoidGravity(), terrain, tick));
            for (int l = 0; l < 2; l++)
            {
                Limb leg = c.Legs[l];
                if (prevGc[l] == 0 && leg.GripCounter > 0)
                {
                    if (lastPlantT[l] >= 0)
                    {
                        steps++;
                        strideSum += (leg.Pos - lastPlant[l]).Dot(gDir);
                        periodSum += tick - lastPlantT[l];
                    }
                    lastPlant[l] = leg.Pos;
                    lastPlantT[l] = tick;
                    gripStart[l] = tick;
                }
                if (prevGc[l] > 0 && leg.GripCounter == 0 && gripStart[l] >= 0)
                {
                    contactSum += tick - gripStart[l];
                    contacts++;
                    releaseAdvSum += (leg.Pos - c.Hips.Pos).Dot(gDir);
                    releases++;
                }
                if (leg.Gripping)
                {
                    gripTicks++;
                }
                prevGc[l] = leg.GripCounter;
            }
            if (c.Legs[0].GripCounter == 0 && c.Legs[1].GripCounter == 0)
            {
                bothAir++;
            }
        }
        float stride = steps > 0 ? strideSum / steps : 0f;
        float period = steps > 0 ? (float)periodSum / steps : 0f;
        float contact = contacts > 0 ? (float)contactSum / contacts : 0f;
        float relAdv = releases > 0 ? releaseAdvSum / releases : 9f;
        float duty = gripTicks / 1600f;
        float airFrac = bothAir / 800f;

        // brute 专项：触地必须撑过它的 GripDelay=4（修前恒 3 tick，Gripping 占比恒 0）。
        var bTerrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController b =
            BodyFactory.CreateHumanoidController(new Vector3(0f, 0.5f, 0f), BodyFactory.Brute());
        long bTick = 0;
        SettleHumanoid(b, bTerrain, ref bTick, 200);
        int bGripTicks = 0;
        for (int i = 0; i < 400; i++)
        {
            bTick++;
            b.MoveDir = gDir;
            b.RunSpeed = 1f;
            b.Tick(new TickContext(HumanoidGravity(), bTerrain, bTick));
            foreach (Limb leg in b.Legs)
            {
                if (leg.Gripping)
                {
                    bGripTicks++;
                }
            }
        }
        float bruteDuty = bGripTicks / 800f;

        bool ok = stride >= 0.45f && period >= 7f && contact >= 5f && relAdv <= 0.15f
            && duty >= 0.3f && airFrac <= 0.02f && bruteDuty >= 0.2f;
        message = $"stride={stride:F2}m（≥0.45） period={period:F1}t（≥7） contact={contact:F1}t（≥5） " +
                  $"releaseAdv={relAdv:F2}m（≤0.15） duty={duty:F2}（≥0.3） bothAir={airFrac:F2}（≤0.02） " +
                  $"bruteDuty={bruteDuty:F2}（≥0.2）";
        return ok;
    }

    /// <summary>指关节行走的两手分离（回归钉）：扇扫中轴若朝单一 KnucklePos 汇聚，起点的侧向
    /// 错开会在命中处被精确抵消、两手锁到同一世界点（用户白盒实测暴露的重合 bug）。平地直行中
    /// 双手同时锁点的相位必须出现，且两锁点最小间距不得塌向重合——目标点侧向错开给出 ~0.4m
    /// 底线（斜面最坏收缩仍 >0.24m），门槛取 0.15m 留余量。</summary>
    private static bool CheckHumanoidHandSpread(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        long tick = 0;
        SettleHumanoid(c, terrain, ref tick, 200);

        int bothPlanted = 0;
        float minSep = float.MaxValue;
        Vector3 g = HumanoidGravity();
        for (int i = 0; i < 800; i++)
        {
            tick++;
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));
            if (c.Arms[0].GrabPos is { } g0 && c.Arms[1].GrabPos is { } g1)
            {
                bothPlanted++;
                minSep = Mathf.Min(minSep, (g0 - g1).Length());
            }
        }

        bool phaseOk = bothPlanted >= 100;
        bool sepOk = bothPlanted > 0 && minSep >= 0.15f;
        message = $"bothPlantedTicks={bothPlanted}/800（须 ≥100）" +
                  $" minPlantSep={(bothPlanted > 0 ? minSep : 0f):F3}m（须 ≥0.15）";
        return phaseOk && sepOk;
    }

    /// <summary>解析半空间并集地形（重力开关边界回归用）：solid = 任一半空间
    /// { p·Normal ≤ Offset } 之内。射线取最近入面点、起点在体内给零法线（HitFromInside 语义）；
    /// 球体 MTD 取最深重叠面的法向推出（测试身体不进多面夹角，近似足够）。</summary>
    private sealed class HalfSpaceTerrain : ITerrainQuery
    {
        private readonly (Vector3 Normal, float Offset)[] _faces;

        public HalfSpaceTerrain(params (Vector3 Normal, float Offset)[] faces)
        {
            _faces = faces;
        }

        private bool Inside(Vector3 p)
        {
            foreach ((Vector3 n, float d) in _faces)
            {
                if (p.Dot(n) <= d)
                {
                    return true;
                }
            }
            return false;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (Inside(from))
            {
                hit = new TerrainHit(from, Vector3.Zero, 1UL);
                return true;
            }
            float best = float.MaxValue;
            for (int i = 0; i < _faces.Length; i++)
            {
                (Vector3 n, float d) = _faces[i];
                float a = from.Dot(n) - d;
                float b = to.Dot(n) - d;
                if (a >= 0f && b < 0f)
                {
                    float t = a / (a - b);
                    if (t < best)
                    {
                        best = t;
                        hit = new TerrainHit(from.Lerp(to, t), n, (ulong)(i + 1));
                    }
                }
            }
            return best != float.MaxValue;
        }

        public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            pushDir = Vector3.Up;
            depth = 0f;
            foreach ((Vector3 n, float d) in _faces)
            {
                float pen = radius - (center.Dot(n) - d);
                if (pen > depth)
                {
                    pushDir = n;
                    depth = pen;
                }
            }
            return depth > 0f;
        }
    }

    /// <summary>Shift/Teleport/Launch 完备性：① 行走中 Shift(+512,0,+512) 对每个带世界坐标的量
    /// （chunk Pos/LastPos、腿 Pos/LastPos/HuntPos、手 Pos/LastPos/HuntPos/GrabPos、KnucklePos/
    /// PointTarget/MoveTarget）逐字段精确 + 续走；② Teleport 作废撑点/指向/直喂/蓄力；
    /// ③ Launch 后坠落再落地续走。</summary>
    private static bool CheckHumanoidShift(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        HumanoidLocomotionController c = MakeHumanoid(new Vector3(0f, 0.5f, 0f));
        Vector3 g = HumanoidGravity();
        long tick = 0;
        for (int i = 0; i < 300; i++)
        {
            tick++;
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));
        }
        c.PointTarget = c.Chest.Pos + new Vector3(2f, 0.5f, 0f);
        c.MoveTarget = c.Chest.Pos + new Vector3(6f, 0f, 0f);

        var delta = new Vector3(512f, 0f, 512f);
        var expected = new List<(string Name, Vector3 Want)>();
        foreach (BodyChunk chunk in c.Body.Chunks)
        {
            expected.Add(("chunk.Pos", chunk.Pos + delta));
            expected.Add(("chunk.LastPos", chunk.LastPos + delta));
        }
        foreach (Limb leg in c.Legs)
        {
            expected.Add(("leg.Pos", leg.Pos + delta));
            expected.Add(("leg.LastPos", leg.LastPos + delta));
            expected.Add(("leg.HuntPos", leg.HuntPos + delta));
        }
        foreach (Arm arm in c.Arms)
        {
            expected.Add(("arm.Pos", arm.Pos + delta));
            expected.Add(("arm.LastPos", arm.LastPos + delta));
            expected.Add(("arm.HuntPos", arm.HuntPos + delta));
            if (arm.GrabPos is { } grab)
            {
                expected.Add(("arm.GrabPos", grab + delta));
            }
        }
        bool hadKnuckle = c.KnucklePos is not null;
        Vector3 knuckleWant = (c.KnucklePos ?? Vector3.Zero) + delta;
        Vector3 pointWant = c.PointTarget!.Value + delta;
        Vector3 fedWant = c.MoveTarget!.Value + delta;
        Vector3 mainHandWant = c.MainHandPos + delta;

        c.Shift(delta);

        var actual = new List<Vector3>();
        foreach (BodyChunk chunk in c.Body.Chunks)
        {
            actual.Add(chunk.Pos);
            actual.Add(chunk.LastPos);
        }
        foreach (Limb leg in c.Legs)
        {
            actual.Add(leg.Pos);
            actual.Add(leg.LastPos);
            actual.Add(leg.HuntPos);
        }
        foreach (Arm arm in c.Arms)
        {
            actual.Add(arm.Pos);
            actual.Add(arm.LastPos);
            actual.Add(arm.HuntPos);
            if (arm.GrabPos is { } grab)
            {
                actual.Add(grab);
            }
        }
        bool exact = actual.Count == expected.Count;
        for (int i = 0; exact && i < actual.Count; i++)
        {
            exact &= actual[i] == expected[i].Want;
        }
        exact &= !hadKnuckle || (c.KnucklePos is { } kp && kp == knuckleWant);
        exact &= c.PointTarget == pointWant;
        exact &= c.MoveTarget == fedWant;
        // MainHandPos 是世界坐标观测（宿主用它钉被持物）：Shift 到下个 Tick 之间读到
        // 旧位置会把持物钉回原点——完备性契约必须覆盖它。
        exact &= c.MainHandPos == mainHandWant;

        // 续走（清掉指向/直喂，回到纯方向驱动）
        c.PointTarget = null;
        c.MoveTarget = null;
        Vector3 before = c.Chest.Pos;
        for (int i = 0; i < 300; i++)
        {
            tick++;
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));
        }
        Vector3 walked = c.Chest.Pos - before;
        walked.Y = 0f;
        float resumeDist = walked.Length();
        bool resumed = resumeDist > 10f && c.Chest.Pos.IsFinite();

        // Teleport 作废语义
        c.PointTarget = c.Chest.Pos + new Vector3(1f, 0f, 0f);
        c.MoveTarget = c.Chest.Pos + new Vector3(2f, 0f, 0f);
        c.StartThrowCharge(new Vector3(1f, 0f, 0f));
        c.Teleport(new Vector3(-1000f, 0f, -1000f));
        bool teleportVoids = c.PointTarget is null && c.MoveTarget is null && c.KnucklePos is null
            && c.ThrowChargeTicks == 0 && c.ThrowAnimTicks == 0 && c.GroundedCounter == 0;
        foreach (Arm arm in c.Arms)
        {
            teleportVoids &= arm.GrabPos is null && arm.Mode == Arm.ArmMode.Dangle;
        }
        foreach (Limb leg in c.Legs)
        {
            teleportVoids &= !leg.ReachingForTerrain && !leg.HasGrip;
        }

        // Launch：坠落 → 落地 → 续走
        SettleHumanoid(c, terrain, ref tick, 200);
        c.Launch(new Vector3(0.3f, 0.25f, 0f));
        tick++;
        c.Tick(new TickContext(g, terrain, tick));
        bool launchedAirborne = c.ApplyGravity;
        for (int i = 0; i < 250; i++)
        {
            tick++;
            c.Tick(new TickContext(g, terrain, tick));
        }
        before = c.Chest.Pos;
        for (int i = 0; i < 200; i++)
        {
            tick++;
            c.MoveDir = new Vector3(1f, 0f, 0f);
            c.RunSpeed = 1f;
            c.Tick(new TickContext(g, terrain, tick));
        }
        walked = c.Chest.Pos - before;
        walked.Y = 0f;
        bool launchRecovered = launchedAirborne && walked.Length() > 8f && c.Uprightness > 0.6f;

        message = $"Shift 逐字段精确={exact}（{actual.Count} 项 + knuckle/point/fed），续走 {resumeDist:F2}m，" +
                  $"resumed={resumed}；Teleport 作废撑点/指向/直喂/蓄力={teleportVoids}；" +
                  $"Launch 坠落={launchedAirborne} 落地续走 {walked.Length():F2}m+回正={launchRecovered}";
        return exact && resumed && teleportVoids && launchRecovered;
    }
}
