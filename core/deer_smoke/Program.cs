using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Deer;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.DeerSmoke;

/// <summary>
/// Deer 专项无引擎回归。所有地形均为解析夹具，固定 tick 直接调用纯内核；退出码是判定，
/// 指标只用于解释失败。命令行 --ablate=support|pair|hesitation|release|balance|stance|antler|bend
/// 会故意关闭对应机制，且必须让相应行为门返回非零，供矩阵脚本验证测试门自身有效。
/// </summary>
internal static class Program
{
    private const float TickDt = 0.025f;
    private static readonly Vector3 GravityPerTick =
        new(0f, -36f * TickDt * TickDt, 0f);

    // 全部行为门、40/400Hz 等价与微扰分叉实跑通过后钉定；更新必须附带机制级审计。
    private const ulong ExpectedHash = 0x80249FD24361B9C8UL;

    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        if (!TryParseAblation(args, out Ablation requested, out string parseError))
        {
            Console.Error.WriteLine($"[DEER-CORE-ARGS] FAIL {parseError}");
            return 2;
        }
        if (requested != Ablation.None)
        {
            return RunIntentionalAblation(requested);
        }

        var failures = new List<string>();
        Check("FACTORY", CheckFactory, failures);
        Check("SUPPORT-GEOMETRY", CheckSupportGeometry, failures);
        Check("FORCE-DISTRIBUTION", CheckForceDistribution, failures);
        Check("ALLOC", CheckSteadyStateAllocations, failures);

        DeterminismResult run1 = RunDeterminism(0f, 40);
        DeterminismResult run2 = RunDeterminism(0f, 40);
        DeterminismResult fastHost = RunDeterminism(0f, 400);
        DeterminismResult perturb = RunDeterminism(1e-4f, 40);
        bool hashPinned = ExpectedHash == 0UL || run1.Hash == ExpectedHash;
        Report(
            "DET",
            run1.Hash == run2.Hash && run1.FixedTicks == 900 && run1.Finite && hashPinned
                && run1.MaxBodyDeviation < 0.18f && run1.MaxLegDeviation < 0.25f,
            $"run1={run1.Hash:X16} run2={run2.Hash:X16} expected=" +
            (ExpectedHash == 0UL ? "UNPINNED" : $"{ExpectedHash:X16}") +
            $" ticks={run1.FixedTicks} finite={run1.Finite} maxBodyDev={run1.MaxBodyDeviation:F5}m " +
            $"maxLegDev={run1.MaxLegDeviation:F5}m",
            failures);
        Report(
            "HOST-RATE",
            fastHost.FixedTicks == run1.FixedTicks && fastHost.Hash == run1.Hash,
            $"40Hz={run1.Hash:X16}/{run1.FixedTicks} 400Hz={fastHost.Hash:X16}/{fastHost.FixedTicks}",
            failures);
        Report(
            "PERTURB",
            perturb.Hash != run1.Hash && perturb.Finite,
            $"base={run1.Hash:X16} perturb={perturb.Hash:X16}",
            failures);

        FlatResult flat = RunFlat(Ablation.None);
        Report("FLAT", flat.SupportGate,
            $"gravity={flat.GravityScale:F1}/always={flat.GravityAlwaysOne} " +
            $"moveHeight={flat.AverageHeight:F3}/{flat.AverageDesiredHeight:F3}m " +
            $"restHeight={flat.RestHeight:F3}/{flat.RestDesiredHeight:F3}m " +
            $"support(move/rest)={flat.AverageSupport:F3}/{flat.RestSupport:F3} " +
            $"grips(moveRange/restAvg)={flat.MinimumGrips}..{flat.MaximumGrips}/{flat.RestAverageGrips:F2} " +
            $"contact(move/rest/allMove)={flat.BodyContactRatio:P1}/{flat.RestBodyContactRatio:P1}/" +
            $"{flat.AllChunkContactRatio:P1} " +
            $"minClearance={flat.MinimumBodyClearance:F3}m " +
            $"travel={flat.Travel:F3}m", failures);
        Report("STANCE", flat.StanceGate,
            $"move/rest={flat.AverageHeight:F3}/{flat.RestHeight:F3}m " +
            $"reachLimit={flat.AverageReachLimit:F3}/{flat.RestReachLimit:F3}m " +
            $"restFootReachMax={flat.RestMaximumFootReach:F3}m " +
            $"clearance={flat.MinimumBodyClearance:F3}/{flat.RestMinimumBodyClearance:F3}m",
            failures);
        Report("ANTLER-POSTURE", flat.AntlerGate,
            $"upMin={flat.MinimumAntlerUp:F3} headUp/forward=" +
            $"{flat.MinimumHeadUp:F3}/{flat.MinimumHeadForward:F3} " +
            $"trunkIntrusion={flat.MaximumAntlerTrunkIntrusionRatio:F3}", failures);
        Report("GAIT", flat.GaitGate,
            $"landings={flat.TotalLandings} minPerLeg={flat.MinimumLandingsPerLeg} " +
            $"minStride={flat.MinimumStride:F3}m pairAir={flat.SamePairAirborneTicks} " +
            $"minPoleDot={flat.MinimumPoleDot:F4} maxLean={flat.MaximumLean:F2}deg " +
            $"minMargin={flat.MinimumSupportMargin:F3}m", failures);
        Check("BEND-FRAME", CheckBendFrameTransport, failures);
        Check("BEND-REVERSAL", CheckBendReversal, failures);
        Check("REST-RETRACTION", CheckRestRetraction, failures);

        Check("HYSTERESIS", CheckCandidateHysteresis, failures);
        Check("PAIR-EMERGENCY", CheckEmergencyPairPlantGate, failures);
        Check("RELEASE", CheckOverreachAndOcclusionRelease, failures);
        Check("DRAG", CheckReachLimitDrag, failures);
        Check("HESITATION", CheckHesitation, failures);
        Check("BALANCE", CheckBalanceRecovery, failures);
        Check("WEAK-GRIP", CheckWeakGripReleaseGate, failures);
        Check("COURSE", CheckSlopeAndSteps, failures);
        Check("TARGET", CheckMoveTarget, failures);
        Check("LAUNCH-TARGET", CheckLaunchTargetReevaluation, failures);
        Check("DEEP-REST-WAKE", CheckDeepRestWake, failures);
        Check("LIFECYCLE", CheckLifecycle, failures);
        Check("HASH-FORK", CheckHashStateForks, failures);
        Check("ABLATION", CheckAblations, failures);

