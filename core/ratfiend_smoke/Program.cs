using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.RatFiend;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.RatFiendSmoke;

/// <summary>
/// RatFiend 专项无引擎回归。地形是 AABB 盒子 + 半空间的解析房间（DropBugSmoke 同款）。
/// 全部门为真断言（退出码判定），关键机制均含消融对照：
/// · 驼背来自显式倾斜站立力偶（EnableHunchTilt=false 时静立驼背几何必翻红）；
/// · 爬行推进 ∝ 抓地肢体数（EnableCrawlGripScaling=false 时断肢里程单调必翻红）。
/// 断肢路线走独立脚本化 Sever（固定 tick 调用）；普通路线从不调 Sever——两族基线正交。
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

    // 在完整行为门人工核对后钉定；只有有意改变 RatFiend 内核轨迹时才更新。
    private const ulong ExpectedHash = 0xA7C06ECCF4698B43UL;

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var failures = new List<string>();

        Check("ASSEMBLY", CheckAssembly, failures);
        Check("DET", CheckDeterminism, failures);
        Check("WALK", CheckWalkAndHunch, failures);
        Check("POSTURE", CheckPostureMonotone, failures);
        Check("ARM-SWING", CheckArmSwing, failures);
        Check("WALK-SWING", CheckWalkSwing, failures);
        Check("LOOK-TARGET", CheckLookTarget, failures);
        Check("SEVER-API", CheckSeverApi, failures);
        Check("SEVER-ARM-WALK", CheckSeverArmWalk, failures);
        Check("ARM-STUMP", CheckArmStump, failures);
        Check("SEVER-LEG-CRAWL", CheckSeverLegCrawl, failures);
        Check("CRAWL-TURN", CheckCrawlTurn, failures);
        Check("CRAWL-STEP", CheckCrawlStep, failures);
        Check("SEVER-MONOTONE", CheckSeverMonotone, failures);
        Check("SEVER-ALL", CheckSeverAll, failures);
        Check("ATTACK", CheckAttack, failures);
        Check("GRAB-SPREAD", CheckGrabSpread, failures);
        Check("IMPACT-TWIST", CheckImpactTwist, failures);
        Check("LIFECYCLE", CheckLifecycle, failures);
        Check("QUERY", CheckQueryBudget, failures);
        Report(
            "PENETRATION",
            _maxResidualPenetration < 0.002f,
            $"maxResidual={_maxResidualPenetration:E3}m at={_maxPenetrationContext}（门 2mm）",
            failures);

        bool pass = failures.Count == 0;
        Console.WriteLine(pass
            ? "[RATFIEND-CORE-SMOKE] PASS：固定哈希、装配、驼背姿态、走跑单调、摆臂反相、走姿慢摆、凝视目标、" +
              "断肢 API、断臂行走、断臂垂位、断腿爬行、爬行调头、爬行翻台阶、断肢里程单调、全断蠕动、" +
              "攻击接缝、抓取分手、朝向冲击与生命周期均通过"
            : $"[RATFIEND-CORE-SMOKE] FAIL：{string.Join("；", failures)}");
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
        Console.WriteLine($"[RATFIEND-CORE-{name}] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok)
        {
            failures.Add(name);
        }
    }

    // ================================================================ 装配

    private static (bool, string) CheckAssembly()
    {
        RatFiendParams p = RatFiendFactory.Gaunt();
        RatFiendLocomotionController rat =
            RatFiendFactory.CreateController(new Vector3(0f, 0.5f, 0f), Vector3.Right, p);

        bool chunkOrder = rat.Body.Chunks.Count == 3
            && ReferenceEquals(rat.Body.Chunks[0], rat.Chest)
            && ReferenceEquals(rat.Body.Chunks[1], rat.Hips)
            && ReferenceEquals(rat.Body.Chunks[2], rat.Head);
        bool connections = rat.Body.Connections.Count == 2
            && rat.Body.Connections[0].ConstraintMode == ChunkConnection.Mode.Rigid
            && rat.Body.Connections[1].ConstraintMode == ChunkConnection.Mode.PullOnly
            && MathF.Abs(rat.Body.Connections[0].RestLength - p.ChestHipsDist) < 1e-5f
            && MathF.Abs(rat.Body.Connections[1].RestLength - p.NeckLength) < 1e-5f;
        bool rotation = ReferenceEquals(rat.Chest.RotationChunk, rat.Hips)
            && ReferenceEquals(rat.Hips.RotationChunk, rat.Chest)
            && ReferenceEquals(rat.Head.RotationChunk, rat.Chest);
        bool legs = rat.Legs.Count == 2
            && rat.Legs[0].Side == -1 && rat.Legs[1].Side == +1
            && rat.Legs[0].LookaheadTicks == 3 && rat.Legs[1].LookaheadTicks == 3
            && ReferenceEquals(rat.Legs[0].Pair, rat.Legs[1])
            && ReferenceEquals(rat.Legs[1].Pair, rat.Legs[0]);
        bool arms = rat.Arms.Count == 2
            && rat.Arms[0].Side == -1 && rat.Arms[1].Side == +1
            && MathF.Abs(rat.Arms[0].ArmpitGap - (p.ChestRadius + p.HandRadius)) < 1e-5f
            && !rat.Arms[0].Severed && !rat.Arms[1].Severed;

        RatFiendParams[] presets = RatFiendFactory.AllPresets();
        var ids = new HashSet<string>();
        foreach (RatFiendParams preset in presets)
        {
            ids.Add(preset.Id);
        }
        bool presetTable = presets.Length == 4 && ids.Count == 4;
        bool caseInsensitive = RatFiendFactory.ById("RATFIEND/GAUNT").Id == "ratfiend/gaunt";
        bool unknownThrows;
        try
        {
            RatFiendFactory.ById("ratfiend/nonexistent");
            unknownThrows = false;
        }
        catch (ArgumentException)
        {
            unknownThrows = true;
        }

        // dusk = gaunt 同体格（体格逐位相同，只换 ID——调色板归渲染层）。
        RatFiendParams gaunt = RatFiendFactory.Gaunt();
        RatFiendParams dusk = RatFiendFactory.Dusk();
        bool duskPhysique = dusk.Id == "ratfiend/dusk"
            && dusk.ChestRadius == gaunt.ChestRadius
            && dusk.LegLength == gaunt.LegLength
            && dusk.ArmLength == gaunt.ArmLength
            && dusk.HunchAngleDegrees == gaunt.HunchAngleDegrees;

        // 出生冻结：装配后改同一张参数表，已出生实例轨迹逐位不变。
        bool birthFrozen = BirthFrozen();

        bool ok = chunkOrder && connections && rotation && legs && arms
            && presetTable && caseInsensitive && unknownThrows && duskPhysique && birthFrozen;
        return (ok,
            $"chunks={chunkOrder} conns={connections} rotation={rotation} legs={legs} " +
            $"arms={arms} presets={presetTable} byId={caseInsensitive} " +
            $"unknownThrows={unknownThrows} dusk={duskPhysique} birthFrozen={birthFrozen}");
    }

    private static bool BirthFrozen()
    {
        ulong RunAfterBirth(bool mutateAfterBirth)
        {
            var terrain = FlatFloor();
            RatFiendParams p = RatFiendFactory.Gaunt();
            RatFiendLocomotionController rat =
                RatFiendFactory.CreateController(new Vector3(0f, 0.5f, 0f), Vector3.Right, p);
            if (mutateAfterBirth)
            {
                p.LegLength = 99f;
                p.HunchAngleDegrees = 0f;
                p.CrawlForcePerLimb = 99f;
            }
            var hasher = new DeterminismHasher();
            long tick = 0;
            for (int i = 1; i <= 120; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 0.8f;
                Tick(rat, terrain, ref tick);
                rat.FoldState(hasher);
            }
            return hasher.Value;
        }

        return RunAfterBirth(false) == RunAfterBirth(true);
    }

    // ================================================================ 确定性

    private readonly record struct DetResult(
        ulong Hash, bool Finite, float MaxDeviation, float MaxRunBlend, bool CrawlSeen,
        int MaxGripsThreeLimb, int MaxGripsTwoLimb, int SeverSerial);

    /// <summary>钉哈希的固定路线：慢走 → 全速跑 → 转向 → 固定 tick 断左腿（摔倒 + 爬行涌现）
    /// → 爬行巡走 → 断右臂 → 双肢爬行。覆盖走/跑姿态混合、断肢、爬行推进全路径。</summary>
    private static DetResult RunDeterminism(float perturb)
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(-6f, 0.5f, 0f), Vector3.Right);
        if (perturb != 0f)
        {
            rat.Chest.Pos.X += perturb;
            rat.Chest.LastPos = rat.Chest.Pos;
        }
        var hasher = new DeterminismHasher();
        long tick = 0;
        float maxDev = 0f;
        float maxRunBlend = 0f;
        bool crawlSeen = false;
        int maxGrips3 = 0;
        int maxGrips2 = 0;
        for (int i = 1; i <= 1400; i++)
        {
            rat.MoveTarget = null;
            rat.RunSpeed = i <= 250 ? 0.35f : 1f;
            rat.MoveDir = i <= 500 ? Vector3.Right : Vector3.Back;
            if (i == 651)
            {
                rat.Sever(RatFiendLimbId.LegLeft);
            }
            if (i == 1101)
            {
                rat.Sever(RatFiendLimbId.ArmRight);
            }
            Tick(rat, terrain, ref tick);
            rat.FoldState(hasher);
            maxDev = MathF.Max(maxDev, rat.Body.CurrentMaxDeviation());
            maxRunBlend = MathF.Max(maxRunBlend, rat.RunBlend);
            crawlSeen |= rat.Crawling;
            if (i is > 700 and <= 1100)
            {
                maxGrips3 = Math.Max(maxGrips3, rat.CrawlGripCount);
            }
            if (i > 1150)
            {
                maxGrips2 = Math.Max(maxGrips2, rat.CrawlGripCount);
            }
        }
        return new DetResult(hasher.Value, IsFinite(rat), maxDev, maxRunBlend, crawlSeen,
            maxGrips3, maxGrips2, rat.SeverSerial);
    }

    private static (bool, string) CheckDeterminism()
    {
        DetResult a = RunDeterminism(0f);
        DetResult b = RunDeterminism(0f);
        DetResult p = RunDeterminism(0.001f);
        bool routeCovered = a.MaxRunBlend > 0.9f && a.CrawlSeen
            && a.MaxGripsThreeLimb >= 3 && a.MaxGripsTwoLimb >= 1 && a.SeverSerial == 2;
        // maxDev 门 0.2：断腿摔落的着地冲击会产生一次性约束偏差（下 tick 即被松弛收回），
        // 巡走段应远低于此。
        bool ok = a.Hash == b.Hash && a.Hash == ExpectedHash && p.Hash != a.Hash
            && a.Finite && a.MaxDeviation < 0.2f && routeCovered;
        return (ok,
            $"run1={a.Hash:X16} run2={b.Hash:X16} expected={ExpectedHash:X16} " +
            $"perturb={p.Hash:X16} finite={a.Finite} maxDev={a.MaxDeviation:F5}m " +
            $"maxRunBlend={a.MaxRunBlend:F2} crawl={a.CrawlSeen} " +
            $"grips3={a.MaxGripsThreeLimb} grips2={a.MaxGripsTwoLimb} severs={a.SeverSerial}");
    }

    // ================================================================ 直立行走与驼背

    /// <summary>行走 + 静立的驼背几何。消融对照：EnableHunchTilt=false 时静立前倾必然塌回直立
    /// ——证明驼背来自显式倾斜站立力偶，不是阻尼差的巡航副作用。</summary>
    private static (bool, string) CheckWalkAndHunch()
    {
        (float mileage, float walkHunch, float standHunch, float standDrift, bool finite)
            RunHunch(bool tilt)
        {
            var terrain = FlatFloor();
            RatFiendLocomotionController rat = NewRat(new Vector3(-20f, 0.5f, 0f), Vector3.Right);
            rat.EnableHunchTilt = tilt;
            long tick = 0;
            float startX = rat.Chest.Pos.X;
            float walkHunchSum = 0f;
            int walkSamples = 0;
            for (int i = 1; i <= 800; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 0.6f;
                Tick(rat, terrain, ref tick);
                if (i > 300)
                {
                    walkHunchSum += HunchDot(rat);
                    walkSamples++;
                }
            }
            float mileage = rat.Chest.Pos.X - startX;
            // 停驶静立：驼背必须保持（显式姿态轴的核心断言——阻尼差在这里零输出）。
            // 漂移只测后半窗——前 150 tick 是合法的停驶滑行（惯性 + 阻尼衰减）。
            Vector3 chestMid = default;
            float standHunchSum = 0f;
            int standSamples = 0;
            for (int i = 1; i <= 300; i++)
            {
                rat.MoveDir = Vector3.Zero;
                rat.RunSpeed = 0f;
                Tick(rat, terrain, ref tick);
                if (i == 150)
                {
                    chestMid = rat.Chest.Pos;
                }
                if (i > 100)
                {
                    standHunchSum += HunchDot(rat);
                    standSamples++;
                }
            }
            float standDrift = (rat.Chest.Pos - chestMid).Length();
            return (mileage, walkHunchSum / walkSamples, standHunchSum / standSamples,
                standDrift, IsFinite(rat));
        }

        var full = RunHunch(tilt: true);
        var ablated = RunHunch(tilt: false);
        bool ok = full.mileage > 25f
            && full.walkHunch > 0.15f && full.standHunch > 0.15f
            && full.standDrift < 0.15f && full.finite
            && ablated.standHunch < 0.08f && full.standHunch > ablated.standHunch + 0.1f;
        return (ok,
            $"mileage={full.mileage:F2}m（≥25） walkHunch={full.walkHunch:F3}（≥0.15） " +
            $"standHunch={full.standHunch:F3}（≥0.15） standDrift={full.standDrift:F3}m" +
            $"（<0.15，后半窗） ablatedStandHunch={ablated.standHunch:F3}（<0.08，消融红灯） " +
            $"finite={full.finite}");
    }

    /// <summary>驼背几何：躯干轴（髋→胸）在 Facing 上的投影（>0 = 胸探在髋前 = 前倾）。</summary>
    private static float HunchDot(RatFiendLocomotionController rat)
    {
        Vector3 axis = rat.Chest.Pos - rat.Hips.Pos;
        return axis.LengthSquared() < 1e-10f ? 0f : axis.Normalized().Dot(rat.Facing);
    }

    // ================================================================ 走跑姿态单调

    /// <summary>RunSpeed 0.2/0.5/0.8/1.0 稳态巡航：头相对胸的高度、嘴开度、双手前伸量
    /// 都必须随油门严格单调递增——「慢走耷拉垂手微张 ↔ 快跑抬头前伸大张」的行为门。</summary>
    private static (bool, string) CheckPostureMonotone()
    {
        (float headUp, float mouth, float handFwd) Cruise(float runSpeed)
        {
            var terrain = FlatFloor();
            RatFiendLocomotionController rat = NewRat(new Vector3(-30f, 0.5f, 0f), Vector3.Right);
            long tick = 0;
            float headSum = 0f;
            float mouthSum = 0f;
            float handSum = 0f;
            int samples = 0;
            for (int i = 1; i <= 700; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = runSpeed;
                Tick(rat, terrain, ref tick);
                if (i > 500)
                {
                    headSum += rat.Head.Pos.Y - rat.Chest.Pos.Y;
                    mouthSum += rat.MouthOpen;
                    handSum += ((rat.Arms[0].Pos - rat.Chest.Pos).Dot(rat.Facing)
                        + (rat.Arms[1].Pos - rat.Chest.Pos).Dot(rat.Facing)) * 0.5f;
                    samples++;
                }
            }
            return (headSum / samples, mouthSum / samples, handSum / samples);
        }

        var s20 = Cruise(0.2f);
        var s50 = Cruise(0.5f);
        var s80 = Cruise(0.8f);
        var s100 = Cruise(1f);
        bool headMono = s20.headUp + 0.01f < s50.headUp
            && s50.headUp + 0.01f < s80.headUp && s80.headUp + 0.01f < s100.headUp;
        bool mouthMono = s20.mouth + 0.02f < s50.mouth
            && s50.mouth + 0.02f < s80.mouth && s80.mouth + 0.02f < s100.mouth;
        bool handMono = s20.handFwd + 0.02f < s50.handFwd
            && s50.handFwd + 0.02f < s80.handFwd && s80.handFwd + 0.02f < s100.handFwd;
        bool ok = headMono && mouthMono && handMono;
        return (ok,
            $"headUp=[{s20.headUp:F3},{s50.headUp:F3},{s80.headUp:F3},{s100.headUp:F3}]" +
            $"（单调 {headMono}） mouth=[{s20.mouth:F2},{s50.mouth:F2},{s80.mouth:F2}," +
            $"{s100.mouth:F2}]（单调 {mouthMono}） handFwd=[{s20.handFwd:F2},{s50.handFwd:F2}," +
            $"{s80.handFwd:F2},{s100.handFwd:F2}]（单调 {handMono}）");
    }

    // ================================================================ 摆臂反相

    /// <summary>慢走摆臂：两手前后偏移围绕共同均值反相（右臂随左腿——读对侧腿相位的涌现结果），
    /// 且摆幅可见。</summary>
    private static (bool, string) CheckArmSwing()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(-30f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        var left = new List<float>();
        var right = new List<float>();
        for (int i = 1; i <= 1100; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 0.4f;
            Tick(rat, terrain, ref tick);
            if (i > 300)
            {
                left.Add((rat.Arms[0].Pos - rat.Chest.Pos).Dot(rat.Facing));
                right.Add((rat.Arms[1].Pos - rat.Chest.Pos).Dot(rat.Facing));
            }
        }
        float mean = 0f;
        for (int i = 0; i < left.Count; i++)
        {
            mean += left[i] + right[i];
        }
        mean /= left.Count * 2;
        int antiPhase = 0;
        float maxSpread = 0f;
        for (int i = 0; i < left.Count; i++)
        {
            if ((left[i] - mean) * (right[i] - mean) < 0f)
            {
                antiPhase++;
            }
            maxSpread = MathF.Max(maxSpread, MathF.Abs(left[i] - right[i]));
        }
        float antiFrac = antiPhase / (float)left.Count;
        bool ok = antiFrac > 0.6f && maxSpread > 0.1f;
        return (ok, $"antiPhase={antiFrac:P0}（≥60%） maxSpread={maxSpread:F3}m（≥0.1）");
    }

    // ================================================================ 走姿慢摆

    /// <summary>走姿慢摆（R15）：walk 档（RunSpeed 0.6）单步腾空段（Gripping==false 的连续
    /// tick 段）显著长于跑档，且步频（腾空起点间隔）与消融基线相当——慢的是摆动过程，
    /// 不是步频。消融对照：LegSwingSpeedFactor=1 时 walk 腾空段必然缩回快摆基线。</summary>
    private static (bool, string) CheckWalkSwing()
    {
        (float meanSwing, float meanCycle, int swings, float mileage) Measure(
            float runSpeed, float? swingFactor)
        {
            var terrain = FlatFloor();
            RatFiendParams p = RatFiendFactory.Gaunt();
            if (swingFactor is { } f)
            {
                p.LegSwingSpeedFactor = f;
            }
            RatFiendLocomotionController rat =
                RatFiendFactory.CreateController(new Vector3(-40f, 0.5f, 0f), Vector3.Right, p);
            long tick = 0;
            float startX = rat.Chest.Pos.X;
            var swingLen = new int[2];
            var wasGripping = new bool[2];
            var lastSwingStart = new long[2] { -1, -1 };
            int swingTotal = 0, swingCount = 0;
            long cycleTotal = 0;
            int cycleCount = 0;
            for (int i = 1; i <= 1200; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = runSpeed;
                Tick(rat, terrain, ref tick);
                for (int l = 0; l < 2; l++)
                {
                    bool grip = rat.Legs[l].Gripping;
                    if (i <= 400)
                    {
                        wasGripping[l] = grip; // 预热窗：只跟踪状态，不计量
                        continue;
                    }
                    if (!grip)
                    {
                        if (wasGripping[l])
                        {
                            if (lastSwingStart[l] >= 0)
                            {
                                cycleTotal += tick - lastSwingStart[l];
                                cycleCount++;
                            }
                            lastSwingStart[l] = tick;
                        }
                        swingLen[l]++;
                    }
                    else if (!wasGripping[l] && swingLen[l] > 0)
                    {
                        swingTotal += swingLen[l];
                        swingCount++;
                        swingLen[l] = 0;
                    }
                    wasGripping[l] = grip;
                }
            }
            return (swingCount == 0 ? 0f : (float)swingTotal / swingCount,
                cycleCount == 0 ? 0f : (float)cycleTotal / cycleCount,
                swingCount, rat.Chest.Pos.X - startX);
        }

        var walk = Measure(0.6f, null);
        var run = Measure(1f, null);
        var ablated = Measure(0.6f, 1f);
        bool ok = walk.swings >= 20 && run.swings >= 20 && ablated.swings >= 20
            && walk.meanSwing > ablated.meanSwing * 1.4f
            && run.meanSwing < walk.meanSwing
            && walk.meanCycle > ablated.meanCycle * 0.75f
            && walk.meanCycle < ablated.meanCycle * 1.35f
            && walk.mileage > 20f;
        return (ok,
            $"walkSwing={walk.meanSwing:F1}tick vs ablated={ablated.meanSwing:F1}" +
            $"（>1.4×消融红灯） runSwing={run.meanSwing:F1}（<walk） " +
            $"walkCycle={walk.meanCycle:F1}tick vs ablated={ablated.meanCycle:F1}" +
            $"（±35% 步频不变） swings={walk.swings}/{run.swings}/{ablated.swings}（≥20） " +
            $"mileage={walk.mileage:F1}m（≥20）");
    }

    // ================================================================ 凝视目标

    /// <summary>凝视目标（R16/R16b）：低速走姿（RunSpeed 0.35，竞技场近身油门档——头手本该
    /// 耷拉的工况）下逐 tick 喂前方眼高 LookTarget，头轴（Chest→Head 与世界 up 的点积）与
    /// 手前伸量（hand−Chest 沿 Facing）都必须明显抬起；不喂即消融红灯（回到耷拉基线）。
    /// 附带生命周期：Shift 平移凝视点、Teleport 作废。哈希中立由 DET 门自动背书
    /// （回归路线从不设置 LookTarget）。</summary>
    private static (bool, string) CheckLookTarget()
    {
        (float headDot, float armReach) Measure(bool stare)
        {
            var terrain = FlatFloor();
            RatFiendLocomotionController rat = NewRat(new Vector3(-40f, 0.5f, 0f), Vector3.Right);
            long tick = 0;
            float dotSum = 0f, reachSum = 0f;
            int n = 0;
            for (int i = 1; i <= 900; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 0.35f;
                rat.LookTarget = stare
                    ? rat.Chest.Pos + new Vector3(4f, 1.2f, 0f) // 前方眼高「猎物」，镜像宿主逐 tick 喂法
                    : null;
                Tick(rat, terrain, ref tick);
                if (i <= 300)
                {
                    continue; // 预热窗
                }
                Vector3 axis = rat.Head.Pos - rat.Chest.Pos;
                if (axis.LengthSquared() > 1e-10f)
                {
                    dotSum += axis.Normalized().Dot(Vector3.Up);
                    reachSum += ((rat.Arms[0].Pos - rat.Chest.Pos).Dot(rat.Facing)
                        + (rat.Arms[1].Pos - rat.Chest.Pos).Dot(rat.Facing)) * 0.5f;
                    n++;
                }
            }
            return n == 0 ? (-2f, -2f) : (dotSum / n, reachSum / n);
        }

        (float stare, float stareReach) = Measure(true);
        (float droop, float droopReach) = Measure(false);

        RatFiendLocomotionController probe = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        probe.LookTarget = new Vector3(1f, 2f, 3f);
        probe.Shift(new Vector3(10f, 0f, -5f));
        bool shiftOk = probe.LookTarget == new Vector3(11f, 2f, -2f);
        probe.Teleport(Vector3.Zero);
        bool teleportOk = probe.LookTarget is null;

        bool ok = stare > droop + 0.3f && droop < 0f && stare > -0.05f
            && stareReach > droopReach + 0.3f && shiftOk && teleportOk;
        return (ok,
            $"headDot stare={stare:F2} vs droop={droop:F2}（差 ≥0.3 消融红灯；droop<0、stare≥-0.05） " +
            $"armReach stare={stareReach:F2}m vs droop={droopReach:F2}（差 ≥0.3，R16b 备抓抬臂） " +
            $"shift={(shiftOk ? "ok" : "FAIL")} teleportClear={(teleportOk ? "ok" : "FAIL")}");
    }

    // ================================================================ 断肢 API

    private static (bool, string) CheckSeverApi()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 80; i++)
        {
            rat.MoveDir = Vector3.Zero;
            rat.RunSpeed = 0f;
            Tick(rat, terrain, ref tick);
        }

        RatArm armRight = rat.Arms[1];
        Vector3 tipPos = armRight.Pos;
        Vector3 tipLast = armRight.LastPos;
        Vector3 tipVel = armRight.Vel;
        RatFiendSeveredLimbState s = rat.Sever(RatFiendLimbId.ArmRight);
        bool seedExact = s.TipPos == tipPos && s.TipLastPos == tipLast && s.TipVel == tipVel
            && MathF.Abs(s.SeveredLength - armRight.ArmLength * 0.5f) < 1e-5f
            && s.TipRadius == armRight.Radius;
        bool flagged = armRight.Severed && rat.IsSevered(RatFiendLimbId.ArmRight)
            && rat.SeverSerial == 1 && rat.SeveredLimbCount == 1;
        bool doubleThrows;
        try
        {
            rat.Sever(RatFiendLimbId.ArmRight);
            doubleThrows = false;
        }
        catch (InvalidOperationException)
        {
            doubleThrows = true;
        }

        // 昏迷也可断（尸体断肢是宿主权利）；断腿 = JointDist 减半 + 存活腿 Pair 置空。
        rat.Conscious = false;
        float fullLegLength = rat.Legs[0].JointDist;
        rat.Sever(RatFiendLimbId.LegLeft);
        bool legSevered = MathF.Abs(rat.Legs[0].JointDist - fullLegLength * 0.5f) < 1e-5f
            && rat.Legs[1].Pair is null && !rat.CanStand && rat.SeverSerial == 2;
        rat.Conscious = true;
        bool crawlingNow = rat.Crawling;

        // stagger opt-in：默认零参数不改动 chunk 速度；显式传冲量则精确叠加。
        RatFiendLocomotionController rat2 = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        Vector3 chestVel = rat2.Chest.Vel;
        rat2.Sever(RatFiendLimbId.ArmLeft);
        bool defaultNoImpulse = rat2.Chest.Vel == chestVel;
        Vector3 hipsVel = rat2.Hips.Vel;
        var impulse = new Vector3(0.06f, 0f, 0f);
        rat2.Sever(RatFiendLimbId.LegRight, impulse);
        bool staggerExact = rat2.Hips.Vel == hipsVel + impulse;

        bool ok = seedExact && flagged && doubleThrows && legSevered && crawlingNow
            && defaultNoImpulse && staggerExact;
        return (ok,
            $"seedExact={seedExact} flagged={flagged} doubleThrows={doubleThrows} " +
            $"legSevered={legSevered} crawling={crawlingNow} " +
            $"defaultNoImpulse={defaultNoImpulse} staggerExact={staggerExact}");
    }

    // ================================================================ 断臂行走

    /// <summary>断一只手不影响行走（臂本不承力）：断臂后行走里程与完好基线偏差 &lt; 5%，
    /// 残肢全程被 EffectiveLength 钳在肩→肘半径内、恒为垂摆模式。</summary>
    private static (bool, string) CheckSeverArmWalk()
    {
        float WalkMileage(bool severArm, out float maxStumpReach, out bool alwaysDangle)
        {
            var terrain = FlatFloor();
            RatFiendLocomotionController rat = NewRat(new Vector3(-30f, 0.5f, 0f), Vector3.Right);
            if (severArm)
            {
                rat.Sever(RatFiendLimbId.ArmLeft);
            }
            long tick = 0;
            float startX = 0f;
            maxStumpReach = 0f;
            alwaysDangle = true;
            for (int i = 1; i <= 1000; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 0.7f;
                Tick(rat, terrain, ref tick);
                if (i == 200)
                {
                    startX = rat.Chest.Pos.X;
                }
                if (severArm && i > 5)
                {
                    RatArm stump = rat.Arms[0];
                    maxStumpReach = MathF.Max(maxStumpReach,
                        (stump.Pos - rat.Chest.Pos).Length() - stump.EffectiveLength);
                    alwaysDangle &= stump.Mode == RatArm.ArmMode.Dangle;
                }
            }
            return rat.Chest.Pos.X - startX;
        }

        float baseline = WalkMileage(false, out _, out _);
        float armless = WalkMileage(true, out float maxReach, out bool dangle);
        float deviation = MathF.Abs(armless - baseline) / MathF.Max(baseline, 1e-3f);
        bool ok = baseline > 20f && deviation < 0.05f && maxReach < 0.05f && dangle;
        return (ok,
            $"baseline={baseline:F2}m armless={armless:F2}m deviation={deviation:P1}（<5%） " +
            $"stumpOverreach={maxReach:F3}m（<0.05） alwaysDangle={dangle}");
    }

    /// <summary>双断臂站立残肢垂位（R17）：Dangle 的臂长钳制锚在胸心，无肩侧偏置时纯自重
    /// 平衡在胸心正下方——两截残肢收敛到中线，正面读成「倒三角围脖」。断言站立稳态下
    /// 每截残肢横向偏向自己肩侧、两残端不并拢、整体垂在胸下。
    /// 消融红灯：去掉断臂分支的肩侧垂位弹簧 → 本门必红（side≈0、split≈0）。</summary>
    private static (bool, string) CheckArmStump()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 1; i <= 300; i++)
        {
            Tick(rat, terrain, ref tick);   // 站立稳态（无移动意图）
        }
        rat.Sever(RatFiendLimbId.ArmLeft);
        rat.Sever(RatFiendLimbId.ArmRight);
        for (int i = 1; i <= 400; i++)
        {
            Tick(rat, terrain, ref tick);
        }

        Vector3 right = rat.Facing.Cross(Vector3.Up).Normalized();
        float side0 = (rat.Arms[0].Pos - rat.Chest.Pos).Dot(right) * rat.Arms[0].Side;
        float side1 = (rat.Arms[1].Pos - rat.Chest.Pos).Dot(right) * rat.Arms[1].Side;
        float split = (rat.Arms[0].Pos - rat.Arms[1].Pos).Length();
        float drop0 = (rat.Arms[0].Pos - rat.Chest.Pos).Dot(Vector3.Up);
        float drop1 = (rat.Arms[1].Pos - rat.Chest.Pos).Dot(Vector3.Up);
        // 静定断言：终帧不仅位置对、还得是收敛定点（残摆 ≥3cm 振幅的峰值速度即超阈，
        // 实测定点每 tick 位移 <1e-4，50× 裕量）——焊死「振荡恰好在对的相位采样」的缺口。
        float still0 = rat.Arms[0].Vel.Length();
        float still1 = rat.Arms[1].Vel.Length();

        bool ok = side0 > 0.08f && side1 > 0.08f && split > 0.18f
            && drop0 < -0.3f && drop1 < -0.3f
            && still0 < 0.005f && still1 < 0.005f;
        return (ok,
            $"side=({side0:F3}m, {side1:F3}m)（各 >0.08 且偏向本肩侧） " +
            $"split={split:F3}m（>0.18 不并拢） drop=({drop0:F3}m, {drop1:F3}m)（各 <-0.3 垂在胸下） " +
            $"still=({still0:F4}, {still1:F4})m/tick（各 <0.005 静止收敛）");
    }

    // ================================================================ 断腿爬行

    /// <summary>断腿 → 摔倒涌现（重力回归、胸高跌落）→ 爬行推进（双手撑地 + 存活腿辅助蹬地）。</summary>
    private static (bool, string) CheckSeverLegCrawl()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(-30f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 1; i <= 300; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 0.7f;
            Tick(rat, terrain, ref tick);
        }
        float standChestY = rat.Chest.Pos.Y;
        rat.Sever(RatFiendLimbId.LegRight);
        float severX = rat.Chest.Pos.X;

        int fellTick = -1;
        bool gravityAlways = true;
        int maxGrips = 0;
        int legStepCycles = 0;
        bool prevGripping = rat.Legs[0].Gripping;
        for (int i = 1; i <= 900; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 1f;
            Tick(rat, terrain, ref tick);
            gravityAlways &= rat.ApplyGravity;
            maxGrips = Math.Max(maxGrips, rat.CrawlGripCount);
            if (fellTick < 0 && rat.Chest.Pos.Y < standChestY * 0.55f)
            {
                fellTick = i;
            }
            bool gripping = rat.Legs[0].Gripping;
            if (gripping && !prevGripping)
            {
                legStepCycles++;
            }
            prevGripping = gripping;
        }
        float mileage = rat.Chest.Pos.X - severX;
        bool pairCleared = rat.Legs[0].Pair is null;
        bool ok = fellTick is > 0 and <= 100 && gravityAlways && mileage > 2.5f
            && maxGrips >= 3 && legStepCycles >= 3 && pairCleared && IsFinite(rat);
        return (ok,
            $"fellTick={fellTick}（≤100） gravityAlways={gravityAlways} " +
            $"mileage={mileage:F2}m（≥2.5） maxGrips={maxGrips}（≥3） " +
            $"legStepCycles={legStepCycles}（≥3） pairCleared={pairCleared} finite={IsFinite(rat)}");
    }

    // ================================================================ 爬行调头

    /// <summary>爬行中目标反向的调头门（修复轮新增：此前无断言覆盖，「头从裆下穿过」
    /// 的根因 Facing 瞬时置向就藏在这条路径里）：断腿爬稳后 MoveDir 反向——
    /// ① Facing 每 tick 回转 ≤ CrawlTurnRatePerTick（限速真的在管）；
    /// ② 调头收敛（Facing·反向 ≥ 0.95）且身体真的往回爬；
    /// ③ 全程头髋水平距离不塌进穿插带（头绕外弧扫过去，不从裆下穿）；
    /// ④ 消融红灯：限速调成 π/tick（= 旧的瞬时置向）时头髋最小距必然塌掉。</summary>
    private static (bool, string) CheckCrawlTurn()
    {
        static Vector3 SafeXZNormalized(Vector3 v)
        {
            var flat = new Vector3(v.X, 0f, v.Z);
            return flat.LengthSquared() > 1e-8f ? flat.Normalized() : Vector3.Right;
        }

        (float MinHeadHips, float MaxTurnStep, int ConvergeTick, float ReturnMileage,
            float EndSpineAlign, bool Finite) Run(float turnRate)
        {
            RatFiendParams p = RatFiendFactory.Gaunt();
            p.CrawlTurnRatePerTick = turnRate;
            var terrain = FlatFloor();
            var rat = RatFiendFactory.CreateController(
                new Vector3(-30f, 0.5f, 0f), Vector3.Right, p);
            long tick = 0;
            for (int i = 1; i <= 300; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 0.7f;
                Tick(rat, terrain, ref tick);
            }
            rat.Sever(RatFiendLimbId.LegRight);
            for (int i = 1; i <= 400; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 1f;
                Tick(rat, terrain, ref tick);
            }

            float turnStartX = rat.Chest.Pos.X;
            float minHeadHips = float.MaxValue;
            float maxTurnStep = 0f;
            int convergeTick = -1;
            Vector3 prevFacing = rat.Facing;
            for (int i = 1; i <= 700; i++)
            {
                rat.MoveDir = Vector3.Left;
                rat.RunSpeed = 1f;
                Tick(rat, terrain, ref tick);
                maxTurnStep = Math.Max(maxTurnStep, prevFacing.AngleTo(rat.Facing));
                prevFacing = rat.Facing;
                Vector3 d = rat.Head.Pos - rat.Hips.Pos;
                minHeadHips = Math.Min(minHeadHips, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
                if (convergeTick < 0 && rat.Facing.Dot(Vector3.Left) >= 0.95f)
                {
                    convergeTick = i;
                }
            }
            Vector3 spine = rat.Chest.Pos - rat.Hips.Pos;
            float endSpineAlign = SafeXZNormalized(spine).Dot(Vector3.Left);
            return (minHeadHips, maxTurnStep, convergeTick,
                turnStartX - rat.Chest.Pos.X, endSpineAlign, IsFinite(rat));
        }

        var fix = Run(RatFiendFactory.Gaunt().CrawlTurnRatePerTick);
        var snap = Run(MathF.PI);
        bool ok = fix.MaxTurnStep <= RatFiendFactory.Gaunt().CrawlTurnRatePerTick + 1e-3f
            && fix.ConvergeTick is > 0 and <= 400
            && fix.ReturnMileage > 1.5f
            && fix.MinHeadHips >= 0.32f
            && fix.EndSpineAlign >= 0.9f
            && snap.MinHeadHips < 0.32f
            && fix.Finite;
        return (ok,
            $"maxTurnStep={fix.MaxTurnStep:F4}rad（≤限速+1e-3） " +
            $"convergeTick={fix.ConvergeTick}（≤400） returnMileage={fix.ReturnMileage:F2}m（≥1.5） " +
            $"minHeadHips={fix.MinHeadHips:F3}m（≥0.32） endSpineAlign={fix.EndSpineAlign:F2}（≥0.9，身体轴真的转过来） " +
            $"ablatedMinHeadHips={snap.MinHeadHips:F3}m（<0.32，瞬时置向红灯） finite={fix.Finite}");
    }

    // ================================================================ 爬台阶

    /// <summary>爬行翻越低台阶门（修复轮新增：此前爬行推进纯水平 + 滑墙把意图消零，
    /// 0.3m 台阶就能卡死爬行怪）：断腿爬行撞上 0.3m 台阶——
    /// ① 爬上去（末态胸在台面上方、里程越过台阶沿）；② 直线爬（不被滑墙滑歪）；
    /// ③ 消融红灯：CrawlClimbGain=0（撤掉手拉体升）时必卡在台阶前。</summary>
    private static (bool, string) CheckCrawlStep()
    {
        const float StepX = 3f;
        const float StepTop = 0.3f;

        (float EndX, float EndChestY, float EndZDrift, bool Finite) Run(float climbGain)
        {
            RatFiendParams p = RatFiendFactory.Gaunt();
            p.CrawlClimbGain = climbGain;
            var terrain = FlatFloor()
                .AddBox(new Vector3(StepX, -1f, -8f), new Vector3(40f, StepTop, 8f), 7UL);
            var rat = RatFiendFactory.CreateController(
                new Vector3(-5f, 0.5f, 0f), Vector3.Right, p);
            long tick = 0;
            for (int i = 1; i <= 100; i++)
            {
                rat.MoveDir = Vector3.Zero;
                rat.RunSpeed = 0f;
                Tick(rat, terrain, ref tick);
            }
            rat.Sever(RatFiendLimbId.LegRight);
            for (int i = 1; i <= 900; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 1f;
                Tick(rat, terrain, ref tick);
            }
            return (rat.Chest.Pos.X, rat.Chest.Pos.Y,
                MathF.Abs(rat.Chest.Pos.Z), IsFinite(rat));
        }

        var fix = Run(RatFiendFactory.Gaunt().CrawlClimbGain);
        var ablated = Run(0f);
        bool ok = fix.EndX > StepX + 3f && fix.EndChestY > StepTop + 0.05f
            && fix.EndZDrift < 1.2f && fix.Finite
            && ablated.EndX < StepX + 0.5f && ablated.EndChestY < StepTop + 0.05f;
        return (ok,
            $"endX={fix.EndX:F2}（>{StepX + 3f:F0}，翻过台阶继续爬） " +
            $"endChestY={fix.EndChestY:F2}（>{StepTop + 0.05f:F2}，胸落在台面上） " +
            $"zDrift={fix.EndZDrift:F2}（<1.2，未被滑墙滑歪） finite={fix.Finite} " +
            $"ablatedEndX={ablated.EndX:F2}（<{StepX + 0.5f:F1}，撤掉拽升必卡台阶前红灯） " +
            $"ablatedChestY={ablated.EndChestY:F2}（<{StepTop + 0.05f:F2}）");
    }

    // ================================================================ 断肢里程单调

    /// <summary>「推进 ∝ 抓地肢体数」的行为门：同一场景逐级断肢（可用肢 3 → 2 → 1），
    /// 固定窗口爬行里程严格递减。消融对照：EnableCrawlGripScaling=false（引擎改常数）时
    /// 里程差距必然塌掉——推进不再读抓地计数。</summary>
    private static (bool, string) CheckSeverMonotone()
    {
        float CrawlMileage(bool severRightLeg, bool severLeftArm, bool scaling)
        {
            var terrain = FlatFloor();
            RatFiendLocomotionController rat = NewRat(new Vector3(-30f, 0.5f, 0f), Vector3.Right);
            rat.EnableCrawlGripScaling = scaling;
            long tick = 0;
            for (int i = 0; i < 100; i++)
            {
                rat.MoveDir = Vector3.Zero;
                rat.RunSpeed = 0f;
                Tick(rat, terrain, ref tick);
            }
            rat.Sever(RatFiendLimbId.LegLeft);
            if (severRightLeg)
            {
                rat.Sever(RatFiendLimbId.LegRight);
            }
            if (severLeftArm)
            {
                rat.Sever(RatFiendLimbId.ArmLeft);
            }
            float windowStartX = 0f;
            for (int i = 1; i <= 900; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = 1f;
                Tick(rat, terrain, ref tick);
                if (i == 300)
                {
                    windowStartX = rat.Chest.Pos.X;
                }
            }
            return rat.Chest.Pos.X - windowStartX;
        }

        float three = CrawlMileage(false, false, true);
        float two = CrawlMileage(true, false, true);
        float one = CrawlMileage(true, true, true);
        float spread = three - one;
        float threeA = CrawlMileage(false, false, false);
        float twoA = CrawlMileage(true, false, false);
        float oneA = CrawlMileage(true, true, false);
        float spreadAblated = threeA - oneA;
        bool mono = three > two + 0.3f && two > one + 0.2f;
        bool ablatedFlat = spreadAblated < spread * 0.5f;
        bool ok = mono && spread > 0.8f && ablatedFlat;
        return (ok,
            $"mileage 3/2/1肢=[{three:F2},{two:F2},{one:F2}]m（严格递减 {mono}，" +
            $"极差 {spread:F2}≥0.8） 消融=[{threeA:F2},{twoA:F2},{oneA:F2}]" +
            $"（极差 {spreadAblated:F2}<{spread * 0.5f:F2}，红灯 {ablatedFlat}）");
    }

    // ================================================================ 全断蠕动

    /// <summary>四肢全断 + 满油门：蠕动内力偶只能靠摩擦整流出近零位移——看着挣扎、走不了，
    /// 且全程有限不逃逸。</summary>
    private static (bool, string) CheckSeverAll()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 100; i++)
        {
            rat.MoveDir = Vector3.Zero;
            rat.RunSpeed = 0f;
            Tick(rat, terrain, ref tick);
        }
        rat.Sever(RatFiendLimbId.LegLeft);
        rat.Sever(RatFiendLimbId.LegRight);
        rat.Sever(RatFiendLimbId.ArmLeft);
        rat.Sever(RatFiendLimbId.ArmRight);
        for (int i = 0; i < 200; i++)
        {
            rat.MoveDir = Vector3.Zero;
            rat.RunSpeed = 0f;
            Tick(rat, terrain, ref tick);
        }
        Vector3 settled = rat.Chest.Pos;
        int zeroGrips = 0;
        for (int i = 1; i <= 2000; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 1f;
            Tick(rat, terrain, ref tick);
            if (rat.CrawlGripCount == 0)
            {
                zeroGrips++;
            }
        }
        Vector3 d = rat.Chest.Pos - settled;
        float horizontal = new Vector3(d.X, 0f, d.Z).Length();
        bool ok = horizontal < 0.5f && zeroGrips > 1900 && IsFinite(rat);
        return (ok,
            $"displacement={horizontal:F3}m（<0.5） zeroGripTicks={zeroGrips}/2000 " +
            $"finite={IsFinite(rat)}");
    }

    // ================================================================ 攻击接缝

    /// <summary>GrabTarget/MouthDrive/HandsOnTarget 最小接缝：可及目标双手到位、
    /// MouthDrive 拉满嘴、清目标后回垂手；不可及目标永不误报「抓住」。</summary>
    private static (bool, string) CheckAttack()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 100; i++)
        {
            rat.MoveDir = Vector3.Zero;
            rat.RunSpeed = 0f;
            Tick(rat, terrain, ref tick);
        }

        Vector3 target = rat.Chest.Pos + rat.Facing * 0.9f;
        int reachTick = -1;
        for (int i = 1; i <= 120; i++)
        {
            rat.GrabTarget = target;
            Tick(rat, terrain, ref tick);
            if (reachTick < 0 && rat.HandsOnTarget[0] && rat.HandsOnTarget[1])
            {
                reachTick = i;
            }
        }
        bool reached = reachTick is > 0 and <= 80;

        rat.MouthDrive = 1f;
        bool mouthFull = rat.MouthOpen > 0.95f;
        rat.MouthDrive = 0f;

        rat.GrabTarget = null;
        bool releasedClean = true;
        for (int i = 1; i <= 120; i++)
        {
            Tick(rat, terrain, ref tick);
            releasedClean &= !rat.HandsOnTarget[0] && !rat.HandsOnTarget[1];
        }
        bool mouthBack = rat.MouthOpen < 0.2f;

        // 不可及目标（3m 远 > 臂长 1.15）：手被钳在可及球面上，永不误报抓住。
        Vector3 far = rat.Chest.Pos + rat.Facing * 3f;
        bool neverFar = true;
        float maxReachLen = 0f;
        for (int i = 1; i <= 200; i++)
        {
            rat.GrabTarget = far;
            Tick(rat, terrain, ref tick);
            neverFar &= !rat.HandsOnTarget[0] && !rat.HandsOnTarget[1];
            foreach (RatArm arm in rat.Arms)
            {
                maxReachLen = MathF.Max(maxReachLen,
                    (arm.Pos - rat.Chest.Pos).Length() - arm.EffectiveLength);
            }
        }
        rat.GrabTarget = null;

        bool ok = reached && mouthFull && releasedClean && mouthBack && neverFar
            && maxReachLen < 0.05f;
        return (ok,
            $"reachTick={reachTick}（≤80） mouthFull={mouthFull} released={releasedClean} " +
            $"mouthBack={mouthBack} neverFarGrab={neverFar} overreach={maxReachLen:F3}m（<0.05）");
    }

    /// <summary>R19 抓取分手（opt-in `GrabHandSpread`）：默认 0 时两手追同一目标点**逐位重合**
    /// （渲染两套手爪叠置 z-fighting 读成「手在颤抖」的根因——重合本身要钉死当基线事实）；
    /// 设 0.09 后双手横向对称分开 ≈2×0.09×(臂长钳制径向缩放 0.997)≈0.179m 且抓住判定不受
    /// 影响。守的是：后续重构（改 right 推导 / 钳制挪到偏移后）把分开压塌回重合，或把偏移
    /// 大到破坏 HandsOnTarget——矩阵与 DET 哈希对这个 opt-in 路径全盲。</summary>
    private static (bool, string) CheckGrabSpread()
    {
        var terrain = FlatFloor();

        // 档 A：默认 spread=0 —— 双手逐位重合 + 抓住判定成立（旧行为基线）。
        RatFiendLocomotionController rat = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 100; i++)
        {
            Tick(rat, terrain, ref tick);
        }
        Vector3 target = rat.Chest.Pos + rat.Facing * 0.9f;
        for (int i = 0; i < 80; i++)
        {
            rat.GrabTarget = target;
            Tick(rat, terrain, ref tick);
        }
        float sep0 = (rat.Arms[0].Pos - rat.Arms[1].Pos).Length();
        bool coincident = sep0 < 1e-4f;
        bool held0 = rat.HandsOnTarget[0] && rat.HandsOnTarget[1];

        // 档 B：spread=0.09 —— 双手分开落在 [0.15, 0.21]，抓住判定仍双双成立。
        RatFiendLocomotionController spread = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        spread.GrabHandSpread = 0.09f;
        tick = 0;
        for (int i = 0; i < 100; i++)
        {
            Tick(spread, terrain, ref tick);
        }
        Vector3 target2 = spread.Chest.Pos + spread.Facing * 0.9f;
        for (int i = 0; i < 80; i++)
        {
            spread.GrabTarget = target2;
            Tick(spread, terrain, ref tick);
        }
        float sep1 = (spread.Arms[0].Pos - spread.Arms[1].Pos).Length();
        bool separated = sep1 is > 0.15f and < 0.21f;
        bool held1 = spread.HandsOnTarget[0] && spread.HandsOnTarget[1];

        bool ok = coincident && held0 && separated && held1
            && IsFinite(rat) && IsFinite(spread);
        return (ok,
            $"sep(spread=0)={sep0:E2}m（<1e-4 重合） held0={held0} " +
            $"sep(spread=0.09)={sep1:F3}m（0.15~0.21） held1={held1}");
    }

    /// <summary>R19 朝向冲击（opt-in `ImpactTwist`）：拧转角度/轴向符号、无意图时保持拧姿、
    /// 有意图时 SlewFacing 按转速限甩回、退化轴回退、连拧不丢单位长度。守的是：Rotated 轴向
    /// 符号约定或 SlewFacing 恢复被重构改翻——竞技场「打右臂右肩后拧」的转矩语义建立在
    /// 这个符号之上，矩阵与 DET 哈希对它全盲。</summary>
    private static (bool, string) CheckImpactTwist()
    {
        var terrain = FlatFloor();
        RatFiendLocomotionController rat = NewRat(new Vector3(0f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 100; i++)
        {
            Tick(rat, terrain, ref tick);
        }

        // 拧 +16°：与 Godot Rotated 同式的期望向量逐位对比方向（点积门）。
        float yaw = MathF.PI * 16f / 180f;
        Vector3 before = rat.Facing;
        rat.ImpactTwist(yaw, Vector3.Up);
        Vector3 expected = before.Rotated(Vector3.Up, yaw);
        bool twisted = rat.Facing.Dot(expected) > 0.9999f;

        // 无移动意图：拧姿逐 tick 保持（吃痛回神窗语义的内核侧依据）。
        for (int i = 0; i < 10; i++)
        {
            Tick(rat, terrain, ref tick);
        }
        bool held = rat.Facing.Dot(expected) > 0.999f;

        // 恢复意图：SlewFacing（0.22 rad/tick）3 tick 内甩回意图方向。
        bool recovered = false;
        for (int i = 0; i < 3; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 1f;
            Tick(rat, terrain, ref tick);
            recovered |= rat.Facing.Dot(Vector3.Right) > 0.999f;
        }

        // 退化轴（零向量 up → 回退世界上）与连拧 200 次的单位长度守恒。
        rat.ImpactTwist(0.5f, Vector3.Zero);
        bool degenerateSafe = IsFinite(rat) && MathF.Abs(rat.Facing.Length() - 1f) < 1e-4f;
        for (int i = 0; i < 200; i++)
        {
            rat.ImpactTwist(0.3f, Vector3.Up);
        }
        bool unitKept = MathF.Abs(rat.Facing.Length() - 1f) < 1e-4f && IsFinite(rat);

        bool ok = twisted && held && recovered && degenerateSafe && unitKept;
        return (ok,
            $"twist16°={twisted} heldNoIntent={held} recovered≤3tick={recovered} " +
            $"degenerateAxis={degenerateSafe} unitAfter200={unitKept}");
    }

    // ================================================================ 生命周期

    private static (bool, string) CheckLifecycle()
    {
        // 无限半空间地板：Shift(+512)/Teleport(−500) 后必须**仍站在地上**——用有限盒地板时
        // 身体被挪出地板边缘，「续走/击飞恢复」两个门靠空中漂移通过（推进无 Grounded 门，
        // 失重巡航速度照常累积里程）——评审修复轮定性的假绿。
        var terrain = new BoxRoomTerrain().AddHalfSpace(Vector3.Zero, Vector3.Up, 1UL);
        RatFiendLocomotionController rat = NewRat(new Vector3(-10f, 0.5f, 0f), Vector3.Right);
        long tick = 0;
        for (int i = 0; i < 300; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 0.7f;
            Tick(rat, terrain, ref tick);
        }

        // Shift：全部世界坐标逐字段精确平移（含直喂/抓取目标与断肢残肢）。
        rat.MoveTarget = rat.Chest.Pos + new Vector3(5f, 0f, 0f);
        rat.GrabTarget = rat.Chest.Pos + new Vector3(1f, 0f, 0f);
        var delta = new Vector3(512f, 0f, 512f);
        Vector3[] chunkBefore = { rat.Chest.Pos, rat.Hips.Pos, rat.Head.Pos };
        Vector3[] legBefore = { rat.Legs[0].Pos, rat.Legs[1].Pos };
        Vector3[] armBefore = { rat.Arms[0].Pos, rat.Arms[1].Pos };
        Vector3 moveBefore = rat.MoveTarget.Value;
        Vector3 grabBefore = rat.GrabTarget.Value;
        rat.Shift(delta);
        bool shiftExact = rat.Chest.Pos == chunkBefore[0] + delta
            && rat.Hips.Pos == chunkBefore[1] + delta
            && rat.Head.Pos == chunkBefore[2] + delta
            && rat.Legs[0].Pos == legBefore[0] + delta
            && rat.Legs[1].Pos == legBefore[1] + delta
            && rat.Arms[0].Pos == armBefore[0] + delta
            && rat.Arms[1].Pos == armBefore[1] + delta
            && rat.MoveTarget == moveBefore + delta
            && rat.GrabTarget == grabBefore + delta;
        rat.GrabTarget = null;
        rat.MoveTarget = null;
        // Shift 后地形没跟着动（半空间地板无限大，y 不变）——继续走。
        // 「续走」必须以落地为据（Grounded + 失重伺服态），不许空中漂移里程冒充。
        float xAfterShift = rat.Chest.Pos.X;
        for (int i = 0; i < 300; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 0.7f;
            Tick(rat, terrain, ref tick);
        }
        bool resumed = rat.Chest.Pos.X - xAfterShift > 8f && rat.Grounded && !rat.ApplyGravity;

        // Teleport：作废位置态记忆。
        rat.MoveTarget = rat.Chest.Pos + new Vector3(3f, 0f, 0f);
        rat.GrabTarget = rat.Chest.Pos + new Vector3(1f, 0f, 0f);
        rat.Teleport(new Vector3(-500f, 0f, -500f));
        bool teleportCleared = rat.MoveTarget is null && rat.GrabTarget is null
            && rat.GroundedCounter == 0
            && rat.Arms[0].GrabPos is null && rat.Arms[1].GrabPos is null;

        // Launch：击飞坠落 → 落地爬起续走。
        for (int i = 0; i < 200; i++)
        {
            rat.MoveDir = Vector3.Zero;
            rat.RunSpeed = 0f;
            Tick(rat, terrain, ref tick);
        }
        rat.Launch(new Vector3(0.3f, 0.25f, 0f));
        bool sawFalling = false;
        for (int i = 0; i < 100; i++)
        {
            rat.MoveDir = Vector3.Zero;
            rat.RunSpeed = 0f;
            Tick(rat, terrain, ref tick);
            sawFalling |= rat.ApplyGravity;
        }
        float xBeforeResume = rat.Chest.Pos.X;
        for (int i = 0; i < 400; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 0.7f;
            Tick(rat, terrain, ref tick);
        }
        bool launchRecovered = sawFalling && rat.Chest.Pos.X - xBeforeResume > 10f
            && rat.Grounded && !rat.ApplyGravity;

        // 断肢态 Shift：残肢粒子随世界平移。
        rat.Sever(RatFiendLimbId.LegLeft);
        for (int i = 0; i < 200; i++)
        {
            rat.MoveDir = Vector3.Right;
            rat.RunSpeed = 1f;
            Tick(rat, terrain, ref tick);
        }
        Vector3 stumpBefore = rat.Legs[0].Pos;
        rat.Shift(delta);
        bool severedShift = rat.Legs[0].Pos == stumpBefore + delta;

        bool ok = shiftExact && resumed && teleportCleared && launchRecovered && severedShift;
        return (ok,
            $"shiftExact={shiftExact} resumed={resumed} teleportCleared={teleportCleared} " +
            $"launchRecovered={launchRecovered} severedShift={severedShift}");
    }

    // ================================================================ 查询预算

    private static (bool, string) CheckQueryBudget()
    {
        (long maxRays, long maxShapes, float avgRays) Measure(bool crawling)
        {
            var terrain = FlatFloor();
            RatFiendLocomotionController rat = NewRat(new Vector3(-30f, 0.5f, 0f), Vector3.Right);
            long tick = 0;
            for (int i = 0; i < 100; i++)
            {
                rat.MoveDir = Vector3.Zero;
                rat.RunSpeed = 0f;
                Tick(rat, terrain, ref tick);
            }
            if (crawling)
            {
                rat.Sever(RatFiendLimbId.LegLeft);
                for (int i = 0; i < 200; i++)
                {
                    rat.MoveDir = Vector3.Right;
                    rat.RunSpeed = 1f;
                    Tick(rat, terrain, ref tick);
                }
            }
            long maxRays = 0;
            long maxShapes = 0;
            long totalRays = 0;
            for (int i = 0; i < 300; i++)
            {
                rat.MoveDir = Vector3.Right;
                rat.RunSpeed = crawling ? 1f : 0.7f;
                long raysBefore = terrain.RayCount;
                long shapesBefore = terrain.ShapeQueryCount;
                Tick(rat, terrain, ref tick);
                long rays = terrain.RayCount - raysBefore;
                long shapes = terrain.ShapeQueryCount - shapesBefore;
                maxRays = Math.Max(maxRays, rays);
                maxShapes = Math.Max(maxShapes, shapes);
                totalRays += rays;
            }
            return (maxRays, maxShapes, totalRays / 300f);
        }

        var walk = Measure(false);
        var crawl = Measure(true);
        // shape 预算基线：3 chunk + 2 手 + 2 脚 = 7 个球查询/tick；断腿爬行时残肢 +1、存活脚 -1。
        bool ok = walk.maxRays <= 40 && walk.maxShapes <= 8
            && crawl.maxRays <= 40 && crawl.maxShapes <= 8;
        return (ok,
            $"walk rays avg={walk.avgRays:F1} max={walk.maxRays}（门 40） " +
            $"shapes max={walk.maxShapes}（门 8）；crawl rays avg={crawl.avgRays:F1} " +
            $"max={crawl.maxRays}（门 40） shapes max={crawl.maxShapes}（门 8）");
    }

    // ================================================================ 基础设施

    private static RatFiendLocomotionController NewRat(Vector3 origin, Vector3 forward) =>
        RatFiendFactory.CreateController(origin, forward, RatFiendFactory.Gaunt());

    private static BoxRoomTerrain FlatFloor() => new BoxRoomTerrain()
        .AddBox(new Vector3(-200f, -1f, -200f), new Vector3(200f, 0f, 200f), 1UL);

    private static void Tick(
        RatFiendLocomotionController rat, BoxRoomTerrain terrain, ref long tick)
    {
        tick++;
        rat.Tick(new TickContext(GravityPerTick, terrain, tick));
        float residual = terrain.MeasureResidualPenetration(rat.Body);
        if (residual > _maxResidualPenetration)
        {
            _maxResidualPenetration = residual;
            _maxPenetrationContext = $"{_currentCheck}@tick{tick}";
        }
    }

    private static bool IsFinite(RatFiendLocomotionController rat)
    {
        foreach (BodyChunk chunk in rat.Body.Chunks)
        {
            if (!chunk.Pos.IsFinite() || !chunk.LastPos.IsFinite() || !chunk.Vel.IsFinite())
            {
                return false;
            }
        }
        foreach (var leg in rat.Legs)
        {
            if (!leg.Pos.IsFinite() || !leg.Vel.IsFinite())
            {
                return false;
            }
        }
        foreach (RatArm arm in rat.Arms)
        {
            if (!arm.Pos.IsFinite() || !arm.Vel.IsFinite())
            {
                return false;
            }
        }
        return rat.Facing.IsFinite() && float.IsFinite(rat.Gait)
            && float.IsFinite(rat.CrawlFactor) && float.IsFinite(rat.Uprightness);
    }

    /// <summary>AABB 盒子 + 半空间的解析地形（DropBugSmoke 同款）。语义与 RaycastTerrainQuery
    /// 对齐：起点已在实体内 → HitFromInside（Point=起点、零法线）；SpherePenetration 给出
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

        /// <summary>tick 末残余穿透（米）。</summary>
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