        bool pass = failures.Count == 0;
        Console.WriteLine(pass
            ? "[DEER-CORE-SMOKE] PASS：拓扑、确定性、恒重力支撑、多节腿步态、地形与生命周期均通过"
            : $"[DEER-CORE-SMOKE] FAIL：{string.Join("；", failures)}");
        return pass ? 0 : 1;
    }

    private static void Check(
        string name,
        Func<(bool Ok, string Message)> test,
        List<string> failures)
    {
        try
        {
            (bool ok, string message) = test();
            Report(name, ok, message, failures);
        }
        catch (Exception ex)
        {
            Report(name, false, $"{ex.GetType().Name}: {ex.Message}", failures);
        }
    }

    private static void Report(
        string name,
        bool ok,
        string message,
        List<string> failures)
    {
        Console.WriteLine($"[DEER-CORE-{name}] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok)
        {
            failures.Add(name);
        }
    }

    private static (bool, string) CheckFactory()
    {
        DeerParams[] presets = DeerFactory.AllPresets();
        string[] ids = presets.Select(p => p.StableId).ToArray();
        bool idsOk = ids.SequenceEqual(new[]
        {
            DeerFactory.OriginalId,
            DeerFactory.CompactId,
            DeerFactory.StriderId,
        });
        bool uniqueSnapshots = !ReferenceEquals(presets[0], DeerFactory.AllPresets()[0])
            && !ReferenceEquals(presets[0].LegSlots, DeerFactory.AllPresets()[0].LegSlots)
            && !ReferenceEquals(presets[0].TrunkSegments, DeerFactory.AllPresets()[0].TrunkSegments);

        bool topology = true;
        int[] expectedSegments = { 6, 6, 8 };
        for (int i = 0; i < presets.Length; i++)
        {
            DeerRig deer = NewDeer(Vector3.Zero, presets[i]);
            topology &= deer.Body.Chunks.Count == presets[i].TrunkSegments.Length + 2
                && deer.Trunk.Count == presets[i].TrunkSegments.Length
                && deer.Legs.Count == 4
                && deer.Legs.All(leg => leg.Segments.Length == expectedSegments[i])
                && Math.Abs(deer.Body.GravityScale - 1f) < 1e-7f
                && ReferenceEquals(deer.Head.RotationChunk, deer.Antler)
                && ReferenceEquals(deer.Antler.RotationChunk, deer.Head);
            foreach (DeerLeg leg in deer.Legs)
            {
                topology &= leg.Mate is not null
                    && leg.Mate.PairIndex == leg.PairIndex
                    && leg.Mate.Side == -leg.Side;
            }
            for (int c = 0; c < deer.Body.Connections.Count; c++)
            {
                ChunkConnection connection = deer.Body.Connections[c];
                topology &= connection.RestLength < connection.A.Radius + connection.B.Radius;
                if ((ReferenceEquals(connection.A, deer.Head) && ReferenceEquals(connection.B, deer.Antler))
                    || (ReferenceEquals(connection.B, deer.Head) && ReferenceEquals(connection.A, deer.Antler)))
                {
                    // DLL 的 deerAntlers dominance=0.5 分支明确让鹿角连接不反拖头。
                    topology &= ReferenceEquals(connection.A, deer.Head)
                        ? connection.WeightA == 0f
                        : connection.WeightA == 1f;
                }
            }
        }

        DeerRig original = NewDeer(Vector3.Zero, presets[0]);
        float[] expectedRadii = { 0.830563f, 0.745775f, 0.614812f, 0.452582f };
        float[] expectedMasses = { 7.502312f, 6.552679f, 5.085896f, 3.268919f };
        bool originalDimensions = NearScalar(original.Head.Radius, 0.5625f)
            && NearScalar(original.Head.Mass, 3f)
            && NearScalar(original.Antler.Radius, 1.125f)
            && NearScalar(original.Antler.Mass, 0.5f)
            && original.Trunk.Select(chunk => chunk.Radius).SequenceEqual(expectedRadii)
            && original.Trunk.Select(chunk => chunk.Mass).SequenceEqual(expectedMasses)
            && original.Legs.All(leg => NearScalar(leg.MaxLength, 10f)
                && NearScalar(presets[0].LegSlots[leg.Index].InitialLength, 7.5f)
                && leg.Segments.Length == 6);

        bool birthPosture = true;
        foreach (DeerParams preset in presets)
        {
            DeerRig deer = NewDeer(Vector3.Zero, preset);
            float link = preset.HeadRadius + preset.AntlerRadius - preset.AntlerHeadOverlap;
            Vector3 antlerAxis = (deer.Antler.Pos - deer.Head.Pos) / link;
            Vector3 headAxis = (deer.Head.Pos - deer.Trunk[0].Pos).Normalized();
            birthPosture &= NearScalar(deer.Antler.Pos.DistanceTo(deer.Head.Pos), link, 1e-5f)
                && antlerAxis.Dot(Vector3.Up) >= 0.85f
                && antlerAxis.Dot(Vector3.Right) >= 0f
                && headAxis.Dot(Vector3.Up) > 0.5f
                && headAxis.Dot(Vector3.Right) > 0.3f;
            foreach (BodyChunk trunk in deer.Trunk)
            {
                float penetration = Math.Max(
                    0f, deer.Antler.Radius + trunk.Radius - deer.Antler.Pos.DistanceTo(trunk.Pos));
                birthPosture &= penetration / Math.Min(deer.Antler.Radius, trunk.Radius) <= 0.10f;
            }
        }

        bool lookup = presets.All(p => DeerFactory.ByStableId(p.StableId).StableId == p.StableId)
            && !DeerFactory.TryByStableId("deer/not-real", out _);
        bool unknownThrows = false;
        try
        {
            _ = DeerFactory.ByStableId("deer/not-real");
        }
        catch (ArgumentException)
        {
            unknownThrows = true;
        }

        return (
            idsOk && uniqueSnapshots && topology && originalDimensions && birthPosture
                && lookup && unknownThrows,
            $"presets=[{string.Join(',', ids)}] topology={topology} snapshots={uniqueSnapshots} " +
            $"dllDimensions={originalDimensions} birthPosture={birthPosture} " +
            $"unknownThrows={unknownThrows}");
    }

    /// <summary>
    /// 直接固定同一条腿的根足几何，隔离验证支撑贡献的两个独立自变量：直立度，以及地面
    /// 切平面内真正位于相对方向的另一只踩实脚。若只按抓地数给常量支撑，此门会变红。
    /// </summary>
    private static (bool, string) CheckSupportGeometry()
    {
        DeerParams parameters = DeerFactory.Original();
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        DeerLeg target = deer.Legs[0];
        DeerLeg opposite = deer.Legs[1];
        Vector3 worldUp = Vector3.Up;
        Vector3 lateral = deer.Controller.Right;
        float clearance = target.FootRadius + parameters.TerrainClearance;
        float legLength = Math.Min(2f, target.MaxLength * 0.35f);

        ClearSyntheticGrips(deer);
        Vector3 uprightCenter = target.Anchor.Pos - worldUp * legLength;
        SetSyntheticGrip(target, uprightCenter - worldUp * clearance,
            worldUp, uprightCenter);
        MeasureSupport(deer);
        float upright = target.SupportContribution;

        ClearSyntheticGrips(deer);
        Vector3 angledDirection = (lateral * 0.8f - worldUp * 0.6f).Normalized();
        Vector3 angledCenter = target.Anchor.Pos + angledDirection * legLength;
        Vector3 angledPoint = angledCenter - worldUp * clearance;
        SetSyntheticGrip(target, angledPoint, worldUp, angledCenter);
        MeasureSupport(deer);
        float angledAlone = target.SupportContribution;

        // MeasureSupport 的相对脚判定以第二个躯干节为参考点，并先投影到支撑切平面。
        // 将另一足的切向偏移精确设成目标腿的相反数，避免把“左右标签不同”误当成真展开。
        Vector3 reference = deer.Trunk[Math.Min(1, deer.Trunk.Count - 1)].Pos;
        Vector3 targetOffset = angledPoint - reference;
        targetOffset -= worldUp * targetOffset.Dot(worldUp);
        float surfaceHeight = angledPoint.Dot(worldUp);
        Vector3 referenceOnSurface = reference
            + worldUp * (surfaceHeight - reference.Dot(worldUp));
        Vector3 oppositePoint = referenceOnSurface - targetOffset;
        Vector3 oppositeCenter = oppositePoint + worldUp * clearance;
        SetSyntheticGrip(opposite, oppositePoint, worldUp, oppositeCenter);
        MeasureSupport(deer);
        float angledOpposed = target.SupportContribution;

        Vector3 otherOffset = oppositePoint - reference;
        otherOffset -= worldUp * otherOffset.Dot(worldUp);
        float tangentOpposition = targetOffset.LengthSquared() > 1e-8f
            && otherOffset.LengthSquared() > 1e-8f
                ? -targetOffset.Normalized().Dot(otherOffset.Normalized())
                : 0f;
        bool uprightGate = upright > angledAlone + 0.40f;
        bool oppositionGate = tangentOpposition > 0.999f
            && angledOpposed > angledAlone + 0.20f
            && angledOpposed > angledAlone * 1.4f;
        return (uprightGate && oppositionGate,
            $"upright/angled/opposed={upright:F3}/{angledAlone:F3}/{angledOpposed:F3} " +
            $"tangentOpposition={tangentOpposition:F4}");
    }

    private readonly record struct ForceProbe(
        float HighSupportVelocity,
        float TailSupportVelocity,
        float FrontDriveVelocity,
        float TailDriveVelocity,
        float AverageDriveVelocity,
        float MaximumVerticalVelocity);

    /// <summary>
    /// 直接调用 ApplyBodyForces，分别钉住躯干权重分配、抓地数/总支撑的联合推进，以及
    /// 零支撑绝不产生升力。每个输入组合使用全新身体，保证读到的只是本次速度注入。
    /// </summary>
    private static (bool, string) CheckForceDistribution()
    {
        DeerParams parameters = DeerFactory.Original();
        ForceProbe full = RunForceProbe(forceSupport: 1f, forcePlanted: 4);
        ForceProbe fewerPlanted = RunForceProbe(forceSupport: 1f, forcePlanted: 2);
        ForceProbe lowerSupport = RunForceProbe(forceSupport: 0.25f, forcePlanted: 4);
        ForceProbe bothLower = RunForceProbe(forceSupport: 0.25f, forcePlanted: 2);
        ForceProbe zeroSupport = RunForceProbe(forceSupport: 0f, forcePlanted: 4);

        float supportRatio = full.TailSupportVelocity > 1e-8f
            ? full.HighSupportVelocity / full.TailSupportVelocity
            : 0f;
        float expectedSupportRatio = parameters.TrunkSegments[1].SupportWeight
            / parameters.TrunkSegments[^1].SupportWeight;
        bool supportDistribution = full.HighSupportVelocity > full.TailSupportVelocity * 2.5f
            && NearScalar(supportRatio, expectedSupportRatio, 1e-4f);

        float driveRatio = full.TailDriveVelocity > 1e-8f
            ? full.FrontDriveVelocity / full.TailDriveVelocity
            : 0f;
        float expectedDriveRatio = parameters.TrunkSegments[0].DriveWeight
            / parameters.TrunkSegments[^1].DriveWeight;
        bool driveDistribution = full.FrontDriveVelocity > full.TailDriveVelocity * 3f
            && NearScalar(driveRatio, expectedDriveRatio, 1e-4f);

        const float driveMargin = 0.002f;
        bool jointDrive = full.AverageDriveVelocity > fewerPlanted.AverageDriveVelocity + driveMargin
            && full.AverageDriveVelocity > lowerSupport.AverageDriveVelocity + driveMargin
            && fewerPlanted.AverageDriveVelocity > bothLower.AverageDriveVelocity + driveMargin
            && lowerSupport.AverageDriveVelocity > bothLower.AverageDriveVelocity + driveMargin;
        bool zeroLift = zeroSupport.MaximumVerticalVelocity < 1e-7f;

        return (supportDistribution && driveDistribution && jointDrive && zeroLift,
            $"support(high/tail/ratio)={full.HighSupportVelocity:F5}/" +
            $"{full.TailSupportVelocity:F5}/{supportRatio:F3} " +
            $"drive(front/tail/ratio)={full.FrontDriveVelocity:F5}/" +
            $"{full.TailDriveVelocity:F5}/{driveRatio:F3} " +
            $"joint(full/planted/support/both)={full.AverageDriveVelocity:F5}/" +
            $"{fewerPlanted.AverageDriveVelocity:F5}/{lowerSupport.AverageDriveVelocity:F5}/" +
            $"{bothLower.AverageDriveVelocity:F5} zeroLift={zeroSupport.MaximumVerticalVelocity:E2}");
    }

    private static ForceProbe RunForceProbe(float forceSupport, int forcePlanted)
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Original());
        deer.RunSpeed = 1f;
        deer.MoveDir = Vector3.Right;
        deer.Controller.EnableHesitation = false;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            chunk.Vel = Vector3.Zero;
        }
        ClearSyntheticGrips(deer);

        var context = new TickContext(GravityPerTick, new PlaneTerrainQuery(0f), 1);
        InvokeNonPublic(deer.Controller, "ApplyBodyForces",
            context, Vector3.Right, Vector3.Up, forceSupport, forcePlanted);

        float averageDrive = 0f;
        float maximumVertical = 0f;
        int drivenChunks = 0;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            maximumVertical = Math.Max(maximumVertical, Math.Abs(chunk.Vel.Dot(Vector3.Up)));
            if (ReferenceEquals(chunk, deer.Antler))
            {
                continue;
            }
            averageDrive += chunk.Vel.Dot(Vector3.Right);
            drivenChunks++;
        }
        averageDrive /= Math.Max(drivenChunks, 1);
        return new ForceProbe(
            deer.Trunk[1].Vel.Dot(Vector3.Up),
            deer.Trunk[^1].Vel.Dot(Vector3.Up),
            deer.Trunk[0].Vel.Dot(Vector3.Right),
            deer.Trunk[^1].Vel.Dot(Vector3.Right),
            averageDrive,
            maximumVertical);
    }

    private static void ClearSyntheticGrips(DeerRig deer)
    {
        foreach (DeerLeg leg in deer.Legs)
        {
            SetMember(leg, nameof(DeerLeg.AttachedAtTip), false);
            SetMember(leg, nameof(DeerLeg.GripAge), 0);
            SetMember(leg, nameof(DeerLeg.SupportContribution), 0f);
        }
    }

    private static void SetSyntheticGrip(
        DeerLeg leg,
        Vector3 gripPoint,
        Vector3 gripNormal,
        Vector3 tipCenter)
    {
        SetMember(leg, nameof(DeerLeg.AttachedAtTip), true);
        SetMember(leg, nameof(DeerLeg.GripAge), leg.GripConfirmTicks);
        SetMember(leg, nameof(DeerLeg.GripPoint), gripPoint);
        SetMember(leg, nameof(DeerLeg.GripNormal), gripNormal);
        SetMember(leg, nameof(DeerLeg.GripColliderId), 1UL);
        leg.Tip.Pos = tipCenter;
        leg.Tip.LastPos = tipCenter;
        leg.Tip.Vel = Vector3.Zero;
    }

    private static void MeasureSupport(DeerRig deer) =>
        _ = InvokeNonPublic(deer.Controller, "MeasureSupport", Vector3.Up)
            ?? throw new InvalidOperationException("MeasureSupport returned null.");

    private readonly record struct DeterminismResult(
        ulong Hash,
        int FixedTicks,
        bool Finite,
        float MaxBodyDeviation,
        float MaxLegDeviation);

    private static DeterminismResult RunDeterminism(float perturb, int hostHz)
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Compact());
        if (perturb != 0f)
        {
            deer.Head.Pos.X += perturb;
            deer.Head.LastPos = deer.Head.Pos;
        }

        var terrain = new CourseTerrain();
        var hasher = new DeterminismHasher();
        long tick = 0;
        if (hostHz % 40 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostHz), hostHz,
                "Host rate must be an integer multiple of the 40Hz fixed clock.");
        }
        int hostSubstepsPerTick = hostHz / 40;
        int frames = hostSubstepsPerTick * 900;
        bool finite = true;
        float maxBodyDeviation = 0f;
        float maxLegDeviation = 0f;

        for (int frame = 0; frame < frames; frame++)
        {
            // 宿主可在两个 fixed tick 之间跑任意数量渲染/AI 子帧；内核只在第 N 个子帧推进。
            if ((frame + 1) % hostSubstepsPerTick == 0)
            {
                tick++;
                deer.MoveTarget = null;
                deer.RunSpeed = tick <= 760 ? 0.9f : 0f;
                deer.MoveDir = tick switch
                {
                    <= 280 => Vector3.Right,
                    <= 520 => new Vector3(0.92f, 0f, -0.38f).Normalized(),
                    <= 760 => new Vector3(0.82f, 0f, 0.57f).Normalized(),
                    _ => Vector3.Zero,
                };
                deer.Tick(terrain, tick);
                FoldDeer(hasher, deer);
                finite &= IsFinite(deer);
                maxBodyDeviation = Math.Max(maxBodyDeviation, deer.Body.CurrentMaxDeviation());
                foreach (DeerLeg leg in deer.Legs)
                {
                    maxLegDeviation = Math.Max(maxLegDeviation, leg.MaxConstraintError);
                }
            }
        }

        return new DeterminismResult(
            hasher.Value, checked((int)tick), finite, maxBodyDeviation, maxLegDeviation);
    }

    private readonly record struct FlatResult(
        bool SupportGate,
        bool StanceGate,
        bool AntlerGate,
        bool GaitGate,
        float GravityScale,
        bool GravityAlwaysOne,
        float AverageHeight,
        float AverageDesiredHeight,
        float RestHeight,
        float RestDesiredHeight,
        float AverageSupport,
        float RestSupport,
        float RestAverageGrips,
        float BodyContactRatio,
        float RestBodyContactRatio,
        float AllChunkContactRatio,
        float MinimumBodyClearance,
        float RestMinimumBodyClearance,
        float AverageReachLimit,
        float RestReachLimit,
        float RestMaximumFootReach,
        float MinimumAntlerUp,
        float MinimumHeadUp,
        float MinimumHeadForward,
        float MaximumAntlerTrunkIntrusionRatio,
        int MinimumGrips,
        int MaximumGrips,
        float Travel,
        int TotalLandings,
        int MinimumLandingsPerLeg,
        float MinimumStride,
        int SamePairAirborneTicks,
        float MinimumPoleDot,
        float MaximumLean,
        float MinimumSupportMargin);

    private static FlatResult RunFlat(Ablation ablation)
    {
        DeerParams parameters = DeerFactory.Compact();
        if (ablation == Ablation.Stance)
        {
            float legLength = parameters.LegSlots.Min(slot => slot.MaxLength);
            parameters.PreferredBodyHeight = legLength * 0.18f;
            parameters.RestHeightRatio = 0.92f;
        }
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        deer.ApplyRuntimeAblation(ablation);
        var terrain = new PlaneTerrainQuery(0f);
        Vector3 start = Center(deer);
        float heightSum = 0f;
        float desiredHeightSum = 0f;
        float supportSum = 0f;
        int sampleCount = 0;
        float restHeightSum = 0f;
        float restDesiredSum = 0f;
        float restSupportSum = 0f;
        int restGripSum = 0;
        int restSampleCount = 0;
        int restBodyContacts = 0;
        int restBodyContactSamples = 0;
        int bodyContacts = 0;
        int bodyContactSamples = 0;
        int allChunkContacts = 0;
        int allChunkContactSamples = 0;
        float minimumBodyClearance = float.MaxValue;
        float restMinimumBodyClearance = float.MaxValue;
        float reachLimitSum = 0f;
        float restReachLimitSum = 0f;
        float restMaximumFootReach = 0f;
        float minimumAntlerUp = float.MaxValue;
        float minimumHeadUp = float.MaxValue;
        float minimumHeadForward = float.MaxValue;
        float maximumAntlerTrunkIntrusionRatio = 0f;
        bool gravityAlwaysOne = true;
        float maximumLean = 0f;
        float minimumSupportMargin = float.PositiveInfinity;
        int minimumGrips = 4;
        int maximumGrips = 0;
        int samePairAir = 0;
        float minimumStride = float.MaxValue;
        int[] lastLandings = new int[4];

        for (long tick = 1; tick <= 1060; tick++)
        {
            deer.MoveDir = tick <= 760 ? Vector3.Right : Vector3.Zero;
            deer.RunSpeed = tick <= 760 ? 0.78f : 0f;
            deer.Tick(terrain, tick);
            gravityAlwaysOne &= deer.Body.GravityScale == 1f;
            maximumLean = Math.Max(maximumLean, deer.LeanDegrees);
            if (deer.LegsGripping >= 3)
            {
                minimumSupportMargin = Math.Min(
                    minimumSupportMargin, deer.Controller.SupportMargin);
            }
            if (tick is > 180 and <= 760)
            {
                int grips = deer.Legs.Count(leg => leg.Gripping);
                minimumGrips = Math.Min(minimumGrips, grips);
                maximumGrips = Math.Max(maximumGrips, grips);
                for (int pair = 0; pair < 2; pair++)
                {
                    if (deer.Legs.Where(leg => leg.PairIndex == pair)
                        .All(leg => !leg.AttachedAtTip))
                    {
                        samePairAir++;
                    }
                }
            }
            if (tick is > 220 and <= 700)
            {
                heightSum += deer.ActualBodyHeight;
                desiredHeightSum += deer.DesiredBodyHeight;
                supportSum += deer.TotalSupport;
                reachLimitSum += deer.Legs.Min(leg => leg.CurrentReachLimit);
                sampleCount++;
                float antlerLink = parameters.HeadRadius + parameters.AntlerRadius
                    - parameters.AntlerHeadOverlap;
                Vector3 antlerAxis = (deer.Antler.Pos - deer.Head.Pos) / antlerLink;
                Vector3 headAxis = (deer.Head.Pos - deer.Trunk[0].Pos).Normalized();
                minimumAntlerUp = Math.Min(
                    minimumAntlerUp, antlerAxis.Dot(deer.Controller.Up));
                minimumHeadUp = Math.Min(minimumHeadUp, headAxis.Dot(deer.Controller.Up));
                minimumHeadForward = Math.Min(
                    minimumHeadForward, headAxis.Dot(deer.Controller.Forward));
                foreach (BodyChunk trunk in deer.Trunk)
                {
                    float penetration = Math.Max(
                        0f, deer.Antler.Radius + trunk.Radius
                            - deer.Antler.Pos.DistanceTo(trunk.Pos));
                    maximumAntlerTrunkIntrusionRatio = Math.Max(
                        maximumAntlerTrunkIntrusionRatio,
                        penetration / Math.Min(deer.Antler.Radius, trunk.Radius));
                }
                foreach (BodyChunk chunk in deer.Body.Chunks)
                {
                    allChunkContacts += chunk.TerrainContact ? 1 : 0;
                    allChunkContactSamples++;
                    // 鹿角是大而轻的朝向块，不承载推进/支撑；身体离地门只统计 Head+Trunk，
                    // 同时另报 all-chunk 比例，避免鹿角偶尔扫地掩盖腹部是否在滑行。
                    if (!ReferenceEquals(chunk, deer.Antler))
                    {
                        bodyContacts += chunk.TerrainContact ? 1 : 0;
                        bodyContactSamples++;
                        minimumBodyClearance = Math.Min(
                            minimumBodyClearance, chunk.Pos.Y - chunk.Radius);
                    }
                }
            }
            if (tick > 1020)
            {
                restHeightSum += deer.ActualBodyHeight;
                restDesiredSum += deer.DesiredBodyHeight;
                restSupportSum += deer.TotalSupport;
                restGripSum += deer.LegsGripping;
                restReachLimitSum += deer.Legs.Min(leg => leg.CurrentReachLimit);
                foreach (DeerLeg leg in deer.Legs)
                {
                    restMaximumFootReach = Math.Max(
                        restMaximumFootReach,
                        leg.Anchor.Pos.DistanceTo(leg.Tip.Pos));
                }
                restSampleCount++;
                foreach (BodyChunk chunk in deer.Body.Chunks)
                {
                    if (!ReferenceEquals(chunk, deer.Antler))
                    {
                        restBodyContacts += chunk.TerrainContact ? 1 : 0;
                        restBodyContactSamples++;
                        restMinimumBodyClearance = Math.Min(
                            restMinimumBodyClearance, chunk.Pos.Y - chunk.Radius);
                    }
                }
            }
            for (int i = 0; i < deer.Legs.Count; i++)
            {
                DeerLeg leg = deer.Legs[i];
                if (leg.LandingSerial > lastLandings[i])
                {
                    lastLandings[i] = leg.LandingSerial;
                    if (leg.StepSerial > 0 && leg.LastCompletedStepDistance > 1e-5f)
                    {
                        minimumStride = Math.Min(minimumStride, leg.LastCompletedStepDistance);
                    }
                }
            }
        }

        float averageHeight = heightSum / Math.Max(sampleCount, 1);
        float averageDesiredHeight = desiredHeightSum / Math.Max(sampleCount, 1);
        float restHeight = restHeightSum / Math.Max(restSampleCount, 1);
        float restDesiredHeight = restDesiredSum / Math.Max(restSampleCount, 1);
        float averageSupport = supportSum / Math.Max(sampleCount, 1);
        float restSupport = restSupportSum / Math.Max(restSampleCount, 1);
        float restAverageGrips = restGripSum / (float)Math.Max(restSampleCount, 1);
        float averageReachLimit = reachLimitSum / Math.Max(sampleCount, 1);
        float restReachLimit = restReachLimitSum / Math.Max(restSampleCount, 1);
        float bodyContactRatio = bodyContacts / (float)Math.Max(bodyContactSamples, 1);
        float restBodyContactRatio = restBodyContacts / (float)Math.Max(restBodyContactSamples, 1);
        float allChunkContactRatio = allChunkContacts / (float)Math.Max(allChunkContactSamples, 1);
        float travel = Center(deer).X - start.X;
        int totalLandings = deer.Legs.Sum(leg => leg.LandingSerial);
        int minimumLandings = deer.Legs.Min(leg => leg.LandingSerial);
        if (minimumStride == float.MaxValue)
        {
            minimumStride = 0f;
        }
        float minimumPoleDot = deer.Legs.Min(leg => leg.MinimumPoleDot);
        bool supportGate = deer.Body.GravityScale == 1f && gravityAlwaysOne
            && averageHeight > averageDesiredHeight * 0.80f
            && averageHeight < averageDesiredHeight * 1.24f
            && restDesiredHeight < averageDesiredHeight * 0.9f
            && restHeight < averageHeight - 0.035f
            && restHeight > restDesiredHeight * 0.78f
            && restHeight < restDesiredHeight * 1.18f
            && averageSupport > 0.25f
            && restSupport > 0.22f
            && restAverageGrips >= 2.5f
            && bodyContactRatio < 0.22f
            && restBodyContactRatio < 0.35f
            && minimumBodyClearance > -0.03f
            // 行进中同对互锁允许稳定的两点交替支撑；三脚以上只用于触发主动换步，
            // 不能反过来成为“腿是否真的把身体撑起”的必要条件。主动释放另由
            // RELEASE 及其消融门独立验证，休息态仍要求平均至少 2.5 条确认抓地腿。
            && maximumGrips >= 2
            && IsFinite(deer);
        float legLengthScale = parameters.LegSlots.Min(slot => slot.MaxLength);
        bool stanceGate = averageHeight >= legLengthScale * 0.45f
            && averageHeight <= legLengthScale * 0.90f
            && minimumBodyClearance >= legLengthScale * 0.25f
            && restMinimumBodyClearance >= legLengthScale * 0.15f
            && averageHeight - restHeight >= legLengthScale * 0.15f
            && restHeight / Math.Max(averageHeight, 1e-4f) <= 0.75f
            && averageReachLimit >= legLengthScale * 0.995f
            && restReachLimit >= legLengthScale * (parameters.RestLegReachRatio - 0.01f)
            && restReachLimit <= legLengthScale * (parameters.RestLegReachRatio + 0.01f)
            && restMaximumFootReach <= restReachLimit * 1.08f;
        bool antlerGate = minimumAntlerUp >= 0.85f
            && minimumHeadUp > 0.55f
            && minimumHeadForward > 0.08f
            && maximumAntlerTrunkIntrusionRatio <= 0.10f;
        bool gaitGate = travel > 1.5f
            && totalLandings >= 8
            && minimumLandings >= 1
            && minimumStride > parameters.LegSlots.Min(slot => slot.FootRadius * 1.25f)
            && samePairAir == 0
            && minimumPoleDot > -0.05f
            && maximumLean <= parameters.MaxLeanDegrees + 4f;
        return new FlatResult(
            supportGate, stanceGate, antlerGate, gaitGate,
            deer.Body.GravityScale, gravityAlwaysOne,
            averageHeight, averageDesiredHeight,
            restHeight, restDesiredHeight, averageSupport, restSupport, restAverageGrips,
            bodyContactRatio, restBodyContactRatio, allChunkContactRatio,
            minimumBodyClearance == float.MaxValue ? 0f : minimumBodyClearance,
            restMinimumBodyClearance == float.MaxValue ? 0f : restMinimumBodyClearance,
            averageReachLimit, restReachLimit, restMaximumFootReach,
            minimumAntlerUp == float.MaxValue ? 0f : minimumAntlerUp,
            minimumHeadUp == float.MaxValue ? 0f : minimumHeadUp,
            minimumHeadForward == float.MaxValue ? 0f : minimumHeadForward,
            maximumAntlerTrunkIntrusionRatio,
            minimumGrips, maximumGrips, travel, totalLandings, minimumLandings,
            minimumStride, samePairAir, minimumPoleDot, maximumLean,
            minimumSupportMargin == float.PositiveInfinity ? 0f : minimumSupportMargin);
    }

    private readonly record struct BendReversalResult(bool Gate, string Message);

    private static (bool, string) CheckBendFrameTransport()
    {
        (bool gate, string message) = RunBendFrameTransport(ablate: false);
        return (gate, message);
    }

    /// <summary>
    /// 把整条腿与局部 frame 精确旋转 180°，不给中间过渡帧。正常路径必须把旧 pole
    /// 的 frame 分量平行运输到新 frame；旧“反半球就翻回”路径会精确停在新解剖 pole
    /// 的反向。这是直接机制门，不依赖后续段链形态补偿是否也能让视觉门变红。
    /// </summary>
    private static (bool Gate, string Message) RunBendFrameTransport(bool ablate)
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Original());
        Vector3 oldForward = deer.Controller.Forward;
        Vector3 oldUp = deer.Controller.Up;
        Vector3 oldRight = deer.Controller.Right;
        Vector3 newForward = oldForward.Rotated(oldUp, Mathf.Pi);
        Vector3 newRight = oldRight.Rotated(oldUp, Mathf.Pi);
        var alignments = new float[deer.Legs.Count];

        for (int i = 0; i < deer.Legs.Count; i++)
        {
            DeerLeg leg = deer.Legs[i];
            Vector3 root = leg.Anchor.Pos;
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                Vector3 rotated = root + (segment.Pos - root).Rotated(oldUp, Mathf.Pi);
                segment.Pos = rotated;
                segment.LastPos = rotated;
            }
            SetMember(leg, "_frameForward", newForward);
            SetMember(leg, "_frameUp", oldUp);
            SetMember(leg, "_frameRight", newRight);
            _ = InvokeNonPublic(
                leg, "UpdateBendPole", oldForward, oldUp, oldRight, !ablate);
            Vector3 axis = NormalizeOr(leg.Tip.Pos - root, -oldUp);
            Vector3 expected = (Vector3)(InvokeNonPublic(
                leg, "ComputeAnatomicalPole", axis)
                ?? throw new InvalidOperationException("missing anatomical pole result"));
            alignments[i] = leg.BendPole.Dot(expected);
        }

        float minimum = alignments.Min();
        bool gate = minimum >= 0.95f;
        return (gate, $"alignment=[{string.Join(',', alignments.Select(v => v.ToString("F3")))}] " +
            $"minimum={minimum:F3} ablate={ablate}");
    }

    /// <summary>
    /// 先沿 +X 走到稳定步态，再给出精确 180 度的 -X 输入。旧实现只用上一拍 pole 的世界
    /// 半球来防翻面，转身后会把新的解剖 splay 翻回旧侧；脚虽向 -X 落，整条腿仍长期向
    /// +X 凹。plant-and-trail 的附着腿本来就会被身体拖成纵向弧，因此附着期只检查 pole
    /// 的解剖外撇与连续性；摆动期才把真实段链弓向同 ForwardSplay/BendPole 对照，并验证
    /// 内段确实随足端向新的落点前摆，而不是只让脚尖独自越过身体。
    /// </summary>
    private static (bool, string) CheckBendReversal()
    {
        BendReversalResult result = RunBendReversal(ablate: false);
        return (result.Gate, result.Message);
    }

    private static BendReversalResult RunBendReversal(bool ablate)
    {
        BendReversalResult compact = RunBendReversalPreset(
            DeerFactory.Compact(), ablate);
        BendReversalResult original = RunBendReversalPreset(
            DeerFactory.Original(), ablate);
        BendReversalResult strider = RunBendReversalPreset(
            DeerFactory.Strider(), ablate);
        return new BendReversalResult(
            compact.Gate && original.Gate && strider.Gate,
            $"compact{{{compact.Message}}} original{{{original.Message}}} " +
            $"strider{{{strider.Message}}}");
    }

    private static BendReversalResult RunBendReversalPreset(
        DeerParams parameters,
        bool ablate)
    {
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        if (ablate)
        {
            deer.ApplyRuntimeAblation(Ablation.Bend);
        }
        var terrain = new PlaneTerrainQuery(0f);
        const int forwardTicks = 420;
        const int stableAfterReverseTicks = 180;
        const float bowComparableEpsilon = 0.0025f;
        // original/strider 的长腿一步显著慢于 compact；延长窗只为获得至少四个完整
        // release→inner-swing→landing 样本，稳定窗起点和所有方向阈值保持完全相同。
        int reverseTicks = parameters.StableId == DeerFactory.CompactId ? 720 : 2000;

        int legCount = deer.Legs.Count;
        int[] attachedSamples = new int[legCount];
        int[] swingingSamples = new int[legCount];
        int[] swingingComparable = new int[legCount];
        int[] swingingMatches = new int[legCount];
        float[] swingingSignedBow = new float[legCount];
        int[] swingBowPoleSamples = new int[legCount];
        int[] swingBowPoleMatches = new int[legCount];
        float[] swingBowPoleDotSum = new float[legCount];
        float[] swingClosestLongitudinalSum = new float[legCount];
        float[] swingClosestLongitudinalMinimum = Enumerable.Repeat(
            float.PositiveInfinity, legCount).ToArray();
        float[] swingClosestLongitudinalMaximum = Enumerable.Repeat(
            float.NegativeInfinity, legCount).ToArray();
        float[] swingWantedLongitudinalSum = new float[legCount];
        int[] attachedPoleMatches = new int[legCount];
        float[] attachedPoleDotSum = new float[legCount];
        int[] swingingPoleMatches = new int[legCount];
        float[] swingingPoleDotSum = new float[legCount];
        int[] attachedOutwardMatches = new int[legCount];
        float[] attachedOutwardDotSum = new float[legCount];
        int[] attachedLongitudinalMatches = new int[legCount];
        float[] attachedLongitudinalDotSum = new float[legCount];
        int[] swingingOutwardMatches = new int[legCount];
        float[] swingingOutwardDotSum = new float[legCount];
        int[] swingingLongitudinalMatches = new int[legCount];
        float[] swingingLongitudinalDotSum = new float[legCount];
        float[] minimumAttachedPoleContinuity = Enumerable.Repeat(1f, legCount).ToArray();
        int[] attachedPoleContinuitySamples = new int[legCount];
        int[] currentSwingWrongRun = new int[legCount];
        int[] maximumSwingWrongRun = new int[legCount];
        int[] wrongReachSamples = new int[legCount];
        float[] wrongReachRatioSum = new float[legCount];
        float[] wrongReachRatioMinimum = Enumerable.Repeat(
            float.PositiveInfinity, legCount).ToArray();
        float[] wrongReachRatioMaximum = Enumerable.Repeat(
            float.NegativeInfinity, legCount).ToArray();
        int[] landingSamples = new int[legCount];
        int[] landingForward = new int[legCount];
        float[] landingProgress = new float[legCount];
        bool[] trackingInnerSwing = new bool[legCount];
        Vector3[] innerSwingStart = new Vector3[legCount];
        float[] innerSwingPeak = new float[legCount];
        int[] completedInnerSwings = new int[legCount];
        int[] forwardInnerSwings = new int[legCount];
        float[] innerSwingProgress = new float[legCount];
        float minimumStableForwardAlignment = 1f;
        bool finite = true;

        long tick = 0;
        for (; tick < forwardTicks;)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 1f;
            deer.Tick(terrain, ++tick);
        }

        bool[] previousAttached = deer.Legs.Select(leg => leg.AttachedAtTip).ToArray();
        Vector3[] previousPole = deer.Legs.Select(leg => leg.BendPole).ToArray();
        Vector3[] previousAnchor = deer.Legs.Select(leg => leg.Anchor.Pos).ToArray();
        Vector3[] previousMiddle = deer.Legs.Select(leg =>
        {
            int middleIndex = Math.Max(0, (leg.Segments.Length - 2) / 2);
            return leg.Segments[middleIndex].Pos;
        }).ToArray();

        for (int reverseTick = 1; reverseTick <= reverseTicks; reverseTick++)
        {
            int[] landingBefore = deer.Legs.Select(leg => leg.LandingSerial).ToArray();
            deer.MoveDir = Vector3.Left;
            deer.RunSpeed = 1f;
            deer.Tick(terrain, ++tick);
            finite &= IsFinite(deer);
            if (reverseTick <= stableAfterReverseTicks)
            {
                for (int i = 0; i < legCount; i++)
                {
                    DeerLeg leg = deer.Legs[i];
                    int middleIndex = Math.Max(0, (leg.Segments.Length - 2) / 2);
                    previousAttached[i] = leg.AttachedAtTip;
                    previousPole[i] = leg.BendPole;
                    previousAnchor[i] = leg.Anchor.Pos;
                    previousMiddle[i] = leg.Segments[middleIndex].Pos;
                }
                continue;
            }

            minimumStableForwardAlignment = Math.Min(
                minimumStableForwardAlignment, deer.Forward.Dot(Vector3.Left));
            for (int i = 0; i < legCount; i++)
            {
                DeerLeg leg = deer.Legs[i];
                Vector3 chord = leg.Tip.Pos - leg.Anchor.Pos;
                Vector3 chordDirection = NormalizeOr(chord, -deer.Controller.Up);
                // 外撇量属于左右工作区，不应污染“向前还是向后凹”的侧视判定。先把根、足、
                // 中段投到当前 Forward/Up 解剖矢状面，再量其中段到 Root→Tip 弦的有符号距离。
                Vector3 sagittalChord = chord
                    - deer.Controller.Right * chord.Dot(deer.Controller.Right);
                Vector3 sagittalDirection = NormalizeOr(sagittalChord, -deer.Controller.Up);
                Vector3 anatomicalForward = ProjectDirectionOntoPlane(
                    deer.Forward, sagittalDirection, deer.Controller.Up);
                int middleIndex = Math.Max(0, (leg.Segments.Length - 2) / 2);
                // 用 sin 权重汇总中间段（不含足端）：端点权重自然趋近 0，既对应渲染中最
                // 显眼的“膝”区域，也不会由某一个离散关节偶然越过弦就改变整条腿的判定。
                float signedBow = 0f;
                Vector3 physicalBow = Vector3.Zero;
                float bowWeightSum = 0f;
                float bowWeightSquareSum = 0f;
                for (int segmentIndex = 0;
                    segmentIndex < leg.Segments.Length - 1;
                    segmentIndex++)
                {
                    float segmentT = (segmentIndex + 1f) / leg.Segments.Length;
                    float weight = Mathf.Sin(segmentT * Mathf.Pi);
                    Vector3 offset = leg.Segments[segmentIndex].Pos - leg.Anchor.Pos;
                    float fullClosestT = Mathf.Clamp(
                        offset.Dot(chord) / Math.Max(chord.LengthSquared(), 1e-8f),
                        0f,
                        1f);
                    physicalBow += (offset - chord * fullClosestT) * weight;

                    Vector3 sagittalOffset = offset
                        - deer.Controller.Right * offset.Dot(deer.Controller.Right);
                    float sagittalClosestT = Mathf.Clamp(
                        sagittalOffset.Dot(sagittalChord)
                            / Math.Max(sagittalChord.LengthSquared(), 1e-8f),
                        0f,
                        1f);
                    signedBow += (sagittalOffset - sagittalChord * sagittalClosestT)
                        .Dot(anatomicalForward) * weight;
                    bowWeightSum += weight;
                    bowWeightSquareSum += weight * weight;
                }
                signedBow /= Math.Max(bowWeightSum, 1e-5f);
                physicalBow /= Math.Max(bowWeightSum, 1e-5f);
                float expectedSign = Math.Sign(leg.ForwardSplay);
                bool comparable = Math.Abs(signedBow) >= bowComparableEpsilon;
                bool matches = signedBow * expectedSign > 0f;

                // 候选只驱动足端；可见关节 pole 始终围绕真实 Root→Tip 弦定义。
                Vector3 poleAxis = NormalizeOr(leg.Tip.Pos - leg.Anchor.Pos, chordDirection);
                Vector3 anatomicalSplay = deer.Forward * leg.ForwardSplay
                    + deer.Controller.Right * (leg.Side * leg.OutwardSplay);
                Vector3 projectedSplay = ProjectDirectionOntoPlane(
                    anatomicalSplay, poleAxis, anatomicalForward * expectedSign);
                float poleDot = leg.BendPole.Dot(projectedSplay);
                Vector3 longitudinal = ProjectDirectionOntoPlane(
                    deer.Forward * expectedSign, poleAxis, projectedSplay);
                Vector3 projectedOutward = deer.Controller.Right * leg.Side;
                projectedOutward -= poleAxis * projectedOutward.Dot(poleAxis);
                // 与生产定义一致，先从外撇轴移除 longitudinal 分量；否则完整3D投影可用
                // 很强的 lateral dot 掩盖纵向已经反号这一视觉错误。
                projectedOutward -= longitudinal * projectedOutward.Dot(longitudinal);
                projectedOutward = NormalizeOr(projectedOutward, projectedSplay);
                if (projectedOutward.Dot(deer.Controller.Right * leg.Side) < 0f)
                {
                    projectedOutward = -projectedOutward;
                }
                float longitudinalDot = leg.BendPole.Dot(longitudinal);
                float outwardDot = leg.BendPole.Dot(projectedOutward);

                if (leg.AttachedAtTip)
                {
                    attachedSamples[i]++;
                    attachedPoleDotSum[i] += poleDot;
                    attachedPoleMatches[i] += poleDot > 0f ? 1 : 0;
                    attachedOutwardDotSum[i] += outwardDot;
                    attachedOutwardMatches[i] += outwardDot > 0f ? 1 : 0;
                    attachedLongitudinalDotSum[i] += longitudinalDot;
                    attachedLongitudinalMatches[i] += longitudinalDot > 0f ? 1 : 0;
                    if (previousAttached[i])
                    {
                        minimumAttachedPoleContinuity[i] = Math.Min(
                            minimumAttachedPoleContinuity[i],
                            previousPole[i].Dot(leg.BendPole));
                        attachedPoleContinuitySamples[i]++;
                    }
                    currentSwingWrongRun[i] = 0;
                }
                else
                {
                    swingingSamples[i]++;
                    swingingPoleDotSum[i] += poleDot;
                    swingingPoleMatches[i] += poleDot > 0f ? 1 : 0;
                    swingingOutwardDotSum[i] += outwardDot;
                    swingingOutwardMatches[i] += outwardDot > 0f ? 1 : 0;
                    swingingLongitudinalDotSum[i] += longitudinalDot;
                    swingingLongitudinalMatches[i] += longitudinalDot > 0f ? 1 : 0;
                    swingingSignedBow[i] += signedBow;

                    float closestLongitudinal = physicalBow.Dot(longitudinal);
                    swingClosestLongitudinalSum[i] += closestLongitudinal;
                    swingClosestLongitudinalMinimum[i] = Math.Min(
                        swingClosestLongitudinalMinimum[i], closestLongitudinal);
                    swingClosestLongitudinalMaximum[i] = Math.Max(
                        swingClosestLongitudinalMaximum[i], closestLongitudinal);
                    float estimatedIdealLength = leg.Anchor.Pos.DistanceTo(leg.Segments[0].Pos);
                    for (int linkIndex = 1; linkIndex < leg.Segments.Length; linkIndex++)
                    {
                        estimatedIdealLength += leg.Segments[linkIndex - 1].Pos.DistanceTo(
                            leg.Segments[linkIndex].Pos);
                    }
                    float estimatedStraightness = Mathf.Clamp(
                        chord.Length() / Math.Max(estimatedIdealLength, 1e-5f), 0f, 1f);
                    float estimatedBowAmplitude = leg.CurrentReachLimit * 0.10f
                        * (1f - estimatedStraightness * 0.75f);
                    float estimatedWantedLongitudinal = estimatedBowAmplitude * 0.95f
                        * bowWeightSquareSum / Math.Max(bowWeightSum, 1e-5f);
                    swingWantedLongitudinalSum[i] += estimatedWantedLongitudinal;
                    if (comparable)
                    {
                        swingingComparable[i]++;
                        swingingMatches[i] += matches ? 1 : 0;
                        currentSwingWrongRun[i] = matches
                            ? 0
                            : currentSwingWrongRun[i] + 1;
                        maximumSwingWrongRun[i] = Math.Max(
                            maximumSwingWrongRun[i], currentSwingWrongRun[i]);
                        if (!matches)
                        {
                            wrongReachSamples[i]++;
                            wrongReachRatioSum[i] += leg.ReachRatio;
                            wrongReachRatioMinimum[i] = Math.Min(
                                wrongReachRatioMinimum[i], leg.ReachRatio);
                            wrongReachRatioMaximum[i] = Math.Max(
                                wrongReachRatioMaximum[i], leg.ReachRatio);
                        }
                    }
                    else
                    {
                        currentSwingWrongRun[i] = 0;
                    }

                    if (physicalBow.LengthSquared()
                        >= bowComparableEpsilon * bowComparableEpsilon)
                    {
                        float bowPoleDot = physicalBow.Normalized().Dot(leg.BendPole);
                        swingBowPoleSamples[i]++;
                        swingBowPoleDotSum[i] += bowPoleDot;
                        swingBowPoleMatches[i] += bowPoleDot > 0f ? 1 : 0;
                    }
                }

                if (previousAttached[i] && !leg.AttachedAtTip)
                {
                    trackingInnerSwing[i] = true;
                    innerSwingStart[i] = previousMiddle[i] - previousAnchor[i];
                    innerSwingPeak[i] = 0f;
                }
                if (trackingInnerSwing[i] && !leg.AttachedAtTip)
                {
                    innerSwingPeak[i] = Math.Max(
                        innerSwingPeak[i],
                        (leg.Segments[middleIndex].Pos - leg.Anchor.Pos - innerSwingStart[i])
                            .Dot(Vector3.Left));
                }

                int landingDelta = leg.LandingSerial - landingBefore[i];
                if (landingDelta > 0)
                {
                    float progress = (leg.LastLandingPoint - leg.LastReleasePoint)
                        .Dot(Vector3.Left);
                    landingSamples[i] += landingDelta;
                    landingProgress[i] += progress * landingDelta;
                    landingForward[i] += progress > leg.FootRadius * 0.20f
                        ? landingDelta
                        : 0;
                    if (trackingInnerSwing[i])
                    {
                        completedInnerSwings[i]++;
                        innerSwingProgress[i] += innerSwingPeak[i];
                        forwardInnerSwings[i] += innerSwingPeak[i] > leg.FootRadius * 0.30f
                            ? 1
                            : 0;
                    }
                }
                if (leg.AttachedAtTip)
                {
                    trackingInnerSwing[i] = false;
                }
                previousAttached[i] = leg.AttachedAtTip;
                previousPole[i] = leg.BendPole;
                previousAnchor[i] = leg.Anchor.Pos;
                previousMiddle[i] = leg.Segments[middleIndex].Pos;
            }
        }

        float[] swingingMatchRatio = Ratio(swingingMatches, swingingComparable);
        float[] attachedPoleMatchRatio = Ratio(attachedPoleMatches, attachedSamples);
        float[] swingingPoleMatchRatio = Ratio(swingingPoleMatches, swingingSamples);
        float[] attachedOutwardMatchRatio = Ratio(attachedOutwardMatches, attachedSamples);
        float[] attachedLongitudinalMatchRatio = Ratio(
            attachedLongitudinalMatches, attachedSamples);
        float[] swingingOutwardMatchRatio = Ratio(swingingOutwardMatches, swingingSamples);
        float[] swingingLongitudinalMatchRatio = Ratio(
            swingingLongitudinalMatches, swingingSamples);
        float[] swingBowPoleMatchRatio = Ratio(swingBowPoleMatches, swingBowPoleSamples);
        float[] landingForwardRatio = Ratio(landingForward, landingSamples);
        float[] innerForwardRatio = Ratio(forwardInnerSwings, completedInnerSwings);
        float[] swingingMeanBow = Mean(swingingSignedBow, swingingSamples);
        float[] attachedMeanPoleDot = Mean(attachedPoleDotSum, attachedSamples);
        float[] swingingMeanPoleDot = Mean(swingingPoleDotSum, swingingSamples);
        float[] attachedMeanOutwardDot = Mean(attachedOutwardDotSum, attachedSamples);
        float[] attachedMeanLongitudinalDot = Mean(
            attachedLongitudinalDotSum, attachedSamples);
        float[] swingingMeanOutwardDot = Mean(swingingOutwardDotSum, swingingSamples);
        float[] swingingMeanLongitudinalDot = Mean(
            swingingLongitudinalDotSum, swingingSamples);
        float[] swingMeanBowPoleDot = Mean(swingBowPoleDotSum, swingBowPoleSamples);
        float[] swingMeanClosestLongitudinal = Mean(
            swingClosestLongitudinalSum, swingingSamples);
        float[] swingMeanWantedLongitudinal = Mean(
            swingWantedLongitudinalSum, swingingSamples);
        float[] meanLandingProgress = Mean(landingProgress, landingSamples);
        float[] meanInnerProgress = Mean(innerSwingProgress, completedInnerSwings);
        float[] wrongMeanReachRatio = Mean(wrongReachRatioSum, wrongReachSamples);
        for (int i = 0; i < legCount; i++)
        {
            if (wrongReachSamples[i] == 0)
            {
                wrongReachRatioMinimum[i] = 0f;
                wrongReachRatioMaximum[i] = 0f;
            }
        }

        bool samplesGate = attachedSamples.All(count => count >= 180)
            && swingingSamples.All(count => count >= 12)
            && swingingComparable.All(count => count >= 8)
            && swingBowPoleSamples.All(count => count >= 8)
            && attachedPoleContinuitySamples.All(count => count >= 120)
            && completedInnerSwings.All(count => count >= 4)
            && landingSamples.All(count => count >= 4);
        bool swingBowGate = Enumerable.Range(0, legCount).All(i =>
            swingingMatchRatio[i] >= 0.62f
            && swingingMeanBow[i] * Math.Sign(deer.Legs[i].ForwardSplay) > 0.003f
            && swingBowPoleMatchRatio[i] >= 0.68f
            && swingMeanBowPoleDot[i] >= 0.18f
            && maximumSwingWrongRun[i] <= 8);
        bool poleGate = Enumerable.Range(0, legCount).All(i =>
            attachedPoleMatchRatio[i] >= 0.92f
            && swingingPoleMatchRatio[i] >= 0.88f
            && attachedMeanPoleDot[i] >= 0.28f
            && swingingMeanPoleDot[i] >= 0.24f
            && attachedOutwardMatchRatio[i] >= 0.90f
            && attachedMeanOutwardDot[i] >= 0.20f
            && attachedLongitudinalMatchRatio[i] >= 0.90f
            && attachedMeanLongitudinalDot[i] >= 0.12f
            && swingingOutwardMatchRatio[i] >= 0.88f
            && swingingMeanOutwardDot[i] >= 0.18f
            && swingingLongitudinalMatchRatio[i] >= 0.88f
            && swingingMeanLongitudinalDot[i] >= 0.12f
            && minimumAttachedPoleContinuity[i] >= 0.82f);
        bool innerSwingGate = Enumerable.Range(0, legCount).All(i =>
            innerForwardRatio[i] >= 0.68f && meanInnerProgress[i] > 0.04f);
        bool landingGate = Enumerable.Range(0, legCount).All(i =>
            landingForwardRatio[i] >= 0.68f && meanLandingProgress[i] > 0.04f);
        bool frameGate = minimumStableForwardAlignment >= 0.92f;
        bool gate = samplesGate && swingBowGate && poleGate && innerSwingGate
            && landingGate && frameGate && finite;

        string Format(float[] values) => string.Join(',', values.Select(value => value.ToString("F2")));
        string message =
            $"attachedSamples=[{string.Join(',', attachedSamples)}] " +
            $"swingSamples=[{string.Join(',', swingingSamples)}] " +
            $"swingMatch=[{Format(swingingMatchRatio)}] " +
            $"swingBow=[{Format(swingingMeanBow)}]m " +
            $"swingBowPole=[{Format(swingMeanBowPoleDot)}]/[{Format(swingBowPoleMatchRatio)}] " +
            $"closestLong(mean/min/max)=[{Format(swingMeanClosestLongitudinal)}]/" +
            $"[{Format(swingClosestLongitudinalMinimum)}]/" +
            $"[{Format(swingClosestLongitudinalMaximum)}]m " +
            $"wantedLong=[{Format(swingMeanWantedLongitudinal)}]m " +
            $"poleAttached=[{Format(attachedMeanPoleDot)}]/[{Format(attachedPoleMatchRatio)}] " +
            $"poleSwing=[{Format(swingingMeanPoleDot)}]/[{Format(swingingPoleMatchRatio)}] " +
            $"poleLong(A/S)=[{Format(attachedMeanLongitudinalDot)}]/" +
            $"[{Format(swingingMeanLongitudinalDot)}] " +
            $"poleOut(A/S)=[{Format(attachedMeanOutwardDot)}]/" +
            $"[{Format(swingingMeanOutwardDot)}] " +
            $"continuity=[{Format(minimumAttachedPoleContinuity)}] " +
            $"wrongSwingRun=[{string.Join(',', maximumSwingWrongRun)}] " +
            $"wrongReach(mean/min/max)=[{Format(wrongMeanReachRatio)}]/" +
            $"[{Format(wrongReachRatioMinimum)}]/[{Format(wrongReachRatioMaximum)}] " +
            $"innerN=[{string.Join(',', completedInnerSwings)}] " +
            $"innerProgress=[{Format(meanInnerProgress)}]m/[{Format(innerForwardRatio)}] " +
            $"landingN=[{string.Join(',', landingSamples)}] " +
            $"landingProgress=[{Format(meanLandingProgress)}]m " +
            $"landingForward=[{Format(landingForwardRatio)}] " +
            $"frameMin={minimumStableForwardAlignment:F3} finite={finite}";
        return new BendReversalResult(gate, message);
    }

    private static Vector3 ProjectDirectionOntoPlane(
        Vector3 direction,
        Vector3 planeNormal,
        Vector3 fallback)
    {
        Vector3 projected = direction - planeNormal * direction.Dot(planeNormal);
        if (projected.LengthSquared() < 1e-10f)
        {
            projected = fallback - planeNormal * fallback.Dot(planeNormal);
        }
        return NormalizeOr(projected, fallback);
    }

    private static float[] Ratio(int[] numerator, int[] denominator) =>
        numerator.Select((value, i) => value / (float)Math.Max(denominator[i], 1)).ToArray();

    private static float[] Mean(float[] sum, int[] count) =>
        sum.Select((value, i) => value / Math.Max(count[i], 1)).ToArray();

    private readonly record struct RestRetractionResult(
        bool Qualified,
        int QualificationIdleTick,
        int PreDelayVoluntaryReleases,
        int RetractionVoluntaryReleases,
        int MappedReleaseEvents,
        int ReplantEvents,
        int ReleaseMappingMismatches,
        int ForcedReleaseEvents,
        int[] OutstandingReleasesByLeg,
        int SamePairAirborneTicks,
        int MinimumStablePairsAttached,
        int StableSampleCount,
        bool Finite);

    /// <summary>
    /// 休息收腿不能只验证最终腿长：这里监视从无输入到稳态的完整过程，
    /// 钉住延迟窗口、逐腿释放→重落、同对互锁和稳态真实落地四个契约。
    /// </summary>
    private static (bool, string) CheckRestRetraction()
    {
        RestRetractionResult result = RunRestRetraction();
        DeerParams parameters = DeerFactory.Compact();
        bool ok = result.Qualified
            && result.QualificationIdleTick == parameters.RestDelayTicks + 1
            && result.PreDelayVoluntaryReleases == 0
            && result.RetractionVoluntaryReleases > 0
            && result.MappedReleaseEvents == result.RetractionVoluntaryReleases
            && result.ReplantEvents == result.MappedReleaseEvents
            && result.ReleaseMappingMismatches == 0
            && result.ForcedReleaseEvents == 0
            && result.OutstandingReleasesByLeg.All(count => count == 0)
            && result.SamePairAirborneTicks == 0
            && result.StableSampleCount > 0
            && result.MinimumStablePairsAttached == 2
            && result.Finite;
        return (ok,
            $"qualified={result.Qualified}@idle{result.QualificationIdleTick}/" +
            $"delay{parameters.RestDelayTicks} preDelayRelease={result.PreDelayVoluntaryReleases} " +
            $"release/replant={result.MappedReleaseEvents}/{result.ReplantEvents} " +
            $"controllerRelease={result.RetractionVoluntaryReleases} " +
            $"mappingMismatch={result.ReleaseMappingMismatches} forced={result.ForcedReleaseEvents} " +
            $"outstanding=[{string.Join(',', result.OutstandingReleasesByLeg)}] " +
            $"pairAir={result.SamePairAirborneTicks} " +
            $"stablePairsMin={result.MinimumStablePairsAttached}/2 " +
            $"stableSamples={result.StableSampleCount} finite={result.Finite}");
    }

    private static RestRetractionResult RunRestRetraction()
    {
        DeerParams parameters = DeerFactory.Compact();
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        var terrain = new PlaneTerrainQuery(0f);
        const int activeTicks = 760;
        long tick = 0;
        for (; tick < activeTicks;)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.78f;
            deer.Tick(terrain, ++tick);
        }

        int releaseAtIdleStart = deer.Controller.VoluntaryReleaseSerial;
        int preDelayVoluntaryReleases = 0;
        int retractionVoluntaryReleases = 0;
        int mappedReleaseEvents = 0;
        int replantEvents = 0;
        int releaseMappingMismatches = 0;
        int forcedReleaseEvents = 0;
        int qualificationIdleTick = 0;
        int samePairAirborneTicks = 0;
        int minimumStablePairsAttached = 2;
        int stableSampleCount = 0;
        bool finite = IsFinite(deer);
        int[] outstandingReleasesByLeg = new int[deer.Legs.Count];

        int idleBudget = parameters.RestDelayTicks + 260;
        for (int idleTick = 1; idleTick <= idleBudget; idleTick++)
        {
            int releaseBeforeTick = deer.Controller.VoluntaryReleaseSerial;
            int[] stepsBeforeTick = deer.Legs.Select(leg => leg.StepSerial).ToArray();
            int[] forcedBeforeTick = deer.Legs.Select(leg => leg.ForcedReleaseSerial).ToArray();
            int[] landingsBeforeTick = deer.Legs.Select(leg => leg.LandingSerial).ToArray();
            deer.MoveDir = Vector3.Zero;
            deer.RunSpeed = 0f;
            deer.Tick(terrain, ++tick);
            finite &= IsFinite(deer);

            if (deer.Controller.IdleTicks <= parameters.RestDelayTicks)
            {
                preDelayVoluntaryReleases = Math.Max(
                    preDelayVoluntaryReleases,
                    deer.Controller.VoluntaryReleaseSerial - releaseAtIdleStart);
            }

            bool reachIsShrinking = deer.Controller.RestAmount > 0f
                && deer.Legs.Any(leg => leg.CurrentReachLimit < leg.MaxLength - 1e-5f);
            if (!reachIsShrinking)
            {
                continue;
            }
            if (qualificationIdleTick == 0)
            {
                qualificationIdleTick = deer.Controller.IdleTicks;
            }

            int releasesThisTick = deer.Controller.VoluntaryReleaseSerial - releaseBeforeTick;
            retractionVoluntaryReleases += Math.Max(releasesThisTick, 0);
            int mappedThisTick = 0;
            for (int i = 0; i < deer.Legs.Count; i++)
            {
                DeerLeg leg = deer.Legs[i];
                int stepDelta = leg.StepSerial - stepsBeforeTick[i];
                int forcedDelta = leg.ForcedReleaseSerial - forcedBeforeTick[i];
                forcedReleaseEvents += Math.Max(forcedDelta, 0);
                // Tick 内顺序是先落脚、后统一选腿释放：先用本拍 LandingSerial
                // 结清旧 outstanding，再登记本拍新释放，禁止新债被同拍早先落脚伪结清。
                int landingDelta = leg.LandingSerial - landingsBeforeTick[i];
                int closed = Math.Min(
                    outstandingReleasesByLeg[i], Math.Max(landingDelta, 0));
                if (closed > 0)
                {
                    outstandingReleasesByLeg[i] -= closed;
                    replantEvents += closed;
                }

                int activeReleaseDelta = stepDelta - forcedDelta;
                if (activeReleaseDelta < 0)
                {
                    releaseMappingMismatches += -activeReleaseDelta;
                    activeReleaseDelta = 0;
                }
                mappedThisTick += activeReleaseDelta;
                mappedReleaseEvents += activeReleaseDelta;
                outstandingReleasesByLeg[i] += activeReleaseDelta;
            }
            releaseMappingMismatches += Math.Abs(releasesThisTick - mappedThisTick);

            for (int pair = 0; pair < 2; pair++)
            {
                if (deer.Legs.Where(leg => leg.PairIndex == pair)
                    .All(leg => !leg.AttachedAtTip))
                {
                    samePairAirborneTicks++;
                }
            }

            if (idleTick > idleBudget - 60)
            {
                stableSampleCount++;
                int attachedPairs = 0;
                for (int pair = 0; pair < 2; pair++)
                {
                    if (deer.Legs.Where(leg => leg.PairIndex == pair)
                        .Any(leg => leg.AttachedAtTip))
                    {
                        attachedPairs++;
                    }
                }
                minimumStablePairsAttached = Math.Min(
                    minimumStablePairsAttached, attachedPairs);
            }
        }

        return new RestRetractionResult(
            qualificationIdleTick > 0,
            qualificationIdleTick,
            preDelayVoluntaryReleases,
            retractionVoluntaryReleases,
            mappedReleaseEvents,
            replantEvents,
            releaseMappingMismatches,
            forcedReleaseEvents,
            outstandingReleasesByLeg,
            samePairAirborneTicks,
            minimumStablePairsAttached,
            stableSampleCount,
            finite);
    }

    private static (bool, string) CheckCandidateHysteresis()
    {
        HysteresisResult normal = RunHysteresis(0.18f);
        HysteresisResult disabled = RunHysteresis(0f);
        bool ok = normal.InitialCandidates == 1
            && normal.SwitchesAfterSmallImprovement == 0
            && disabled.SwitchesAfterSmallImprovement > normal.SwitchesAfterSmallImprovement;
        return (ok,
            $"initial={normal.InitialCandidates} normalSwitch={normal.SwitchesAfterSmallImprovement} " +
            $"zeroHysteresisSwitch={disabled.SwitchesAfterSmallImprovement} " +
            $"offerHits={normal.ImprovedHits}/{disabled.ImprovedHits} " +
            $"finalCandidates={normal.FinalCandidates}/{disabled.FinalCandidates}");
    }

    private readonly record struct HysteresisResult(
        int InitialCandidates,
        int SwitchesAfterSmallImprovement,
        int ImprovedHits,
        int FinalCandidates);

    private static HysteresisResult RunHysteresis(float hysteresis)
    {
        DeerParams p = DeerFactory.Compact();
        foreach (DeerLegSlotParams slot in p.LegSlots)
        {
            slot.GripConfirmTicks = 120;
            slot.CandidateHysteresisRatio = hysteresis;
        }
        DeerRig deer = NewDeer(Vector3.Zero, p);
        var terrain = new HysteresisTerrain();
        DeerLeg leg = deer.Legs[0];
        for (long tick = 1; tick <= 3; tick++)
        {
            TickLegForHysteresis(leg, terrain, tick, p);
        }
        // 平面上按理想点投影得到的首候选已经是全局最优，无法构造“略优但仍合法”的邻居。
        // 把同一真实平面上的旧候选确定性地横移，隔离滞回本身；候选仍需通过完整的
        // SurfaceStillPresent/PathClear/Reach 环，随后 terrain 才提供 15% 的同面改善。
        if (leg.HasCandidate)
        {
            Vector3 oldPoint = leg.CandidatePoint + Vector3.Back * (leg.MaxLength * 0.32f);
            SetMember(leg, nameof(DeerLeg.CandidatePoint), oldPoint);
            SetMember(leg, "_candidateCost", oldPoint.DistanceTo(leg.DesiredGripPoint));
        }
        int candidates = leg.HasCandidate ? 1 : 0;
        int before = leg.CandidateSwitchSerial;

        // 保留旧表面可重命中，同时另给一块成本只改善 15% 的候选；正式 18% 滞回应拒绝，
        // 零滞回应接受。这样不会把“旧候选因表面消失被清掉”误当成候选替换。
        terrain.OfferSmallImprovement(new[] { leg }, 0.15f);
        for (long tick = 4; tick <= 6; tick++)
        {
            TickLegForHysteresis(leg, terrain, tick, p);
        }
        int switches = leg.CandidateSwitchSerial - before;
        return new HysteresisResult(
            candidates, switches, terrain.ImprovedHitCount,
            leg.HasCandidate ? 1 : 0);
    }

    private static void TickLegForHysteresis(
        DeerLeg leg,
        ITerrainQuery terrain,
        long tick,
        DeerParams parameters)
    {
        MethodInfo method = typeof(DeerLeg).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "Tick"
                && candidate.GetParameters().Length == 14);
        var context = new TickContext(GravityPerTick, terrain, tick);
        float desiredReach = Math.Min(
            leg.MaxLength * parameters.IdealFootReachRatio,
            parameters.PreferredBodyHeight * 1.2f);
        method.Invoke(leg, new object[]
        {
            context,
            Vector3.Right,
            Vector3.Up,
            Vector3.Back,
            Vector3.Up,
            Vector3.Zero,
            0f,
            desiredReach,
            parameters.FootTargetDownWeight,
            parameters.FootTargetMoveWeight,
            parameters.FootTargetSplayWeight,
            MathF.Cos(MathF.PI / 180f * parameters.MaxStandableSlopeDegrees),
            parameters.TerrainClearance,
            true,
        });
    }

    /// <summary>
    /// 紧急同对保护只能省候选确认的最后一拍，而且必须由本 tick 另一条已确认支点的
    /// 物理失效触发。分别钉住：无失效证据不种、单帧候选不种、只差一拍且有证据才种。
    /// </summary>
    private static (bool, string) CheckEmergencyPairPlantGate()
    {
        var floor = new PlaneTerrainQuery(0f);
        DeerParams parameters = DeerFactory.Original();

        DeerRig noEvidence = NewDeer(Vector3.Zero, parameters);
        DeerLeg noEvidenceCandidate = noEvidence.Legs[1];
        PrimeEmergencyCandidate(noEvidenceCandidate, parameters,
            noEvidenceCandidate.GripConfirmTicks - 1);
        _ = InvokeNonPublic(noEvidence.Controller, "ResolvePairAirborneEmergency", floor);
        bool noEvidenceRejected = !noEvidenceCandidate.AttachedAtTip;

        DeerRig freshOriginal = NewDeer(Vector3.Zero, parameters);
        DeerLeg freshOriginalSupport = freshOriginal.Legs[0];
        DeerLeg freshOriginalCandidate = freshOriginal.Legs[1];
        InvalidateConfirmedGripThisTick(freshOriginalSupport, parameters, floor);
        PrimeEmergencyCandidate(freshOriginalCandidate, parameters, 1,
            changedThisTick: true);
        _ = InvokeNonPublic(freshOriginal.Controller,
            "ResolvePairAirborneEmergency", floor);
        bool freshOriginalRejected = !freshOriginalCandidate.AttachedAtTip;

        DeerParams compactParameters = DeerFactory.Compact();
        DeerRig freshCompact = NewDeer(Vector3.Zero, compactParameters);
        DeerLeg freshCompactSupport = freshCompact.Legs[0];
        DeerLeg freshCompactCandidate = freshCompact.Legs[1];
        InvalidateConfirmedGripThisTick(freshCompactSupport, compactParameters, floor);
        PrimeEmergencyCandidate(freshCompactCandidate, compactParameters, 1,
            changedThisTick: true);
        _ = InvokeNonPublic(freshCompact.Controller,
            "ResolvePairAirborneEmergency", floor);
        bool freshCompactRejected = !freshCompactCandidate.AttachedAtTip;

        DeerRig ready = NewDeer(Vector3.Zero, parameters);
        DeerLeg readySupport = ready.Legs[0];
        DeerLeg readyCandidate = ready.Legs[1];
        InvalidateConfirmedGripThisTick(readySupport, parameters, floor);
        PrimeEmergencyCandidate(readyCandidate, parameters,
            readyCandidate.GripConfirmTicks - 1);
        _ = InvokeNonPublic(ready.Controller, "ResolvePairAirborneEmergency", floor);
        bool oneTickShortAccepted = readyCandidate.Gripping;

        DeerRig readyCompact = NewDeer(Vector3.Zero, compactParameters);
        DeerLeg readyCompactSupport = readyCompact.Legs[0];
        DeerLeg readyCompactCandidate = readyCompact.Legs[1];
        InvalidateConfirmedGripThisTick(readyCompactSupport, compactParameters, floor);
        PrimeEmergencyCandidate(readyCompactCandidate, compactParameters,
            readyCompactCandidate.GripConfirmTicks - 1);
        _ = InvokeNonPublic(readyCompact.Controller,
            "ResolvePairAirborneEmergency", floor);
        bool oneTickShortCompactAccepted = readyCompactCandidate.Gripping;

        return (noEvidenceRejected && freshOriginalRejected && freshCompactRejected
                && oneTickShortAccepted && oneTickShortCompactAccepted,
            $"noEvidence={noEvidenceRejected} " +
            $"freshOriginal/compact={freshOriginalRejected}/{freshCompactRejected} " +
            $"oneTickShortOriginal/compact={oneTickShortAccepted}/" +
            $"{oneTickShortCompactAccepted}");
    }

    private static void InvalidateConfirmedGripThisTick(
        DeerLeg leg,
        DeerParams parameters,
        ITerrainQuery terrain)
    {
        float clearance = leg.FootRadius + parameters.TerrainClearance;
        Vector3 point = new(leg.Tip.Pos.X, 0f, leg.Tip.Pos.Z);
        SetSyntheticGrip(leg, point, Vector3.Up, point + Vector3.Up * clearance);
        _ = InvokeNonPublic(leg, "BeginControllerTick");
        SetMember(leg, nameof(DeerLeg.GripColliderId), 999UL);
        _ = InvokeNonPublic(leg, "ValidateGripBeforeBody",
            terrain,
            Vector3.Up,
            MathF.Cos(MathF.PI / 180f * parameters.MaxStandableSlopeDegrees),
            parameters.TerrainClearance);
    }

    private static void PrimeEmergencyCandidate(
        DeerLeg leg,
        DeerParams parameters,
        int confirmCounter,
        bool changedThisTick = false)
    {
        float clearance = leg.FootRadius + parameters.TerrainClearance;
        Vector3 point = new(leg.Tip.Pos.X, 0f, leg.Tip.Pos.Z);
        Vector3 center = point + Vector3.Up * clearance;
        SetMember(leg, nameof(DeerLeg.AttachedAtTip), false);
        SetMember(leg, nameof(DeerLeg.GripAge), 0);
        SetMember(leg, nameof(DeerLeg.GripCooldown), 0);
        SetMember(leg, nameof(DeerLeg.HasCandidate), true);
        SetMember(leg, nameof(DeerLeg.CandidatePoint), point);
        SetMember(leg, nameof(DeerLeg.CandidateNormal), Vector3.Up);
        SetMember(leg, nameof(DeerLeg.CandidateColliderId), 1UL);
        SetMember(leg, nameof(DeerLeg.CandidateConfirmCounter), confirmCounter);
        SetMember(leg, "CandidateChangedThisTick", changedThisTick);
        leg.Tip.Pos = center;
        leg.Tip.LastPos = center;
        leg.Tip.Vel = Vector3.Zero;
    }

    private static (bool, string) CheckOverreachAndOcclusionRelease()
    {
        DeerParams overreachParams = DeerFactory.Compact();
        overreachParams.RestDelayTicks = 1000;
        DeerRig overreach = NewDeer(Vector3.Zero, overreachParams);
        var floor = new PlaneTerrainQuery(0f);
        Settle(overreach, floor, 180, Vector3.Zero, 0f);
        int grippingBefore = overreach.Legs.Count(leg => leg.Gripping);
        int forcedBefore = overreach.Legs.Sum(leg => leg.ForcedReleaseSerial);
        foreach (BodyChunk chunk in overreach.Body.Chunks)
        {
            chunk.Pos += Vector3.Right * 8f;
            chunk.LastPos = chunk.Pos;
        }
        overreach.Tick(floor, 181);
        int overreachForced = overreach.Legs.Sum(leg => leg.ForcedReleaseSerial) - forcedBefore;

        DeerParams occludedParams = DeerFactory.Compact();
        occludedParams.RestDelayTicks = 1000;
        DeerRig occluded = NewDeer(Vector3.Zero, occludedParams);
        var blocker = new SelectiveOcclusionTerrain();
        Settle(occluded, blocker, 180, Vector3.Zero, 0f);
        int occlusionBefore = occluded.Legs.Sum(leg => leg.ForcedReleaseSerial);
        blocker.Block(occluded.Legs.Where(leg => leg.Gripping));
        occluded.Tick(blocker, 181);
        int occlusionForced = occluded.Legs.Sum(leg => leg.ForcedReleaseSerial) - occlusionBefore;

        bool ok = grippingBefore >= 3 && overreachForced >= 1 && occlusionForced >= 1;
        return (ok,
            $"grippingBefore={grippingBefore} overreachForced={overreachForced} " +
            $"occlusionForced={occlusionForced}");
    }

    private static (bool, string) CheckReachLimitDrag()
    {
        const float maximumImpulse = 0.02f;
        float below = RunDragProbe(0.80f, maximumImpulse, out float belowVelocity);
        float near = RunDragProbe(0.95f, maximumImpulse, out float nearVelocity);
        float limit = RunDragProbe(0.999f, maximumImpulse, out float limitVelocity);
        bool ok = below == 0f && Math.Abs(belowVelocity) < 1e-7f
            && near > 0f && near < maximumImpulse
            && NearScalar(near, nearVelocity, 1e-6f)
            && limit > near && limit <= maximumImpulse + 1e-7f
            && NearScalar(limit, limitVelocity, 1e-6f);
        return (ok,
            $"below={below:F5}/{belowVelocity:F5} near={near:F5}/{nearVelocity:F5} " +
            $"limit={limit:F5}/{limitVelocity:F5} cap={maximumImpulse:F5}");
    }

    private static float RunDragProbe(
        float reachRatio,
        float maximumImpulse,
        out float anchorVelocity)
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Original());
        DeerLeg leg = deer.Legs[0];
        ClearSyntheticGrips(deer);
        Vector3 center = leg.Anchor.Pos + Vector3.Right * (leg.MaxLength * reachRatio);
        SetSyntheticGrip(leg, center - Vector3.Up * leg.FootRadius, Vector3.Up, center);
        leg.Anchor.Vel = Vector3.Zero;
        object value = InvokeNonPublic(leg, "ApplyBodyDrag", maximumImpulse)
            ?? throw new InvalidOperationException("ApplyBodyDrag returned null.");
        anchorVelocity = leg.Anchor.Vel.Dot(Vector3.Right);
        return (float)value;
    }

    private readonly record struct HesitationResult(
        float Travel,
        float FinalSpeed,
        int ForwardGrips,
        float Hesitation);

    private static (bool, string) CheckHesitation()
    {
        HesitationResult normal = RunHesitation(false);
        HesitationResult disabled = RunHesitation(true);
        float normalDrive = RunHesitationDriveProbe(disabled: false);
        float disabledDrive = RunHesitationDriveProbe(disabled: true);
        bool aheadMissing = normal.ForwardGrips == 0;
        bool weakened = normalDrive < disabledDrive * 0.25f;
        bool ok = aheadMissing && weakened && normal.Hesitation > 0.35f;
        return (ok,
            $"normalTravel/speed={normal.Travel:F3}/{normal.FinalSpeed:F4} " +
            $"disabled={disabled.Travel:F3}/{disabled.FinalSpeed:F4} " +
            $"drive={normalDrive:F5}/{disabledDrive:F5} " +
            $"forwardGrips={normal.ForwardGrips} hesitation={normal.Hesitation:F3}");
    }

    private static float RunHesitationDriveProbe(bool disabled)
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Original());
        deer.MoveDir = Vector3.Right;
        deer.RunSpeed = 1f;
        deer.Controller.EnableHesitation = !disabled;
        SetMember(deer.Controller, nameof(DeerLocomotionController.Hesitation), 1f);
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            chunk.Vel = Vector3.Zero;
        }
        var context = new TickContext(GravityPerTick, new PlaneTerrainQuery(0f), 1);
        _ = InvokeNonPublic(deer.Controller, "ApplyBodyForces",
            context, Vector3.Right, Vector3.Up, 1f, 4);
        float drive = 0f;
        int count = 0;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            if (ReferenceEquals(chunk, deer.Antler))
            {
                continue;
            }
            drive += chunk.Vel.Dot(Vector3.Right);
            count++;
        }
        return drive / Math.Max(count, 1);
    }

    private static HesitationResult RunHesitation(bool disabled)
    {
        DeerParams p = DeerFactory.Compact();
        var terrain = new RearOnlyTerrain();
        DeerRig deer = NewDeer(new Vector3(-0.35f, 0f, 0f), p);
        if (disabled)
        {
            deer.ApplyRuntimeAblation(Ablation.Hesitation);
        }
        Vector3 start = Center(deer);
        for (long tick = 1; tick <= 260; tick++)
        {
            terrain.MaximumFootX = Center(deer).X - 0.08f;
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 1f;
            deer.Tick(terrain, tick);
        }
        int forward = deer.Legs.Count(leg => leg.Gripping
            && (leg.GripPoint - leg.Anchor.Pos).Dot(Vector3.Right) > 0.05f);
        return new HesitationResult(
            Center(deer).X - start.X,
            AverageVelocity(deer).Dot(Vector3.Right),
            forward,
            deer.Hesitation);
    }

    private readonly record struct BalanceResult(
        float ToppleOffset,
        float SupportHalfWidth,
        float LeanDegrees,
        float RecoverySpeed);

    private static (bool, string) CheckBalanceRecovery()
    {
        BalanceResult normal = RunBalanceProbe(disabled: false);
        BalanceResult disabled = RunBalanceProbe(disabled: true);
        bool fixtureToppled = Math.Abs(normal.ToppleOffset) > 0.75f
            && normal.LeanDegrees > DeerFactory.Compact().MaxLeanDegrees;
        bool normalGate = fixtureToppled && normal.RecoverySpeed > 0.003f;
        bool ablatedGate = disabled.RecoverySpeed > 0.003f;
        return (normalGate && !ablatedGate,
            $"offset/halfWidth/lean={normal.ToppleOffset:F3}/{normal.SupportHalfWidth:F3}/" +
            $"{normal.LeanDegrees:F2}deg response={normal.RecoverySpeed:F5} " +
            $"noBalance={disabled.RecoverySpeed:F5}");
    }

    private static BalanceResult RunBalanceProbe(bool disabled)
    {
        DeerParams p = DeerFactory.Compact();
        p.RestDelayTicks = 1000;
        DeerRig deer = NewDeer(Vector3.Zero, p);
        var terrain = new PlaneTerrainQuery(0f);
        Settle(deer, terrain, 180, Vector3.Zero, 0f);
        if (deer.Legs.Count(leg => leg.Gripping) != 4)
        {
            return default;
        }
        deer.Controller.EnableBalanceRecovery = !disabled;
        Vector3 lateral = deer.Controller.Right;
        object initialSnapshot = InvokeNonPublic(deer.Controller, "MeasureSupport", Vector3.Up)
            ?? throw new InvalidOperationException("MeasureSupport returned null.");
        InvokeNonPublic(deer.Controller, "CommitSupport", initialSnapshot, Vector3.Up);
        float leanTarget = MathF.Tan(MathF.PI / 180f * (p.MaxLeanDegrees + 8f))
            * MathF.Max(deer.Controller.CurrentRideHeight, 1f);
        float lateralShift = deer.Controller.SupportHalfWidth + leanTarget;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            chunk.Pos += lateral * lateralShift;
            chunk.LastPos = chunk.Pos;
            chunk.Vel = Vector3.Zero;
        }

        object snapshot = InvokeNonPublic(deer.Controller, "MeasureSupport", Vector3.Up)
            ?? throw new InvalidOperationException("MeasureSupport returned null.");
        InvokeNonPublic(deer.Controller, "CommitSupport", snapshot, Vector3.Up);
        InvokeNonPublic(deer.Controller, "ApplyBalanceRecovery", snapshot, Vector3.Up);
        float recovery = 0f;
        int count = 0;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            if (!ReferenceEquals(chunk, deer.Antler))
            {
                recovery += chunk.Vel.Dot(-lateral);
                count++;
            }
        }
        return new BalanceResult(
            deer.Controller.ToppleOffset,
            deer.Controller.SupportHalfWidth,
            deer.Controller.LeanDegrees,
            recovery / Math.Max(count, 1));
    }

    private readonly record struct WeakGripResult(
        int Releases,
        int RemainingGrips,
        float TotalSupport);

    private static (bool, string) CheckWeakGripReleaseGate()
    {
        WeakGripResult weak = RunWeakGripReleaseProbe(0.08f, 0.02f);
        WeakGripResult weakUrgent = RunWeakGripReleaseProbe(
            0.08f, 0.02f, reachUrgent: true);
        WeakGripResult strong = RunWeakGripReleaseProbe(3.0f, 0.92f);
        bool ok = weak.Releases == 0 && weak.RemainingGrips == 4
            && weakUrgent.Releases == 0 && weakUrgent.RemainingGrips == 4
            && strong.Releases == 1 && strong.RemainingGrips == 3;
        return (ok,
            $"weak releases/grips/support={weak.Releases}/{weak.RemainingGrips}/{weak.TotalSupport:F2} " +
            $"urgent={weakUrgent.Releases}/{weakUrgent.RemainingGrips}/" +
            $"{weakUrgent.TotalSupport:F2} " +
            $"strong={strong.Releases}/{strong.RemainingGrips}/{strong.TotalSupport:F2}");
    }

    private static WeakGripResult RunWeakGripReleaseProbe(
        float rawSupport,
        float perLegSupport,
        bool reachUrgent = false)
    {
        var terrain = new PlaneTerrainQuery(0f);
        DeerParams parameters = DeerFactory.Compact();
        parameters.RestDelayTicks = 1000;
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        Settle(deer, terrain, 180, Vector3.Zero, 0f);
        if (deer.Legs.Count(leg => leg.Gripping) != 4)
        {
            return new WeakGripResult(-1, deer.Legs.Count(leg => leg.Gripping), deer.TotalSupport);
        }

        deer.MoveDir = Vector3.Right;
        deer.RunSpeed = 1f;
        SetMember(deer.Controller, nameof(DeerLocomotionController.TotalSupport),
            Math.Min(rawSupport / 3f, 1f));
        SetMember(deer.Controller, nameof(DeerLocomotionController.RawSupport), rawSupport);
        foreach (DeerLeg leg in deer.Legs)
        {
            SetMember(leg, nameof(DeerLeg.SupportContribution), perLegSupport);
            SetMember(leg, nameof(DeerLeg.GripAge), 120);
            SetMember(leg, nameof(DeerLeg.DesiredGripPoint),
                leg.GripPoint + Vector3.Right * (leg.MaxLength * 1.5f));
        }
        if (reachUrgent)
        {
            DeerLeg urgent = deer.Legs[0];
            Vector3 direction = urgent.Tip.Pos - urgent.Anchor.Pos;
            direction = direction.LengthSquared() > 1e-8f
                ? direction.Normalized()
                : Vector3.Down;
            // 0.85 高于预释放 warning 的 0.82 上限、仍低于 0.98 物理失效门；旧实现会
            // 因此绕开弱支撑门，修复后它只能提高评分，不能授权主动松脚。
            urgent.Tip.Pos = urgent.Anchor.Pos + direction * (urgent.MaxLength * 0.85f);
        }

        int before = deer.Controller.VoluntaryReleaseSerial;
        object support = CreateSupportSnapshot(deer, rawSupport);
        InvokeNonPublic(deer.Controller, "UpdateStepCycle", terrain, Vector3.Right, support);
        return new WeakGripResult(
            deer.Controller.VoluntaryReleaseSerial - before,
            deer.Legs.Count(leg => leg.Gripping),
            rawSupport);
    }

    private static object CreateSupportSnapshot(DeerRig deer, float rawSupport)
    {
        Type snapshotType = typeof(DeerLocomotionController).GetNestedType(
            "SupportSnapshot", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(DeerLocomotionController).FullName,
                "SupportSnapshot");
        return Activator.CreateInstance(snapshotType, new object[]
        {
            4,
            rawSupport,
            Math.Min(rawSupport / 3f, 1f),
            Vector3.Up * rawSupport,
            Vector3.Zero,
            0f,
            1f,
            1f,
            1f,
            true,
            0f,
        }) ?? throw new InvalidOperationException("Could not create Deer support snapshot.");
    }

    private static (bool, string) CheckSlopeAndSteps()
    {
        CourseResult slope = RunCourse(new SlopeTerrain(0.14f), 720);
        CourseResult steps = RunCourse(new StairTerrain(), 1100);
        bool slopeOk = slope.Travel > 5f && slope.MinimumGrips >= 2
            && slope.MaximumHeightError < 0.65f && slope.SamePairAirborneTicks == 0
            && slope.MaximumLean < 58f && slope.Finite;
        bool stepsOk = steps.Travel > 6f && steps.MinimumGrips >= 2
            && steps.MaximumHeightError < 0.70f && steps.SamePairAirborneTicks == 0
            && steps.MaximumLean < 58f && steps.Finite;
        return (slopeOk && stepsOk,
            $"slope travel/grips/heightErr/pairAir/lean={slope.Travel:F2}/{slope.MinimumGrips}/" +
            $"{slope.MaximumHeightError:F2}/{slope.SamePairAirborneTicks}/{slope.MaximumLean:F1} " +
            $"steps={steps.Travel:F2}/{steps.MinimumGrips}/{steps.MaximumHeightError:F2}/" +
            $"{steps.SamePairAirborneTicks}/{steps.MaximumLean:F1}");
    }

    private readonly record struct CourseResult(
        float Travel,
        int MinimumGrips,
        float MaximumHeightError,
        int SamePairAirborneTicks,
        float MaximumLean,
        bool Finite);

    private static CourseResult RunCourse(IHeightTerrain terrain, int ticks)
    {
        DeerParams p = DeerFactory.Compact();
        DeerRig deer = NewDeer(Vector3.Zero, p);
        float startX = Center(deer).X;
        int minimumGrips = 4;
        float maximumHeightError = 0f;
        int samePairAirborneTicks = 0;
        float maximumLean = 0f;
        bool finite = true;
        for (long tick = 1; tick <= ticks; tick++)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.85f;
            deer.Tick(terrain, tick);
            if (tick > 160)
            {
                minimumGrips = Math.Min(minimumGrips, deer.Legs.Count(leg => leg.Gripping));
                float expected = terrain.HeightAt(Center(deer).X) + p.PreferredBodyHeight;
                maximumHeightError = Math.Max(maximumHeightError,
                    Math.Abs(AverageTrunkHeight(deer) - expected));
                for (int pair = 0; pair < 2; pair++)
                {
                    if (deer.Legs.Where(leg => leg.PairIndex == pair)
                        .All(leg => !leg.AttachedAtTip))
                    {
                        samePairAirborneTicks++;
                    }
                }
                maximumLean = Math.Max(maximumLean, deer.LeanDegrees);
            }
            finite &= IsFinite(deer);
        }
        return new CourseResult(
            Center(deer).X - startX, minimumGrips, maximumHeightError,
            samePairAirborneTicks, maximumLean, finite);
    }

    private static (bool, string) CheckMoveTarget()
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Compact());
        var terrain = new PlaneTerrainQuery(0f);
        Settle(deer, terrain, 140, Vector3.Zero, 0f);
        long tick = 140;
        Vector3 start = Center(deer);
        Vector3 target = SurfacePoint(deer) + Vector3.Right * 2.4f;
        deer.MoveTarget = target;
        bool externalOnly = true;
        bool arrived = false;
        for (int i = 0; i < 520; i++)
        {
            tick++;
            deer.MoveDir = Vector3.Left;
            deer.RunSpeed = 1f;
            deer.Tick(terrain, tick);
            if (deer.AtMoveTarget)
            {
                arrived = true;
                break;
            }
            externalOnly &= deer.LastMoveTargetKind == MoveTargetKind.External;
        }
        Vector3 targetDelta = Center(deer) - target;
        targetDelta.Y = 0f;
        float distance = targetDelta.Length();
        bool arrivalStops = arrived && !deer.HasMoveIntent
            && deer.LastMoveTargetKind == MoveTargetKind.None;

        deer.MoveTarget = SurfacePoint(deer) + Vector3.Back * 1.6f;
        Vector3 turnStart = Center(deer);
        for (int i = 0; i < 180; i++)
        {
            tick++;
            deer.RunSpeed = 1f;
            deer.Tick(terrain, tick);
        }
        bool replan = (Center(deer) - turnStart).Dot(Vector3.Back) > 0.2f;

        DeerRig vertical = NewDeer(Vector3.Zero, DeerFactory.Compact());
        Settle(vertical, terrain, 140, Vector3.Zero, 0f);
        vertical.MoveTarget = SurfacePoint(vertical) + Vector3.Up * 5f;
        vertical.RunSpeed = 1f;
        vertical.Tick(terrain, 141);
        bool verticalOffsetRejected = !vertical.AtMoveTarget
            && vertical.LastMoveTargetKind == MoveTargetKind.None;

        DeerRig slopedTarget = NewDeer(Vector3.Zero, DeerFactory.Compact());
        Vector3 syntheticSupport = new Vector3(-0.30f, 1f, 0f).Normalized();
        SetMember(slopedTarget.Controller,
            nameof(DeerLocomotionController.SupportNormal), syntheticSupport);
        SetMember(slopedTarget.Controller,
            nameof(DeerLocomotionController.CurrentRideHeight), 4f);
        Vector3 surfaceTarget = new(2f, 0.8f, 0f);
        InvokeNonPublic(slopedTarget.Controller, "DeriveMoveFromTarget",
            surfaceTarget, Vector3.Up);
        Vector3 expectedVerticalCarrot = (surfaceTarget + Vector3.Up * 4f
            - slopedTarget.Controller.BodyCenter).Normalized();
        bool slopeUsesWorldUp = Near(slopedTarget.MoveDir, expectedVerticalCarrot);

        deer.MoveTarget = null;
        deer.MoveDir = Vector3.Zero;
        deer.RunSpeed = 1f;
        deer.Tick(terrain, ++tick);
        bool cancel = !deer.HasMoveIntent && !deer.AtMoveTarget
            && deer.LastMoveTargetKind == MoveTargetKind.None;
        return (externalOnly && arrivalStops && distance < 0.9f && replan
                && verticalOffsetRejected && slopeUsesWorldUp && cancel,
            $"external={externalOnly} arrived/stopped={arrived}/{arrivalStops} " +
            $"distance={distance:F3}m replan={replan} verticalRejected={verticalOffsetRejected} " +
            $"slopeWorldUp={slopeUsesWorldUp} cancel={cancel}");
    }

    private static (bool, string) CheckLifecycle()
    {
        var terrain = new PlaneTerrainQuery(0f);
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Compact());
        Settle(deer, terrain, 180, Vector3.Right, 0.6f);
        long tick = 180;
        while (tick < 240 && deer.Legs.Count(leg => leg.Gripping) < 3)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.6f;
            deer.Tick(terrain, ++tick);
        }
        bool settled = deer.Legs.Count(leg => leg.Gripping) >= 2
            && Enumerable.Range(0, 2).All(pair =>
                deer.Legs.Any(leg => leg.PairIndex == pair && leg.Gripping));

        deer.MoveTarget = SurfacePoint(deer) + Vector3.Right * 2f;
        RigSnapshot beforeShift = Capture(deer);
        Vector3 shift = new(512f, 3f, -256f);
        deer.Shift(shift);
        bool shiftExact = Capture(deer).EqualsShifted(beforeShift, shift)
            && deer.MoveTarget is { } shiftedTarget
            && Near(shiftedTarget, beforeShift.MoveTarget!.Value + shift);

        bool hadGrip = deer.Legs.Any(leg => leg.Gripping);
        RigSnapshot beforeTeleport = Capture(deer);
        Vector3 teleport = new(4f, 1.2f, 0.5f);
        deer.Teleport(teleport);
        bool teleportNoGrips = deer.Legs.All(leg => !leg.Gripping);
        bool teleportTargetCleared = deer.MoveTarget is null;
        RigSnapshot afterTeleport = Capture(deer);
        bool teleportPositions = afterTeleport.ParticlePositionsShifted(beforeTeleport, teleport);
        bool teleportCachesReset = deer.Legs.All(leg =>
            Near(leg.GripPoint, leg.Tip.Pos)
            && Near(leg.CandidatePoint, leg.Tip.Pos)
            && leg.GripColliderId == 0UL
            && leg.CandidateColliderId == 0UL)
            && NearScalar(deer.Controller.CurrentRideHeight,
                deer.Controller.DesiredBodyHeight)
            && !deer.Controller.HasCurrentFloor
            && !deer.Controller.HasAheadFloor;
        string teleportPositionError = teleportPositions ? "none" : "body-or-leg-particle";
        bool teleportCleared = hadGrip && teleportNoGrips
            && teleportTargetCleared && teleportPositions && teleportCachesReset;
        bool teleportRecovered = Recover(deer, terrain, ref tick, 700);

        deer.MoveTarget = SurfacePoint(deer) + Vector3.Right;
        Vector3 launchTarget = deer.MoveTarget.Value;
        bool launchHadGrip = deer.Legs.Any(leg => leg.Gripping);
        float preLaunchRideHeight = deer.Controller.CurrentRideHeight;
        Vector3[] oldVelocities = deer.Body.Chunks.Select(chunk => chunk.Vel).ToArray();
        Vector3 impulse = new(0.04f, 0.32f, -0.015f);
        deer.Launch(impulse);
        bool launchCleared = launchHadGrip && deer.Legs.All(leg => !leg.Gripping)
            && deer.MoveTarget is { } preservedTarget && Near(preservedTarget, launchTarget)
            && deer.TotalSupport == 0f
            && NearScalar(deer.Controller.CurrentRideHeight, preLaunchRideHeight);
        for (int i = 0; i < deer.Body.Chunks.Count; i++)
        {
            launchCleared &= Near(deer.Body.Chunks[i].Vel, oldVelocities[i] + impulse);
        }
        bool launchRecovered = Recover(deer, terrain, ref tick, 900);

        return (settled && shiftExact && teleportCleared && teleportRecovered
                && launchCleared && launchRecovered,
            $"settled={settled} shift={shiftExact} teleport={teleportCleared}/{teleportRecovered}" +
            $"[grips={teleportNoGrips},target={teleportTargetCleared},positions={teleportPositions}:" +
            $"{teleportPositionError},cacheReset={teleportCachesReset}] " +
            $"launch={launchCleared}/{launchRecovered}");
    }

    private static (bool, string) CheckLaunchTargetReevaluation()
    {
        DeerParams parameters = DeerFactory.Compact();
        parameters.RestDelayTicks = 1000;
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        var terrain = new PlaneTerrainQuery(0f);
        Settle(deer, terrain, 180, Vector3.Right, 0.6f);
        long tick = 180;

        Vector3 target = SurfacePoint(deer);
        deer.MoveTarget = target;
        deer.RunSpeed = 1f;
        deer.Tick(terrain, ++tick);
        bool arrivedBefore = deer.AtMoveTarget;
        float rideBefore = deer.Controller.CurrentRideHeight;

        Vector3 impulse = new(0.8f, 0.30f, -0.02f);
        deer.Launch(impulse);
        bool immediate = arrivedBefore
            && deer.MoveTarget is { } retained && Near(retained, target)
            && NearScalar(deer.Controller.CurrentRideHeight, rideBefore)
            && !deer.AtMoveTarget;

        bool matchedEveryTick = true;
        bool targetPreservedEveryTick = true;
        bool sawTrue = false;
        bool sawFalse = false;
        for (int i = 0; i < 6; i++)
        {
            Vector3 carrot = target + Vector3.Up * deer.Controller.CurrentRideHeight;
            bool expected = carrot.DistanceTo(deer.Controller.BodyCenter)
                <= deer.Controller.MoveTargetArriveRadius;
            deer.Tick(terrain, ++tick);
            matchedEveryTick &= deer.AtMoveTarget == expected;
            targetPreservedEveryTick &= deer.MoveTarget is { } currentTarget
                && Near(currentTarget, target);
            sawTrue |= deer.AtMoveTarget;
            sawFalse |= !deer.AtMoveTarget;
        }

        return (immediate && matchedEveryTick && targetPreservedEveryTick
                && sawTrue && sawFalse && IsFinite(deer),
            $"arrivedBefore={arrivedBefore} immediate={immediate} reeval={matchedEveryTick} " +
            $"sawTrue/False={sawTrue}/{sawFalse} targetRetained={targetPreservedEveryTick} " +
            $"ride={rideBefore:F3}->{deer.Controller.CurrentRideHeight:F3} finite={IsFinite(deer)}");
    }

    private static (bool, string) CheckSteadyStateAllocations()
    {
        DeerParams parameters = DeerFactory.Compact();
        parameters.RestDelayTicks = 2000;
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        var terrain = new PlaneTerrainQuery(0f);
        long tick = 0;
        for (int i = 0; i < 320; i++)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.65f;
            deer.Tick(terrain, ++tick);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.65f;
            deer.Tick(terrain, ++tick);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (allocated == 0 && IsFinite(deer),
            $"steadyTicks=256 allocated={allocated}B finite={IsFinite(deer)}");
    }

    private readonly record struct DeepRestWakeResult(
        bool DeepRestReached,
        int IdleBefore,
        float RestBefore,
        float ReachBefore,
        bool ImmediateWake,
        bool EventContract,
        bool Recovered,
        int RecoveryTick,
        float FinalRideHeight,
        float FinalDesiredHeight,
        float FinalSupport,
        bool Finite);

    private static (bool, string) CheckDeepRestWake()
    {
        DeepRestWakeResult teleport = RunDeepRestWake(launch: false);
        DeepRestWakeResult launch = RunDeepRestWake(launch: true);
        bool ok = WakePassed(teleport) && WakePassed(launch);
        return (ok,
            $"teleport={FormatWake(teleport)} launch={FormatWake(launch)}");
    }

    private static DeepRestWakeResult RunDeepRestWake(bool launch)
    {
        DeerParams parameters = DeerFactory.Compact();
        DeerRig deer = NewDeer(Vector3.Zero, parameters);
        var terrain = new PlaneTerrainQuery(0f);
        long tick = 0;
        for (int i = 0; i < 900; i++)
        {
            deer.MoveDir = Vector3.Zero;
            deer.RunSpeed = 0f;
            deer.Tick(terrain, ++tick);
        }

        bool pairsBefore = BothPairsAttached(deer);
        bool deepRestReached = deer.Controller.RestAmount >= 0.99f
            && deer.Controller.CurrentLegReachScale
                <= parameters.RestLegReachRatio + 0.01f
            && pairsBefore && IsFinite(deer);
        int idleBefore = deer.Controller.IdleTicks;
        float restBefore = deer.Controller.RestAmount;
        float reachBefore = deer.Controller.CurrentLegReachScale;
        float rideBefore = deer.Controller.CurrentRideHeight;
        Vector3[] velocitiesBefore = deer.Body.Chunks.Select(chunk => chunk.Vel).ToArray();
        Vector3 impulse = new(0.035f, 0.32f, -0.012f);
        SetMember(deer.Controller, nameof(DeerLocomotionController.Hesitation), 0.75f);

        bool eventContract;
        if (launch)
        {
            deer.Launch(impulse);
            eventContract = NearScalar(deer.Controller.CurrentRideHeight, rideBefore)
                && deer.Legs.All(leg => !leg.AttachedAtTip)
                && deer.TotalSupport == 0f
                && deer.Controller.Hesitation == 0f
                && !deer.Controller.AtMoveTarget;
            for (int i = 0; i < deer.Body.Chunks.Count; i++)
            {
                eventContract &= Near(deer.Body.Chunks[i].Vel, velocitiesBefore[i] + impulse);
            }
        }
        else
        {
            deer.Teleport(new Vector3(3f, 1.4f, 0.25f));
            eventContract = NearScalar(
                    deer.Controller.CurrentRideHeight, parameters.PreferredBodyHeight)
                && deer.MoveTarget is null
                && deer.Legs.All(leg => !leg.AttachedAtTip)
                && deer.TotalSupport == 0f
                && deer.Controller.Hesitation == 0f;
        }

        bool immediateWake = deer.Controller.IdleTicks == 0
            && deer.Controller.RestAmount == 0f
            && NearScalar(deer.Controller.CurrentLegReachScale, 1f)
            && NearScalar(deer.Controller.DesiredBodyHeight, parameters.PreferredBodyHeight)
            && deer.Legs.All(leg => NearScalar(
                leg.CurrentReachLimit, leg.MaxLength, 1e-5f));

        int stableTicks = 0;
        int recoveryTick = 0;
        // 必须在正式 Compact 再次取得休息资格前完成；放宽测试参数会掩盖慢恢复回归。
        for (int i = 1; i <= parameters.RestDelayTicks; i++)
        {
            deer.MoveDir = Vector3.Zero;
            deer.RunSpeed = 0f;
            deer.Tick(terrain, ++tick);
            bool stable = deer.Controller.RestAmount <= 0.02f
                && deer.Controller.CurrentLegReachScale >= 0.98f
                && NearScalar(deer.Controller.DesiredBodyHeight,
                    parameters.PreferredBodyHeight, 1e-4f)
                && BothPairsAttached(deer)
                && deer.TotalSupport >= 0.55f
                && Math.Abs(deer.Controller.CurrentRideHeight
                    - deer.Controller.DesiredBodyHeight) <= 0.65f
                && AverageVelocity(deer).Length() < 0.20f
                && IsFinite(deer);
            stableTicks = stable ? stableTicks + 1 : 0;
            if (stableTicks >= 20)
            {
                recoveryTick = i;
                break;
            }
        }

        return new DeepRestWakeResult(
            deepRestReached,
            idleBefore,
            restBefore,
            reachBefore,
            immediateWake,
            eventContract,
            recoveryTick > 0,
            recoveryTick,
            deer.Controller.CurrentRideHeight,
            deer.Controller.DesiredBodyHeight,
            deer.TotalSupport,
            IsFinite(deer));
    }

    private static bool WakePassed(DeepRestWakeResult result) =>
        result.DeepRestReached && result.ImmediateWake && result.EventContract
        && result.Recovered && result.Finite;

    private static string FormatWake(DeepRestWakeResult result) =>
        $"deep={result.DeepRestReached}[idle={result.IdleBefore},rest={result.RestBefore:F3}," +
        $"reach={result.ReachBefore:F3}] immediate={result.ImmediateWake} " +
        $"contract={result.EventContract} recovered={result.Recovered}@{result.RecoveryTick} " +
        $"ride/desired={result.FinalRideHeight:F3}/{result.FinalDesiredHeight:F3} " +
        $"support={result.FinalSupport:F3} finite={result.Finite}";

    private static bool BothPairsAttached(DeerRig deer) =>
        Enumerable.Range(0, 2).All(pair =>
            deer.Legs.Any(leg => leg.PairIndex == pair && leg.AttachedAtTip));

    private static (bool, string) CheckHashStateForks()
    {
        ulong baseline = HashFreshDeer(_ => { });
        var forks = new (string Name, Action<DeerRig> Change)[]
        {
            ("arrive-radius", deer => deer.Controller.MoveTargetArriveRadius += 0.03125f),
            ("enable-support", deer => deer.Controller.EnableSupport = false),
            ("enable-pair", deer => deer.Controller.EnablePairInterlock = false),
            ("enable-hesitation", deer => deer.Controller.EnableHesitation = false),
            ("enable-release", deer => deer.Controller.EnableStepRelease = false),
            ("enable-balance", deer => deer.Controller.EnableBalanceRecovery = false),
            ("enable-antler", deer => deer.Controller.EnableAntlerPosture = false),
            ("enable-anatomical-bend", deer => deer.Controller.EnableAnatomicalBend = false),
            ("body-air-friction", deer => deer.Body.AirFriction -= 0.03125f),
            ("body-surface-friction", deer => deer.Body.SurfaceFriction += 0.03125f),
            ("body-iterations", deer => deer.Body.ConstraintIterations += 1),
            ("body-skin", deer => deer.Body.Skin += 0.03125f),
            ("body-snag-ratio", deer => deer.Body.SnagStretchRatio += 0.03125f),
            ("body-snag-ticks", deer => deer.Body.SnagReleaseTicks += 1),
            ("body-structure-recovery", deer =>
                deer.Body.EnablePostCollisionStructureRecovery =
                    !deer.Body.EnablePostCollisionStructureRecovery),
            ("max-pair-airborne", deer =>
                SetMember(deer.Controller,
                    nameof(DeerLocomotionController.MaxPairAirborneRun), 1)),
            ("body-contact", deer =>
            {
                deer.Head.TerrainContact = true;
                deer.Head.HadContactLastTick = true;
                deer.Head.ContactNormal = Vector3.Up;
            }),
            ("connection-snag", deer => deer.Body.Connections[0].SnagTicks = 1),
        };
        var missing = new List<string>();
        foreach ((string name, Action<DeerRig> change) in forks)
        {
            if (HashFreshDeer(change) == baseline)
            {
                missing.Add(name);
            }
        }
        return (missing.Count == 0,
            $"baseline={baseline:X16} forked={forks.Length - missing.Count}/{forks.Length} " +
            $"missing=[{string.Join(',', missing)}]");
    }

    private static ulong HashFreshDeer(Action<DeerRig> change)
    {
        DeerRig deer = NewDeer(Vector3.Zero, DeerFactory.Compact());
        change(deer);
        var hasher = new DeterminismHasher();
        FoldDeer(hasher, deer);
        return hasher.Value;
    }

    private static (bool, string) CheckAblations()
    {
        FlatResult normalSupport = RunFlat(Ablation.None);
        FlatResult noSupport = RunFlat(Ablation.Support);
        FlatResult lowStance = RunFlat(Ablation.Stance);
        FlatResult noAntlerPosture = RunFlat(Ablation.Antler);
        PairStressResult pairNormal = RunPairStress(false);
        PairStressResult pairDisabled = RunPairStress(true);
        HesitationResult hesitationNormal = RunHesitation(false);
        HesitationResult hesitationDisabled = RunHesitation(true);
        float hesitationNormalDrive = RunHesitationDriveProbe(disabled: false);
        float hesitationDisabledDrive = RunHesitationDriveProbe(disabled: true);
        ReleaseProbeResult releaseNormal = RunReleaseProbe(false);
        ReleaseProbeResult releaseDisabled = RunReleaseProbe(true);
        BendReversalResult bendNormal = RunBendReversal(ablate: false);
        BendReversalResult bendDisabled = RunBendReversal(ablate: true);
        (bool bendFrameNormal, _) = RunBendFrameTransport(ablate: false);
        (bool bendFrameDisabled, _) = RunBendFrameTransport(ablate: true);

        bool supportRed = normalSupport.SupportGate && !noSupport.SupportGate;
        supportRed &= normalSupport.AverageHeight > noSupport.AverageHeight + 0.20f
            && normalSupport.BodyContactRatio + 0.25f < noSupport.BodyContactRatio;
        bool pairRed = pairNormal.SamePairAirborneTicks == 0
            && pairDisabled.SamePairAirborneTicks > 0;
        bool hesitationRed = hesitationNormal.Hesitation > 0.35f
            && hesitationNormalDrive < hesitationDisabledDrive * 0.25f;
        bool releaseRed = releaseNormal.VoluntaryReleases >= 2
            && releaseDisabled.VoluntaryReleases == 0;
        bool stanceRed = normalSupport.StanceGate && !lowStance.StanceGate;
        bool antlerRed = normalSupport.AntlerGate && !noAntlerPosture.AntlerGate;
        bool bendRed = bendNormal.Gate && bendFrameNormal
            && !bendDisabled.Gate && !bendFrameDisabled;
        return (supportRed && pairRed && hesitationRed && releaseRed && stanceRed && antlerRed
                && bendRed,
            $"support={supportRed} heights={normalSupport.AverageHeight:F2}/{noSupport.AverageHeight:F2} " +
            $"contacts={normalSupport.BodyContactRatio:P0}/{noSupport.BodyContactRatio:P0}; " +
            $"pair={pairRed} air={pairNormal.SamePairAirborneTicks}/{pairDisabled.SamePairAirborneTicks}; " +
            $"hesitation={hesitationRed} drive={hesitationNormalDrive:F4}/{hesitationDisabledDrive:F4} " +
            $"travel={hesitationNormal.Travel:F2}/{hesitationDisabled.Travel:F2}; " +
            $"release={releaseRed} voluntary={releaseNormal.VoluntaryReleases}/" +
            $"{releaseDisabled.VoluntaryReleases} totalSteps={releaseNormal.TotalSteps}/" +
            $"{releaseDisabled.TotalSteps}; " +
            $"stance={stanceRed} heights={normalSupport.AverageHeight:F2}/{lowStance.AverageHeight:F2}; " +
            $"antler={antlerRed} up={normalSupport.MinimumAntlerUp:F2}/" +
            $"{noAntlerPosture.MinimumAntlerUp:F2}; bend={bendRed} " +
            $"frame={bendFrameNormal}/{bendFrameDisabled}");
    }

    private readonly record struct PairStressResult(int SamePairAirborneTicks);

    private static PairStressResult RunPairStress(bool disabled)
    {
        DeerParams p = DeerFactory.Compact();
        p.ReleaseWhenPlantedAbove = 2;
        p.MinimumPlantedLegs = 1;
        p.ReleaseScoreThreshold = 0.05f;
        p.LegSlots[0].ForwardSplay = -0.72f;
        p.LegSlots[1].ForwardSplay = -0.68f;
        p.LegSlots[2].ForwardSplay = 0.68f;
        p.LegSlots[3].ForwardSplay = 0.72f;
        DeerRig deer = NewDeer(Vector3.Zero, p);
        if (disabled)
        {
            deer.ApplyRuntimeAblation(Ablation.Pair);
        }
        var terrain = new PlaneTerrainQuery(0f);
        int bothAir = 0;
        for (long tick = 1; tick <= 520; tick++)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.80f;
            deer.Tick(terrain, tick);
            if (tick > 160)
            {
                for (int pair = 0; pair < 2; pair++)
                {
                    if (deer.Legs.Where(leg => leg.PairIndex == pair)
                        .All(leg => !leg.AttachedAtTip))
                    {
                        bothAir++;
                    }
                }
            }
        }
        return new PairStressResult(bothAir);
    }

    private readonly record struct ReleaseProbeResult(int VoluntaryReleases, int TotalSteps);

    private static ReleaseProbeResult RunReleaseProbe(bool disabled)
    {
        DeerParams p = DeerFactory.Compact();
        DeerRig deer = NewDeer(Vector3.Zero, p);
        if (disabled)
        {
            deer.ApplyRuntimeAblation(Ablation.Release);
        }
        var terrain = new PlaneTerrainQuery(0f);
        for (long tick = 1; tick <= 300; tick++)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.38f;
            deer.Tick(terrain, tick);
        }
        return new ReleaseProbeResult(
            deer.Controller.VoluntaryReleaseSerial,
            deer.Legs.Sum(leg => leg.StepSerial));
    }

    private static int RunIntentionalAblation(Ablation ablation)
    {
        bool gateStillPasses;
        string message;
        switch (ablation)
        {
            case Ablation.Support:
                FlatResult support = RunFlat(Ablation.Support);
                gateStillPasses = support.SupportGate;
                message = $"supportGate={support.SupportGate} avgHeight={support.AverageHeight:F3}";
                break;
            case Ablation.Pair:
                PairStressResult pair = RunPairStress(true);
                gateStillPasses = pair.SamePairAirborneTicks == 0;
                message = $"pairGate={gateStillPasses} samePairAir={pair.SamePairAirborneTicks}";
                break;
            case Ablation.Hesitation:
                HesitationResult normal = RunHesitation(false);
                float normalDrive = RunHesitationDriveProbe(disabled: false);
                float noHesitationDrive = RunHesitationDriveProbe(disabled: true);
                gateStillPasses = normal.Hesitation > 0.35f
                    && noHesitationDrive < normalDrive * 1.25f;
                message = $"hesitationGate={gateStillPasses} " +
                    $"drive={normalDrive:F5}/{noHesitationDrive:F5}";
                break;
            case Ablation.Release:
                ReleaseProbeResult release = RunReleaseProbe(true);
                gateStillPasses = release.VoluntaryReleases >= 2;
                message = $"releaseGate={gateStillPasses} voluntary={release.VoluntaryReleases} " +
                    $"totalSteps={release.TotalSteps}";
                break;
            case Ablation.Balance:
                BalanceResult balance = RunBalanceProbe(disabled: true);
                gateStillPasses = balance.RecoverySpeed > 0.003f;
                message = $"balanceGate={gateStillPasses} " +
                    $"offset={balance.ToppleOffset:F3} lean={balance.LeanDegrees:F1} " +
                    $"recovery={balance.RecoverySpeed:F4}";
                break;
            case Ablation.Stance:
                FlatResult stance = RunFlat(Ablation.Stance);
                gateStillPasses = stance.StanceGate;
                message = $"stanceGate={stance.StanceGate} height={stance.AverageHeight:F3} " +
                    $"clearance={stance.MinimumBodyClearance:F3}";
                break;
            case Ablation.Antler:
                FlatResult antler = RunFlat(Ablation.Antler);
                gateStillPasses = antler.AntlerGate;
                message = $"antlerGate={antler.AntlerGate} up={antler.MinimumAntlerUp:F3} " +
                    $"intrusion={antler.MaximumAntlerTrunkIntrusionRatio:F3}";
                break;
            case Ablation.Bend:
                BendReversalResult bend = RunBendReversal(ablate: true);
                (bool frameGate, string frameMessage) = RunBendFrameTransport(ablate: true);
                // 两个子门必须各自变红；任一仍通过都是消融未能证明该机制。
                gateStillPasses = bend.Gate || frameGate;
                message = $"bendGate={bend.Gate} frameGate={frameGate} " +
                    $"frame{{{frameMessage}}} {bend.Message}";
                break;
            default:
                return 2;
        }
        Console.WriteLine($"[DEER-CORE-ABLATE-{ablation.ToString().ToUpperInvariant()}] " +
            $"{(gateStillPasses ? "UNEXPECTED-PASS" : "EXPECTED-FAIL")} {message}");
        // 消融命令的契约是制造红灯：机制门失败时刻意返回 1。
        return gateStillPasses ? 0 : 1;
    }

    private static bool TryParseAblation(
        string[] args,
        out Ablation ablation,
        out string error)
    {
        ablation = Ablation.None;
        error = string.Empty;
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--ablate=", StringComparison.Ordinal))
            {
                error = $"unknown argument '{arg}'";
                return false;
            }
            string value = arg["--ablate=".Length..];
            ablation = value switch
            {
                "support" => Ablation.Support,
                "pair" => Ablation.Pair,
                "hesitation" => Ablation.Hesitation,
                "release" => Ablation.Release,
                "balance" or "righting" => Ablation.Balance,
                "stance" => Ablation.Stance,
                "antler" => Ablation.Antler,
                "bend" => Ablation.Bend,
                _ => Ablation.None,
            };
            if (ablation == Ablation.None)
            {
                error = $"unknown ablation '{value}'";
                return false;
            }
        }
        return true;
    }

    private enum Ablation
    {
        None,
        Support,
        Pair,
        Hesitation,
        Release,
        Balance,
        Stance,
        Antler,
        Bend,
    }

    private static DeerRig NewDeer(Vector3 origin, DeerParams parameters) =>
        new(DeerFactory.CreateController(origin, Vector3.Right, parameters));

    private static void Settle(
        DeerRig deer,
        ITerrainQuery terrain,
        int ticks,
        Vector3 direction,
        float speed)
    {
        for (long tick = 1; tick <= ticks; tick++)
        {
            deer.MoveDir = direction;
            deer.RunSpeed = speed;
            deer.Tick(terrain, tick);
        }
    }

    private static bool Recover(
        DeerRig deer,
        ITerrainQuery terrain,
        ref long tick,
        int budget)
    {
        for (int i = 0; i < budget; i++)
        {
            deer.MoveDir = Vector3.Right;
            deer.RunSpeed = 0.7f;
            deer.Tick(terrain, ++tick);
            if (deer.Legs.Count(leg => leg.Gripping) >= 3
                && AverageTrunkHeight(deer) > 0.45f
                && AverageVelocity(deer).Length() < 0.25f)
            {
                return true;
            }
        }
        return false;
    }

    private static void FoldDeer(DeterminismHasher hasher, DeerRig deer)
    {
        hasher.FoldBody(deer.Body);
        deer.FoldDeterministicState(hasher);
    }

    private static Vector3 Center(DeerRig deer)
    {
        Vector3 sum = Vector3.Zero;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            sum += chunk.Pos;
        }
        return sum / deer.Body.Chunks.Count;
    }

    private static Vector3 SurfacePoint(DeerRig deer)
    {
        if (deer.Controller.HasCurrentFloor)
        {
            return deer.Controller.CurrentFloorPoint;
        }
        Vector3 center = Center(deer);
        return new Vector3(center.X, 0f, center.Z);
    }

    private static Vector3 AverageVelocity(DeerRig deer)
    {
        Vector3 sum = Vector3.Zero;
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            sum += chunk.Vel;
        }
        return sum / deer.Body.Chunks.Count;
    }

    private static float AverageTrunkHeight(DeerRig deer) =>
        deer.Trunk.Average(chunk => chunk.Pos.Y);

    private static bool IsFinite(DeerRig deer)
    {
        foreach (BodyChunk chunk in deer.Body.Chunks)
        {
            if (!Finite(chunk.Pos) || !Finite(chunk.LastPos) || !Finite(chunk.Vel))
            {
                return false;
            }
        }
        foreach (DeerLeg leg in deer.Legs)
        {
            if (!Finite(leg.BendPole) || !float.IsFinite(leg.SupportContribution)
                || !float.IsFinite(leg.MaxConstraintError))
            {
                return false;
            }
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                if (!Finite(segment.Pos) || !Finite(segment.LastPos) || !Finite(segment.Vel))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f ? value.Normalized() : fallback;

    private static bool Near(Vector3 a, Vector3 b, float epsilon = 1e-5f) =>
        a.DistanceSquaredTo(b) <= epsilon * epsilon;

    private static bool NearScalar(float a, float b, float epsilon = 1e-6f) =>
        Math.Abs(a - b) <= epsilon;

    private static void SetMember<T>(object target, string name, T value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = target.GetType().GetProperty(name, flags);
        if (property?.SetMethod is not null)
        {
            property.SetValue(target, value);
            return;
        }
        FieldInfo? field = target.GetType().GetField(name, flags);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }
        throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static object? InvokeNonPublic(object target, string name, params object[] arguments)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo method = target.GetType().GetMethods(flags)
            .Single(candidate => candidate.Name == name
                && candidate.GetParameters().Length == arguments.Length);
        return method.Invoke(target, arguments);
    }

    /// <summary>只包装固定重力的 TickContext；其余均为强类型正式宿主面。</summary>
    private sealed class DeerRig
    {
        private readonly DeerLocomotionController _controller;

        public DeerRig(DeerLocomotionController controller) =>
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        public Body Body => _controller.Body;
        public DeerLocomotionController Controller => _controller;
        public BodyChunk Head => _controller.Head;
        public BodyChunk Antler => _controller.Antler;
        public IReadOnlyList<BodyChunk> Trunk => _controller.Trunk;
        public IReadOnlyList<DeerLeg> Legs => _controller.Legs;
        public Vector3 SupportNormal => _controller.SupportNormal;
        public Vector3 Forward => _controller.Forward;
        public int LegsGripping => _controller.LegsGripping;
        public bool AtMoveTarget => _controller.AtMoveTarget;
        public bool HasMoveIntent => _controller.HasMoveIntent;
        public MoveTargetKind LastMoveTargetKind => _controller.LastMoveTargetKind;
        public Vector3 LastMoveTarget => _controller.LastMoveTarget;
        public float TotalSupport => _controller.TotalSupport;
        public float Hesitation => _controller.Hesitation;
        public float DesiredBodyHeight => _controller.DesiredBodyHeight;
        public float ActualBodyHeight => _controller.ActualBodyHeight;
        public float LeanDegrees => _controller.LeanDegrees;

        public Vector3 MoveDir
        {
            get => _controller.MoveDir;
            set => _controller.MoveDir = value;
        }

        public float RunSpeed
        {
            get => _controller.RunSpeed;
            set => _controller.RunSpeed = value;
        }

        public Vector3? MoveTarget
        {
            get => _controller.MoveTarget;
            set => _controller.MoveTarget = value;
        }

        public void Tick(ITerrainQuery terrain, long tick)
        {
            var context = new TickContext(GravityPerTick, terrain, tick);
            _controller.Tick(context);
        }

        public void Shift(Vector3 delta) => _controller.Shift(delta);
        public void Teleport(Vector3 delta) => _controller.Teleport(delta);
        public void Launch(Vector3 velocity) => _controller.Launch(velocity);
        public void FoldDeterministicState(DeterminismHasher hasher) =>
            _controller.FoldDeterministicState(hasher);

        public void ApplyRuntimeAblation(Ablation ablation)
        {
            switch (ablation)
            {
                case Ablation.Support:
                    _controller.EnableSupport = false;
                    break;
                case Ablation.Pair:
                    _controller.EnablePairInterlock = false;
                    break;
                case Ablation.Hesitation:
                    _controller.EnableHesitation = false;
                    break;
                case Ablation.Release:
                    _controller.EnableStepRelease = false;
                    break;
                case Ablation.Balance:
                    _controller.EnableBalanceRecovery = false;
                    break;
                case Ablation.Antler:
                    _controller.EnableAntlerPosture = false;
                    break;
                case Ablation.Bend:
                    _controller.EnableAnatomicalBend = false;
                    break;
            }
        }
    }

    private readonly record struct ChunkSnapshot(Vector3 Pos, Vector3 LastPos, Vector3 Vel);
    private readonly record struct SegmentSnapshot(Vector3 Pos, Vector3 LastPos, Vector3 Vel);

    private sealed class RigSnapshot
    {
        public required ChunkSnapshot[] Chunks { get; init; }
        public required SegmentSnapshot[][] Legs { get; init; }
        public required Vector3[] Desired { get; init; }
        public required Vector3[] Grip { get; init; }
        public required Vector3[] Candidate { get; init; }
        public required Vector3[] LastRelease { get; init; }
        public required Vector3[] LastLanding { get; init; }
        public required Vector3? MoveTarget { get; init; }

        public bool EqualsShifted(RigSnapshot before, Vector3 delta) =>
            PositionsShifted(before, delta)
            && Chunks.Select(c => c.Vel).SequenceEqual(before.Chunks.Select(c => c.Vel))
            && Legs.SelectMany(s => s).Select(s => s.Vel)
                .SequenceEqual(before.Legs.SelectMany(s => s).Select(s => s.Vel));

        public bool PositionsShifted(RigSnapshot before, Vector3 delta)
            => FirstShiftError(before, delta) == "none";

        public bool ParticlePositionsShifted(RigSnapshot before, Vector3 delta)
        {
            if (Chunks.Length != before.Chunks.Length || Legs.Length != before.Legs.Length)
            {
                return false;
            }
            for (int i = 0; i < Chunks.Length; i++)
            {
                if (!Near(Chunks[i].Pos, before.Chunks[i].Pos + delta)
                    || !Near(Chunks[i].LastPos, before.Chunks[i].LastPos + delta))
                {
                    return false;
                }
            }
            for (int i = 0; i < Legs.Length; i++)
            {
                for (int j = 0; j < Legs[i].Length; j++)
                {
                    if (!Near(Legs[i][j].Pos, before.Legs[i][j].Pos + delta)
                        || !Near(Legs[i][j].LastPos, before.Legs[i][j].LastPos + delta))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public string FirstShiftError(RigSnapshot before, Vector3 delta)
        {
            if (Chunks.Length != before.Chunks.Length || Legs.Length != before.Legs.Length)
            {
                return "topology";
            }
            for (int i = 0; i < Chunks.Length; i++)
            {
                if (!Near(Chunks[i].Pos, before.Chunks[i].Pos + delta)
                    || !Near(Chunks[i].LastPos, before.Chunks[i].LastPos + delta))
                {
                    return $"chunk[{i}]";
                }
            }
            for (int i = 0; i < Legs.Length; i++)
            {
                for (int j = 0; j < Legs[i].Length; j++)
                {
                    if (!Near(Legs[i][j].Pos, before.Legs[i][j].Pos + delta)
                        || !Near(Legs[i][j].LastPos, before.Legs[i][j].LastPos + delta))
                    {
                        return $"leg[{i}].segment[{j}]";
                    }
                }
                if (!Near(Desired[i], before.Desired[i] + delta)) return $"leg[{i}].desired";
                if (!Near(Grip[i], before.Grip[i] + delta)) return $"leg[{i}].grip";
                if (!Near(Candidate[i], before.Candidate[i] + delta)) return $"leg[{i}].candidate";
                if (!Near(LastRelease[i], before.LastRelease[i] + delta)) return $"leg[{i}].release";
                if (!Near(LastLanding[i], before.LastLanding[i] + delta)) return $"leg[{i}].landing";
            }
            return "none";
        }
    }

    private static RigSnapshot Capture(DeerRig deer) => new()
    {
        Chunks = deer.Body.Chunks
            .Select(c => new ChunkSnapshot(c.Pos, c.LastPos, c.Vel)).ToArray(),
        Legs = deer.Legs.Select(leg => leg.Segments
            .Select(s => new SegmentSnapshot(s.Pos, s.LastPos, s.Vel)).ToArray()).ToArray(),
        Desired = deer.Legs.Select(leg => leg.DesiredGripPoint).ToArray(),
        Grip = deer.Legs.Select(leg => leg.GripPoint).ToArray(),
        Candidate = deer.Legs.Select(leg => leg.CandidatePoint).ToArray(),
        LastRelease = deer.Legs.Select(leg => leg.LastReleasePoint).ToArray(),
        LastLanding = deer.Legs.Select(leg => leg.LastLandingPoint).ToArray(),
        MoveTarget = deer.MoveTarget,
    };

    private interface IHeightTerrain : ITerrainQuery
    {
        float HeightAt(float x);
    }

    private abstract class HeightTerrain : IHeightTerrain
    {
        public abstract float HeightAt(float x);
        protected abstract Vector3 NormalAt(float x);

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            float fromDistance = SignedDistance(from);
            if (fromDistance <= 0f)
            {
                hit = new TerrainHit(from, Vector3.Zero, 1UL);
                return true;
            }

            Vector3 previous = from;
            float previousDistance = fromDistance;
            const int samples = 80;
            for (int i = 1; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 current = from.Lerp(to, t);
                float currentDistance = SignedDistance(current);
                if (currentDistance <= 0f && previousDistance > 0f)
                {
                    Vector3 low = previous;
                    Vector3 high = current;
                    for (int iteration = 0; iteration < 14; iteration++)
                    {
                        Vector3 middle = low.Lerp(high, 0.5f);
                        if (SignedDistance(middle) > 0f)
                        {
                            low = middle;
                        }
                        else
                        {
                            high = middle;
                        }
                    }
                    Vector3 point = high;
                    point.Y = HeightAt(point.X);
                    hit = new TerrainHit(point, NormalAt(point.X), 1UL);
                    return true;
                }
                previous = current;
                previousDistance = currentDistance;
            }
            return false;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            pushDir = NormalAt(center.X);
            float signed = SignedDistance(center) * Math.Max(pushDir.Y, 0.15f);
            depth = radius - signed;
            return depth > 0f;
        }

        private float SignedDistance(Vector3 point) => point.Y - HeightAt(point.X);
    }

    private sealed class CourseTerrain : HeightTerrain
    {
        public override float HeightAt(float x)
        {
            if (x <= 2f) return 0f;
            if (x <= 5.5f) return (x - 2f) * 0.12f;
            if (x <= 8f) return 0.42f;
            int step = Math.Clamp((int)Math.Floor((x - 8f) / 1.25f) + 1, 0, 4);
            return 0.42f + step * 0.11f;
        }

        protected override Vector3 NormalAt(float x) =>
            x is > 2f and < 5.5f
                ? new Vector3(-0.12f, 1f, 0f).Normalized()
                : Vector3.Up;
    }

    private sealed class SlopeTerrain : HeightTerrain
    {
        private readonly float _slope;
        public SlopeTerrain(float slope) => _slope = slope;
        public override float HeightAt(float x) => Math.Max(0f, x * _slope);
        protected override Vector3 NormalAt(float x) =>
            x > 0f ? new Vector3(-_slope, 1f, 0f).Normalized() : Vector3.Up;
    }

    private sealed class StairTerrain : HeightTerrain
    {
        public override float HeightAt(float x)
        {
            if (x <= 1f) return 0f;
            int step = Math.Clamp((int)Math.Floor((x - 1f) / 1.25f) + 1, 0, 7);
            return step * 0.12f;
        }

        protected override Vector3 NormalAt(float x)
        {
            _ = x;
            return Vector3.Up;
        }
    }

    private sealed class HysteresisTerrain : ITerrainQuery
    {
        private readonly PlaneTerrainQuery _floor = new(0f);
        private readonly List<CandidateOffer> _offers = new();
        public int ImprovedHitCount { get; private set; }

        public void OfferSmallImprovement(IEnumerable<DeerLeg> legs, float fraction)
        {
            _offers.Clear();
            foreach (DeerLeg leg in legs)
            {
                if (!leg.HasCandidate)
                {
                    continue;
                }
                Vector3 desiredOnSurface = new(
                    leg.DesiredGripPoint.X, leg.CandidatePoint.Y, leg.DesiredGripPoint.Z);
                Vector3 improved = leg.CandidatePoint.Lerp(desiredOnSurface, fraction);
                _offers.Add(new CandidateOffer(
                    leg.Anchor.Pos,
                    leg.CandidatePoint,
                    leg.DesiredGripPoint,
                    improved,
                    leg.FootRadius));
            }
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            CandidateOffer? nearest = null;
            float nearestScore = float.MaxValue;
            foreach (CandidateOffer offer in _offers)
            {
                if (from.DistanceTo(offer.Anchor) > 0.45f)
                {
                    continue;
                }

                // PathClear 既会查旧候选中心，也会查略优候选中心；返回离终点最近的那块
                // 表面，避免把新候选误判为被旧表面遮挡。两块表面本身都继续存在。
                Vector3 oldCenter = offer.OldPoint + Vector3.Up * (offer.FootRadius * 1.25f);
                Vector3 improvedCenter = offer.ImprovedPoint + Vector3.Up * (offer.FootRadius * 1.25f);
                float oldEndpointDistance = to.DistanceTo(oldCenter);
                float improvedEndpointDistance = to.DistanceTo(improvedCenter);
                if (Math.Min(oldEndpointDistance, improvedEndpointDistance) <= offer.FootRadius * 3.5f)
                {
                    Vector3 endpoint = improvedEndpointDistance < oldEndpointDistance
                        ? offer.ImprovedPoint
                        : offer.OldPoint;
                    if (endpoint == offer.ImprovedPoint)
                    {
                        ImprovedHitCount++;
                    }
                    hit = new TerrainHit(endpoint, Vector3.Up, 1UL);
                    return true;
                }

                float score = to.DistanceSquaredTo(offer.DesiredPoint);
                if (score < nearestScore)
                {
                    nearest = offer;
                    nearestScore = score;
                }
            }
            if (nearest is { } candidate)
            {
                ImprovedHitCount++;
                hit = new TerrainHit(candidate.ImprovedPoint, Vector3.Up, 1UL);
                return true;
            }
            return _floor.Raycast(from, to, out hit);
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            return _floor.SpherePenetration(center, radius, out pushDir, out depth);
        }

        private readonly record struct CandidateOffer(
            Vector3 Anchor,
            Vector3 OldPoint,
            Vector3 DesiredPoint,
            Vector3 ImprovedPoint,
            float FootRadius);
    }

    private sealed class RearOnlyTerrain : ITerrainQuery
    {
        public float MaximumFootX;
        private readonly PlaneTerrainQuery _floor = new(0f);

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            if (!_floor.Raycast(from, to, out hit))
            {
                return false;
            }
            if (hit.Normal.LengthSquared() > 0f && hit.Point.X > MaximumFootX
                && (to - from).Length() > 0.3f)
            {
                hit = default;
                return false;
            }
            return true;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth) =>
            _floor.SpherePenetration(center, radius, out pushDir, out depth);
    }

    private sealed class SelectiveOcclusionTerrain : ITerrainQuery
    {
        private readonly PlaneTerrainQuery _floor = new(0f);
        private readonly List<(Vector3 Anchor, Vector3 Tip)> _blocked = new();

        public void Block(IEnumerable<DeerLeg> legs)
        {
            _blocked.Clear();
            foreach (DeerLeg leg in legs)
            {
                _blocked.Add((leg.Anchor.Pos, leg.Tip.Pos));
            }
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            foreach ((Vector3 anchor, Vector3 tip) in _blocked)
            {
                if (from.DistanceTo(anchor) < 0.4f && to.DistanceTo(tip) < 0.4f)
                {
                    hit = new TerrainHit(from.Lerp(to, 0.45f), Vector3.Left, 99UL);
                    return true;
                }
            }
            return _floor.Raycast(from, to, out hit);
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth) =>
            _floor.SpherePenetration(center, radius, out pushDir, out depth);
    }
}
