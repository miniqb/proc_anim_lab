using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DaddyLongLegs;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.DaddyLongLegsSmoke;

/// <summary>
/// DaddyLongLegs 专项无引擎回归。几何场景只由解析半空间/AABB 实现 ITerrainQuery，
/// 固定逻辑 tick 直接调用纯内核。每个 PASS/FAIL 都参与退出码；指标只解释判定。
/// --ablate=... 会关闭一项本物种机制，并要求相应行为门真实变红。
/// </summary>
internal static class Program
{
    private const float TickDt = 0.025f;
    private static readonly Vector3 GravityPerTick =
        new(0f, -36f * TickDt * TickDt, 0f);

    // 只在全部专项行为门绿色后更新；任何运行态漂移都必须先解释再重钉。
    private const ulong ExpectedHash = 0xC6AE88A2B807488EUL;
    private const ulong ExpectedDaddySeed1MorphologyHash = 0xA53D15D48A0D40ECUL;
    private const ulong ExpectedTerrorSeed7MorphologyHash = 0x61D6475A16C3845CUL;

    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (!TryParseAblation(args, out Ablation ablation, out string error))
        {
            Console.Error.WriteLine($"[DADDY-CORE-ARGS] FAIL {error}");
            return 2;
        }
        if (ablation != Ablation.None)
            return RunIntentionalAblation(ablation);

        var failures = new List<string>();
        Check("FACTORY", CheckFactoryAndMorphology, failures);
        Check("SUPPORT", () => CheckSupport(Ablation.None), failures);
        Check("SEGMENT-ADHESION", () => CheckSegmentAdhesion(Ablation.None), failures);
        Check("GRIP-LAYER", () => CheckGripDiscrimination(Ablation.None), failures);
        Check("RESIDUAL-TERRAIN", () => CheckResidualTerrain(Ablation.None), failures);
        Check("TERRAIN-BACKTRACK",
            () => CheckTerrainBacktrack(Ablation.None), failures);
        Check("DIRECTIONAL-DRIVE", () => CheckDirectionalDrive(Ablation.None), failures);
        Check("ALLOCATOR", () => CheckAllocator(Ablation.None), failures);
        Check("DUTY-SEPARATION", () => CheckDutySeparation(Ablation.None), failures);
        Check("STEP-SEARCH", () => CheckStepAndSearch(Ablation.None), failures);
        Check("REPLANT", () => CheckReplantPhases(Ablation.None), failures);
        Check("GUIDE-SHAPE", () => CheckFlatGuideShape(Ablation.None), failures);
        Check("STUN", () => CheckStunTakeover(Ablation.None), failures);
        Check("TARGET", () => CheckExternalTargetContract(Ablation.None), failures);
        Check("MOVE-TARGET", CheckMoveTargetContract, failures);
        Check("SURFACES", CheckSurfaceCourse, failures);
        Check("STUCK", () => CheckStuckRecovery(Ablation.None), failures);
        Check("STUCK-RETRY", CheckStuckRetryPair, failures);
        Check("STUCK-JITTER", () => CheckStuckJitter(Ablation.None), failures);
        Check("MULTI-SEED", CheckMultipleMorphologiesWalk, failures);
        Check("SUSTAINED-GAIT", () => CheckSustainedGait(Ablation.None), failures);
        Check("START-REPLANT", () => CheckStartReplant(Ablation.None), failures);
        Check("TAP-VS-HOLD", CheckTapVsHold, failures);
        Check("IDLE-WALL-STABILITY",
            () => CheckIdleWallStability(Ablation.None), failures);
        Check("MOVING-STANCE", () => CheckMovingStance(Ablation.None), failures);
        Check("STEP-SUPPORT-RESERVE",
            () => CheckStepSupportReserve(Ablation.None), failures);
        Check("SERIAL-REPLANT", () => CheckSerialReplant(Ablation.None), failures);
        Check("SHORT-STUN-REPLANT", CheckShortStunReplantInterruption, failures);
        Check("MOVING-HEIGHT-RETENTION",
            () => CheckMovingHeightRetention(Ablation.None), failures);
        Check("TALL-STANCE", () => CheckTallStance(Ablation.None), failures);
        Check("LIFECYCLE", CheckLifecycle, failures);
        Check("BOUNDS", CheckBoundsAndNoSpin, failures);
        Check("HASH-COVERAGE", CheckHashCoverage, failures);

        DeterminismResult first = RunDeterminism(40, 0f);
        DeterminismResult second = RunDeterminism(40, 0f);
        DeterminismResult fastHost = RunDeterminism(400, 0f);
        DeterminismResult perturb = RunDeterminism(40, 1e-4f);
        bool pinned = ExpectedHash == 0UL || first.Hash == ExpectedHash;
        Report("DET",
            first.Hash == second.Hash && first.FixedTicks == 760 && first.Finite && pinned,
            $"run1={first.Hash:X16} run2={second.Hash:X16} expected=" +
            (ExpectedHash == 0UL ? "UNPINNED" : $"{ExpectedHash:X16}") +
            $" ticks={first.FixedTicks} finite={first.Finite} peakQ={first.PeakQueries}", failures);
        Report("HOST-RATE",
            fastHost.FixedTicks == first.FixedTicks && fastHost.Hash == first.Hash,
            $"40Hz={first.Hash:X16}/{first.FixedTicks} 400Hz={fastHost.Hash:X16}/{fastHost.FixedTicks}",
            failures);
        Report("PERTURB",
            perturb.Finite && perturb.Hash != first.Hash,
            $"base={first.Hash:X16} perturb={perturb.Hash:X16}", failures);
        Check("ABLATION", CheckAblationGates, failures);

        bool pass = failures.Count == 0;
        Console.WriteLine(pass
            ? "[DADDY-CORE-SMOKE] PASS：形态、整链支撑、连续重力、职责预算、全向地形、生命周期与确定性均通过"
            : $"[DADDY-CORE-SMOKE] FAIL：{string.Join("；", failures)}");
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

    private static void Report(string name, bool ok, string message, List<string> failures)
    {
        Console.WriteLine($"[DADDY-CORE-{name}] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok)
            failures.Add(name);
    }

    private static (bool, string) CheckFactoryAndMorphology()
    {
        DaddyLongLegsParams[] presets = DaddyLongLegsFactory.AllPresets();
        string[] ids = presets.Select(p => p.StableId).ToArray();
        bool idsOk = ids.SequenceEqual(new[]
        {
            DaddyLongLegsFactory.BrotherId,
            DaddyLongLegsFactory.DaddyId,
            DaddyLongLegsFactory.TerrorId,
        });
        bool snapshots = !ReferenceEquals(presets[0], DaddyLongLegsFactory.AllPresets()[0]);
        bool lookup = presets.All(p =>
            DaddyLongLegsFactory.ByStableId(p.StableId).StableId == p.StableId)
            && !DaddyLongLegsFactory.TryByStableId("daddy-long-legs/not-real", out _)
            && Throws<ArgumentException>(() =>
                DaddyLongLegsFactory.ByStableId("daddy-long-legs/not-real"));

        bool deterministic = true;
        bool differentSeed = false;
        bool topology = true;
        bool roundRobin = true;
        bool varyingDimensions = false;
        bool bounded = true;
        bool budgetExact = true;
        bool spherical = true;
        int minBodies = int.MaxValue;
        int maxBodies = 0;
        int minTentacles = int.MaxValue;
        int maxTentacles = 0;
        int maximumSegments = 0;
        var shapeFingerprints = new HashSet<ulong>();

        foreach (DaddyLongLegsParams preset in presets)
        {
            for (ulong seed = 1; seed <= 9; seed++)
            {
                DaddyLongLegsMorphology a = DaddyLongLegsFactory.GenerateMorphology(preset, seed);
                DaddyLongLegsMorphology b = DaddyLongLegsFactory.GenerateMorphology(preset, seed);
                DaddyLongLegsMorphology other = DaddyLongLegsFactory.GenerateMorphology(preset, seed + 1000UL);
                deterministic &= MorphologyBitsEqual(a, b);
                differentSeed |= !MorphologyBitsEqual(a, other);
                shapeFingerprints.Add(HashMorphology(a));

                int bodyCount = a.BodyChunks.Length;
                int tentacleCount = a.Tentacles.Length;
                float expectedLength = Mathf.Lerp(
                    preset.TentacleBudgetBase,
                    tentacleCount * preset.TentacleBudgetPerTentacle,
                    preset.TentacleBudgetBlend);
                budgetExact &= Near(a.TotalTentacleLength, expectedLength, 2e-4f);
                minBodies = Math.Min(minBodies, bodyCount);
                maxBodies = Math.Max(maxBodies, bodyCount);
                minTentacles = Math.Min(minTentacles, tentacleCount);
                maxTentacles = Math.Max(maxTentacles, tentacleCount);
                topology &= bodyCount >= preset.MinimumBodyChunks
                    && bodyCount <= preset.MaximumBodyChunks
                    && a.ConnectionCount == bodyCount * (bodyCount - 1) / 2;
                bounded &= tentacleCount >= preset.MinimumTentacles
                    && tentacleCount <= preset.MaximumTentacles
                    && a.TotalTentacleSegments <= preset.MaximumTotalTentacleSegments;

                float minLength = float.PositiveInfinity;
                float maxLength = 0f;
                int minSegments = int.MaxValue;
                int maxSegments = 0;
                Vector3 preferenceSum = Vector3.Zero;
                for (int i = 0; i < tentacleCount; i++)
                {
                    DaddyTentacleSpec spec = a.TentacleAt(i);
                    roundRobin &= spec.AnchorBodyIndex == i % bodyCount;
                    bounded &= spec.SegmentCount >= preset.MinimumSegmentsPerTentacle
                        && spec.SegmentCount <= preset.MaximumSegmentsPerTentacle
                        && spec.Length >= preset.MinimumTentacleLength;
                    minLength = Math.Min(minLength, spec.Length);
                    maxLength = Math.Max(maxLength, spec.Length);
                    minSegments = Math.Min(minSegments, spec.SegmentCount);
                    maxSegments = Math.Max(maxSegments, spec.SegmentCount);
                    maximumSegments = Math.Max(maximumSegments, spec.SegmentCount);
                    preferenceSum += spec.LocalPreference;
                    spherical &= Near(spec.LocalPreference.Length(), 1f, 2e-5f);
                }
                varyingDimensions |= maxLength - minLength > 0.05f && maxSegments > minSegments;
                spherical &= preferenceSum.Length() / tentacleCount < 0.22f;

                DaddyLongLegsLocomotionController controller =
                    DaddyLongLegsFactory.CreateController(Vector3.Zero, preset, seed);
                topology &= controller.Body.Chunks.Count == bodyCount
                    && controller.Body.Connections.Count == a.ConnectionCount
                    && controller.Body.Chunks.All(chunk => chunk.RotationChunk is null)
                    && controller.Tentacles.Count == tentacleCount;
                int connectionIndex = 0;
                for (int i = 0; i < bodyCount; i++)
                {
                    for (int j = i + 1; j < bodyCount; j++)
                    {
                        ChunkConnection connection = controller.Body.Connections[connectionIndex];
                        topology &= ReferenceEquals(connection.A, controller.Body.Chunks[i])
                            && ReferenceEquals(connection.B, controller.Body.Chunks[j])
                            && BitEqual(connection.RestLength, a.RestDistanceAt(connectionIndex));
                        connectionIndex++;
                    }
                }
            }
        }

        DaddyLongLegsParams mutable = DaddyLongLegsFactory.Brother();
        DaddyLongLegsLocomotionController frozen =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, mutable, 77UL);
        int frozenCount = frozen.Body.Chunks.Count;
        mutable.MinimumBodyChunks = mutable.MaximumBodyChunks = 12;
        mutable.BaseDrive = 99f;
        bool frozenSnapshot = frozen.Body.Chunks.Count == frozenCount
            && frozen.Params.MinimumBodyChunks != 12
            && frozen.Params.BaseDrive != 99f;
        DaddyLongLegsParams exactBudget = DaddyLongLegsFactory.Daddy();
        exactBudget.MaximumTerrainQueriesPerTick = 2870;
        bool queryBudgetBoundary = ReferenceEquals(exactBudget.Validate(), exactBudget)
            && DaddyLongLegsFactory.Brother().MaximumTerrainQueriesPerTick == 1700
            && DaddyLongLegsFactory.Daddy().MaximumTerrainQueriesPerTick == 2900
            && DaddyLongLegsFactory.Terror().MaximumTerrainQueriesPerTick == 4050
            && Throws<ArgumentOutOfRangeException>(() =>
            {
                DaddyLongLegsParams invalid = DaddyLongLegsFactory.Daddy();
                invalid.MaximumTerrainQueriesPerTick = 2869;
                invalid.Validate();
            });
        bool validation = Throws<ArgumentOutOfRangeException>(() =>
        {
            DaddyLongLegsParams invalid = DaddyLongLegsFactory.Daddy();
            invalid.MaximumTerrainQueriesPerTick = 1;
            invalid.Validate();
        }) && Throws<ArgumentOutOfRangeException>(() =>
        {
            DaddyLongLegsParams invalid = DaddyLongLegsFactory.Daddy();
            invalid.MaximumTotalTentacleSegments =
                invalid.MinimumTentacles * invalid.MinimumSegmentsPerTentacle;
            invalid.Validate();
        }) && Throws<ArgumentOutOfRangeException>(() =>
        {
            DaddyLongLegsParams invalid = DaddyLongLegsFactory.Daddy();
            invalid.MinimumArrivedTentaclesForStep = invalid.MinimumTentacles + 1;
            invalid.Validate();
        }) && Throws<ArgumentOutOfRangeException>(() =>
        {
            DaddyLongLegsParams invalid = DaddyLongLegsFactory.Daddy();
            invalid.NominalSegmentLength = invalid.SegmentRadius * 2f;
            invalid.Validate();
        }) && Throws<ArgumentException>(() =>
            DaddyLongLegsFactory.CreateController(
                new Vector3(float.NaN, 0f, 0f), DaddyLongLegsFactory.Daddy(), 1UL));

        ulong daddyGolden = HashMorphology(
            DaddyLongLegsFactory.GenerateMorphology(DaddyLongLegsFactory.Daddy(), 1UL));
        ulong terrorGolden = HashMorphology(
            DaddyLongLegsFactory.GenerateMorphology(DaddyLongLegsFactory.Terror(), 7UL));
        bool golden = (ExpectedDaddySeed1MorphologyHash == 0UL
                || daddyGolden == ExpectedDaddySeed1MorphologyHash)
            && (ExpectedTerrorSeed7MorphologyHash == 0UL
                || terrorGolden == ExpectedTerrorSeed7MorphologyHash);

        bool ok = idsOk && snapshots && lookup && deterministic && differentSeed
            && topology && roundRobin && varyingDimensions && bounded && budgetExact && spherical
            && shapeFingerprints.Count >= 12 && frozenSnapshot
            && queryBudgetBoundary && validation && golden;
        return (ok,
            $"ids=[{string.Join(',', ids)}] deterministic={deterministic} otherSeed={differentSeed} " +
            $"completeGraph={topology} roundRobin={roundRobin} bodyRange={minBodies}..{maxBodies} " +
            $"tentacleRange={minTentacles}..{maxTentacles} maxSegments={maximumSegments} " +
            $"shapeVariants={shapeFingerprints.Count} variedLengthAndSegments={varyingDimensions} " +
            $"lengthBudget={budgetExact} sphereMean={spherical} caps={bounded} " +
            $"snapshot={frozenSnapshot} queryBudget={queryBudgetBoundary}/2870 " +
            $"validation={validation} " +
            $"golden={daddyGolden:X16}/{terrorGolden:X16}" +
            $"{(ExpectedDaddySeed1MorphologyHash == 0UL ? "(UNPINNED)" : string.Empty)}");
    }

    private static (bool, string) CheckSupport(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        if (ablation == Ablation.Support)
            p.EnableSupport = false;
        DaddyLongLegsLocomotionController chain =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 4f, 0f), p, 0x51UL);
        DaddyLongLegsLocomotionController tip =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 4f, 0f), p, 0x51UL);
        DaddyLongLegsLocomotionController unsupported =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 4f, 0f), p, 0x51UL);
        // Synthetic mapping probes must not be masked by PlaceInRoom's 40-tick birth support.
        chain.Launch(Vector3.Zero);
        tip.Launch(Vector3.Zero);
        unsupported.Launch(Vector3.Zero);
        DaddyTentacle chainTentacle = FirstLocomotion(chain);
        DaddyTentacle tipTentacle = FirstLocomotion(tip);
        PrepareSyntheticGrip(chainTentacle, chain.BodyCenter, chainTentacle.Segments.Count, true);
        PrepareSyntheticGrip(tipTentacle, tip.BodyCenter, 1, true);
        InvokePrivate(chainTentacle, "UpdateSupport");
        InvokePrivate(tipTentacle, "UpdateSupport");
        float chainSupport = chainTentacle.SupportContribution;
        float tipSupport = tipTentacle.SupportContribution;

        DaddyLongLegsLocomotionController unarrived =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 4f, 0f), p, 0x51UL);
        unarrived.Launch(Vector3.Zero);
        DaddyTentacle unarrivedTentacle = FirstLocomotion(unarrived);
        PrepareSyntheticGrip(unarrivedTentacle, unarrived.BodyCenter, 1, false);
        InvokePrivate(unarrivedTentacle, "UpdateSupport");
        float unarrivedSupport = unarrivedTentacle.SupportContribution;

        var empty = new EmptyTerrain();
        chain.Body.Tick(new TickContext(GravityPerTick, empty, 0));
        tip.Body.Tick(new TickContext(GravityPerTick, empty, 0));
        unsupported.Body.Tick(new TickContext(GravityPerTick, empty, 0));
        for (int i = 0; i < 24; i++)
        {
            InvokePrivate(chain, "AggregateSupport", Vector3.Right);
            InvokePrivate(tip, "AggregateSupport", Vector3.Right);
            InvokePrivate(unsupported, "AggregateSupport", Vector3.Right);
        }
        InvokePrivate(chain, "ApplyContinuousSupportToBody", GravityPerTick);
        InvokePrivate(tip, "ApplyContinuousSupportToBody", GravityPerTick);
        InvokePrivate(unsupported, "ApplyContinuousSupportToBody", GravityPerTick);
        float chainGravity = chain.GravityCancellation;
        float tipGravity = tip.GravityCancellation;
        float unsupportedGravity = unsupported.GravityCancellation;
        float chainFall = AverageVelocityVector(chain.Body).Y;
        float tipFall = AverageVelocityVector(tip.Body).Y;
        float unsupportedFall = AverageVelocityVector(unsupported.Body).Y;

        bool ok = chainTentacle.GripFraction > tipTentacle.GripFraction
            && chainSupport > tipSupport + 0.05f
            && tipSupport > unarrivedSupport
            && chainGravity > tipGravity && tipGravity > unsupportedGravity
            && chainFall > tipFall && tipFall > unsupportedFall
            && chain.Body.GravityScale == 1f && tip.Body.GravityScale == 1f
            && unsupported.Body.GravityScale == 1f
            && Near(chain.Body.AirFriction,
                Mathf.Lerp(p.UnsupportedAirFriction, p.SupportedAirFriction,
                    chain.ContinuousSupport), 1e-6f);
        return (ok,
            $"grip(chain/tip)={chainTentacle.GripFraction:F3}/{tipTentacle.GripFraction:F3} " +
            $"support(chain/tip/unarrived)={chainSupport:F3}/{tipSupport:F3}/{unarrivedSupport:F3} " +
            $"compensation(chain/tip/none)={chainGravity:F3}/{tipGravity:F3}/{unsupportedGravity:F3} " +
            $"fallVel={chainFall:F5}/{tipFall:F5}/{unsupportedFall:F5}");
    }

    private static (bool, string) CheckSegmentAdhesion(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableSegmentAdhesion = ablation != Ablation.SegmentAdhesion;
        p.SegmentPathServo = 0f;
        p.SegmentTargetServo = 0f;
        p.SegmentRootSpreadForce = 0f;
        p.MinimumTentacles = p.MaximumTentacles = 3;
        p.MinimumLocomotionTentacles = 1;
        p.MaximumTotalTentacleSegments = 42;
        p.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 2f, 0f), p, 33UL);
        foreach (BodyChunk chunk in daddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        DaddyTentacle tentacle = FirstLocomotion(daddy);
        float y = p.SegmentRadius + p.ContactProbeExtra * 0.55f;
        Vector3 anchorDelta = new(0f, y - tentacle.Anchor.Pos.Y, 0f);
        daddy.Body.Shift(anchorDelta);
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            segment.Pos = tentacle.Anchor.Pos + Vector3.Right * (tentacle.LinkLength * (i + 1));
            segment.Pos.Y = y;
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Down * 0.04f;
            segment.ContactNormal = Vector3.Up;
            segment.TerrainContact = false;
        }
        SetPrivateField(tentacle, "_needsTerrainExpansion", false);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint",
            new Vector3(tentacle.Segments[^1].Pos.X, 0f, tentacle.Segments[^1].Pos.Z));
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Up);
        SetPrivateProperty(tentacle, "LandingColliderId", 1UL);
        SetPrivateField(tentacle, "_landingAge", p.StepArrivalMinimumTicks);

        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        daddy.RunSpeed = 0f;
        daddy.Tick(new TickContext(Vector3.Zero, floor, 0));
        int contacts = tentacle.Segments.Count(s => s.TerrainContact);
        int grips = tentacle.Segments.Count(s => s.ActiveGrip);
        int expectedGrips = tentacle.Segments.Count(s =>
            s.Pos.DistanceSquaredTo(tentacle.LandingPoint)
                < p.ContactTargetRange * p.ContactTargetRange);
        float maximumSeparation = tentacle.Segments
            .Where(s => s.TerrainContact)
            .Select(s => s.Pos.Y - p.SegmentRadius)
            .DefaultIfEmpty(float.PositiveInfinity)
            .Max();
        bool ok = contacts == tentacle.Segments.Count
            && grips == expectedGrips && tentacle.ActiveGripCount == expectedGrips
            && tentacle.Segments.All(s => !s.ActiveGrip || s.TerrainContact)
            && maximumSeparation <= p.LandingSurfaceOffset + 0.004f;
        return (ok,
            $"enabled={p.EnableSegmentAdhesion} touch/grip/expected=" +
            $"{contacts}/{grips}/{expectedGrips}/{tentacle.Segments.Count} " +
            $"maxSurfaceOffset={maximumSeparation:F5}m queries={daddy.TickQueryCount}");
    }

    private static (bool, string) CheckGripDiscrimination(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableGripDiscrimination = ablation != Ablation.GripDiscrimination;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 0x610UL);
        daddy.Launch(Vector3.Zero);
        DaddyTentacle tentacle = FirstLocomotion(daddy);
        SetPrivateProperty(tentacle, "HasLandingTarget", false);
        foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
        {
            segment.TerrainContact = true;
            segment.ContactNormal = Vector3.Up;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
        }
        InvokePrivate(tentacle, "UpdateSupport");
        bool passivePreserved = tentacle.Segments.All(s => s.TerrainContact && !s.ActiveGrip);
        bool excluded = tentacle.ActiveGripCount == 0
            && tentacle.GripFraction == 0f
            && tentacle.SupportContribution == 0f;
        return (passivePreserved && excluded,
            $"enabled={p.EnableGripDiscrimination} passive={passivePreserved} " +
            $"active={tentacle.ActiveGripCount}/{tentacle.Segments.Count} " +
            $"grip={tentacle.GripFraction:F3} support={tentacle.SupportContribution:F3}");
    }

    private static (bool, string) CheckResidualTerrain(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableResidualTerrainResolve = ablation != Ablation.ResidualTerrain;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(8f, 8f, 8f), p, 801UL);
        DaddyTentacle tentacle = daddy.Tentacles[0];
        DaddyTentacleSegmentState segment = tentacle.Segments[0];
        var corner = new SequentialContactTerrain(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Right, 11UL),
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 12UL),
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Back, 13UL));
        segment.Pos = new Vector3(-0.05f, -0.05f, 0.05f);
        segment.LastPos = segment.Pos;
        InvokePrivate(tentacle, "ResolveResidualTerrain",
            new TickContext(Vector3.Zero, corner, 1L));
        float residual = corner.MaximumPenetration(segment.Pos, segment.Radius);

        DaddyLongLegsParams onePassParams = ProbeParams();
        onePassParams.ResidualTerrainResolveIterations = 1;
        DaddyLongLegsLocomotionController onePassDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(8f, 8f, 8f), onePassParams, 801UL);
        DaddyTentacle onePassTentacle = onePassDaddy.Tentacles[0];
        DaddyTentacleSegmentState onePassSegment = onePassTentacle.Segments[0];
        onePassSegment.Pos = new Vector3(-0.05f, -0.05f, 0.05f);
        onePassSegment.LastPos = onePassSegment.Pos;
        InvokePrivate(onePassTentacle, "ResolveResidualTerrain",
            new TickContext(Vector3.Zero, corner, 1L));
        float onePassResidual = corner.MaximumPenetration(
            onePassSegment.Pos, onePassSegment.Radius);

        DaddyLongLegsLocomotionController bodyDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(8f, 8f, 8f), p, 802UL);
        BodyChunk bodyChunk = bodyDaddy.Body.Chunks[0];
        var bodyCorner = new SequentialContactTerrain(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Right, 21UL),
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 22UL),
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Back, 23UL));
        bodyChunk.Pos = new Vector3(-0.05f, -0.05f, 0.05f);
        bodyChunk.LastPos = bodyChunk.Pos;
        InvokePrivate(bodyDaddy, "ResolveResidualBodyTerrain",
            new TickContext(Vector3.Zero, bodyCorner, 2L));
        float bodyResidual = bodyCorner.MaximumPenetration(
            bodyChunk.Pos, bodyChunk.TerrainRadius);

        DaddyLongLegsLocomotionController cycleDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(8f, 8f, 8f), p, 803UL);
        DaddyTentacle cycleTentacle = cycleDaddy.Tentacles[0];
        foreach (DaddyTentacleSegmentState other in cycleTentacle.Segments)
        {
            other.Pos = cycleTentacle.Anchor.Pos;
            other.LastPos = cycleTentacle.Anchor.Pos;
            other.Vel = cycleTentacle.Anchor.Vel;
            other.TerrainContact = false;
            other.ContactNormal = Vector3.Zero;
        }
        DaddyTentacleSegmentState cycleSegment = cycleTentacle.Segments[^1];
        SetPrivateProperty(cycleTentacle, "HasLandingTarget", true);
        cycleSegment.Pos = Vector3.Zero;
        cycleSegment.LastPos = cycleTentacle.Anchor.Pos;
        cycleSegment.Vel = new Vector3(0.2f, 0.1f, -0.1f);
        cycleSegment.TerrainContact = true;
        cycleSegment.ContactNormal = Vector3.Up;
        var cycleTerrain = new OpposingContactTerrain(cycleTentacle.Anchor.Pos);
        InvokePrivate(cycleTentacle, "ResolveResidualTerrain",
            new TickContext(Vector3.Zero, cycleTerrain, 3L));
        InvokePrivate(cycleTentacle, "UpdateSupport");
        bool cycleRolledBack = NearVector(
                cycleSegment.Pos, cycleTentacle.Anchor.Pos, 1e-6f)
            && NearVector(cycleSegment.LastPos, cycleTentacle.Anchor.Pos, 1e-6f)
            && cycleTentacle.HasLandingTarget
            && cycleTentacle.ResidualRecoverySerial == 1
            && cycleTentacle.ResidualInvalidationSerial == 0
            && cycleTentacle.SupportContribution == 0f
            && cycleTerrain.HitCount == 2
            && cycleTerrain.CallCount <= cycleTentacle.Segments.Count + 2;

        DaddyLongLegsLocomotionController fallbackDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(8f, 8f, 8f), p, 804UL);
        DaddyTentacle fallbackTentacle = fallbackDaddy.Tentacles[0];
        foreach (DaddyTentacleSegmentState other in fallbackTentacle.Segments)
        {
            other.Pos = fallbackTentacle.Anchor.Pos;
            other.LastPos = fallbackTentacle.Anchor.Pos;
            other.Vel = fallbackTentacle.Anchor.Vel;
            other.TerrainContact = false;
            other.ContactNormal = Vector3.Zero;
        }
        DaddyTentacleSegmentState fallbackSegment = fallbackTentacle.Segments[^1];
        SetPrivateProperty(fallbackTentacle, "HasLandingTarget", true);
        fallbackSegment.Pos = Vector3.Zero;
        fallbackSegment.LastPos = Vector3.Zero;
        fallbackSegment.Vel = new Vector3(0.2f, 0.1f, -0.1f);
        fallbackSegment.TerrainContact = true;
        fallbackSegment.ContactNormal = Vector3.Up;
        var fallbackTerrain = new OpposingContactTerrain(fallbackTentacle.Anchor.Pos);
        InvokePrivate(fallbackTentacle, "ResolveResidualTerrain",
            new TickContext(Vector3.Zero, fallbackTerrain, 4L));
        InvokePrivate(fallbackTentacle, "UpdateSupport");
        bool cycleFallback = NearVector(
                fallbackSegment.Pos, fallbackTentacle.Anchor.Pos, 1e-6f)
            && NearVector(fallbackSegment.LastPos, fallbackTentacle.Anchor.Pos, 1e-6f)
            && NearVector(fallbackSegment.Vel, fallbackTentacle.Anchor.Vel, 1e-6f)
            && !fallbackSegment.TerrainContact && !fallbackTentacle.HasLandingTarget
            && fallbackTentacle.ResidualRecoverySerial == 0
            && fallbackTentacle.ResidualInvalidationSerial == 1
            && fallbackTentacle.SupportContribution == 0f
            && fallbackTerrain.HitCount == 3
            && fallbackTerrain.CallCount <= fallbackTentacle.Segments.Count + 2;

        bool iterative = residual <= 2e-5f && bodyResidual <= 2e-5f;
        bool singlePassGateIsLive = onePassResidual > 0.02f;
        return (iterative && singlePassGateIsLive && cycleRolledBack && cycleFallback,
            $"enabled={p.EnableResidualTerrainResolve} residual={residual:F5}m " +
            $"body={bodyResidual:F5}m onePass={onePassResidual:F5}m " +
            $"cycle={cycleRolledBack}/{cycleFallback}/" +
            $"{cycleTerrain.HitCount},{fallbackTerrain.HitCount}/" +
            $"{cycleTerrain.CallCount},{fallbackTerrain.CallCount} " +
            $"gate={singlePassGateIsLive}");
    }

    /// <summary>
    /// 五个彼此独立的几何门钉住 Daddy 触手与点式肢体不同的防卡语义：
    /// ① 首轮 terrain 已背书的位置在墙外时，后续约束/自避制造的穿越必须被最终 sweep 拦住；
    /// ② 只有末次 residual MTD 才把端点推到墙另一侧时，同 tick 的最终审计必须看到；
    /// ③ 所有段球都在 collider 外、但一条相邻链边横穿薄墙时，必须从首个阻断段开始
    ///    取消远端抓附并在有限 tick 内回到锚点侧；
    /// ④ 远端同时横穿第二道正交薄墙时，一次恢复必须逐边收回，不能整段平移后留下新卡点；
    /// ⑤ 凸角上的合法折线路径应保留，即使 Anchor→tip 的直线本身穿过实体。
    /// 最后一条防止修复退化成 Centipede 足端式的整条直线 LOS——长触手必须能绕角。
    /// </summary>
    private static (bool, string) CheckTerrainBacktrack(Ablation ablation)
    {
        bool enabled = ablation != Ablation.TerrainBacktrack;

        DaddyLongLegsParams sweepParams = TerrainBacktrackProbeParams(enabled);
        sweepParams.SegmentPathServo = 0f;
        sweepParams.SegmentRootSpreadForce = 0f;
        DaddyLongLegsLocomotionController sweepDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, sweepParams, 0xBA11UL);
        DaddyTentacle sweepTentacle = FirstLocomotion(sweepDaddy);
        foreach (BodyChunk chunk in sweepDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var sweepWall = new ThinWallTerrain();
        ConfigureOneSidedWallChain(sweepDaddy, sweepTentacle, sweepWall);
        DaddyTentacleSegmentState tunneling = sweepTentacle.Segments[^1];
        int sweepSerialBefore = sweepTentacle.PostConstraintSweepSerial;
        tunneling.Pos = new Vector3(
            -ThinWallTerrain.HalfWidth - tunneling.Radius - 0.02f,
            tunneling.Pos.Y,
            tunneling.Pos.Z);
        bool postConstraintPrecondition = CountBlockedLinks(
            sweepTentacle, sweepWall) == 1;
        InvokePrivate(sweepTentacle, "ResolvePostConstraintTerrain",
            new TickContext(Vector3.Zero, sweepWall, 1L));
        bool postSweepCaught = postConstraintPrecondition
            && sweepTentacle.PostConstraintSweepSerial > sweepSerialBefore
            && tunneling.Pos.X
                >= ThinWallTerrain.HalfWidth + tunneling.Radius - 1e-4f
            && !sweepWall.SpherePenetration(
                tunneling.Pos, tunneling.Radius, out _, out _)
            && CountBlockedLinks(sweepTentacle, sweepWall) == 0;

        DaddyLongLegsParams residualOrderParams = TerrainBacktrackProbeParams(enabled);
        residualOrderParams.EnableSegmentAdhesion = false;
        residualOrderParams.SegmentPathServo = 0f;
        residualOrderParams.SegmentRootSpreadForce = 0f;
        residualOrderParams.SegmentSelfSeparation =
            residualOrderParams.SegmentRadius * 2f;
        DaddyLongLegsLocomotionController residualOrderDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, residualOrderParams, 0xBA12UL);
        DaddyTentacle residualOrderTentacle = FirstLocomotion(residualOrderDaddy);
        foreach (BodyChunk chunk in residualOrderDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var setupWall = new ThinWallTerrain();
        ConfigureOneSidedWallChain(residualOrderDaddy, residualOrderTentacle, setupWall);
        SetPrivateProperty(residualOrderTentacle, "HasLandingTarget", false);
        SetPrivateProperty(residualOrderTentacle, "ReplantPhase",
            DaddyTentacleReplantPhase.Reaching);
        SetPrivateField(residualOrderTentacle, "_forceLandingSearch", false);
        foreach (DaddyTentacleSegmentState segment in residualOrderTentacle.Segments)
        {
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
        }
        int residualBarrierIndex = Math.Clamp(
            residualOrderTentacle.Segments.Count / 2,
            1,
            residualOrderTentacle.Segments.Count - 2);
        DaddyTentacleSegmentState residualBarrierSegment =
            residualOrderTentacle.Segments[residualBarrierIndex];
        float residualDestinationX = -residualBarrierSegment.Pos.X;
        var residualBarrierTerrain = new ResidualCrossingTerrain(
            residualBarrierSegment.Pos, residualDestinationX);
        TickTentacle(
            residualOrderDaddy, residualOrderTentacle, residualBarrierTerrain, 1L);
        int residualOrderBlocked = CountBlockedLinks(
            residualOrderTentacle,
            residualBarrierTerrain);
        bool residualOrderCaught = residualBarrierTerrain.Pushed
            && residualOrderTentacle.BacktrackFrom == residualBarrierIndex
            && residualOrderBlocked >= 1
            && AllSegmentsClear(residualOrderTentacle, residualBarrierTerrain);

        // 合同有效路径：两个独立有限 slab 对窄缝内球给出各自合法最小 MTD，形成
        // Jolt 接缝式二周期；回滚到上一 tick 的外侧可行 LastPos 后，链边审计仍须抓到穿墙。
        DaddyLongLegsParams rollbackParams = TerrainBacktrackProbeParams(enabled);
        rollbackParams.EnableSegmentAdhesion = false;
        DaddyLongLegsLocomotionController rollbackDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, rollbackParams, 0xBA29UL);
        DaddyTentacle rollbackTentacle = FirstLocomotion(rollbackDaddy);
        foreach (BodyChunk chunk in rollbackDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var rollbackTerrain = new NarrowGapSlabsTerrain();
        int rollbackIndex = Math.Clamp(
            rollbackTentacle.Segments.Count / 2, 1,
            rollbackTentacle.Segments.Count - 2);
        float rollbackSafeX = NarrowGapSlabsTerrain.GapHalfWidth
            + NarrowGapSlabsTerrain.SlabThickness
            + rollbackTentacle.Segments[rollbackIndex].Radius + 0.01f;
        float rollbackSpacing = SurfaceChainSpacing(rollbackTentacle);
        rollbackDaddy.Body.Shift(
            new Vector3(-rollbackSafeX - rollbackTentacle.LinkLength * 0.45f, 0f, 0f)
            - rollbackTentacle.Anchor.Pos);
        for (int i = 0; i < rollbackTentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = rollbackTentacle.Segments[i];
            segment.Pos = new Vector3(-rollbackSafeX, rollbackSpacing * (i + 1), 0f);
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            segment.TerrainContact = false;
            segment.ContactNormal = Vector3.Zero;
            segment.ActiveGrip = false;
            segment.GripNormal = Vector3.Zero;
            segment.GripColliderId = 0UL;
        }
        DaddyTentacleSegmentState rollbackSegment = rollbackTentacle.Segments[rollbackIndex];
        rollbackSegment.Pos = new Vector3(0f, rollbackSegment.Pos.Y, 0f);
        rollbackSegment.LastPos = new Vector3(rollbackSafeX, rollbackSegment.Pos.Y, 0f);
        int rollbackSerialBefore = rollbackTentacle.ResidualRecoverySerial;
        InvokePrivate(
            rollbackTentacle, "ResolveResidualTerrain",
            new TickContext(Vector3.Zero, rollbackTerrain, 1L));
        bool contractRollbackApplied = rollbackTentacle.ResidualRecoverySerial
                == rollbackSerialBefore + 1
            && rollbackSegment.Pos == rollbackSegment.LastPos
            && rollbackSegment.Pos.X == rollbackSafeX
            && AllSegmentsClear(rollbackTentacle, rollbackTerrain);
        InvokePrivate(
            rollbackTentacle, "AuditTerrainBacktrack",
            new TickContext(Vector3.Zero, rollbackTerrain, 1L));
        int rollbackBlocked = CountBlockedLinks(rollbackTentacle, rollbackTerrain);
        bool contractRollbackCaught = contractRollbackApplied
            && rollbackBlocked >= 1
            && rollbackTentacle.BacktrackFrom == rollbackIndex;

        DaddyLongLegsParams crossedParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController crossedDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, crossedParams, 0xBA22UL);
        DaddyTentacle crossedTentacle = FirstLocomotion(crossedDaddy);
        foreach (BodyChunk chunk in crossedDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var wall = new ThinWallTerrain();
        int blockedIndex = ConfigureCrossedWallChain(crossedDaddy, crossedTentacle, wall);
        int initiallyBlocked = CountBlockedLinks(
            crossedTentacle, wall);
        bool initiallyClear = AllSegmentsClear(crossedTentacle, wall);
        bool initiallyReachable = MaximumLinkExcess(crossedTentacle) <= 1e-5f;
        float initialSupport = crossedTentacle.SupportContribution;
        int backtrackSerialBefore = crossedTentacle.TerrainBacktrackSerial;
        bool sawBoundary = false;
        bool sawDistalNeutral = false;
        bool sawProximalRetained = false;
        bool stayedClear = true;
        int clearedAt = -1;
        for (int tick = 1; tick <= 32; tick++)
        {
            TickTentacle(crossedDaddy, crossedTentacle, wall, tick);
            stayedClear &= AllSegmentsClear(crossedTentacle, wall);
            sawBoundary |= crossedTentacle.BacktrackFrom == blockedIndex;
            if (crossedTentacle.BacktrackFrom == blockedIndex)
            {
                sawDistalNeutral |= crossedTentacle.Segments
                    .Skip(blockedIndex).All(segment => !segment.ActiveGrip);
                sawProximalRetained |= crossedTentacle.Segments
                    .Take(blockedIndex).All(segment => segment.Pos.X
                        >= ThinWallTerrain.HalfWidth + segment.Radius - 1e-4f);
            }
            if (CountBlockedLinks(crossedTentacle, wall) == 0)
            {
                clearedAt = tick;
                break;
            }
        }
        int backtrackCount = crossedTentacle.TerrainBacktrackSerial
            - backtrackSerialBefore;
        bool boundedBacktrack = backtrackCount is >= 1 and <= 2;
        bool staleFarLandingGone = !crossedTentacle.HasLandingTarget
            || crossedTentacle.LandingPoint.X >= ThinWallTerrain.HalfWidth - 1e-4f;
        bool distalBacktracked = initiallyBlocked == 1
            && initiallyClear && initiallyReachable && initialSupport > 0f
            && sawBoundary && sawDistalNeutral && sawProximalRetained
            && clearedAt is >= 1 and <= 32
            && boundedBacktrack && staleFarLandingGone && stayedClear;

        DaddyLongLegsParams multiBarrierParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController multiBarrierDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, multiBarrierParams, 0xBA23UL);
        DaddyTentacle multiBarrierTentacle = FirstLocomotion(multiBarrierDaddy);
        foreach (BodyChunk chunk in multiBarrierDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var orthogonalWalls = new OrthogonalThinWallsTerrain();
        ConfigureOrthogonalCrossedChain(
            multiBarrierDaddy, multiBarrierTentacle,
            out int firstBarrierIndex);
        int initialMultiBlocked = CountBlockedLinks(
            multiBarrierTentacle, orthogonalWalls);
        bool multiSegmentsInitiallyClear = AllSegmentsClear(
            multiBarrierTentacle, orthogonalWalls);
        int multiSerialBefore = multiBarrierTentacle.TerrainBacktrackSerial;
        for (int tick = 1; tick <= multiBarrierParams.TerrainBacktrackReleaseTicks; tick++)
        {
            Array.Clear((bool[])GetPrivateField(
                multiBarrierTentacle, "_barrierObserved"));
            InvokePrivate(
                multiBarrierTentacle,
                "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, orthogonalWalls, tick));
        }
        InvokePrivate(multiBarrierTentacle, "UpdateSupport");
        int finalMultiBlocked = CountBlockedLinks(
            multiBarrierTentacle, orthogonalWalls);
        bool multiBarrierRecovered = initialMultiBlocked >= 2
            && multiSegmentsInitiallyClear
            && multiBarrierTentacle.TerrainBacktrackSerial == multiSerialBefore + 1
            && finalMultiBlocked == 0
            && AllSegmentsClear(multiBarrierTentacle, orthogonalWalls)
            && !multiBarrierTentacle.HasLandingTarget
            && multiBarrierTentacle.Segments
                .Skip(firstBarrierIndex)
                .All(segment => !segment.ActiveGrip);

        // TerrainSkin 不是实体体积：墙厚小于 2*skin 时，两个端球之间仍有真实空隙，
        // 旧的 radius+skin 裁剪会把整段短路掉。这里钉住物理半径裁剪确实发出 ray。
        DaddyLongLegsParams ultraThinParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController ultraThinDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, ultraThinParams, 0xBA24UL);
        DaddyTentacle ultraThinTentacle = FirstLocomotion(ultraThinDaddy);
        foreach (BodyChunk chunk in ultraThinDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var ultraThinWall = new ThinWallTerrain(ultraThinParams.TerrainSkin * 0.25f);
        int ultraThinIndex = Math.Clamp(
            ultraThinTentacle.Segments.Count / 2, 1,
            ultraThinTentacle.Segments.Count - 1);
        float ultraCenter = ultraThinWall.HalfWidthValue
            + ultraThinTentacle.Segments[0].Radius + 0.001f;
        ultraThinDaddy.Body.Shift(
            new Vector3(ultraCenter + ultraThinTentacle.LinkLength * 0.45f, 0f, 0f)
            - ultraThinTentacle.Anchor.Pos);
        PositionCrossedWallSegments(
            ultraThinTentacle, ultraThinWall, ultraThinIndex,
            setGrip: false, surfacePadding: 0.001f);
        SetPrivateField(ultraThinTentacle, "_needsTerrainExpansion", false);
        InvokePrivate(
            ultraThinTentacle, "AuditTerrainBacktrack",
            new TickContext(Vector3.Zero, ultraThinWall, 1L));
        bool ultraThinEndpointsClear = AllSegmentsClear(
            ultraThinTentacle, ultraThinWall);
        bool ultraThinCaught = ultraThinEndpointsClear
            && CountBlockedLinks(ultraThinTentacle, ultraThinWall) == 1
            && ultraThinTentacle.BacktrackFrom == ultraThinIndex
            && ultraThinTentacle.MaximumBarrierTicks == 1;

        // 每 tick 把唯一阻断边在两个索引间迁移，使每条边自己的 run 始终小于门限；
        // 触手级 any-block episode 仍必须在固定门限触发恢复。
        DaddyLongLegsParams migratingParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController migratingDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, migratingParams, 0xBA25UL);
        DaddyTentacle migratingTentacle = FirstLocomotion(migratingDaddy);
        foreach (BodyChunk chunk in migratingDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var migratingWall = new ThinWallTerrain();
        int migratingA = Math.Clamp(
            migratingTentacle.Segments.Count / 3, 1,
            migratingTentacle.Segments.Count - 2);
        int migratingB = migratingA + 1;
        float migratingCenter = migratingWall.HalfWidthValue
            + migratingTentacle.Segments[0].Radius + 0.02f;
        migratingDaddy.Body.Shift(
            new Vector3(migratingCenter + migratingTentacle.LinkLength * 0.45f, 0f, 0f)
            - migratingTentacle.Anchor.Pos);
        SetPrivateField(migratingTentacle, "_needsTerrainExpansion", false);
        int migratingSerialBefore = migratingTentacle.TerrainBacktrackSerial;
        int migratingMaximumPerEdge = 0;
        for (int tick = 1; tick <= migratingParams.TerrainBacktrackReleaseTicks; tick++)
        {
            int blocked = (tick & 1) == 1 ? migratingA : migratingB;
            PositionCrossedWallSegments(
                migratingTentacle, migratingWall, blocked, setGrip: false);
            Array.Clear((bool[])GetPrivateField(migratingTentacle, "_barrierObserved"));
            InvokePrivate(
                migratingTentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, migratingWall, tick));
            if (tick < migratingParams.TerrainBacktrackReleaseTicks)
            {
                migratingMaximumPerEdge = Math.Max(
                    migratingMaximumPerEdge,
                    ((int[])GetPrivateField(migratingTentacle, "_barrierTicks")).Max());
            }
        }
        bool migratingRecovered = migratingMaximumPerEdge
                < migratingParams.TerrainBacktrackReleaseTicks
            && migratingTentacle.MaximumBarrierTicks
                >= migratingParams.TerrainBacktrackReleaseTicks
            && migratingTentacle.TerrainBacktrackSerial == migratingSerialBefore + 1
            && CountBlockedLinks(migratingTentacle, migratingWall) == 0
            && !migratingTentacle.TerrainRecoveryActive;

        // phase0 最后一颗候选球失败时不得提交前半条新链；下一 phase 通过后，
        // 再同时验证球不重叠、链长和每条物理半径裁剪边。
        DaddyLongLegsParams atomicParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController atomicDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, atomicParams, 0xBA26UL);
        DaddyTentacle atomicTentacle = FirstLocomotion(atomicDaddy);
        foreach (BodyChunk chunk in atomicDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var atomicWall = new RecoveryCandidateFailureTerrain();
        int atomicIndex = ConfigureCrossedWallChain(atomicDaddy, atomicTentacle, atomicWall);
        for (int tick = 1; tick < atomicParams.TerrainBacktrackReleaseTicks; tick++)
        {
            Array.Clear((bool[])GetPrivateField(atomicTentacle, "_barrierObserved"));
            InvokePrivate(
                atomicTentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, atomicWall, tick));
        }
        Vector3[] atomicPosBefore = atomicTentacle.Segments
            .Skip(atomicIndex).Select(segment => segment.Pos).ToArray();
        Vector3[] atomicLastBefore = atomicTentacle.Segments
            .Skip(atomicIndex).Select(segment => segment.LastPos).ToArray();
        Vector3[] atomicVelBefore = atomicTentacle.Segments
            .Skip(atomicIndex).Select(segment => segment.Vel).ToArray();
        atomicWall.FailAfterClearCalls(atomicTentacle.Segments.Count - atomicIndex);
        Array.Clear((bool[])GetPrivateField(atomicTentacle, "_barrierObserved"));
        InvokePrivate(
            atomicTentacle, "AuditTerrainBacktrack",
            new TickContext(Vector3.Zero, atomicWall,
                atomicParams.TerrainBacktrackReleaseTicks));
        bool failureWasAtomic = enabled && atomicWall.InjectedFailures == 1
            && (atomicTentacle.TerrainRecoveryActive
                && atomicTentacle.TerrainRecoveryPhase == 1
                && atomicPosBefore.SequenceEqual(atomicTentacle.Segments
                    .Skip(atomicIndex).Select(segment => segment.Pos))
                && atomicLastBefore.SequenceEqual(atomicTentacle.Segments
                    .Skip(atomicIndex).Select(segment => segment.LastPos))
                && atomicVelBefore.SequenceEqual(atomicTentacle.Segments
                    .Skip(atomicIndex).Select(segment => segment.Vel)));
        Array.Clear((bool[])GetPrivateField(atomicTentacle, "_barrierObserved"));
        InvokePrivate(
            atomicTentacle, "AuditTerrainBacktrack",
            new TickContext(Vector3.Zero, atomicWall,
                atomicParams.TerrainBacktrackReleaseTicks + 1L));
        float atomicMinimumSeparation = float.PositiveInfinity;
        for (int i = 0; i < atomicTentacle.Segments.Count; i++)
        {
            for (int j = i + 1; j < atomicTentacle.Segments.Count; j++)
            {
                if (j < atomicIndex)
                    continue;
                atomicMinimumSeparation = Math.Min(
                    atomicMinimumSeparation,
                    atomicTentacle.Segments[i].Pos.DistanceTo(
                        atomicTentacle.Segments[j].Pos));
            }
        }
        bool atomicRecovered = failureWasAtomic
            && atomicTentacle.TerrainBacktrackSerial == 1
            && (!atomicTentacle.TerrainRecoveryActive
                && atomicMinimumSeparation
                    >= Math.Max(atomicParams.SegmentSelfSeparation,
                        atomicParams.SegmentRadius * 2f)
                && MaximumLinkExcess(atomicTentacle) <= 1e-5f
                && CountBlockedLinks(atomicTentacle, atomicWall) == 0
                && AllSegmentsClear(atomicTentacle, atomicWall));

        DaddyLongLegsParams selfAvoidParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController selfAvoidDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, selfAvoidParams, 0xBA2DUL);
        DaddyTentacle selfAvoidTentacle = FirstLocomotion(selfAvoidDaddy);
        foreach (BodyChunk chunk in selfAvoidDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var selfAvoidWall = new ThinWallTerrain();
        int selfAvoidIndex = ConfigureCrossedWallChain(
            selfAvoidDaddy, selfAvoidTentacle, selfAvoidWall);
        float selfAvoidMinimum = Math.Max(
            selfAvoidParams.SegmentSelfSeparation,
            selfAvoidParams.SegmentRadius * 2f);
        float selfAvoidSpacing = selfAvoidMinimum
            + Math.Max(1e-5f,
                Math.Min(0.05f,
                    (selfAvoidTentacle.LinkLength - selfAvoidMinimum) * 0.25f));
        Vector3 phaseZeroFirstCandidate = selfAvoidTentacle.Segments[selfAvoidIndex - 1].Pos
            + Vector3.Right * selfAvoidSpacing;
        selfAvoidTentacle.Segments[0].Pos = phaseZeroFirstCandidate;
        selfAvoidTentacle.Segments[0].LastPos = phaseZeroFirstCandidate;
        selfAvoidTentacle.Segments[0].Vel = Vector3.Zero;
        for (int tick = 1; tick <= selfAvoidParams.TerrainBacktrackReleaseTicks; tick++)
        {
            Array.Clear((bool[])GetPrivateField(selfAvoidTentacle, "_barrierObserved"));
            InvokePrivate(
                selfAvoidTentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, selfAvoidWall, tick));
        }
        bool phaseZeroRejectedOverlap = enabled
            && selfAvoidTentacle.TerrainRecoveryActive
            && selfAvoidTentacle.TerrainRecoveryPhase == 1
            && selfAvoidTentacle.TerrainBacktrackSerial == 0;
        int selfAvoidAttempts = 0;
        while (selfAvoidTentacle.TerrainRecoveryActive
            && selfAvoidAttempts < selfAvoidParams.TerrainBacktrackCandidatePhases)
        {
            selfAvoidAttempts++;
            Array.Clear((bool[])GetPrivateField(selfAvoidTentacle, "_barrierObserved"));
            InvokePrivate(
                selfAvoidTentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, selfAvoidWall,
                    selfAvoidParams.TerrainBacktrackReleaseTicks + selfAvoidAttempts));
        }
        float selfAvoidSuffixSeparation = float.PositiveInfinity;
        for (int i = 0; i < selfAvoidTentacle.Segments.Count; i++)
        {
            for (int j = Math.Max(i + 1, selfAvoidIndex);
                 j < selfAvoidTentacle.Segments.Count;
                 j++)
            {
                selfAvoidSuffixSeparation = Math.Min(
                    selfAvoidSuffixSeparation,
                    selfAvoidTentacle.Segments[i].Pos.DistanceTo(
                        selfAvoidTentacle.Segments[j].Pos));
            }
        }
        bool candidateSelfAvoided = phaseZeroRejectedOverlap
            && !selfAvoidTentacle.TerrainRecoveryActive
            && selfAvoidTentacle.TerrainBacktrackSerial == 1
            && selfAvoidAttempts <= selfAvoidParams.TerrainBacktrackCandidatePhases
            && selfAvoidSuffixSeparation >= selfAvoidMinimum
            && CountBlockedLinks(selfAvoidTentacle, selfAvoidWall) == 0;

        // ExternalReach 的墙后 tip 即使几何上“够到”目标，只要任一真实短链边被挡，
        // 从首 tick 起便不得 Held/拉力；门限后排队且只发布一次 Released。
        DaddyLongLegsParams externalParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController externalDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, externalParams, 0xBA27UL);
        int externalIndex = externalDaddy.FindIdleTentacle();
        DaddyTentacle externalTentacle = externalDaddy.Tentacles[externalIndex];
        foreach (BodyChunk chunk in externalDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var externalWall = new ThinWallTerrain();
        ConfigureCrossedWallChain(externalDaddy, externalTentacle, externalWall);
        const ulong blockedTargetId = 0xBA2701UL;
        bool externalAssigned = externalDaddy.TryAssignExternalTarget(
            externalIndex,
            new DaddyLongLegsTargetSnapshot(
                blockedTargetId,
                externalTentacle.Segments[^1].Pos,
                Vector3.Zero, 0.15f, 1f, true));
        bool externalSuppressed = externalAssigned;
        for (int tick = 1; tick <= externalParams.TerrainBacktrackReleaseTicks; tick++)
        {
            SetPrivateProperty(externalTentacle, "TargetEffect", default(DaddyLongLegsTargetEffect));
            Array.Clear((bool[])GetPrivateField(externalTentacle, "_barrierObserved"));
            InvokePrivate(
                externalTentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, externalWall, tick));
            InvokePrivate(externalTentacle, "UpdateSupport");
            InvokePrivate(externalTentacle, "UpdateExternalEffect", externalDaddy.BodyCenter);
            DaddyLongLegsTargetEffect blockedEffect = externalTentacle.TargetEffect;
            externalSuppressed &= !blockedEffect.Reached && !blockedEffect.Held
                && blockedEffect.PositionCorrection == Vector3.Zero
                && blockedEffect.VelocityDelta == Vector3.Zero
                && externalTentacle.SupportContribution == 0f
                && !externalTentacle.CanAcceptExternalTarget;
        }
        bool externalReleaseQueued = externalTentacle.ExternalTarget is null
            && externalTentacle.Task == DaddyTentacleTask.Locomotion
            && !externalTentacle.CanAcceptExternalTarget;
        TickTentacle(
            externalDaddy, externalTentacle, externalWall,
            externalParams.TerrainBacktrackReleaseTicks + 1L);
        DaddyLongLegsTargetEffect topologyReleased = externalTentacle.TargetEffect;
        bool externalReleasedOnce = topologyReleased.Released
            && topologyReleased.TargetId == blockedTargetId
            && !topologyReleased.Held;
        bool externalReassigned = externalDaddy.TryAssignExternalTarget(
            externalIndex,
            new DaddyLongLegsTargetSnapshot(
                0xBA2702UL, externalDaddy.BodyCenter + Vector3.Up * 2f,
                Vector3.Zero, 0.1f, 1f, false));
        TickTentacle(
            externalDaddy, externalTentacle, externalWall,
            externalParams.TerrainBacktrackReleaseTicks + 2L);
        bool noDuplicateRelease = !externalTentacle.TargetEffect.Released;
        bool externalTopologySafe = externalSuppressed && externalReleaseQueued
            && externalReleasedOnce && externalReassigned && noDuplicateRelease;

        // guide target 在碰撞面后方只属于任务阻挡：达到滞回门后重搜/Released，
        // 不得冒充真实链边穿墙而启动几何候选或增加 backtrack serial。
        DaddyLongLegsParams guideBlockParams = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController guideBlockDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, guideBlockParams, 0xBA28UL);
        int guideBlockIndex = guideBlockDaddy.FindIdleTentacle();
        DaddyTentacle guideBlockTentacle = guideBlockDaddy.Tentacles[guideBlockIndex];
        const ulong guideBlockedTargetId = 0xBA2801UL;
        bool guideBlockAssigned = guideBlockDaddy.TryAssignExternalTarget(
            guideBlockIndex,
            new DaddyLongLegsTargetSnapshot(
                guideBlockedTargetId,
                guideBlockTentacle.Segments[^1].Pos,
                Vector3.Zero, 0.1f, 1f, false));
        int guideSegment = guideBlockTentacle.Segments.Count - 1;
        Vector3 guidePredecessor = guideBlockTentacle.Segments[guideSegment - 1].Pos;
        Vector3 guidePoint = guidePredecessor - Vector3.Right * 0.10f;
        bool[] guideValid = (bool[])GetPrivateField(
            guideBlockTentacle, "_guideTargetValid");
        Vector3[] guideTargets = (Vector3[])GetPrivateField(
            guideBlockTentacle, "_guideTargets");
        var emptyTerrain = new EmptyTerrain();
        for (int tick = 1; tick <= guideBlockParams.TerrainBacktrackReleaseTicks; tick++)
        {
            SetPrivateField(guideBlockTentacle, "_guideObstructionObserved", false);
            guideValid[guideSegment] = true;
            guideTargets[guideSegment] = guidePoint - Vector3.Right;
            InvokePrivate(
                guideBlockTentacle, "ObserveGuideBarrier",
                guideSegment, guidePoint, Vector3.Right);
            Array.Clear((bool[])GetPrivateField(guideBlockTentacle, "_barrierObserved"));
            InvokePrivate(
                guideBlockTentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, emptyTerrain, tick));
        }
        bool guideOnlyReleased = guideBlockAssigned
            && guideBlockTentacle.GuideObstructionReleaseSerial == (enabled ? 1 : 0)
            && guideBlockTentacle.TerrainBacktrackSerial == 0
            && guideBlockTentacle.BacktrackFrom == -1
            && !guideBlockTentacle.TerrainRecoveryActive
            && (!enabled || guideBlockTentacle.ExternalTarget is null);
        TickTentacle(
            guideBlockDaddy, guideBlockTentacle, emptyTerrain,
            guideBlockParams.TerrainBacktrackReleaseTicks + 1L);
        bool guideReleasedOnce = enabled
            && guideBlockTentacle.TargetEffect.Released
            && guideBlockTentacle.TargetEffect.TargetId == guideBlockedTargetId;
        TickTentacle(
            guideBlockDaddy, guideBlockTentacle, emptyTerrain,
            guideBlockParams.TerrainBacktrackReleaseTicks + 2L);
        guideReleasedOnce &= !guideBlockTentacle.TargetEffect.Released;
        bool guideObstructionSafe = guideOnlyReleased && guideReleasedOnce;

        // 几何恢复状态属于完整生命周期：Shift 必须平移证据且保留 phase；
        // Teleport/Launch/Stun 则必须原子清掉旧房间/旧速度下的候选状态。
        var shiftedRecovery = CreateActiveRecoveryProbe(enabled, 0xBA2AUL);
        Vector3 recoveryPointBefore = (Vector3)GetPrivateField(
            shiftedRecovery.Tentacle, "_terrainRecoveryPoint");
        Vector3 recoveryShift = new(1.25f, -0.50f, 0.75f);
        shiftedRecovery.Daddy.Shift(recoveryShift);
        Vector3 recoveryPointAfter = (Vector3)GetPrivateField(
            shiftedRecovery.Tentacle, "_terrainRecoveryPoint");
        bool recoveryShifted = enabled
            && shiftedRecovery.Tentacle.TerrainRecoveryActive
            && shiftedRecovery.Tentacle.TerrainRecoveryPhase == 1
            && recoveryPointAfter == recoveryPointBefore + recoveryShift;
        shiftedRecovery.Daddy.Teleport(new Vector3(2f, 1f, -1f));
        bool recoveryTeleportCleared = !shiftedRecovery.Tentacle.TerrainRecoveryActive
            && shiftedRecovery.Tentacle.BacktrackFrom == -1
            && shiftedRecovery.Tentacle.TerrainRecoveryPhase == 0
            && (Vector3)GetPrivateField(
                shiftedRecovery.Tentacle, "_terrainRecoveryPoint") == Vector3.Zero;

        var launchedRecovery = CreateActiveRecoveryProbe(enabled, 0xBA2BUL);
        launchedRecovery.Daddy.Launch(new Vector3(0.03f, 0.07f, -0.02f));
        bool recoveryLaunchCleared = !launchedRecovery.Tentacle.TerrainRecoveryActive
            && launchedRecovery.Tentacle.BacktrackFrom == -1
            && launchedRecovery.Tentacle.TerrainRecoveryPhase == 0
            && (Vector3)GetPrivateField(
                launchedRecovery.Tentacle, "_terrainRecoveryPoint") == Vector3.Zero;

        var stunnedRecovery = CreateActiveRecoveryProbe(enabled, 0xBA2CUL);
        stunnedRecovery.Daddy.StunTentacle(stunnedRecovery.Tentacle.Index, 7);
        bool recoveryStunCleared = !stunnedRecovery.Tentacle.TerrainRecoveryActive
            && stunnedRecovery.Tentacle.BacktrackFrom == -1
            && stunnedRecovery.Tentacle.TerrainRecoveryPhase == 0
            && stunnedRecovery.Tentacle.StunTicks == 7
            && (Vector3)GetPrivateField(
                stunnedRecovery.Tentacle, "_terrainRecoveryPoint") == Vector3.Zero;
        bool recoveryLifecycleSafe = recoveryShifted && recoveryTeleportCleared
            && recoveryLaunchCleared && recoveryStunCleared;

        DaddyLongLegsParams wrapParams = TerrainBacktrackProbeParams(enabled);
        wrapParams.SegmentPathServo = 0f;
        wrapParams.SegmentRootSpreadForce = 0f;
        DaddyLongLegsLocomotionController wrapDaddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, wrapParams, 0xBA33UL);
        DaddyTentacle wrapTentacle = FirstLocomotion(wrapDaddy);
        foreach (BodyChunk chunk in wrapDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var corner = new AabbTerrain(
            new Vector3(-6f, -6f, -1f), Vector3.Zero, 0xBA33UL);
        ConfigureLegalCornerWrap(wrapDaddy, wrapTentacle, corner);
        int wrapSerialBefore = wrapTentacle.TerrainBacktrackSerial;
        Vector3 wrapLandingCenter = wrapTentacle.LandingPoint
            + wrapTentacle.LandingNormal
                * (wrapTentacle.Segments[^1].Radius + wrapParams.LandingSurfaceOffset);
        bool directOccluded = corner.Raycast(
            wrapTentacle.Anchor.Pos, wrapLandingCenter, out _);
        bool linksInitiallyClear = CountBlockedLinks(
            wrapTentacle, corner) == 0;
        bool wrapSegmentsClear = AllSegmentsClear(wrapTentacle, corner);
        bool landingRetained = true;
        bool noFalseBacktrack = true;
        for (int tick = 1; tick <= 8; tick++)
        {
            TickTentacle(wrapDaddy, wrapTentacle, corner, tick);
            landingRetained &= wrapTentacle.HasLandingTarget;
            noFalseBacktrack &= wrapTentacle.BacktrackFrom == -1
                && wrapTentacle.TerrainBacktrackSerial == wrapSerialBefore;
            wrapSegmentsClear &= AllSegmentsClear(wrapTentacle, corner)
                && CountBlockedLinks(wrapTentacle, corner) == 0;
        }
        bool legalWrapKept = directOccluded && linksInitiallyClear
            && landingRetained && noFalseBacktrack && wrapSegmentsClear;

        bool ok = postSweepCaught && residualOrderCaught && contractRollbackCaught
            && distalBacktracked && multiBarrierRecovered
            && ultraThinCaught && migratingRecovered && atomicRecovered
            && candidateSelfAvoided
            && externalTopologySafe && guideObstructionSafe
            && recoveryLifecycleSafe && legalWrapKept;
        return (ok,
            $"enabled={enabled} sweep={postSweepCaught}/" +
            $"serial{sweepSerialBefore}->{sweepTentacle.PostConstraintSweepSerial}/" +
            $"pre{postConstraintPrecondition} residualOrder=" +
            $"{residualBarrierTerrain.Pushed}/{residualOrderTentacle.BacktrackFrom}/" +
            $"blocked{residualOrderBlocked}/{residualOrderCaught} " +
            $"rollback={contractRollbackApplied}/{rollbackBlocked}/" +
            $"{contractRollbackCaught} crossed=" +
            $"{initiallyBlocked}/boundary{sawBoundary}/neutral{sawDistalNeutral}/" +
            $"prox{sawProximalRetained}/clear{clearedAt}/" +
            $"serial{backtrackSerialBefore}->{crossedTentacle.TerrainBacktrackSerial}/" +
            $"landing{staleFarLandingGone} multi={initialMultiBlocked}->" +
            $"{finalMultiBlocked}/serial{multiSerialBefore}->" +
            $"{multiBarrierTentacle.TerrainBacktrackSerial}/" +
            $"clear{multiBarrierRecovered} ultra={ultraThinCaught}/" +
            $"migrate{migratingMaximumPerEdge}," +
            $"{migratingTentacle.MaximumBarrierTicks}/{migratingRecovered} " +
            $"atomic={atomicWall.InjectedFailures}/{failureWasAtomic}/" +
            $"sep{atomicMinimumSeparation:F3}/{atomicRecovered} " +
            $"self={phaseZeroRejectedOverlap}/" +
            $"p{selfAvoidAttempts}/{selfAvoidSuffixSeparation:F3}/" +
            $"{candidateSelfAvoided} " +
            $"external={externalSuppressed}/{externalReleaseQueued}/" +
            $"{externalReleasedOnce}/{externalReassigned}/{noDuplicateRelease} " +
            $"guide={guideOnlyReleased}/{guideReleasedOnce}/" +
            $"g{guideBlockTentacle.GuideObstructionReleaseSerial}/" +
            $"b{guideBlockTentacle.TerrainBacktrackSerial} " +
            $"life={recoveryShifted}/{recoveryTeleportCleared}/" +
            $"{recoveryLaunchCleared}/{recoveryStunCleared} " +
            $"wrap={directOccluded}/" +
            $"{linksInitiallyClear}/{landingRetained}/{noFalseBacktrack}/" +
            $"clear{wrapSegmentsClear}");
    }

    private static (bool, string) CheckDirectionalDrive(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableDirectionalDrive = ablation != Ablation.DirectionalDrive;
        DaddyLongLegsLocomotionController front =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 81UL);
        DaddyLongLegsLocomotionController back =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 81UL);
        ConfigureDirectionalSupports(front, Vector3.Right, frontSide: true);
        ConfigureDirectionalSupports(back, Vector3.Right, frontSide: false);
        for (int i = 0; i < 20; i++)
        {
            InvokePrivate(front, "AggregateSupport", Vector3.Right);
            InvokePrivate(back, "AggregateSupport", Vector3.Right);
        }
        InvokePrivate(front, "ApplyWholeBodyDrive", Vector3.Right);
        InvokePrivate(back, "ApplyWholeBodyDrive", Vector3.Right);
        float frontSpeed = AverageVelocityVector(front.Body).Dot(Vector3.Right);
        float backSpeed = AverageVelocityVector(back.Body).Dot(Vector3.Right);
        bool equalImpulse = VelocitySpread(front.Body) < 2e-6f;
        bool ok = front.DirectionalSupport > back.DirectionalSupport + 0.05f
            && front.DriveScale > back.DriveScale + 0.02f
            && frontSpeed > backSpeed + 1e-4f
            && equalImpulse;
        return (ok,
            $"enabled={p.EnableDirectionalDrive} directional(front/back)=" +
            $"{front.DirectionalSupport:F3}/{back.DirectionalSupport:F3} drive=" +
            $"{front.DriveScale:F3}/{back.DriveScale:F3} velocity=" +
            $"{frontSpeed:F5}/{backSpeed:F5} equalImpulse={equalImpulse}");
    }

    private static (bool, string) CheckAllocator(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.MinimumTentacles = p.MaximumTentacles = 6;
        p.MinimumLocomotionTentacles = 2;
        p.DutyChangeCooldownTicks = 1;
        p.EnableDutyAllocation = ablation != Ablation.Allocation;
        p.MaximumTotalTentacleSegments = 84;
        p.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 20f, 0f), p, 14UL);
        daddy.Launch(Vector3.Zero);
        daddy.MoveDir = Vector3.Right;
        var empty = new EmptyTerrain();
        long tick = 0;
        int initial = daddy.LocomotionTentacleCount;
        for (int i = 0; i < 18; i++)
            Tick(daddy, empty, ref tick, Vector3.Zero);
        int expanded = daddy.LocomotionTentacleCount;
        bool countSynchronized = expanded == daddy.LocomotionTentCount();
        int assigned = daddy.DutyAssignmentSerial;
        bool idleReserved = daddy.FindIdleTentacle() >= 0
            && expanded == daddy.Tentacles.Count - p.ReservedIdleTentacles;

        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            if (tentacle.Role == DaddyTentacleRole.Idle)
                InvokePrivate(tentacle, "SetLocomotion");
            if (tentacle.Role == DaddyTentacleRole.Locomotion)
            {
                PrepareSyntheticGrip(tentacle, daddy.BodyCenter,
                    tentacle.Segments.Count, true);
                InvokePrivate(tentacle, "UpdateSupport");
            }
        }
        for (int i = 0; i < 12; i++)
        {
            InvokePrivate(daddy, "AggregateSupport", Vector3.Zero);
            InvokePrivate(daddy, "UpdateDutyAllocator", (long)i);
        }
        int releasedCount = daddy.LocomotionTentacleCount;
        countSynchronized &= releasedCount == daddy.LocomotionTentCount();
        int releases = daddy.DutyReleaseSerial;

        // 低支撑时必须挑真正空闲且离地形最近的一条；即使 ExternalReach / Stunned
        // 的距离提示更小，职责分配器也不能抢占它们。
        DaddyLongLegsLocomotionController priorityDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 20f, 0f), p, 15UL);
        priorityDaddy.Launch(Vector3.Zero);
        DaddyTentacle[] priorityIdle = priorityDaddy.Tentacles
            .Where(t => t.Role == DaddyTentacleRole.Idle)
            .ToArray();
        int externalIndex = priorityIdle[0].Index;
        int stunnedIndex = priorityIdle[1].Index;
        int nearestIndex = priorityIdle[2].Index;
        int fartherIndex = priorityIdle[3].Index;
        DaddyTentacle external = priorityDaddy.Tentacles[externalIndex];
        bool externalAssigned = priorityDaddy.TryAssignExternalTarget(
            externalIndex,
            new DaddyLongLegsTargetSnapshot(
                0xA110CUL,
                external.Segments[^1].Pos,
                Vector3.Zero,
                0.1f,
                1f,
                false));
        priorityDaddy.StunTentacle(stunnedIndex, 20);
        SetPrivateProperty(priorityDaddy.Tentacles[externalIndex], "TerrainDistanceHint", 0.001f);
        SetPrivateProperty(priorityDaddy.Tentacles[stunnedIndex], "TerrainDistanceHint", 0.002f);
        SetPrivateProperty(priorityDaddy.Tentacles[nearestIndex], "TerrainDistanceHint", 0.05f);
        SetPrivateProperty(priorityDaddy.Tentacles[fartherIndex], "TerrainDistanceHint", 4f);
        InvokePrivate(priorityDaddy, "UpdateDutyAllocator", 123L);
        bool nearestPriority = externalAssigned
            && priorityDaddy.Tentacles[nearestIndex].Role == DaddyTentacleRole.Locomotion
            && priorityDaddy.Tentacles[fartherIndex].Role == DaddyTentacleRole.Idle
            && priorityDaddy.Tentacles[externalIndex].Role == DaddyTentacleRole.ExternalReach
            && priorityDaddy.Tentacles[stunnedIndex].Role == DaddyTentacleRole.Stunned
            && priorityDaddy.DutyAssignmentSerial == 1;
        countSynchronized &= priorityDaddy.LocomotionTentacleCount
            == priorityDaddy.LocomotionTentCount();

        bool ok = initial == p.MinimumLocomotionTentacles
            && expanded > initial && assigned > 0 && idleReserved
            && releasedCount < daddy.Tentacles.Count && releases > 0
            && daddy.Tentacles.All(t => t.Role != DaddyTentacleRole.ExternalReach)
            && nearestPriority && countSynchronized;
        return (ok,
            $"enabled={p.EnableDutyAllocation} duty={initial}->{expanded}->{releasedCount} " +
            $"idleReserved={idleReserved} assignSerial={assigned} releaseSerial={releases} " +
            $"priority={nearestIndex}/{fartherIndex} busy={externalIndex}/{stunnedIndex} " +
            $"nearestSelected={nearestPriority} countSync={countSynchronized}");
    }

    private static (bool, string) CheckDutySeparation(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.MinimumTentacles = p.MaximumTentacles = 5;
        p.MinimumLocomotionTentacles = 2;
        p.EnableDutyAllocation = false;
        p.EnableStuckRecovery = false;
        p.EnableStartReplant = false;
        p.EnableIndependentLocomotionDuty = ablation != Ablation.IndependentDuty;
        p.SearchRefreshTicks = 1;
        p.MaximumTotalTentacleSegments = 70;
        p.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 0xD017UL);
        daddy.Launch(Vector3.Zero);

        DaddyTentacle[] free = daddy.Tentacles
            .Where(t => !t.NeededForLocomotion)
            .ToArray();
        DaddyTentacle reachProbe = free[0];
        PrepareSyntheticGrip(reachProbe, daddy.BodyCenter,
            reachProbe.Segments.Count, true);
        SetPrivateProperty(reachProbe, "LandingPoint",
            reachProbe.Anchor.Pos + Vector3.Right * (reachProbe.Length * 1.25f));
        InvokePrivate(reachProbe, "ValidateLanding",
            new TickContext(Vector3.Zero, new EmptyTerrain(), 0), false);
        bool staleCleared = !reachProbe.HasLandingTarget;
        PrepareSyntheticGrip(reachProbe, daddy.BodyCenter,
            reachProbe.Segments.Count, true);
        SetPrivateProperty(reachProbe, "LandingPoint",
            reachProbe.Anchor.Pos + Vector3.Right * (reachProbe.Length * 0.65f));
        InvokePrivate(reachProbe, "ValidateLanding",
            new TickContext(Vector3.Zero, new EmptyTerrain(), 1), false);
        bool reachableKept = reachProbe.HasLandingTarget;

        DaddyTentacle highestFree = free[^1];
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            PrepareSyntheticGrip(tentacle, daddy.BodyCenter,
                tentacle.Segments.Count, true);
            SetPrivateProperty(tentacle, "ReleaseScore", tentacle.Index + 1f);
            InvokePrivate(tentacle, "UpdateSupport");
        }
        bool orthogonalState = daddy.Tentacles.All(
                t => t.Task == DaddyTentacleTask.Locomotion)
            && daddy.Tentacles.Count(t => t.NeededForLocomotion)
                == p.MinimumLocomotionTentacles
            && highestFree.Role == DaddyTentacleRole.Idle
            && highestFree.SupportContribution > 0f;
        float freeSupport = highestFree.SupportContribution;
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        InvokePrivate(daddy, "ResolveMoveIntent");
        InvokePrivate(daddy, "AggregateSupport", Vector3.Right);
        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);
        bool freeDutyStepped = highestFree.StepSerial == 1
            && daddy.StepReleaseSerial == 1;

        SetPrivateProperty(highestFree, "HasLandingTarget", false);
        SetPrivateProperty(highestFree, "AtGrabDestination", false);
        int searchBefore = highestFree.SearchSerial;
        long tick = 0;
        daddy.MoveDir = Vector3.Right;
        for (int i = 0; i <= p.StepPeelTicks + 1; i++)
            Tick(daddy, new EmptyTerrain(), ref tick, Vector3.Zero);
        bool freeDutySearched = highestFree.SearchSerial > searchBefore;

        bool ok = orthogonalState && freeDutyStepped && freeDutySearched
            && staleCleared && reachableKept;
        return (ok,
            $"enabled={p.EnableIndependentLocomotionDuty} task/needed=" +
            $"{daddy.Tentacles.Count(t => t.Task == DaddyTentacleTask.Locomotion)}/" +
            $"{daddy.LocomotionTentacleCount} freeSupport={freeSupport:F3} " +
            $"freeStep={freeDutyStepped} freeSearch={freeDutySearched} " +
            $"reach={staleCleared}/{reachableKept}");
    }

    private static (bool, string) CheckStepAndSearch(Ablation ablation)
    {
        DaddyLongLegsParams stepParams = ProbeParams();
        stepParams.EnableStepRelease = ablation != Ablation.Step;
        stepParams.EnableStartReplant = false;
        stepParams.MinimumTentacles = stepParams.MaximumTentacles = 5;
        stepParams.MinimumLocomotionTentacles = 2;
        stepParams.MinimumArrivedTentaclesForStep = 2;
        stepParams.MaximumTotalTentacleSegments = 70;
        stepParams.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController stepDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), stepParams, 23UL);
        foreach (DaddyTentacle tentacle in stepDaddy.Tentacles)
        {
            InvokePrivate(tentacle, "SetLocomotion");
            PrepareSyntheticGrip(tentacle, stepDaddy.BodyCenter,
                tentacle.Segments.Count, true);
            SetPrivateProperty(tentacle, "ReleaseScore", tentacle.Index + 1f);
            InvokePrivate(tentacle, "UpdateSupport");
        }
        stepDaddy.MoveDir = Vector3.Right;
        stepDaddy.RunSpeed = 1f;
        InvokePrivate(stepDaddy, "ResolveMoveIntent");
        InvokePrivate(stepDaddy, "AggregateSupport", Vector3.Right);
        InvokePrivate(stepDaddy, "UpdateStepRelease", Vector3.Right);
        int steppedIndex = -1;
        for (int i = 0; i < stepDaddy.Tentacles.Count; i++)
            if (stepDaddy.Tentacles[i].StepSerial > 0)
                steppedIndex = i;
        bool stepOk = stepDaddy.StepReleaseSerial == 1
            && steppedIndex == stepDaddy.Tentacles.Count - 1
            && !stepDaddy.Tentacles[steppedIndex].HasLandingTarget;

        DaddyLongLegsParams searchParams = ProbeParams();
        searchParams.MinimumTentacles = searchParams.MaximumTentacles = 3;
        searchParams.MinimumLocomotionTentacles = 1;
        searchParams.SearchFailureExpandTicks = 12;
        searchParams.SearchRefreshTicks = 1;
        searchParams.SearchReachMinimumRatio = 0.35f;
        searchParams.SearchReachMaximumRatio = 1.20f;
        searchParams.SearchRayCount = 1;
        searchParams.SearchConeMinimumDegrees = 0f;
        searchParams.SearchConeMaximumDegrees = 1f;
        searchParams.EnableStuckRecovery = false;
        searchParams.EnableSearchExpansion = ablation != Ablation.SearchExpansion;
        searchParams.MaximumTotalTentacleSegments = 42;
        searchParams.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController searchDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 10f, 0f), searchParams, 5UL);
        foreach (BodyChunk chunk in searchDaddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        DaddyTentacle seeking = FirstLocomotion(searchDaddy);
        SetPrivateField(seeking, "_needsTerrainExpansion", false);
        Vector3 preference = WorldPreference(searchDaddy, seeking);
        Vector3 planePoint = seeking.Anchor.Pos + preference * (seeking.Length * 0.83f);
        var farPlane = new HalfSpaceTerrain(planePoint, -preference, 9UL);
        searchDaddy.MoveDir = preference;
        searchDaddy.RunSpeed = 0f;
        long tick = 0;
        int maxFailure = 0;
        int foundTick = -1;
        for (int i = 0; i < 80; i++)
        {
            Tick(searchDaddy, farPlane, ref tick, Vector3.Zero);
            maxFailure = Math.Max(maxFailure, seeking.SearchFailureTicks);
            if (seeking.HasLandingTarget)
            {
                foundTick = i;
                break;
            }
        }
        bool searchOk = maxFailure >= searchParams.SearchFailureExpandTicks / 3
            && foundTick > 0 && seeking.SearchFailureTicks == 0
            && seeking.LandingSerial > 0;
        return (stepOk && searchOk,
            $"stepEnabled={stepParams.EnableStepRelease} stepSerial={stepDaddy.StepReleaseSerial} " +
            $"selected={steppedIndex} searchMaxFailure={maxFailure} foundTick={foundTick} " +
            $"landingSerial={seeking.LandingSerial}");
    }

    private static (bool, string) CheckReplantPhases(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableStepPeel = ablation != Ablation.StepPeel;
        p.EnableDutyAllocation = false;
        p.EnableStepRelease = false;
        p.EnableDirectionalDrive = false;
        p.EnableStuckRecovery = false;
        p.SearchRefreshTicks = 1;
        p.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 0x5EEDUL);
        foreach (BodyChunk chunk in daddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        DaddyTentacle tentacle = FirstLocomotion(daddy);
        float surfaceY = p.SegmentRadius + p.LandingSurfaceOffset;
        daddy.Body.Shift(Vector3.Up * (surfaceY - tentacle.Anchor.Pos.Y));
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            segment.Pos = tentacle.Anchor.Pos
                + Vector3.Right * (tentacle.LinkLength * (i + 1));
            segment.Pos.Y = surfaceY;
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            segment.TerrainContact = true;
            segment.ContactNormal = Vector3.Up;
            segment.ActiveGrip = true;
            segment.GripNormal = Vector3.Up;
            segment.GripColliderId = 1UL;
        }
        SetPrivateField(tentacle, "_needsTerrainExpansion", false);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint",
            new Vector3(tentacle.Segments[^1].Pos.X, 0f, tentacle.Segments[^1].Pos.Z));
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Up);
        SetPrivateProperty(tentacle, "LandingColliderId", 1UL);
        SetPrivateProperty(tentacle, "ReplantPhase", DaddyTentacleReplantPhase.Planted);
        SetPrivateField(tentacle, "_landingAge", p.StepArrivalMinimumTicks + 1);
        InvokePrivate(tentacle, "UpdateSupport");

        int releaseStart = Math.Clamp(
            (int)MathF.Floor(tentacle.Segments.Count * p.StepPeelStartFraction),
            0,
            tentacle.Segments.Count - 1);
        float tipStart = tentacle.Segments[^1].Pos.Y;
        InvokePrivate(tentacle, "BeginStep");
        bool sawPeeling = tentacle.ReplantPhase == DaddyTentacleReplantPhase.Peeling;
        bool rootRetained = false;
        bool progressive = false;
        int previousActive = tentacle.Segments.Count(s => s.ActiveGrip);
        long tick = 0;
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        for (int i = 0; i < p.StepPeelMaximumTicks + 2
             && tentacle.ReplantPhase == DaddyTentacleReplantPhase.Peeling; i++)
        {
            Tick(daddy, floor, ref tick, Vector3.Zero);
            int active = tentacle.Segments.Count(s => s.ActiveGrip);
            rootRetained |= tentacle.Segments.Take(releaseStart).Any(s => s.ActiveGrip);
            progressive |= active < previousActive && active > 0;
            previousActive = active;
        }
        bool reached = tentacle.ReplantPhase == DaddyTentacleReplantPhase.Reaching;
        int distalCount = tentacle.Segments.Count - releaseStart;
        int distalContacts = tentacle.Segments.Skip(releaseStart).Count(s => s.TerrainContact);
        float distalContactFraction = (float)distalContacts / distalCount;
        float tipClearance = tentacle.Segments[^1].Pos.Y - tipStart;

        // 一 tick 无地形，证明旧落点留下的抓附不会穿过 Reaching 偷渡；随后恢复
        // 地面，必须重新搜索并回到 Planted，而不是永久失去这条腿。
        Tick(daddy, new EmptyTerrain(), ref tick, Vector3.Zero);
        bool oldGripCleared = tentacle.Segments.All(s => !s.ActiveGrip);
        // 夹具的锚点为验证贴面剥离而特意放在地表高度；重落脚阶段把整只个体
        // 平移到正常可搜索高度，避免候选因“离锚点小于半个身体半径”被正确拒绝。
        daddy.Shift(Vector3.Up * 1.5f);
        bool replanted = false;
        int reacquireTicks = -1;
        for (int i = 0; i < 96; i++)
        {
            Tick(daddy, floor, ref tick, Vector3.Zero);
            if (tentacle.ReplantPhase == DaddyTentacleReplantPhase.Planted
                && tentacle.AtGrabDestination && tentacle.ActiveGripCount > 0)
            {
                replanted = true;
                reacquireTicks = i + 1;
                break;
            }
        }
        bool physicallyPeeled = distalContactFraction <= p.StepPeelMaximumContactFraction + 0.01f
            && tipClearance >= 0.05f;
        bool ok = sawPeeling && progressive && rootRetained && reached
            && physicallyPeeled && oldGripCleared && replanted;
        return (ok,
            $"enabled={p.EnableStepPeel} phase={sawPeeling}/{reached}/{replanted} " +
            $"progressive={progressive} rootHeld={rootRetained} " +
            $"distalTouch={distalContacts}/{distalCount}({distalContactFraction:F2}) " +
            $"tipClear={tipClearance:F3} oldClear={oldGripCleared} reacquire={reacquireTicks} " +
            $"search={tentacle.SearchSerial}/failure={tentacle.SearchFailureTicks} " +
            $"landing={tentacle.HasLandingTarget}/{tentacle.AtGrabDestination}/" +
            $"{tentacle.ActiveGripCount} phaseNow={tentacle.ReplantPhase}");
    }

    private static (bool, string) CheckFlatGuideShape(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableSlackGuide = ablation != Ablation.SlackGuide;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 8f, 0f), p, 0xC0DEUL);
        DaddyTentacle tentacle = FirstLocomotion(daddy);
        Vector3 landingCenter = tentacle.Anchor.Pos
            + Vector3.Right * (tentacle.Length * 0.45f)
            + Vector3.Down * (tentacle.Length * 0.60f);
        Vector3 landing = landingCenter - Vector3.Up
            * (p.SegmentRadius + p.LandingSurfaceOffset);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint", landing);
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Up);
        SetPrivateProperty(tentacle, "LandingColliderId", 1UL);
        InvokePrivate(tentacle, "BuildLandingGuide", Vector3.Right);

        const int samples = 96;
        var points = new Vector3[samples + 1];
        bool finite = true;
        for (int i = 0; i <= samples; i++)
        {
            points[i] = (Vector3)(InvokePrivate(
                tentacle, "EvaluateLandingGuide", (float)i / samples) ?? Vector3.Zero);
            finite &= Finite(points[i]);
        }
        float maximumTurn = 0f;
        float totalTurn = 0f;
        int hardTurns = 0;
        for (int i = 1; i < samples; i++)
        {
            Vector3 before = points[i] - points[i - 1];
            Vector3 after = points[i + 1] - points[i];
            if (before.LengthSquared() <= 1e-10f || after.LengthSquared() <= 1e-10f)
                continue;
            float degrees = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(
                before.Normalized().Dot(after.Normalized()), -1f, 1f)));
            maximumTurn = Math.Max(maximumTurn, degrees);
            totalTurn += degrees;
            if (degrees >= 70f)
                hardTurns++;
        }
        float guideRatio = tentacle.GuideLength / tentacle.Length;
        // 每个段按引导曲线等弧长排布，因此这个比值就是计划贴地段占比。
        float contactRatio = tentacle.GuideContactLength
            / Math.Max(tentacle.GuideLength, 1e-5f);
        float endpointError = points[^1].DistanceTo(landingCenter);
        Vector3 chord = points[^1] - points[0];
        float maximumChordDeviation = 0f;
        if (chord.LengthSquared() > 1e-10f)
        {
            Vector3 chordDirection = chord.Normalized();
            foreach (Vector3 point in points)
            {
                Vector3 fromStart = point - points[0];
                Vector3 perpendicular = fromStart
                    - chordDirection * fromStart.Dot(chordDirection);
                maximumChordDeviation = Math.Max(
                    maximumChordDeviation, perpendicular.Length());
            }
        }
        float turnConcentration = maximumTurn / Math.Max(totalTurn, 1e-5f);
        bool ok = finite
            && guideRatio is >= 0.80f and <= 1.001f
            && contactRatio > 0.01f && contactRatio < 0.25f
            && maximumTurn < 35f && hardTurns == 0
            && totalTurn >= 35f && turnConcentration < 0.55f
            && maximumChordDeviation >= tentacle.Length * 0.04f
            && endpointError < 0.015f;
        return (ok,
            $"enabled={p.EnableSlackGuide} length={tentacle.GuideLength:F3}/" +
            $"{tentacle.Length:F3}({guideRatio:F3}) contact=" +
            $"{tentacle.GuideContactLength:F3}({contactRatio:F3}) " +
            $"turn={maximumTurn:F2}/{totalTurn:F2}/{turnConcentration:F2} " +
            $"chordDev={maximumChordDeviation:F3} hard={hardTurns} " +
            $"endErr={endpointError:F5} finite={finite}");
    }

    private static (bool, string) CheckStunTakeover(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.MinimumTentacles = p.MaximumTentacles = 6;
        p.MinimumLocomotionTentacles = 2;
        p.DutyChangeCooldownTicks = 1;
        p.MaximumTotalTentacleSegments = 84;
        p.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, 94UL);
        daddy.MoveDir = Vector3.Right;
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        long tick = 0;
        for (int i = 0; i < 80; i++)
            Tick(daddy, floor, ref tick);
        DaddyTentacle[] allocated = daddy.Tentacles
            .Where(t => t.Role == DaddyTentacleRole.Locomotion)
            .ToArray();
        for (int i = p.MinimumLocomotionTentacles; i < allocated.Length; i++)
            InvokePrivate(allocated[i], "SetIdle");
        int victim = daddy.Tentacles
            .First(t => t.Role == DaddyTentacleRole.Locomotion).Index;
        int beforeCount = daddy.LocomotionTentCount();
        int beforeAssignments = daddy.DutyAssignmentSerial;
        DaddyTentacle victimTentacle = daddy.Tentacles[victim];
        victimTentacle.Segments[0].TerrainContact = true;
        victimTentacle.Segments[0].ContactNormal = Vector3.Up;
        daddy.StunTentacle(victim, 28);
        bool terrainMemoryCleared = victimTentacle.Segments.All(
            segment => !segment.TerrainContact
                && segment.ContactNormal.LengthSquared() <= 1e-10f);
        bool immediateCount = daddy.LocomotionTentacleCount == beforeCount - 1
            && daddy.LocomotionTentacleCount == daddy.LocomotionTentCount();
        bool strictlyZero = true;
        bool remainedStunned = true;
        int minimumOthers = int.MaxValue;
        float maximumSpeed = 0f;
        for (int i = 0; i < 20; i++)
        {
            Tick(daddy, floor, ref tick);
            DaddyTentacle stunned = daddy.Tentacles[victim];
            strictlyZero &= stunned.GripFraction == 0f && stunned.SupportContribution == 0f
                && !stunned.NeededForLocomotion;
            remainedStunned &= stunned.Role == DaddyTentacleRole.Stunned;
            minimumOthers = Math.Min(minimumOthers, daddy.LocomotionTentCount());
            maximumSpeed = Math.Max(maximumSpeed, AverageVelocityVector(daddy.Body).Length());
        }
        bool takeover = daddy.DutyAssignmentSerial > beforeAssignments
            && minimumOthers >= Math.Min(p.MinimumLocomotionTentacles, beforeCount);
        bool stable = IsFinite(daddy) && maximumSpeed < 1.5f
            && daddy.BodyCenter.Y > -0.2f;

        // 在无地形空中隔离“变软下垂”：相对持续下坠的锚点，受击触手的末端与
        // 全链质心都必须沿重力方向进一步下垂，而不只是换一个 Role 名字。
        DaddyLongLegsParams limpParams = ProbeParams();
        limpParams.EnableDutyAllocation = false;
        limpParams.EnableStunLimp = ablation != Ablation.StunLimp;
        DaddyLongLegsLocomotionController limpDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 20f, 0f), limpParams, 95UL);
        DaddyTentacle limpTentacle = FirstLocomotion(limpDaddy);
        Vector3 gravityDirection = GravityPerTick.Normalized();
        float tipBefore = (limpTentacle.Segments[^1].Pos - limpTentacle.Anchor.Pos)
            .Dot(gravityDirection);
        float centroidBefore = limpTentacle.Segments
            .Average(s => (s.Pos - limpTentacle.Anchor.Pos).Dot(gravityDirection));
        limpDaddy.StunTentacle(limpTentacle.Index, 40);
        long limpTick = 0;
        for (int i = 0; i < 20; i++)
        {
            // 只隔离触手的 LimpGravityScale；否则自由落体中的锚点速度会掩盖相对下垂。
            limpDaddy.Body.GravityScale = 0f;
            foreach (BodyChunk chunk in limpDaddy.Body.Chunks)
                chunk.Vel = Vector3.Zero;
            Tick(limpDaddy, new EmptyTerrain(), ref limpTick);
        }
        float tipAfter = (limpTentacle.Segments[^1].Pos - limpTentacle.Anchor.Pos)
            .Dot(gravityDirection);
        float centroidAfter = limpTentacle.Segments
            .Average(s => (s.Pos - limpTentacle.Anchor.Pos).Dot(gravityDirection));
        bool limpDroop = limpTentacle.Role == DaddyTentacleRole.Stunned
            && tipAfter > tipBefore + 0.02f
            && centroidAfter > centroidBefore + 0.02f
            && IsFinite(limpDaddy);

        DaddyLongLegsParams rootParams = ProbeParams();
        rootParams.EnableDutyAllocation = false;
        rootParams.EnableStunLimp = false;
        DaddyLongLegsLocomotionController rootDaddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 20f, 0f), rootParams, 96UL);
        DaddyTentacle rootTentacle = FirstLocomotion(rootDaddy);
        foreach (DaddyTentacleSegmentState segment in rootTentacle.Segments)
            segment.Vel = Vector3.Zero;
        rootDaddy.StunTentacle(rootTentacle.Index, 4);
        Vector3 rootBefore = rootTentacle.Segments[0].Pos;
        InvokePrivate(
            rootTentacle,
            "IntegrateSegments",
            new TickContext(Vector3.Zero, new EmptyTerrain(), 0L),
            Vector3.Right);
        bool stunHasNoTerrainTaskForce = NearVector(
            rootTentacle.Segments[0].Pos, rootBefore, 1e-7f);

        return (strictlyZero && remainedStunned && takeover && stable && limpDroop
                && immediateCount && terrainMemoryCleared && stunHasNoTerrainTaskForce,
            $"victim={victim} zero={strictlyZero} roleHeld={remainedStunned} " +
            $"dutyBefore/minOther={beforeCount}/{minimumOthers} assignment=" +
            $"{beforeAssignments}->{daddy.DutyAssignmentSerial} maxSpeed={maximumSpeed:F3} " +
            $"centerY={daddy.BodyCenter.Y:F3} droop=" +
            $"{tipBefore:F3}->{tipAfter:F3}/{centroidBefore:F3}->{centroidAfter:F3} " +
            $"limp={limpDroop} immediateCount={immediateCount} " +
            $"detach={terrainMemoryCleared}/{stunHasNoTerrainTaskForce}");
    }

    private static (bool, string) CheckExternalTargetContract(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableDutyAllocation = false;
        p.EnableExternalPull = ablation != Ablation.ExternalPull;
        p.MinimumTentacles = p.MaximumTentacles = 5;
        p.MinimumLocomotionTentacles = 2;
        p.MaximumTotalTentacleSegments = 70;
        p.MaximumTerrainQueriesPerTick = 3500;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 4f, 0f), p, 60UL);
        int locomotionIndex = Enumerable.Range(0, daddy.Tentacles.Count)
            .First(i => daddy.Tentacles[i].Role == DaddyTentacleRole.Locomotion);
        DaddyTentacle locomotion = daddy.Tentacles[locomotionIndex];
        SetPrivateProperty(locomotion, "HasLandingTarget", true);
        SetPrivateProperty(locomotion, "LandingPoint", locomotion.Anchor.Pos + Vector3.Down);
        SetPrivateProperty(locomotion, "GripFraction", 0.625f);
        SetPrivateProperty(locomotion, "SupportContribution", 0.75f);
        daddy.ClearExternalTarget(locomotionIndex);
        daddy.ClearExternalTarget(locomotionIndex);
        bool clearIdempotent = locomotion.Role == DaddyTentacleRole.Locomotion
            && locomotion.HasLandingTarget
            && Near(locomotion.GripFraction, 0.625f, 1e-7f)
            && Near(locomotion.SupportContribution, 0.75f, 1e-7f);
        int idle = daddy.FindIdleTentacle();
        DaddyTentacle tentacle = daddy.Tentacles[idle];
        // 把整条链放在无拉伸的远端直线上：tip 已够到目标，同时目标离身体超过
        // 0.45L，PositionCorrection 与有上限的 VelocityDelta 都应非零。
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            float u = (float)(i + 1) / tentacle.Segments.Count;
            segment.Pos = tentacle.Anchor.Pos + Vector3.Right * (tentacle.Length * 0.80f * u);
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            segment.TerrainContact = true;
            segment.ContactNormal = Vector3.Up;
        }
        Vector3 targetPosition = tentacle.Segments[^1].Pos;
        var target = new DaddyLongLegsTargetSnapshot(
            0xCAFEUL, targetPosition, Vector3.Zero, 0.25f, 0.5f, true);
        int locomotionBefore = daddy.LocomotionTentCount();
        bool assigned = daddy.TryAssignExternalTarget(idle, target);
        bool externalTerrainMemoryCleared = tentacle.Segments.All(
            segment => !segment.TerrainContact
                && segment.ContactNormal.LengthSquared() <= 1e-10f);
        bool differentIdRejected = !daddy.TryAssignExternalTarget(
            idle,
            new DaddyLongLegsTargetSnapshot(
                0xBEEFUL, targetPosition, Vector3.Zero, 0.1f, 1f, false));

        var empty = new EmptyTerrain();
        long tick = 0;
        Tick(daddy, empty, ref tick, Vector3.Zero);
        DaddyLongLegsTargetEffect effect = daddy.TargetEffects[idle];
        Vector3 expectedPullDirection = (daddy.BodyCenter - target.Position).Normalized();
        float targetDistance = daddy.BodyCenter.DistanceTo(target.Position);
        float massScale = 1f / Math.Max(0.25f, target.Mass);
        float expectedVelocity = Math.Min(
            p.ExternalPullVelocityCap,
            targetDistance * p.ExternalPullGain * massScale);
        float expectedCorrection = Math.Max(0f, targetDistance - tentacle.Length * 0.45f)
            * p.ExternalPositionCorrectionGain * massScale;
        bool pull = effect.VelocityDelta.Length() > 1e-4f
            && effect.PositionCorrection.Length() > 1e-4f
            && effect.VelocityDelta.Normalized().Dot(expectedPullDirection) > 0.9999f
            && effect.PositionCorrection.Normalized().Dot(expectedPullDirection) > 0.9999f
            && Near(effect.VelocityDelta.Length(), expectedVelocity, 2e-5f)
            && effect.VelocityDelta.Length() <= p.ExternalPullVelocityCap + 1e-6f
            && Near(effect.PositionCorrection.Length(), expectedCorrection, 2e-5f);
        bool pureEffect = effect.TargetId == target.StableId && effect.Reached && effect.Held
            && !effect.Released && Finite(effect.PositionCorrection)
            && Finite(effect.VelocityDelta) && pull;
        bool excluded = daddy.Tentacles[idle].Role == DaddyTentacleRole.ExternalReach
            && daddy.Tentacles[idle].SupportContribution == 0f
            && daddy.LocomotionTentCount() == locomotionBefore;
        daddy.ClearExternalTarget(idle);
        var nextTarget = new DaddyLongLegsTargetSnapshot(
            0xBEEFUL, targetPosition, Vector3.Zero, 0.1f, 1f, false);
        bool pendingReleaseNotAdvertised = !daddy.Tentacles[idle].CanAcceptExternalTarget
            && daddy.FindIdleTentacle() != idle;
        bool sameTickReassignRejected = !daddy.TryAssignExternalTarget(idle, nextTarget);
        Tick(daddy, empty, ref tick, Vector3.Zero);
        DaddyLongLegsTargetEffect released = daddy.TargetEffects[idle];
        bool release = released.TargetId == target.StableId && released.Released
            && !released.Held && daddy.Tentacles[idle].Role == DaddyTentacleRole.Idle;
        bool nextTickReassignAccepted = daddy.TryAssignExternalTarget(idle, nextTarget);
        daddy.StunTentacle(idle, 4);
        bool stunClearsTarget = daddy.Tentacles[idle].Role == DaddyTentacleRole.Stunned
            && daddy.Tentacles[idle].ExternalTarget is null
            && daddy.Tentacles[idle].SupportContribution == 0f;
        bool stunnedRejectsAssignment = !daddy.TryAssignExternalTarget(
            idle,
            new DaddyLongLegsTargetSnapshot(
                0xD00DUL, targetPosition, Vector3.Zero, 0.1f, 1f, false));
        Tick(daddy, empty, ref tick, Vector3.Zero);
        DaddyLongLegsTargetEffect stunRelease = daddy.TargetEffects[idle];
        bool stunReleased = stunRelease.TargetId == nextTarget.StableId
            && stunRelease.Released && !stunRelease.Held
            && daddy.Tentacles[idle].Role == DaddyTentacleRole.Stunned;
        bool validation = Throws<ArgumentOutOfRangeException>(() =>
            new DaddyLongLegsTargetSnapshot(0UL, Vector3.Zero, Vector3.Zero, 0f, 1f, false))
            && Throws<ArgumentOutOfRangeException>(() => daddy.StunTentacle(-1, 2));
        return (clearIdempotent && assigned && externalTerrainMemoryCleared
                && differentIdRejected && pureEffect && excluded && release
                && sameTickReassignRejected && nextTickReassignAccepted
                && pendingReleaseNotAdvertised && stunClearsTarget
                && stunnedRejectsAssignment && stunReleased && validation,
            $"idle={idle} clearIdempotent={clearIdempotent} assigned={assigned}/" +
            $"detached={externalTerrainMemoryCleared} " +
            $"effect={effect.TargetId:X}/{effect.Reached}/{effect.Held} " +
            $"pull={effect.PositionCorrection.Length():F4}/{effect.VelocityDelta.Length():F4} " +
            $"expected={expectedCorrection:F4}/{expectedVelocity:F4} direction={pull} " +
            $"differentIdRejected={differentIdRejected} excluded={excluded} " +
            $"released={release} pendingHidden={pendingReleaseNotAdvertised} " +
            $"reassign={sameTickReassignRejected}/{nextTickReassignAccepted} " +
            $"stunRelease={stunClearsTarget}/{stunnedRejectsAssignment}/{stunReleased} " +
            $"validation={validation}");
    }

    private static (bool, string) CheckMoveTargetContract()
    {
        DaddyLongLegsLocomotionController daddy = NewWalker(new Vector3(0f, 0.9f, 0f), 61UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        Vector3 target = daddy.BodyCenter + Vector3.Right * 1.5f;
        daddy.MoveDir = Vector3.Left; // MoveTarget 必须覆盖相反的普通方向输入。
        daddy.MoveTarget = target;
        daddy.RunSpeed = 1f;
        bool hadIntent = daddy.HasMoveIntent;
        bool externalKind = true;
        int reachedTick = -1;
        long tick = 0;
        for (int i = 0; i < 40; i++)
        {
            Tick(daddy, floor, ref tick);
            externalKind &= daddy.AtMoveTarget
                ? daddy.LastMoveTargetKind == MoveTargetKind.None
                    && NearVector(daddy.LastMoveTarget, daddy.BodyCenter, 1e-6f)
                : daddy.LastMoveTargetKind == MoveTargetKind.External
                    && NearVector(daddy.LastMoveTarget, target, 1e-6f);
        }
        // 宿主路径器随后直喂一个邻近可达点；到达半径是本控制器唯一的完成语义。
        target = daddy.BodyCenter + Vector3.Right * (daddy.MoveTargetArriveRadius * 0.5f);
        daddy.MoveTarget = target;
        Tick(daddy, floor, ref tick);
        externalKind &= daddy.LastMoveTargetKind == MoveTargetKind.None
            && NearVector(daddy.LastMoveTarget, daddy.BodyCenter, 1e-6f);
        if (daddy.AtMoveTarget)
            reachedTick = 40;
        bool arrival = reachedTick >= 0 && daddy.AtMoveTarget && !daddy.HasMoveIntent
            && daddy.BodyCenter.DistanceTo(target) <= daddy.MoveTargetArriveRadius + 0.03f;

        daddy.MoveTarget = null;
        daddy.MoveDir = Vector3.Back;
        bool directIntent = daddy.HasMoveIntent;
        Tick(daddy, floor, ref tick);
        bool directRestored = !daddy.AtMoveTarget
            && daddy.LastMoveTargetKind == MoveTargetKind.Fallback
            && daddy.HasMoveIntent
            && (daddy.LastMoveTarget - daddy.BodyCenter).Normalized().Dot(Vector3.Back) > 0.99f;
        daddy.MoveDir = Vector3.Zero;
        Tick(daddy, floor, ref tick);
        bool stopped = !daddy.HasMoveIntent && !daddy.AtMoveTarget
            && daddy.LastMoveTargetKind == MoveTargetKind.None
            && daddy.LastMoveTarget.DistanceTo(daddy.BodyCenter) < 0.05f;

        daddy.MoveTarget = daddy.BodyCenter + Vector3.Right * 2f;
        daddy.RunSpeed = 0f;
        Tick(daddy, floor, ref tick);
        bool zeroThrottle = !daddy.HasMoveIntent && !daddy.AtMoveTarget
            && daddy.LastMoveTargetKind == MoveTargetKind.None
            && NearVector(daddy.LastMoveTarget, daddy.BodyCenter, 1e-6f);

        daddy.RunSpeed = 1f;
        daddy.MoveTarget = daddy.BodyCenter + Vector3.Right;
        daddy.Teleport(new Vector3(2f, 3f, -1f));
        bool teleportClears = daddy.MoveTarget is null && !daddy.AtMoveTarget
            && daddy.LastMoveTargetKind == MoveTargetKind.None;
        return (hadIntent && externalKind && arrival && directIntent && directRestored
                && stopped && zeroThrottle && teleportClears,
            $"intent={hadIntent} reachedTick={reachedTick} arrived={arrival} " +
            $"externalKind={externalKind} direct={directIntent}/{directRestored} " +
            $"stopped={stopped} zeroThrottle={zeroThrottle} teleportClears={teleportClears}");
    }

    private static (bool, string) CheckSurfaceCourse()
    {
        SurfaceResult floor = RunSurface(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL),
            new Vector3(0f, 0.9f, 0f), Vector3.Right, Vector3.Up, GravityPerTick, 620, 101UL);
        SurfaceResult wall = RunSurface(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Right, 2UL),
            new Vector3(0.9f, 1.5f, 0f), Vector3.Up, Vector3.Right, Vector3.Zero, 620, 102UL);
        SurfaceResult ceiling = RunSurface(
            new HalfSpaceTerrain(new Vector3(0f, 4f, 0f), Vector3.Down, 3UL),
            new Vector3(0f, 3.1f, 0f), Vector3.Right, Vector3.Down,
            GravityPerTick * 0.35f, 760, 103UL);

        var inner = new UnionTerrain(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 11UL),
            new HalfSpaceTerrain(new Vector3(4f, 0f, 0f), Vector3.Left, 12UL));
        DaddyLongLegsLocomotionController innerDaddy = NewWalker(new Vector3(1.2f, 0.9f, 0f), 104UL);
        long tick = 0;
        bool innerFloor = false;
        bool innerWall = false;
        float innerStartY = innerDaddy.BodyCenter.Y;
        float innerMaximumY = innerStartY;
        for (int i = 0; i < 1050; i++)
        {
            innerDaddy.MoveDir = i < 520 ? Vector3.Right : Vector3.Up;
            Tick(innerDaddy, inner, ref tick, i < 300 ? Vector3.Zero : GravityPerTick * 0.25f);
            ObserveNormal(innerDaddy, Vector3.Up, ref innerFloor);
            ObserveNormal(innerDaddy, Vector3.Left, ref innerWall);
            innerMaximumY = Math.Max(innerMaximumY, innerDaddy.BodyCenter.Y);
        }
        bool innerPass = innerFloor && innerWall && innerMaximumY > innerStartY + 0.15f
            && IsFinite(innerDaddy);

        var block = new AabbTerrain(
            new Vector3(0f, -8f, -5f), new Vector3(2.2f, 2.2f, 5f), 21UL);
        DaddyLongLegsLocomotionController outerDaddy = NewWalker(new Vector3(-0.9f, 0.8f, 0f), 105UL);
        tick = 0;
        bool outerSide = false;
        bool outerTop = false;
        for (int i = 0; i < 1250; i++)
        {
            outerDaddy.MoveDir = i < 680 ? Vector3.Up : Vector3.Right;
            Tick(outerDaddy, block, ref tick, Vector3.Zero);
            ObserveNormal(outerDaddy, Vector3.Left, ref outerSide);
            ObserveNormal(outerDaddy, Vector3.Up, ref outerTop);
        }
        bool outerPass = outerSide && outerTop && outerDaddy.BodyCenter.Y > 1.5f
            && outerDaddy.BodyCenter.X > -0.7f && IsFinite(outerDaddy);

        bool simple = floor.Pass && wall.Pass && ceiling.Pass;
        return (simple && innerPass && outerPass,
            $"floor={floor}; wall={wall}; ceiling={ceiling}; " +
            $"inner={innerFloor}/{innerWall}/yMax{innerMaximumY:F2}/end{innerDaddy.BodyCenter.Y:F2}; " +
            $"outer={outerSide}/{outerTop}/pos{Format(outerDaddy.BodyCenter)}");
    }

    private static (bool, string) CheckStuckRecovery(Ablation ablation)
    {
        bool enabled = ablation != Ablation.StuckRecovery;
        ulong[] seeds = enabled ? [1UL, 3UL, 4UL, 7UL, 93UL] : [1UL];
        bool all = true;
        var summaries = new List<string>();
        foreach (ulong seed in seeds)
        {
            StuckEscapeResult result = RunStuckEscape(enabled, seed);
            bool ok = result.MaxStuck >= 0.75f
                && result.ForcedSteps > 0
                && result.CrossedObstacle
                && result.MinimumTargetDistance < 0.60f
                && result.RecoveredAfterCrossing
                && result.NoTorque
                && result.Finite
                && !result.QueryBudgetExceeded;
            all &= ok;
            summaries.Add($"{seed}:cross{result.CrossedObstacle}/recover" +
                $"{result.RecoveredAfterCrossing}/d{result.MinimumTargetDistance:F2}/" +
                $"step{result.ForcedSteps}/lat{result.MaximumLateral:F2}/torque{result.NoTorque}");
        }
        return (all, $"enabled={enabled} seeds=[{string.Join(',', summaries)}]");
    }

    private static (bool, string) CheckStuckJitter(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableStuckBodyJitter = ablation != Ablation.StuckJitter;
        DaddyLongLegsLocomotionController first =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 8f, 0f), p, 501UL);
        DaddyLongLegsLocomotionController second =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 8f, 0f), p, 501UL);
        foreach (DaddyLongLegsLocomotionController daddy in new[] { first, second })
        {
            daddy.MoveDir = Vector3.Right;
            daddy.RunSpeed = 1f;
            InvokePrivate(daddy, "ResolveMoveIntent");
            SetPrivateProperty(daddy, "StuckDetourDirection", Vector3.Back);
            SetPrivateProperty(daddy, "StuckDetourActive", true);
            InvokePrivate(daddy, "ApplyDeterministicStuckJitter",
                17L, 1f, Vector3.Right);
        }
        Vector3 firstDelta = AverageVelocityVector(first.Body);
        Vector3 secondDelta = AverageVelocityVector(second.Body);
        bool nonZero = firstDelta.Length() > 1e-5f;
        bool reproducible = NearVector(firstDelta, secondDelta, 1e-7f);
        bool lateral = firstDelta.LengthSquared() > 1e-10f
            && firstDelta.Normalized().Dot(Vector3.Back) > 0.999f;
        bool commonDelta = VelocitySpread(first.Body) < 2e-6f;
        float speedCap = p.MaxMoveSpeed * p.StuckJitterSpeedCapMultiplier;
        bool capped = firstDelta.Length() <= speedCap + 1e-6f;
        return (nonZero && reproducible && lateral && commonDelta && capped,
            $"enabled={p.EnableStuckBodyJitter} delta={Format(firstDelta)} " +
            $"repro={reproducible} lateral={lateral} common={commonDelta} cap={capped}");
    }

    private static (bool, string) CheckStuckRetryPair()
    {
        DaddyLongLegsParams p = ProbeParams();
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, p, 611UL);
        var context = new TickContext(Vector3.Zero, new EmptyTerrain(), 0L);
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        InvokePrivate(daddy, "ResolveMoveIntent");
        SetPrivateProperty(daddy, "StuckCounter", p.StuckRiseTicks + 1);
        SetPrivateField(daddy, "_stuckDetourArmed", true);
        InvokePrivate(daddy, "UpdateStuckDetour", context, Vector3.Right);
        Vector3 first = daddy.StuckDetourDirection;
        int firstSerial = daddy.StuckEpisodeSerial;

        SetPrivateField(daddy, "_stuckDetourTicks", p.StuckDetourMaximumTicks - 1);
        SetPrivateProperty(daddy, "StuckCounter", p.StuckRiseTicks + 1);
        InvokePrivate(daddy, "UpdateStuckDetour", context, Vector3.Right);
        Vector3 second = daddy.StuckDetourDirection;
        bool exactOpposite = first.LengthSquared() > 0.99f
            && second.LengthSquared() > 0.99f
            && first.Dot(second) < -0.999999f;
        bool retried = firstSerial == 1 && daddy.StuckEpisodeSerial == 2
            && daddy.StuckDetourActive;
        return (retried && exactOpposite,
            $"serial={firstSerial}->{daddy.StuckEpisodeSerial} " +
            $"dot={first.Dot(second):F7} active={daddy.StuckDetourActive}");
    }

    private static StuckEscapeResult RunStuckEscape(bool enabled, ulong seed)
    {
        DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
        p.EnableStuckRecovery = enabled;
        // 缩短解析夹具的触发预算；Godot stuck 路线另以正式预设的 80/40/100 参数验收。
        p.StuckDistance = 0.90f;
        p.StuckRiseTicks = 60;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(-4f, 1.35f, 0f), p, seed);
        Vector3 target = new(4f, 1.70f, 0f);
        var terrain = new UnionTerrain(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 70UL),
            new AabbTerrain(new Vector3(-2f, 0f, -1.5f),
                new Vector3(2f, 3.5f, 1.5f), 71UL));
        long tick = 0;
        float maxStuck = 0f;
        float minimumTargetDistance = float.PositiveInfinity;
        float maximumLateral = 0f;
        bool crossed = false;
        bool recovered = false;
        bool finite = true;
        bool noTorque = true;
        if (enabled)
        {
            SetPrivateProperty(daddy, "StuckDetourDirection", Vector3.Back);
            SetPrivateProperty(daddy, "StuckDetourActive", true);
            InvokePrivate(daddy, "ApplyDeterministicStuckJitter",
                0L, 1f, Vector3.Right);
            noTorque = VelocitySpread(daddy.Body) < 2e-6f;
            SetPrivateProperty(daddy, "StuckDetourActive", false);
            SetPrivateProperty(daddy, "StuckDetourDirection", Vector3.Zero);
            foreach (BodyChunk chunk in daddy.Body.Chunks)
                chunk.Vel = Vector3.Zero;
        }
        for (int i = 0; i < 1800; i++)
        {
            daddy.MoveTarget = target;
            daddy.RunSpeed = 1f;
            Tick(daddy, terrain, ref tick);
            maxStuck = Math.Max(maxStuck, daddy.StuckAmount);
            minimumTargetDistance = Math.Min(
                minimumTargetDistance, daddy.BodyCenter.DistanceTo(target));
            maximumLateral = Math.Max(maximumLateral, Math.Abs(daddy.BodyCenter.Z));
            crossed |= daddy.BodyCenter.X > 2.45f;
            recovered |= crossed && daddy.StuckAmount <= 0.15f && daddy.AtMoveTarget;
            finite &= IsFinite(daddy);
        }
        return new StuckEscapeResult(
            enabled,
            crossed,
            recovered,
            maxStuck,
            minimumTargetDistance,
            daddy.StepReleaseSerial,
            maximumLateral,
            noTorque,
            finite,
            daddy.QueryBudgetExceeded);
    }

    private static (bool, string) CheckMultipleMorphologiesWalk()
    {
        var results = new List<string>();
        bool all = true;
        int distinctBodies = 0;
        int previousBodies = -1;
        foreach (ulong seed in new[] { 2UL, 17UL, 93UL, 421UL })
        {
            DaddyLongLegsLocomotionController daddy = NewWalker(new Vector3(0f, 0.9f, 0f), seed);
            Vector3 start = daddy.BodyCenter;
            var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
            long tick = 0;
            float maxSupport = 0f;
            float supportSum = 0f;
            int supportSamples = 0;
            int supportedTicks = 0;
            Vector3 firstPhaseEnd = start;
            for (int i = 0; i < 720; i++)
            {
                daddy.MoveDir = i < 520 ? Vector3.Right : Vector3.Back;
                Tick(daddy, floor, ref tick);
                maxSupport = Math.Max(maxSupport, daddy.EffectiveSupport);
                if (i >= 80)
                {
                    supportSum += daddy.EffectiveSupport;
                    supportSamples++;
                    if (daddy.EffectiveSupport >= 0.10f)
                        supportedTicks++;
                }
                if (i == 519)
                    firstPhaseEnd = daddy.BodyCenter;
            }
            float travel = daddy.BodyCenter.DistanceTo(start);
            float firstProgress = (firstPhaseEnd - start).Dot(Vector3.Right);
            float secondProgress = (daddy.BodyCenter - firstPhaseEnd).Dot(Vector3.Back);
            float averageSupport = supportSamples > 0 ? supportSum / supportSamples : 0f;
            float supportedRatio = supportSamples > 0
                ? (float)supportedTicks / supportSamples
                : 0f;
            int landings = daddy.Tentacles.Sum(t => t.LandingSerial);
            bool seedPass = travel > 0.18f
                && firstProgress > 0.50f && secondProgress > 0.20f
                && maxSupport > 0.05f && averageSupport > 0.20f && supportedRatio >= 0.60f
                && landings > 0
                && daddy.StepReleaseSerial > 0 && IsFinite(daddy)
                && !daddy.QueryBudgetExceeded;
            all &= seedPass;
            if (daddy.Body.Chunks.Count != previousBodies)
                distinctBodies++;
            previousBodies = daddy.Body.Chunks.Count;
            results.Add($"{seed}:{daddy.Body.Chunks.Count}b/{daddy.Tentacles.Count}t/" +
                $"{travel:F2}m/p{firstProgress:F2}+{secondProgress:F2}/" +
                $"s{averageSupport:F2}@{supportedRatio:F2}/max{maxSupport:F2}/" +
                $"l{landings}/step{daddy.StepReleaseSerial}");
        }
        return (all && distinctBodies >= 2,
            $"runs=[{string.Join(',', results)}] bodyTransitions={distinctBodies}");
    }

    private static (bool, string) CheckSustainedGait(Ablation ablation)
    {
        DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
        p.EnableStuckRecovery = false;
        p.EnableIndependentLocomotionDuty = ablation != Ablation.IndependentDuty;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, 1UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        var x = new float[821];
        x[0] = daddy.BodyCenter.X;
        long tick = 0;
        int firstStepTick = -1;
        int stepsAtTurn = 0;
        int startReplantsAtTurn = 0;
        int maximumStuck = 0;
        float minimumForwardWindow = float.PositiveInfinity;
        float maximumReachExcess = 0f;
        bool finite = true;
        for (int i = 0; i < 820; i++)
        {
            daddy.MoveDir = i < 520 ? Vector3.Right : Vector3.Left;
            daddy.RunSpeed = 1f;
            Tick(daddy, floor, ref tick);
            x[i + 1] = daddy.BodyCenter.X;
            if (i >= 119 && i < 520)
                minimumForwardWindow = Math.Min(
                    minimumForwardWindow, x[i + 1] - x[i + 1 - 120]);
            if (firstStepTick < 0 && daddy.StepReleaseSerial > 0)
                firstStepTick = i;
            if (i == 519)
            {
                stepsAtTurn = daddy.StepReleaseSerial;
                startReplantsAtTurn = daddy.StartReplantSerial;
            }
            maximumStuck = Math.Max(maximumStuck, daddy.StuckCounter);
            foreach (DaddyTentacle tentacle in daddy.Tentacles)
            {
                if (!tentacle.HasLandingTarget)
                    continue;
                maximumReachExcess = Math.Max(maximumReachExcess,
                    tentacle.Anchor.Pos.DistanceTo(tentacle.LandingPoint)
                        - tentacle.Length * 1.22f);
            }
            finite &= IsFinite(daddy);
        }
        float forward = x[520] - x[0];
        float reverse100 = x[520] - x[620];
        float reverse300 = x[520] - x[820];
        int reverseSteps = daddy.StepReleaseSerial - stepsAtTurn;
        int reverseStartReplants = daddy.StartReplantSerial - startReplantsAtTurn;
        bool ok = forward >= 3f && minimumForwardWindow >= 0.45f
            && firstStepTick is >= 0 and < 100
            && stepsAtTurn >= 4
            && reverse100 >= 0.30f && reverse300 >= 1f
            && reverseSteps >= 1 && reverseStartReplants >= 1
            && maximumStuck < p.StuckRiseTicks
            && maximumReachExcess <= 1e-4f
            && finite && !daddy.QueryBudgetExceeded;
        return (ok,
            $"enabled={p.EnableIndependentLocomotionDuty} forward={forward:F2} " +
            $"window120={minimumForwardWindow:F2} firstStep={firstStepTick} " +
            $"reverse100/300={reverse100:F2}/{reverse300:F2} " +
            $"steps={stepsAtTurn}+{reverseSteps}/start{reverseStartReplants} " +
            $"stuck={maximumStuck} " +
            $"reachExcess={maximumReachExcess:F5} finite={finite}");
    }

    private static (bool, string) CheckStartReplant(Ablation ablation)
    {
        DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
        p.EnableStuckRecovery = false;
        p.EnableMoveStartAssist = false;
        p.EnableStartReplant = ablation != Ablation.StartReplant;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, 1UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        long tick = 0;
        daddy.MoveDir = Vector3.Zero;
        daddy.RunSpeed = 0f;
        for (int i = 0; i < 120; i++)
            Tick(daddy, floor, ref tick);

        int[] stepsBefore = daddy.Tentacles.Select(t => t.StepSerial).ToArray();
        int startingStepSerial = daddy.StepReleaseSerial;
        int startingReplantSerial = daddy.StartReplantSerial;
        int triggerTick = -1;
        int releasedAtTrigger = 0;
        int maximumStepsBeforePlant = 0;
        int selected = -1;
        float releaseDot = 1f;
        float cancellation = float.NegativeInfinity;
        bool completed = false;
        float landingDot = -1f;
        bool finite = true;
        for (int i = 1; i <= 160; i++)
        {
            daddy.MoveDir = Vector3.Right;
            daddy.RunSpeed = 1f;
            Tick(daddy, floor, ref tick);
            finite &= IsFinite(daddy);
            if (triggerTick < 0 && daddy.StartReplantSerial > startingReplantSerial)
            {
                triggerTick = i;
                selected = daddy.LastStartReplantTentacleIndex;
                releaseDot = daddy.LastStartReplantReleaseDot;
                cancellation = daddy.LastStartReplantPredictedGravityCancellation;
                releasedAtTrigger = daddy.Tentacles.Select((t, index) =>
                    t.StepSerial > stepsBefore[index] ? 1 : 0).Sum();
            }
            if (triggerTick >= 0 && !completed)
            {
                maximumStepsBeforePlant = Math.Max(maximumStepsBeforePlant,
                    daddy.StepReleaseSerial - startingStepSerial);
                if (selected >= 0)
                {
                    DaddyTentacle tentacle = daddy.Tentacles[selected];
                    if (!tentacle.StartReplantActive
                        && tentacle.ReplantPhase == DaddyTentacleReplantPhase.Planted
                        && tentacle.AtGrabDestination)
                    {
                        Vector3 side = tentacle.LandingPoint - daddy.BodyCenter;
                        landingDot = side.LengthSquared() > 1e-10f
                            ? side.Normalized().Dot(Vector3.Right)
                            : -1f;
                        completed = true;
                    }
                }
            }
            if (completed)
                break;
        }

        EpisodeContinuityResult episode = RunEpisodeContinuity(p);
        bool ok = !p.EnableMoveStartAssist
            && triggerTick is >= 1 and <= 4
            && daddy.StartReplantSerial - startingReplantSerial == 1
            && releasedAtTrigger == 1 && maximumStepsBeforePlant == 1
            && selected >= 0
            && releaseDot <= p.StartReplantReleaseDotMaximum + 1e-5f
            && cancellation + 1e-5f >= p.StartReplantMinimumGravityCancellation
            && completed && landingDot >= 0.10f
            && episode.ShortGapsPreserved && episode.LongGapRestarted
            && episode.Finite && !episode.QueryBudgetExceeded
            && finite && !daddy.QueryBudgetExceeded;
        return (ok,
            $"enabled={p.EnableStartReplant} assist={p.EnableMoveStartAssist} " +
            $"trigger={triggerTick} release={releasedAtTrigger}/max{maximumStepsBeforePlant} " +
            $"index={selected} dot={releaseDot:F3}->{landingDot:F3} " +
            $"cancel={cancellation:F3}>={p.StartReplantMinimumGravityCancellation:F3} " +
            $"planted={completed} episode={episode.SerialBeforeLongGap}->" +
            $"{episode.SerialAfterLongGap} short={episode.ShortGapsPreserved} " +
            $"finite={finite && episode.Finite}");
    }

    private static EpisodeContinuityResult RunEpisodeContinuity(DaddyLongLegsParams parameters)
    {
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), parameters, 17UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        long tick = 0;
        daddy.MoveDir = Vector3.Zero;
        daddy.RunSpeed = 0f;
        for (int i = 0; i < 120; i++)
            Tick(daddy, floor, ref tick);

        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        Tick(daddy, floor, ref tick);
        int serial = daddy.MovementEpisodeSerial;
        bool shortGapsPreserved = serial == 1;
        foreach (int gap in new[] { 1, 4, 10, 20, parameters.MoveEpisodeGraceTicks })
        {
            daddy.MoveDir = Vector3.Zero;
            daddy.RunSpeed = 0f;
            for (int i = 0; i < gap; i++)
                Tick(daddy, floor, ref tick);
            daddy.MoveDir = Vector3.Right;
            daddy.RunSpeed = 1f;
            Tick(daddy, floor, ref tick);
            shortGapsPreserved &= daddy.MovementEpisodeSerial == serial;
        }
        int serialBeforeLongGap = daddy.MovementEpisodeSerial;
        daddy.MoveDir = Vector3.Zero;
        daddy.RunSpeed = 0f;
        for (int i = 0; i <= parameters.MoveEpisodeGraceTicks; i++)
            Tick(daddy, floor, ref tick);
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        Tick(daddy, floor, ref tick);
        int serialAfterLongGap = daddy.MovementEpisodeSerial;
        return new EpisodeContinuityResult(
            shortGapsPreserved,
            serialAfterLongGap == serialBeforeLongGap + 1,
            serialBeforeLongGap,
            serialAfterLongGap,
            IsFinite(daddy),
            daddy.QueryBudgetExceeded);
    }

    private static (bool, string) CheckTapVsHold()
    {
        TapGaitResult hold = RunTapGait("hold", _ => true);
        TapGaitResult[] taps =
        {
            RunTapGait("1/1", i => (i & 1) == 0),
            RunTapGait("4/4", i => i % 8 < 4),
            RunTapGait("10/10", i => i % 20 < 10),
            RunTapGait("20/20", i => i % 40 < 20),
        };
        bool holdOk = hold.OnTicks == 1600 && hold.Forward >= 20f
            && hold.MovementEpisodes == 1 && hold.BodyTouchTicks == 0
            && hold.MinimumBodyClearance > 0.10f
            && hold.Finite && !hold.QueryBudgetExceeded && !hold.AssistEnabled;
        bool tapsOk = taps.All(result => result.OnTicks == 800
            && result.Forward >= hold.Forward * 0.12f
            && result.Forward <= hold.Forward * 0.75f
            && result.MovementEpisodes == 1
            && result.BodyTouchTicks == 0
            && result.MinimumBodyClearance > 0.10f
            && result.Finite && !result.QueryBudgetExceeded && !result.AssistEnabled);
        return (holdOk && tapsOk,
            $"hold={hold} taps=[{string.Join(',', taps.Select(result => result.ToString()))}]");
    }

    private static TapGaitResult RunTapGait(string name, Func<int, bool> hasInput)
    {
        const int RunTicks = 1600;
        DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
        p.EnableMoveStartAssist = false;
        p.EnableStuckRecovery = false;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, 1UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        long tick = 0;
        daddy.MoveDir = Vector3.Zero;
        daddy.RunSpeed = 0f;
        for (int i = 0; i < 120; i++)
            Tick(daddy, floor, ref tick);
        float startX = daddy.BodyCenter.X;
        int startingEpisode = daddy.MovementEpisodeSerial;
        int onTicks = 0;
        int bodyTouchTicks = 0;
        float minimumClearance = float.PositiveInfinity;
        bool finite = true;
        for (int i = 0; i < RunTicks; i++)
        {
            bool on = hasInput(i);
            if (on)
                onTicks++;
            daddy.MoveDir = on ? Vector3.Right : Vector3.Zero;
            daddy.RunSpeed = on ? 1f : 0f;
            Tick(daddy, floor, ref tick);
            finite &= IsFinite(daddy);
            float clearance = daddy.Body.Chunks.Min(chunk => chunk.Pos.Y - chunk.Radius);
            minimumClearance = Math.Min(minimumClearance, clearance);
            if (clearance <= p.TerrainSkin + 0.01f)
                bodyTouchTicks++;
        }
        return new TapGaitResult(
            name,
            onTicks,
            daddy.BodyCenter.X - startX,
            daddy.MovementEpisodeSerial - startingEpisode,
            bodyTouchTicks,
            minimumClearance,
            finite,
            daddy.QueryBudgetExceeded,
            p.EnableMoveStartAssist);
    }

    private static (bool, string) CheckIdleWallStability(Ablation ablation)
    {
        const int DrivenTicks = 260;
        // 24 tick episode grace + 在途 peeling/reaching 收尾；稳定窗必须晚于两者。
        const int SettleTicks = 160;
        const int ObserveTicks = 480;
        ulong[] seeds = [1UL, 2UL, 3UL];
        bool all = true;
        var summaries = new List<string>();
        foreach (ulong seed in seeds)
        {
            DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
            p.EnableIdleLandingStability = ablation != Ablation.IdleLandingStability;
            p.EnableIdleSupportNeutrality = ablation != Ablation.IdleSupportNeutrality;
            DaddyLongLegsLocomotionController daddy =
                DaddyLongLegsFactory.CreateController(new Vector3(-5f, 1.2f, 0f), p, seed);
            var floorAndWall = new UnionTerrain(
                new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL),
                new HalfSpaceTerrain(Vector3.Zero, Vector3.Left, 2UL));
            long tick = 0;
            bool finite = true;
            bool sawWallGrip = false;
            int maximumQueries = 0;
            for (int i = 0; i < DrivenTicks; i++)
            {
                daddy.MoveDir = Vector3.Right;
                daddy.RunSpeed = 1f;
                Tick(daddy, floorAndWall, ref tick);
                finite &= IsFinite(daddy);
                maximumQueries = Math.Max(maximumQueries, daddy.TickQueryCount);
                sawWallGrip |= HasWallGrip(daddy);
            }

            daddy.MoveDir = Vector3.Zero;
            daddy.RunSpeed = 0f;
            // 后续 wall=true 必须来自无输入阶段，不能借用接近墙时的一次瞬态接触。
            sawWallGrip = false;
            int stepAtRelease = daddy.StepReleaseSerial;
            int[] tentacleStepsAtRelease = daddy.Tentacles
                .Select(t => t.StepSerial).ToArray();
            int startReplantsAtRelease = daddy.StartReplantSerial;
            for (int i = 0; i < SettleTicks; i++)
            {
                Tick(daddy, floorAndWall, ref tick);
                finite &= IsFinite(daddy);
                maximumQueries = Math.Max(maximumQueries, daddy.TickQueryCount);
                sawWallGrip |= HasWallGrip(daddy);
            }

            int[] landingsAtStableStart = daddy.Tentacles
                .Select(t => t.LandingSerial).ToArray();
            float minimumY = daddy.BodyCenter.Y;
            float maximumY = daddy.BodyCenter.Y;
            var ySamples = new float[ObserveTicks + 1];
            ySamples[0] = daddy.BodyCenter.Y;
            float minimumSupport = daddy.EffectiveSupport;
            float maximumSupport = daddy.EffectiveSupport;
            int landingChangeTicks = 0;
            int lateLandingChangeTicks = 0;
            int previousLandingTotal = landingsAtStableStart.Sum();
            bool staleEpisode = daddy.MoveEpisodeActive
                || daddy.MoveEpisodeGraceTicksRemaining != 0
                || daddy.ActiveStartReplantTentacleIndex >= 0;
            for (int i = 0; i < ObserveTicks; i++)
            {
                Tick(daddy, floorAndWall, ref tick);
                finite &= IsFinite(daddy);
                maximumQueries = Math.Max(maximumQueries, daddy.TickQueryCount);
                minimumY = Math.Min(minimumY, daddy.BodyCenter.Y);
                maximumY = Math.Max(maximumY, daddy.BodyCenter.Y);
                ySamples[i + 1] = daddy.BodyCenter.Y;
                minimumSupport = Math.Min(minimumSupport, daddy.EffectiveSupport);
                maximumSupport = Math.Max(maximumSupport, daddy.EffectiveSupport);
                sawWallGrip |= HasWallGrip(daddy);
                staleEpisode |= daddy.MoveEpisodeActive
                    || daddy.MoveEpisodeGraceTicksRemaining != 0
                    || daddy.ActiveStartReplantTentacleIndex >= 0;
                int landingTotal = daddy.Tentacles.Sum(t => t.LandingSerial);
                if (landingTotal != previousLandingTotal)
                {
                    landingChangeTicks++;
                    if (i >= ObserveTicks / 2)
                        lateLandingChangeTicks++;
                }
                previousLandingTotal = landingTotal;
            }

            int[] landingDeltas = daddy.Tentacles.Select((t, index) =>
                t.LandingSerial - landingsAtStableStart[index]).ToArray();
            int[] stepDeltas = daddy.Tentacles.Select((t, index) =>
                t.StepSerial - tentacleStepsAtRelease[index]).ToArray();
            int totalLandingChanges = landingDeltas.Sum();
            float heightAmplitude = maximumY - minimumY;
            float residualMinimum = float.PositiveInfinity;
            float residualMaximum = float.NegativeInfinity;
            for (int i = 0; i < ySamples.Length; i++)
            {
                float trend = Mathf.Lerp(
                    ySamples[0], ySamples[^1], (float)i / ObserveTicks);
                float residual = ySamples[i] - trend;
                residualMinimum = Math.Min(residualMinimum, residual);
                residualMaximum = Math.Max(residualMaximum, residual);
            }
            float heightResidualAmplitude = residualMaximum - residualMinimum;
            float supportAmplitude = maximumSupport - minimumSupport;
            // 静止时制造一次“接触状态单 tick 未到达、落点本身仍有效”的无害抖动。
            // 连续 3D 中旧实现会因此立刻重新跑落点搜索；提交后的 incumbent 必须吸收它。
            DaddyTentacle? incumbentProbe = daddy.Tentacles.FirstOrDefault(t =>
                t.HasLandingTarget
                && t.ReplantPhase != DaddyTentacleReplantPhase.Peeling);
            bool incumbentProbeAvailable = incumbentProbe is not null;
            int incumbentSearchDelta = 0;
            if (incumbentProbe is not null)
            {
                int searchBefore = incumbentProbe.SearchSerial;
                SetPrivateProperty(incumbentProbe, "AtGrabDestination", false);
                SetPrivateField(incumbentProbe, "_forceLandingSearch", true);
                Tick(daddy, floorAndWall, ref tick);
                incumbentSearchDelta = incumbentProbe.SearchSerial - searchBefore;
                finite &= IsFinite(daddy);
                maximumQueries = Math.Max(maximumQueries, daddy.TickQueryCount);
            }
            bool seedOk = sawWallGrip
                && daddy.StepReleaseSerial == stepAtRelease
                && stepDeltas.All(delta => delta == 0)
                && daddy.StartReplantSerial == startReplantsAtRelease
                && !staleEpisode
                // 有效、已植稳的墙面落点在收敛窗后必须逐位冻结；真正失效仍由
                // ValidateLanding 清除后走 HasLanding=false 的强制搜索路径。
                && totalLandingChanges == 0
                && landingChangeTicks == 0 && lateLandingChangeTicks == 0
                && incumbentProbeAvailable && incumbentSearchDelta == 0
                // DLL 中移动时允许 1.2× 回补，但 direct-control 无输入时回到至多 1×；
                // 静止墙面因此既不能持续净上升，也不能围绕趋势线明显 bob。
                && heightResidualAmplitude <= 0.75f
                && Math.Abs(ySamples[^1] - ySamples[0]) <= 0.75f
                && supportAmplitude <= 0.20f
                && minimumSupport >= 0.50f
                && finite && !daddy.QueryBudgetExceeded
                && maximumQueries <= p.MaximumTerrainQueriesPerTick;
            all &= seedOk;
            summaries.Add($"{seed}:wall{sawWallGrip}/steps" +
                $"{daddy.StepReleaseSerial - stepAtRelease}/" +
                $"[{string.Join(',', stepDeltas)}]/start" +
                $"{daddy.StartReplantSerial - startReplantsAtRelease}/" +
                $"episode{staleEpisode}/land{totalLandingChanges}/" +
                $"[{string.Join(',', landingDeltas)}]/ticks" +
                $"{landingChangeTicks}+late{lateLandingChangeTicks}/" +
                $"incumbent{incumbentProbeAvailable}/{incumbentSearchDelta}/" +
                $"y{heightAmplitude:F3}/residual{heightResidualAmplitude:F3}/" +
                $"drift{ySamples[^1] - ySamples[0]:F3}/support{minimumSupport:F3}.." +
                $"{maximumSupport:F3}/q{maximumQueries}/finite{finite}");
        }
        return (all,
            $"landing={ablation != Ablation.IdleLandingStability}/" +
            $"support={ablation != Ablation.IdleSupportNeutrality} " +
            string.Join(' ', summaries));
    }

    private static bool HasWallGrip(DaddyLongLegsLocomotionController daddy) =>
        daddy.Tentacles.Any(t => t.Segments.Any(s =>
            s.ActiveGrip && s.GripNormal.Dot(Vector3.Left) > 0.75f));

    private static (bool, string) CheckMovingStance(Ablation ablation)
    {
        DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
        p.EnableStuckRecovery = false;
        p.EnableStepSupportReserve = ablation != Ablation.StepSupportReserve;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, 1UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        float startX = daddy.BodyCenter.X;
        float startY = daddy.BodyCenter.Y;
        float medianLength = daddy.Tentacles.Select(t => t.Length).Order()
            .ElementAt(daddy.Tentacles.Count / 2);
        var tailHeights = new float[200];
        var contactRuns = new int[daddy.Tentacles.Count];
        int maximumHighContactRun = 0;
        float maximumContactRatio = 0f;
        float maximumGripRatio = 0f;
        float guideRatioAtMaximumContact = 0f;
        int segmentsAtMaximumContact = 0;
        DaddyTentacleReplantPhase phaseAtMaximumContact =
            DaddyTentacleReplantPhase.Planted;
        float maximumGain = float.NegativeInfinity;
        bool finite = true;
        long tick = 0;
        for (int i = 0; i < 800; i++)
        {
            daddy.MoveDir = Vector3.Right;
            daddy.RunSpeed = 1f;
            Tick(daddy, floor, ref tick);
            float height = daddy.BodyCenter.Y - startY;
            maximumGain = Math.Max(maximumGain, height);
            if (i >= 600)
                tailHeights[i - 600] = height;
            finite &= IsFinite(daddy);
            if (i <= 60)
                continue;
            foreach (DaddyTentacle tentacle in daddy.Tentacles)
            {
                float contactRatio = (float)tentacle.Segments.Count(s => s.TerrainContact)
                    / tentacle.Segments.Count;
                float gripRatio = (float)tentacle.ActiveGripCount / tentacle.Segments.Count;
                if (contactRatio > maximumContactRatio)
                {
                    maximumContactRatio = contactRatio;
                    guideRatioAtMaximumContact = tentacle.GuideLength > 1e-5f
                        ? tentacle.GuideContactLength / tentacle.GuideLength
                        : 0f;
                    segmentsAtMaximumContact = tentacle.Segments.Count;
                    phaseAtMaximumContact = tentacle.ReplantPhase;
                }
                maximumGripRatio = Math.Max(maximumGripRatio, gripRatio);
                float plannedRatio = tentacle.GuideLength > 1e-5f
                    ? tentacle.GuideContactLength / tentacle.GuideLength
                    : 0f;
                int allowedContacts = (int)MathF.Ceiling(
                    plannedRatio * tentacle.Segments.Count) + 1;
                if (tentacle.Segments.Count(s => s.TerrainContact) > allowedContacts)
                {
                    int run = ++contactRuns[tentacle.Index];
                    maximumHighContactRun = Math.Max(maximumHighContactRun, run);
                }
                else
                {
                    contactRuns[tentacle.Index] = 0;
                }
            }
        }
        Array.Sort(tailHeights);
        float p10Height = tailHeights[tailHeights.Length / 10];
        float progress = daddy.BodyCenter.X - startX;
        float minimumReleaseCancellation =
            daddy.MinimumStepReleasePredictedGravityCancellation;
        bool releaseMargin = daddy.StepReleaseSerial > 0
            && float.IsFinite(minimumReleaseCancellation)
            && minimumReleaseCancellation
                >= p.StepReleaseMinimumGravityCancellation - 1e-5f;
        bool ok = finite && progress >= 5f && daddy.StepReleaseSerial >= 4
            && maximumGain >= medianLength * 0.20f
            && p10Height >= medianLength * 0.15f
            && maximumHighContactRun <= p.StepPeelMaximumTicks + 4
            && releaseMargin && !daddy.QueryBudgetExceeded;
        return (ok,
            $"enabled={p.EnableStepSupportReserve} progress={progress:F2} " +
            $"steps={daddy.StepReleaseSerial} height={maximumGain:F2}/p10{p10Height:F2} " +
            $"L50={medianLength:F2} contact/grip={maximumContactRatio:F2}/" +
            $"{maximumGripRatio:F2}/excessRun{maximumHighContactRun} " +
            $"at={guideRatioAtMaximumContact:F2}/{segmentsAtMaximumContact}/" +
            $"{phaseAtMaximumContact} " +
            $"releaseCancel={minimumReleaseCancellation:F3}/" +
            $"{p.StepReleaseMinimumGravityCancellation:F3} finite={finite}");
    }

    private static (bool, string) CheckStepSupportReserve(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.MinimumTentacles = p.MaximumTentacles = 5;
        p.MaximumTotalTentacleSegments = 60;
        p.MaximumTerrainQueriesPerTick = 4096;
        p.EnableStartReplant = false;
        p.EnableStuckRecovery = false;
        p.EnableDutyAllocation = false;
        p.EnableStepSupportReserve = ablation != Ablation.StepSupportReserve;
        p.StepReleaseMinimumGravityCancellation = 1.00f;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 23UL);
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            InvokePrivate(tentacle, "SetLocomotion");
            PrepareSyntheticGrip(tentacle, daddy.BodyCenter,
                tentacle.Segments.Count, true);
            InvokePrivate(tentacle, "UpdateSupport");
            SetPrivateProperty(tentacle, "SupportContribution", 0.50f);
            SetPrivateProperty(tentacle, "ReleaseScore", tentacle.Index + 1f);
        }
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        SetPrivateField(daddy, "_unconditionalSupport", 0f);
        InvokePrivate(daddy, "ResolveMoveIntent");
        InvokePrivate(daddy, "AggregateSupport", Vector3.Right);
        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);

        float rawAfter = 0.40f;
        float predicted = Mathf.Pow(rawAfter, p.SupportResponseExponent)
            * p.GravityCancellationGain;
        bool blocked = daddy.StepReleaseSerial == 0
            && daddy.Tentacles.All(t => t.StepSerial == 0);
        return (blocked,
            $"enabled={p.EnableStepSupportReserve} blocked={blocked} " +
            $"predicted={predicted:F3}<gate{p.StepReleaseMinimumGravityCancellation:F3} " +
            $"steps={daddy.StepReleaseSerial}");
    }

    private static (bool, string) CheckSerialReplant(Ablation ablation)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.MinimumTentacles = p.MaximumTentacles = 5;
        p.MaximumTotalTentacleSegments = 60;
        p.MaximumTerrainQueriesPerTick = 4096;
        p.EnableStartReplant = false;
        p.EnableStuckRecovery = false;
        p.EnableDutyAllocation = false;
        p.EnableStepSupportReserve = false;
        p.EnableSerialReplant = ablation != Ablation.SerialReplant;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, 29UL);
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            InvokePrivate(tentacle, "SetLocomotion");
            PrepareSyntheticGrip(tentacle, daddy.BodyCenter,
                tentacle.Segments.Count, true);
            InvokePrivate(tentacle, "UpdateSupport");
            SetPrivateProperty(tentacle, "ReplantPhase",
                DaddyTentacleReplantPhase.Planted);
            SetPrivateProperty(tentacle, "ReleaseScore", tentacle.Index + 1f);
        }
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        InvokePrivate(daddy, "ResolveMoveIntent");
        InvokePrivate(daddy, "AggregateSupport", Vector3.Right);
        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);
        int first = daddy.StepReleaseSerial;
        SetPrivateField(daddy, "_stepCooldown", 0);
        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);
        int second = daddy.StepReleaseSerial;
        bool serialized = first == 1 && second == 1
            && daddy.Tentacles.Count(t => t.ReplantPhase
                != DaddyTentacleReplantPhase.Planted) == 1;
        return (serialized,
            $"enabled={p.EnableSerialReplant} serial={first}->{second} " +
            $"active={daddy.ActiveReplantTentacleIndex} serialized={serialized}");
    }

    private static (bool, string) CheckShortStunReplantInterruption()
    {
        var ordinary = RunShortStunReplantProbe(startReplant: false, 31UL);
        var start = RunShortStunReplantProbe(startReplant: true, 32UL);
        bool ok = ordinary.ImmediateClear
            && ordinary.SameUpdateBlocked
            && ordinary.Takeover
            && start.ImmediateClear
            && start.SameUpdateBlocked
            && start.Takeover
            && start.StartRearmed;
        return (ok,
            $"ordinary=clear{ordinary.ImmediateClear}/block{ordinary.SameUpdateBlocked}/" +
            $"take{ordinary.Takeover}({ordinary.Victim}->{ordinary.Next}) " +
            $"start=clear{start.ImmediateClear}/block{start.SameUpdateBlocked}/" +
            $"take{start.Takeover}/rearm{start.StartRearmed}" +
            $"({start.Victim}->{start.Next})");
    }

    private static (
        bool ImmediateClear,
        bool SameUpdateBlocked,
        bool Takeover,
        bool StartRearmed,
        int Victim,
        int Next) RunShortStunReplantProbe(bool startReplant, ulong seed)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.MinimumTentacles = p.MaximumTentacles = 5;
        p.MaximumTotalTentacleSegments = 60;
        p.MaximumTerrainQueriesPerTick = 4096;
        p.EnableStartReplant = startReplant;
        p.EnableStuckRecovery = false;
        p.EnableDutyAllocation = false;
        p.EnableStepSupportReserve = false;
        p.EnableSerialReplant = true;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 5f, 0f), p, seed);
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            InvokePrivate(tentacle, "SetLocomotion");
            PrepareSyntheticGrip(tentacle, daddy.BodyCenter,
                tentacle.Segments.Count, true);
            if (startReplant)
            {
                Vector3 landing = daddy.BodyCenter - Vector3.Right * 2f;
                SetPrivateProperty(tentacle, "LandingPoint", landing);
                tentacle.Segments[^1].Pos = landing + Vector3.Up
                    * (tentacle.Segments[^1].Radius + 0.02f);
            }
            InvokePrivate(tentacle, "UpdateSupport");
            SetPrivateProperty(tentacle, "ReplantPhase",
                DaddyTentacleReplantPhase.Planted);
            SetPrivateProperty(tentacle, "ReleaseScore", tentacle.Index + 1f);
        }
        daddy.MoveDir = Vector3.Right;
        daddy.RunSpeed = 1f;
        InvokePrivate(daddy, "ResolveMoveIntent");
        InvokePrivate(daddy, "AggregateSupport", Vector3.Right);
        if (startReplant)
        {
            SetPrivateField(daddy, "_moveEpisodeActive", true);
            SetPrivateField(daddy, "_moveEpisodeDirection", Vector3.Right);
            SetPrivateField(daddy, "_startReplantPending", true);
        }
        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);

        int victim = daddy.ActiveReplantTentacleIndex;
        int serialBefore = startReplant
            ? daddy.StartReplantSerial
            : daddy.StepReleaseSerial;
        SetPrivateField(daddy, "_stepCooldown", 1);
        daddy.StunTentacle(victim, 1);
        bool immediateClear = daddy.ActiveReplantTentacleIndex < 0
            && daddy.ActiveStartReplantTentacleIndex < 0;
        bool startRearmed = !startReplant || daddy.StartReplantPending;

        // 单独跑真实触手 tick：它会先把 1-tick stun 减为 0，随后控制器的
        // UpdateStepRelease 只能看到 0。不能让中断事件靠当前计数值侥幸存活。
        TickTentacle(daddy, daddy.Tentacles[victim], new EmptyTerrain(), 1L);
        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);
        int serialAfterBlockedUpdate = startReplant
            ? daddy.StartReplantSerial
            : daddy.StepReleaseSerial;
        bool sameUpdateBlocked = serialAfterBlockedUpdate == serialBefore;

        InvokePrivate(daddy, "UpdateStepRelease", Vector3.Right);
        int serialAfterTakeover = startReplant
            ? daddy.StartReplantSerial
            : daddy.StepReleaseSerial;
        int next = daddy.ActiveReplantTentacleIndex;
        bool takeover = serialAfterTakeover == serialBefore + 1
            && next >= 0
            && next != victim;
        return (immediateClear, sameUpdateBlocked, takeover,
            startRearmed, victim, next);
    }

    private static (bool, string) CheckMovingHeightRetention(Ablation ablation)
    {
        const int StandTicks = 900;
        const int StandWindow = 200;
        const int MoveTicks = 900;
        const int MoveWindow = 240;
        ulong[] seeds = [1UL, 33UL, 93UL];
        bool all = true;
        var summaries = new List<string>();
        foreach (ulong seed in seeds)
        {
            DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
            p.EnableSurfaceSpanReplant = ablation != Ablation.SurfaceSpanReplant;
            p.EnableSerialReplant = ablation != Ablation.SerialReplant;
            if (ablation == Ablation.SupportResponse3D)
                p.SupportResponseExponent = 0.30f;
            DaddyLongLegsLocomotionController daddy =
                DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, seed);
            var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
            float spawnY = daddy.BodyCenter.Y;
            float medianLength = daddy.Tentacles.Select(t => t.Length).Order()
                .ElementAt(daddy.Tentacles.Count / 2);
            var standHeights = new float[StandWindow];
            long tick = 0;
            bool finite = true;
            int maximumQueries = 0;
            for (int i = 0; i < StandTicks; i++)
            {
                daddy.MoveDir = Vector3.Zero;
                daddy.RunSpeed = 0f;
                Tick(daddy, floor, ref tick);
                finite &= IsFinite(daddy);
                maximumQueries = Math.Max(maximumQueries, daddy.TickQueryCount);
                if (i >= StandTicks - StandWindow)
                    standHeights[i - (StandTicks - StandWindow)] = daddy.BodyCenter.Y;
            }

            Array.Sort(standHeights);
            float standingP10 = standHeights[StandWindow / 10];
            float standingMedian = standHeights[StandWindow / 2];
            float moveStartX = daddy.BodyCenter.X;
            int stepsBeforeMove = daddy.StepReleaseSerial;
            int landingsBeforeMove = daddy.Tentacles.Sum(t => t.LandingSerial);
            var movingHeights = new float[MoveWindow];
            float movingSupportSum = 0f;
            float minimumMovingSupport = float.PositiveInfinity;
            for (int i = 0; i < MoveTicks; i++)
            {
                daddy.MoveDir = Vector3.Right;
                daddy.RunSpeed = 1f;
                Tick(daddy, floor, ref tick);
                finite &= IsFinite(daddy);
                maximumQueries = Math.Max(maximumQueries, daddy.TickQueryCount);
                if (i >= MoveTicks - MoveWindow)
                {
                    int sample = i - (MoveTicks - MoveWindow);
                    movingHeights[sample] = daddy.BodyCenter.Y;
                    movingSupportSum += daddy.EffectiveSupport;
                    minimumMovingSupport = Math.Min(
                        minimumMovingSupport, daddy.EffectiveSupport);
                }
            }

            Array.Sort(movingHeights);
            float movingP10 = movingHeights[MoveWindow / 10];
            float movingMedian = movingHeights[MoveWindow / 2];
            float averageMovingSupport = movingSupportSum / MoveWindow;
            float loss = standingP10 - movingP10;
            float progress = daddy.BodyCenter.X - moveStartX;
            int stepDelta = daddy.StepReleaseSerial - stepsBeforeMove;
            int landingDelta = daddy.Tentacles.Sum(t => t.LandingSerial)
                - landingsBeforeMove;
            float normalizedRetention = standingP10 > 1e-5f
                ? movingP10 / standingP10
                : 0f;

            // 先把同一只个体静置到稳定高站姿，再测持续水平移动的末窗。
            // 允许步态造成有限下沉，但不能把出生高度当基准而掩盖“站高后跪行”。
            // 同一门也直接承担同面跨度、串行换步和 3D 支撑响应三项机制的消融验证。
            bool seedPass = standingP10 - spawnY >= medianLength * 0.18f
                && movingP10 >= standingP10 * 0.80f
                && standingP10 >= medianLength * 0.70f
                && loss <= Math.Max(0.65f, medianLength * 0.10f)
                && progress >= 5f
                && stepDelta >= 4 && landingDelta >= stepDelta
                && averageMovingSupport >= 0.15f
                && minimumMovingSupport >= 0f
                && finite && !daddy.QueryBudgetExceeded
                && maximumQueries <= p.MaximumTerrainQueriesPerTick;
            all &= seedPass;
            summaries.Add($"{seed}:stand={standingP10:F2}/{standingMedian:F2}" +
                $" move={movingP10:F2}/{movingMedian:F2}" +
                $" retain={normalizedRetention:F2}/loss{loss:F2}" +
                $" progress={progress:F2}/step{stepDelta}/land{landingDelta}" +
                $" support={minimumMovingSupport:F2}/{averageMovingSupport:F2}" +
                $" q={maximumQueries}/{p.MaximumTerrainQueriesPerTick}/finite{finite}");
        }
        return (all, string.Join("; ", summaries));
    }

    private static (bool, string) CheckTallStance(Ablation ablation)
    {
        var results = new List<string>();
        bool all = true;
        foreach (ulong seed in new[] { 1UL, 33UL, 93UL })
        {
            DaddyLongLegsParams p = DaddyLongLegsFactory.Daddy();
            p.EnableSupportOvercompensation = ablation != Ablation.SupportLift;
            DaddyLongLegsLocomotionController daddy =
                DaddyLongLegsFactory.CreateController(new Vector3(0f, 0.9f, 0f), p, seed);
            float startY = daddy.BodyCenter.Y;
            float[] lengths = daddy.Tentacles.Select(t => t.Length).Order().ToArray();
            float medianLength = lengths[lengths.Length / 2];
            float maxLength = lengths[^1];
            float bodyEnvelope = daddy.Body.Chunks.Max(
                c => c.Pos.DistanceTo(daddy.BodyCenter) + c.Radius);
            var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
            var tailHeights = new float[200];
            long tick = 0;
            float maximumGain = float.NegativeInfinity;
            float maximumCancellation = 0f;
            int highRun = 0;
            int maximumHighRun = 0;
            float supportSum = 0f;
            bool finite = true;
            for (int i = 0; i < 800; i++)
            {
                daddy.MoveDir = Vector3.Zero;
                daddy.RunSpeed = 0f;
                Tick(daddy, floor, ref tick);
                float gain = daddy.BodyCenter.Y - startY;
                maximumGain = Math.Max(maximumGain, gain);
                maximumCancellation = Math.Max(
                    maximumCancellation, daddy.GravityCancellation);
                if (i >= 400 && gain >= medianLength * 0.18f)
                    highRun++;
                else
                    highRun = 0;
                maximumHighRun = Math.Max(maximumHighRun, highRun);
                if (i >= 600)
                {
                    tailHeights[i - 600] = gain;
                    supportSum += daddy.EffectiveSupport;
                }
                finite &= IsFinite(daddy);
            }
            Array.Sort(tailHeights);
            float tailP10 = tailHeights[19];
            float averageSupport = supportSum / tailHeights.Length;
            bool seedPass = maximumGain >= medianLength * 0.25f
                && tailP10 >= medianLength * 0.18f
                && maximumHighRun >= 160
                && maximumGain <= maxLength + bodyEnvelope + 1f
                && averageSupport > 0.15f
                && maximumCancellation > 1.01f
                && daddy.Body.GravityScale == 1f
                && finite && !daddy.QueryBudgetExceeded;
            all &= seedPass;
            results.Add($"{seed}:L50={medianLength:F2}/rise={maximumGain:F2}/" +
                $"p10={tailP10:F2}/run={maximumHighRun}/s={averageSupport:F2}/" +
                $"g={maximumCancellation:F2}/q={daddy.PeakQueryCount}");
        }
        return (all, $"enabled={ablation != Ablation.SupportLift} [{string.Join(',', results)}]");
    }

    private static (bool, string) CheckLifecycle()
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableDutyAllocation = false;
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 3f, 0f), p, 71UL);
        bool bornSupport = Near(daddy.UnconditionalSupport, 1f, 1e-7f);
        int external = daddy.FindIdleTentacle();
        DaddyTentacle externalTentacle = daddy.Tentacles[external];
        var target = new DaddyLongLegsTargetSnapshot(
            123UL, externalTentacle.Segments[^1].Pos, Vector3.Zero, 0.1f, 1f, false);
        daddy.TryAssignExternalTarget(external, target);
        daddy.MoveTarget = new Vector3(8f, 3f, 0f);

        DaddyTentacle shiftedLanding = FirstLocomotion(daddy);
        PrepareSyntheticGrip(
            shiftedLanding, daddy.BodyCenter, shiftedLanding.Segments.Count, true);
        InvokePrivate(shiftedLanding, "UpdateSupport");
        bool landingWasValid = shiftedLanding.HasLandingTarget
            && shiftedLanding.AtGrabDestination
            && shiftedLanding.SupportContribution > 0f;
        Vector3 landingBefore = shiftedLanding.LandingPoint;

        Vector3[] bodyBefore = daddy.Body.Chunks.Select(c => c.Pos).ToArray();
        Vector3[] bodyLastBefore = daddy.Body.Chunks.Select(c => c.LastPos).ToArray();
        Vector3[] segmentBefore = daddy.Tentacles.SelectMany(t => t.Segments)
            .Select(s => s.Pos).ToArray();
        Vector3[] segmentLastBefore = daddy.Tentacles.SelectMany(t => t.Segments)
            .Select(s => s.LastPos).ToArray();
        Vector3 delta = new(7f, -2f, 4f);
        float supportBeforeShift = daddy.UnconditionalSupport;
        daddy.Shift(delta);
        bool landingShifted = landingWasValid && shiftedLanding.HasLandingTarget
            && NearVector(shiftedLanding.LandingPoint, landingBefore + delta, 1e-6f);
        bool shift = daddy.Body.Chunks.Select((c, i) =>
                NearVector(c.Pos, bodyBefore[i] + delta, 1e-6f)
                && NearVector(c.LastPos, bodyLastBefore[i] + delta, 1e-6f)).All(v => v)
            && daddy.Tentacles.SelectMany(t => t.Segments).Select((s, i) =>
                NearVector(s.Pos, segmentBefore[i] + delta, 1e-6f)
                && NearVector(s.LastPos, segmentLastBefore[i] + delta, 1e-6f)).All(v => v)
            && landingShifted
            && daddy.MoveTarget == new Vector3(15f, 1f, 4f)
            && Near(daddy.UnconditionalSupport, supportBeforeShift, 1e-7f)
            && daddy.Tentacles[external].ExternalTarget is { } shiftedTarget
            && NearVector(shiftedTarget.Position, target.Position + delta, 1e-6f);

        daddy.StunTentacle(shiftedLanding.Index, 19);
        bool stunnedBeforeTeleport = shiftedLanding.Role == DaddyTentacleRole.Stunned
            && shiftedLanding.StunTicks == 19;
        daddy.Teleport(new Vector3(-3f, 5f, -1f));
        bool teleportClearsStun = stunnedBeforeTeleport
            && daddy.Tentacles.All(t => t.Role != DaddyTentacleRole.Stunned
                && t.StunTicks == 0);
        bool teleport = daddy.MoveTarget is null && !daddy.AtMoveTarget
            && daddy.Tentacles.All(t => !t.HasLandingTarget && t.ExternalTarget is null
                && t.SupportContribution == 0f)
            && daddy.LocomotionTentCount() == p.MinimumLocomotionTentacles
            && daddy.Body.GravityScale == 1f
            && Near(daddy.UnconditionalSupport, 1f, 1e-7f);

        // ResetForTeleport 会把触手重新展开。先用一次零位移 Teleport 得到这套规范姿态，
        // 再挂上待清状态并做真正位移；这样可以逐粒子证明第二次 Teleport 的最终位置
        // 恰为规范姿态 + delta，而不是只验证“旧状态被清掉”。
        DaddyLongLegsLocomotionController translated =
            DaddyLongLegsFactory.CreateController(new Vector3(2f, 6f, -3f), p, 72UL);
        translated.Teleport(Vector3.Zero);
        DaddyTentacle translatedLanding = FirstLocomotion(translated);
        SetPrivateProperty(translatedLanding, "HasLandingTarget", true);
        SetPrivateProperty(translatedLanding, "LandingPoint",
            translatedLanding.Anchor.Pos + Vector3.Right);
        SetPrivateProperty(translatedLanding, "LandingNormal", Vector3.Up);
        SetPrivateProperty(translatedLanding, "LandingColliderId", 99UL);
        int translatedExternalIndex = translated.FindIdleTentacle();
        DaddyTentacle translatedExternal = translated.Tentacles[translatedExternalIndex];
        bool translatedExternalAssigned = translated.TryAssignExternalTarget(
            translatedExternalIndex,
            new DaddyLongLegsTargetSnapshot(
                789UL,
                translatedExternal.Segments[^1].Pos,
                Vector3.Zero,
                0.1f,
                1f,
                false));
        translated.MoveTarget = translated.BodyCenter + Vector3.Right * 2f;
        Vector3 translatedCenterBefore = translated.BodyCenter;
        Vector3[] translatedBodies = translated.Body.Chunks.Select(c => c.Pos).ToArray();
        Vector3[] translatedBodyLast = translated.Body.Chunks.Select(c => c.LastPos).ToArray();
        Vector3[] translatedSegments = translated.Tentacles.SelectMany(t => t.Segments)
            .Select(s => s.Pos).ToArray();
        Vector3[] translatedSegmentLast = translated.Tentacles.SelectMany(t => t.Segments)
            .Select(s => s.LastPos).ToArray();
        Vector3 teleportDelta = new(-1.25f, 2.5f, 3.75f);
        translated.Teleport(teleportDelta);
        bool teleportTranslation = translatedExternalAssigned
            && NearVector(translated.BodyCenter,
                translatedCenterBefore + teleportDelta, 2e-5f)
            && translated.Body.Chunks.Select((c, i) =>
                NearVector(c.Pos, translatedBodies[i] + teleportDelta, 2e-5f)
                && NearVector(c.LastPos, translatedBodyLast[i] + teleportDelta, 2e-5f)).All(v => v)
            && translated.Tentacles.SelectMany(t => t.Segments).Select((s, i) =>
                NearVector(s.Pos, translatedSegments[i] + teleportDelta, 2e-5f)
                && NearVector(s.LastPos,
                    translatedSegmentLast[i] + teleportDelta, 2e-5f)).All(v => v)
            && translated.MoveTarget is null && !translated.AtMoveTarget
            && translated.Tentacles.All(t => !t.HasLandingTarget && t.ExternalTarget is null)
            && translated.LocomotionTentCount() == p.MinimumLocomotionTentacles;

        // Teleport 清掉旧外部任务时保留一个 Released 事件；先给它独占一 tick，再派新目标。
        var empty = new EmptyTerrain();
        long tick = 0;
        Tick(daddy, empty, ref tick, Vector3.Zero);
        DaddyLongLegsTargetEffect teleportRelease = daddy.TargetEffects[external];
        bool teleportReleased = teleportRelease.TargetId == target.StableId
            && teleportRelease.Released && !teleportRelease.Held;
        int keepExternal = daddy.FindIdleTentacle();
        DaddyTentacle keepTentacle = daddy.Tentacles[keepExternal];
        daddy.TryAssignExternalTarget(keepExternal,
            new DaddyLongLegsTargetSnapshot(
                456UL, keepTentacle.Segments[^1].Pos, Vector3.Zero, 0.1f, 1f, false));
        daddy.MoveTarget = daddy.BodyCenter + Vector3.Right * 4f;
        Vector3 launch = new(0.05f, 0.19f, -0.03f);
        Vector3[] beforeLaunch = daddy.Body.Chunks.Select(c => c.Vel).ToArray();
        daddy.Launch(launch);
        bool launchApplied = daddy.Body.Chunks.Select((c, i) =>
                NearVector(c.Vel, beforeLaunch[i] + launch, 1e-6f)).All(v => v)
            && daddy.Tentacles.All(t => !t.HasLandingTarget && t.SupportContribution == 0f)
            && daddy.Body.GravityScale == 1f && daddy.MoveTarget is not null
            && daddy.Tentacles[keepExternal].ExternalTarget is not null
            && daddy.UnconditionalSupport == 0f;

        var floor = new HalfSpaceTerrain(new Vector3(0f, daddy.BodyCenter.Y - 1.5f, 0f),
            Vector3.Up, 7UL);
        bool recovered = false;
        for (int i = 0; i < 260; i++)
        {
            Tick(daddy, floor, ref tick);
            recovered |= daddy.EffectiveSupport > 0.08f;
        }
        return (bornSupport && shift && teleport && teleportClearsStun && teleportTranslation
                && teleportReleased && launchApplied && recovered && IsFinite(daddy),
            $"birthSupport={bornSupport} shift={shift}/landing={landingShifted} " +
            $"teleport={teleport}/clearsStun={teleportClearsStun}/" +
            $"translated={teleportTranslation}/released={teleportReleased} " +
            $"launch={launchApplied} recovered={recovered} " +
            $"support={daddy.EffectiveSupport:F3} finite={IsFinite(daddy)}");
    }

    private static (bool, string) CheckBoundsAndNoSpin()
    {
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(
                new Vector3(0f, 0.9f, 0f), DaddyLongLegsFactory.Terror(), 808UL);
        var floor = new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL);
        long tick = 0;
        bool finite = true;
        float maximumConstraint = 0f;
        float minimumSeparation = float.PositiveInfinity;
        float maximumTentacleLinkExcess = 0f;
        float maximumSegmentPenetration = 0f;
        bool activeGripConsistent = true;
        for (int i = 0; i < 420; i++)
        {
            daddy.MoveDir = i < 240 ? Vector3.Right : Vector3.Back;
            Tick(daddy, floor, ref tick);
            finite &= IsFinite(daddy);
            maximumConstraint = Math.Max(maximumConstraint, daddy.Body.CurrentMaxDeviation());
            minimumSeparation = Math.Min(minimumSeparation, ActualSelfSeparation(daddy));
            maximumTentacleLinkExcess = Math.Max(
                maximumTentacleLinkExcess, ActualMaximumLinkExcess(daddy));
            foreach (DaddyTentacle tentacle in daddy.Tentacles)
            {
                foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                {
                    maximumSegmentPenetration = Math.Max(
                        maximumSegmentPenetration,
                        Math.Max(0f, segment.Radius - segment.Pos.Y));
                    activeGripConsistent &= !segment.ActiveGrip || segment.TerrainContact;
                }
            }
        }
        bool query = daddy.PeakQueryCount <= daddy.Params.MaximumTerrainQueriesPerTick
            && !daddy.QueryBudgetExceeded && daddy.TickQueryCount > 0;

        DaddyLongLegsLocomotionController flying =
            DaddyLongLegsFactory.CreateController(new Vector3(0f, 20f, 0f),
                DaddyLongLegsFactory.Brother(), 809UL);
        Vector3 initialX = flying.MaterialAxisX;
        Vector3 initialY = flying.MaterialAxisY;
        Vector3 initialZ = flying.MaterialAxisZ;
        flying.Launch(new Vector3(0.07f, 0.03f, -0.02f));
        var empty = new EmptyTerrain();
        tick = 0;
        for (int i = 0; i < 160; i++)
        {
            flying.MoveDir = Vector3.Right;
            Tick(flying, empty, ref tick, Vector3.Zero);
        }
        float frameDot = Math.Min(initialX.Dot(flying.MaterialAxisX),
            Math.Min(initialY.Dot(flying.MaterialAxisY), initialZ.Dot(flying.MaterialAxisZ)));
        bool noSpin = frameDot > 0.995f;
        bool separation = minimumSeparation >= daddy.Params.SegmentSelfSeparation * 0.70f;
        bool tentacleLinks = maximumTentacleLinkExcess < 0.12f;
        bool terrainFeasible = maximumSegmentPenetration <= 2e-4f;
        return (finite && query && maximumConstraint < 0.25f && separation && noSpin
                && tentacleLinks && terrainFeasible && activeGripConsistent,
            $"finite={finite} query={daddy.PeakQueryCount}/{daddy.Params.MaximumTerrainQueriesPerTick} " +
            $"budgetExceeded={daddy.QueryBudgetExceeded} maxBodyDev={maximumConstraint:F4} " +
            $"minSelfSep={minimumSeparation:F4} linkExcess={maximumTentacleLinkExcess:F4} " +
            $"penetration={maximumSegmentPenetration:F6} gripState={activeGripConsistent} " +
            $"noSpinDot={frameDot:F6}");
    }

    private static (bool, string) CheckHashCoverage()
    {
        DaddyLongLegsLocomotionController first =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, DaddyLongLegsFactory.Brother(), 912UL);
        DaddyLongLegsLocomotionController changed =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, DaddyLongLegsFactory.Brother(), 912UL);
        changed.Body.Chunks[0].LastPos += Vector3.Right * 1e-4f;
        var firstHasher = new DeterminismHasher();
        var changedHasher = new DeterminismHasher();
        first.FoldDeterministicState(firstHasher);
        changed.FoldDeterministicState(changedHasher);
        bool positionsAndVelocitiesEqual = first.Body.Chunks.Zip(
            changed.Body.Chunks,
            (a, b) => BitEqual(a.Pos.X, b.Pos.X)
                && BitEqual(a.Pos.Y, b.Pos.Y)
                && BitEqual(a.Pos.Z, b.Pos.Z)
                && BitEqual(a.Vel.X, b.Vel.X)
                && BitEqual(a.Vel.Y, b.Vel.Y)
                && BitEqual(a.Vel.Z, b.Vel.Z)).All(equal => equal);
        bool covered = positionsAndVelocitiesEqual && firstHasher.Value != changedHasher.Value;
        return (covered,
            $"posVelEqual={positionsAndVelocitiesEqual} base={firstHasher.Value:X16} " +
            $"lastPosChanged={changedHasher.Value:X16}");
    }

    private readonly record struct DeterminismResult(
        ulong Hash,
        int FixedTicks,
        bool Finite,
        int PeakQueries);

    private readonly record struct EpisodeContinuityResult(
        bool ShortGapsPreserved,
        bool LongGapRestarted,
        int SerialBeforeLongGap,
        int SerialAfterLongGap,
        bool Finite,
        bool QueryBudgetExceeded);

    private readonly record struct TapGaitResult(
        string Name,
        int OnTicks,
        float Forward,
        int MovementEpisodes,
        int BodyTouchTicks,
        float MinimumBodyClearance,
        bool Finite,
        bool QueryBudgetExceeded,
        bool AssistEnabled)
    {
        public override string ToString() =>
            $"{Name}:x{Forward:F2}/on{OnTicks}/ep{MovementEpisodes}/" +
            $"touch{BodyTouchTicks}/clear{MinimumBodyClearance:F2}/" +
            $"q{QueryBudgetExceeded}/f{Finite}";
    }

    private readonly record struct StuckEscapeResult(
        bool Enabled,
        bool CrossedObstacle,
        bool RecoveredAfterCrossing,
        float MaxStuck,
        float MinimumTargetDistance,
        int ForcedSteps,
        float MaximumLateral,
        bool NoTorque,
        bool Finite,
        bool QueryBudgetExceeded);

    private static DeterminismResult RunDeterminism(int hostHz, float perturb)
    {
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(
                new Vector3(0f, 0.9f, 0f), DaddyLongLegsFactory.Brother(), 0x1234ABCDUL);
        if (perturb != 0f)
        {
            daddy.Body.Chunks[0].Pos.X += perturb;
            daddy.Body.Chunks[0].LastPos.X += perturb;
        }
        var terrain = new UnionTerrain(
            new HalfSpaceTerrain(Vector3.Zero, Vector3.Up, 1UL),
            new HalfSpaceTerrain(new Vector3(7f, 0f, 0f), Vector3.Left, 2UL));
        var hasher = new DeterminismHasher();
        int fixedTicks = 0;
        long tick = 0;
        int framesPerTick = hostHz / 40;
        int totalFrames = 760 * framesPerTick;
        int externalIndex = -1;
        for (int frame = 0; frame < totalFrames; frame++)
        {
            if ((frame + 1) % framesPerTick != 0)
                continue;
            int i = fixedTicks;
            daddy.RunSpeed = 1f;
            daddy.MoveTarget = null;
            daddy.MoveDir = i switch
            {
                < 180 => Vector3.Right,
                < 330 => Vector3.Back,
                < 470 => new Vector3(1f, 0.4f, 1f).Normalized(),
                < 610 => Vector3.Left,
                _ => new Vector3(1f, 0f, -1f).Normalized(),
            };
            if (i == 250)
                daddy.StunTentacle(FirstLocomotion(daddy).Index, 35);
            if (i == 390)
            {
                externalIndex = daddy.FindIdleTentacle();
                if (externalIndex >= 0)
                {
                    DaddyTentacle t = daddy.Tentacles[externalIndex];
                    daddy.TryAssignExternalTarget(externalIndex,
                        new DaddyLongLegsTargetSnapshot(
                            0xB00BUL, t.Segments[^1].Pos, Vector3.Zero, 0.2f, 1f, true));
                }
            }
            if (i == 430 && externalIndex >= 0)
                daddy.ClearExternalTarget(externalIndex);
            if (i == 520)
                daddy.Launch(new Vector3(0.02f, 0.12f, -0.01f));
            if (i == 650)
                daddy.Shift(new Vector3(0.25f, 0f, -0.15f));
            Tick(daddy, terrain, ref tick);
            daddy.FoldDeterministicState(hasher);
            fixedTicks++;
        }
        return new DeterminismResult(
            hasher.Value, fixedTicks, IsFinite(daddy), daddy.PeakQueryCount);
    }

    private static (bool, string) CheckAblationGates()
    {
        var results = new Dictionary<Ablation, bool>
        {
            [Ablation.Support] = CheckSupport(Ablation.Support).Item1,
            [Ablation.SupportLift] = CheckTallStance(Ablation.SupportLift).Item1,
            [Ablation.Allocation] = CheckAllocator(Ablation.Allocation).Item1,
            [Ablation.IndependentDuty] = CheckSustainedGait(Ablation.IndependentDuty).Item1,
            [Ablation.DirectionalDrive] = CheckDirectionalDrive(Ablation.DirectionalDrive).Item1,
            [Ablation.Step] = CheckStepAndSearch(Ablation.Step).Item1,
            [Ablation.SearchExpansion] = CheckStepAndSearch(Ablation.SearchExpansion).Item1,
            [Ablation.StuckRecovery] = CheckStuckRecovery(Ablation.StuckRecovery).Item1,
            [Ablation.StuckJitter] = CheckStuckJitter(Ablation.StuckJitter).Item1,
            [Ablation.StunLimp] = CheckStunTakeover(Ablation.StunLimp).Item1,
            [Ablation.ExternalPull] = CheckExternalTargetContract(Ablation.ExternalPull).Item1,
            [Ablation.SegmentAdhesion] = CheckSegmentAdhesion(Ablation.SegmentAdhesion).Item1,
            [Ablation.ResidualTerrain] = CheckResidualTerrain(Ablation.ResidualTerrain).Item1,
            [Ablation.TerrainBacktrack] =
                CheckTerrainBacktrack(Ablation.TerrainBacktrack).Item1,
            [Ablation.GripDiscrimination] =
                CheckGripDiscrimination(Ablation.GripDiscrimination).Item1,
            [Ablation.StepPeel] = CheckReplantPhases(Ablation.StepPeel).Item1,
            [Ablation.SlackGuide] = CheckFlatGuideShape(Ablation.SlackGuide).Item1,
            [Ablation.StartReplant] = CheckStartReplant(Ablation.StartReplant).Item1,
            [Ablation.IdleLandingStability] =
                CheckIdleWallStability(Ablation.IdleLandingStability).Item1,
            [Ablation.IdleSupportNeutrality] =
                CheckIdleWallStability(Ablation.IdleSupportNeutrality).Item1,
            [Ablation.StepSupportReserve] =
                CheckStepSupportReserve(Ablation.StepSupportReserve).Item1,
            [Ablation.SurfaceSpanReplant] =
                CheckMovingHeightRetention(Ablation.SurfaceSpanReplant).Item1,
            [Ablation.SerialReplant] =
                CheckSerialReplant(Ablation.SerialReplant).Item1,
            [Ablation.SupportResponse3D] =
                CheckMovingHeightRetention(Ablation.SupportResponse3D).Item1,
        };
        bool allRed = results.All(pair => !pair.Value);
        return (allRed,
            string.Join(' ', results.Select(pair => $"{AblationName(pair.Key)}={!pair.Value}")));
    }

    private static int RunIntentionalAblation(Ablation ablation)
    {
        (bool ok, string message) = ablation switch
        {
            Ablation.Support => CheckSupport(ablation),
            Ablation.SupportLift => CheckTallStance(ablation),
            Ablation.Allocation => CheckAllocator(ablation),
            Ablation.IndependentDuty => CheckSustainedGait(ablation),
            Ablation.DirectionalDrive => CheckDirectionalDrive(ablation),
            Ablation.Step => CheckStepAndSearch(ablation),
            Ablation.SearchExpansion => CheckStepAndSearch(ablation),
            Ablation.StuckRecovery => CheckStuckRecovery(ablation),
            Ablation.StuckJitter => CheckStuckJitter(ablation),
            Ablation.StunLimp => CheckStunTakeover(ablation),
            Ablation.ExternalPull => CheckExternalTargetContract(ablation),
            Ablation.SegmentAdhesion => CheckSegmentAdhesion(ablation),
            Ablation.ResidualTerrain => CheckResidualTerrain(ablation),
            Ablation.TerrainBacktrack => CheckTerrainBacktrack(ablation),
            Ablation.GripDiscrimination => CheckGripDiscrimination(ablation),
            Ablation.StepPeel => CheckReplantPhases(ablation),
            Ablation.SlackGuide => CheckFlatGuideShape(ablation),
            Ablation.StartReplant => CheckStartReplant(ablation),
            Ablation.IdleLandingStability => CheckIdleWallStability(ablation),
            Ablation.IdleSupportNeutrality => CheckIdleWallStability(ablation),
            Ablation.StepSupportReserve => CheckStepSupportReserve(ablation),
            Ablation.SurfaceSpanReplant => CheckMovingHeightRetention(ablation),
            Ablation.SerialReplant => CheckSerialReplant(ablation),
            Ablation.SupportResponse3D => CheckMovingHeightRetention(ablation),
            _ => (true, "none"),
        };
        if (!ok)
        {
            Console.WriteLine($"[DADDY-CORE-ABLATE-{AblationName(ablation).ToUpperInvariant()}] " +
                $"EXPECTED-FAIL {message}");
            return 1;
        }
        Console.WriteLine($"[DADDY-CORE-ABLATE-{AblationName(ablation).ToUpperInvariant()}] " +
            $"UNEXPECTED-PASS {message}");
        return 0;
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
                "support-lift" => Ablation.SupportLift,
                "allocation" => Ablation.Allocation,
                "independent-duty" => Ablation.IndependentDuty,
                "directional-drive" => Ablation.DirectionalDrive,
                "step" => Ablation.Step,
                "search-expansion" => Ablation.SearchExpansion,
                "stuck-recovery" => Ablation.StuckRecovery,
                "stuck-jitter" => Ablation.StuckJitter,
                "stun-limp" => Ablation.StunLimp,
                "external-pull" => Ablation.ExternalPull,
                "segment-adhesion" => Ablation.SegmentAdhesion,
                "residual-terrain" => Ablation.ResidualTerrain,
                "terrain-backtrack" => Ablation.TerrainBacktrack,
                "grip-discrimination" => Ablation.GripDiscrimination,
                "step-peel" => Ablation.StepPeel,
                "slack-guide" => Ablation.SlackGuide,
                "start-replant" => Ablation.StartReplant,
                "idle-landing-stability" => Ablation.IdleLandingStability,
                "idle-support-neutrality" => Ablation.IdleSupportNeutrality,
                "step-support-reserve" => Ablation.StepSupportReserve,
                "surface-span-replant" => Ablation.SurfaceSpanReplant,
                "serial-replant" => Ablation.SerialReplant,
                "support-response-3d" => Ablation.SupportResponse3D,
                _ => Ablation.Invalid,
            };
            if (ablation == Ablation.Invalid)
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
        SupportLift,
        Allocation,
        IndependentDuty,
        DirectionalDrive,
        Step,
        SearchExpansion,
        StuckRecovery,
        StuckJitter,
        StunLimp,
        ExternalPull,
        SegmentAdhesion,
        ResidualTerrain,
        TerrainBacktrack,
        GripDiscrimination,
        StepPeel,
        SlackGuide,
        StartReplant,
        IdleLandingStability,
        IdleSupportNeutrality,
        StepSupportReserve,
        SurfaceSpanReplant,
        SerialReplant,
        SupportResponse3D,
        Invalid,
    }

    private static string AblationName(Ablation ablation) => ablation switch
    {
        Ablation.Support => "support",
        Ablation.SupportLift => "support-lift",
        Ablation.Allocation => "allocation",
        Ablation.IndependentDuty => "independent-duty",
        Ablation.DirectionalDrive => "directional-drive",
        Ablation.Step => "step",
        Ablation.SearchExpansion => "search-expansion",
        Ablation.StuckRecovery => "stuck-recovery",
        Ablation.StuckJitter => "stuck-jitter",
        Ablation.StunLimp => "stun-limp",
        Ablation.ExternalPull => "external-pull",
        Ablation.SegmentAdhesion => "segment-adhesion",
        Ablation.ResidualTerrain => "residual-terrain",
        Ablation.TerrainBacktrack => "terrain-backtrack",
        Ablation.GripDiscrimination => "grip-discrimination",
        Ablation.StepPeel => "step-peel",
        Ablation.SlackGuide => "slack-guide",
        Ablation.StartReplant => "start-replant",
        Ablation.IdleLandingStability => "idle-landing-stability",
        Ablation.IdleSupportNeutrality => "idle-support-neutrality",
        Ablation.StepSupportReserve => "step-support-reserve",
        Ablation.SurfaceSpanReplant => "surface-span-replant",
        Ablation.SerialReplant => "serial-replant",
        Ablation.SupportResponse3D => "support-response-3d",
        _ => "none",
    };

    private static DaddyLongLegsParams TerrainBacktrackProbeParams(bool enabled)
    {
        DaddyLongLegsParams p = ProbeParams();
        p.EnableTerrainBacktrack = enabled;
        p.EnableDutyAllocation = false;
        p.EnableStepRelease = false;
        p.EnableStartReplant = false;
        p.EnableDirectionalDrive = false;
        p.EnableStuckRecovery = false;
        p.SearchRefreshTicks = 120;
        p.LandingValidationTicks = 120;
        p.IdleProximityProbeTicks = 120;
        p.MaximumTerrainQueriesPerTick = 4096;
        return p;
    }

    private static void TickTentacle(
        DaddyLongLegsLocomotionController daddy,
        DaddyTentacle tentacle,
        ITerrainQuery terrain,
        long tick)
    {
        InvokePrivate(
            tentacle,
            "Tick",
            new TickContext(Vector3.Zero, terrain, tick),
            daddy.BodyCenter,
            Vector3.Right,
            Vector3.Zero,
            0f,
            false,
            Vector3.Up,
            Vector3.Back);
    }

    private static void ConfigureOneSidedWallChain(
        DaddyLongLegsLocomotionController daddy,
        DaddyTentacle tentacle,
        ThinWallTerrain wall)
    {
        float centerX = ThinWallTerrain.HalfWidth
            + tentacle.Segments[0].Radius + 0.02f;
        float spacing = SurfaceChainSpacing(tentacle);
        Vector3 targetAnchor = new(
            centerX + tentacle.LinkLength * 0.45f, 0f, 0f);
        daddy.Body.Shift(targetAnchor - tentacle.Anchor.Pos);
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            segment.Pos = new Vector3(centerX, spacing * (i + 1), 0f);
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            SetSegmentGrip(segment, Vector3.Right, ThinWallTerrain.ColliderId);
        }
        SetPrivateField(tentacle, "_needsTerrainExpansion", false);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint",
            new Vector3(ThinWallTerrain.HalfWidth,
                tentacle.Segments[^1].Pos.Y, 0f));
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Right);
        SetPrivateProperty(tentacle, "LandingColliderId", ThinWallTerrain.ColliderId);
        SetPrivateProperty(tentacle, "ReplantPhase", DaddyTentacleReplantPhase.Planted);
        SetPrivateField(tentacle, "_landingAge", 20);
        InvokePrivate(tentacle, "UpdateSupport");
    }

    private static int ConfigureCrossedWallChain(
        DaddyLongLegsLocomotionController daddy,
        DaddyTentacle tentacle,
        ThinWallTerrain wall)
    {
        float radius = tentacle.Segments[0].Radius;
        float centerX = wall.HalfWidthValue + radius + 0.02f;
        int blockedIndex = Math.Clamp(
            tentacle.Segments.Count / 2, 1, tentacle.Segments.Count - 1);
        Vector3 targetAnchor = new(
            centerX + tentacle.LinkLength * 0.45f, 0f, 0f);
        daddy.Body.Shift(targetAnchor - tentacle.Anchor.Pos);
        PositionCrossedWallSegments(tentacle, wall, blockedIndex, setGrip: true);
        SetPrivateField(tentacle, "_needsTerrainExpansion", false);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint",
            new Vector3(-wall.HalfWidthValue,
                tentacle.Segments[^1].Pos.Y, 0f));
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Left);
        SetPrivateProperty(tentacle, "LandingColliderId", ThinWallTerrain.ColliderId);
        SetPrivateProperty(tentacle, "ReplantPhase", DaddyTentacleReplantPhase.Planted);
        SetPrivateField(tentacle, "_landingAge", 20);
        InvokePrivate(tentacle, "UpdateSupport");
        return blockedIndex;
    }

    private static void PositionCrossedWallSegments(
        DaddyTentacle tentacle,
        ThinWallTerrain wall,
        int blockedIndex,
        bool setGrip,
        float surfacePadding = 0.02f)
    {
        float centerX = wall.HalfWidthValue
            + tentacle.Segments[0].Radius + surfacePadding;
        float spacing = SurfaceChainSpacing(tentacle);
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            bool distal = i >= blockedIndex;
            Vector3 normal = distal ? Vector3.Left : Vector3.Right;
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            segment.Pos = new Vector3(
                distal ? -centerX : centerX,
                spacing * (i + 1),
                0f);
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            if (setGrip)
                SetSegmentGrip(segment, normal, ThinWallTerrain.ColliderId);
            else
            {
                segment.TerrainContact = false;
                segment.ContactNormal = Vector3.Zero;
                segment.ActiveGrip = false;
                segment.GripNormal = Vector3.Zero;
                segment.GripColliderId = 0UL;
            }
        }
    }

    private static (
        DaddyLongLegsLocomotionController Daddy,
        DaddyTentacle Tentacle,
        RecoveryCandidateFailureTerrain Terrain,
        int BlockedIndex) CreateActiveRecoveryProbe(bool enabled, ulong seed)
    {
        DaddyLongLegsParams p = TerrainBacktrackProbeParams(enabled);
        DaddyLongLegsLocomotionController daddy =
            DaddyLongLegsFactory.CreateController(Vector3.Zero, p, seed);
        DaddyTentacle tentacle = FirstLocomotion(daddy);
        foreach (BodyChunk chunk in daddy.Body.Chunks)
            chunk.CollideWithTerrain = false;
        var terrain = new RecoveryCandidateFailureTerrain();
        int blockedIndex = ConfigureCrossedWallChain(daddy, tentacle, terrain);
        for (int tick = 1; tick < p.TerrainBacktrackReleaseTicks; tick++)
        {
            Array.Clear((bool[])GetPrivateField(tentacle, "_barrierObserved"));
            InvokePrivate(
                tentacle, "AuditTerrainBacktrack",
                new TickContext(Vector3.Zero, terrain, tick));
        }
        terrain.FailAfterClearCalls(tentacle.Segments.Count - blockedIndex);
        Array.Clear((bool[])GetPrivateField(tentacle, "_barrierObserved"));
        InvokePrivate(
            tentacle, "AuditTerrainBacktrack",
            new TickContext(Vector3.Zero, terrain, p.TerrainBacktrackReleaseTicks));
        return (daddy, tentacle, terrain, blockedIndex);
    }

    private static void ConfigureOrthogonalCrossedChain(
        DaddyLongLegsLocomotionController daddy,
        DaddyTentacle tentacle,
        out int firstBarrierIndex)
    {
        float radius = tentacle.Segments[0].Radius;
        float clearance = OrthogonalThinWallsTerrain.HalfWidth + radius + 0.04f;
        float spacing = SurfaceChainSpacing(tentacle);
        firstBarrierIndex = Math.Clamp(
            tentacle.Segments.Count / 3, 1, tentacle.Segments.Count - 2);
        int secondBarrierIndex = Math.Clamp(
            tentacle.Segments.Count * 2 / 3,
            firstBarrierIndex + 1,
            tentacle.Segments.Count - 1);
        Vector3 targetAnchor = new(
            clearance + tentacle.LinkLength * 0.35f,
            clearance,
            0f);
        daddy.Body.Shift(targetAnchor - tentacle.Anchor.Pos);
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            float x = i < firstBarrierIndex ? clearance : -clearance;
            float y = i < secondBarrierIndex ? clearance : -clearance;
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            segment.Pos = new Vector3(x, y, spacing * (i + 1));
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            Vector3 normal = i < firstBarrierIndex
                ? Vector3.Right
                : i < secondBarrierIndex
                    ? Vector3.Left
                    : Vector3.Down;
            SetSegmentGrip(segment, normal, OrthogonalThinWallsTerrain.ColliderId);
        }
        SetPrivateField(tentacle, "_needsTerrainExpansion", false);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint",
            new Vector3(-clearance, -OrthogonalThinWallsTerrain.HalfWidth,
                tentacle.Segments[^1].Pos.Z));
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Down);
        SetPrivateProperty(tentacle, "LandingColliderId",
            OrthogonalThinWallsTerrain.ColliderId);
        SetPrivateProperty(tentacle, "ReplantPhase", DaddyTentacleReplantPhase.Planted);
        SetPrivateField(tentacle, "_landingAge", 20);
        InvokePrivate(tentacle, "UpdateSupport");
    }

    private static void ConfigureLegalCornerWrap(
        DaddyLongLegsLocomotionController daddy,
        DaddyTentacle tentacle,
        AabbTerrain corner)
    {
        float radius = tentacle.Segments[0].Radius;
        float clearance = radius + 0.02f;
        float spacing = SurfaceChainSpacing(tentacle);
        int cornerIndex = Math.Clamp(
            tentacle.Segments.Count / 2, 1, tentacle.Segments.Count - 2);
        float rightSpan = spacing * (cornerIndex + 1);
        float topSpan = spacing * (tentacle.Segments.Count - 1 - cornerIndex);
        Vector3 targetAnchor = new(clearance, -rightSpan, 0f);
        Vector3 cornerCenter = new(clearance, clearance, 0f);
        Vector3 landingCenter = new(-topSpan, clearance, 0f);
        daddy.Body.Shift(targetAnchor - tentacle.Anchor.Pos);

        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            Vector3 normal;
            if (i <= cornerIndex)
            {
                float amount = (float)(i + 1) / (cornerIndex + 1);
                segment.Pos = targetAnchor.Lerp(cornerCenter, amount);
                normal = i == cornerIndex
                    ? (Vector3.Right + Vector3.Up).Normalized()
                    : Vector3.Right;
            }
            else
            {
                float amount = (float)(i - cornerIndex)
                    / (tentacle.Segments.Count - 1 - cornerIndex);
                segment.Pos = cornerCenter.Lerp(landingCenter, amount);
                normal = Vector3.Up;
            }
            segment.LastPos = segment.Pos;
            segment.Vel = Vector3.Zero;
            SetSegmentGrip(segment, normal, 0xBA33UL);
        }
        SetPrivateField(tentacle, "_needsTerrainExpansion", false);
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint",
            new Vector3(landingCenter.X, 0f, 0f));
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Up);
        SetPrivateProperty(tentacle, "LandingColliderId", 0xBA33UL);
        SetPrivateProperty(tentacle, "ReplantPhase", DaddyTentacleReplantPhase.Planted);
        SetPrivateField(tentacle, "_landingAge", 20);
        InvokePrivate(tentacle, "UpdateSupport");
    }

    private static float SurfaceChainSpacing(DaddyTentacle tentacle) => Math.Min(
        tentacle.LinkLength * 0.45f,
        Math.Max(tentacle.Segments[0].Radius * 2f + 0.025f, 0.18f));

    private static void SetSegmentGrip(
        DaddyTentacleSegmentState segment,
        Vector3 normal,
        ulong colliderId)
    {
        segment.TerrainContact = true;
        segment.ContactNormal = normal;
        segment.ActiveGrip = true;
        segment.GripNormal = normal;
        segment.GripColliderId = colliderId;
    }

    private static bool AllSegmentsClear(
        DaddyTentacle tentacle,
        ITerrainQuery terrain) => tentacle.Segments.All(segment =>
        !terrain.SpherePenetration(segment.Pos, segment.Radius, out _, out _));

    private static int CountBlockedLinks(
        DaddyTentacle tentacle,
        ITerrainQuery terrain)
    {
        int blocked = 0;
        Vector3 previous = tentacle.Anchor.Pos;
        float previousRadius = tentacle.Anchor.TerrainRadius;
        foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
        {
            Vector3 delta = segment.Pos - previous;
            float length = delta.Length();
            float fromClearance = previousRadius;
            float toClearance = segment.Radius;
            if (length > fromClearance + toClearance)
            {
                Vector3 direction = delta / length;
                if (terrain.Raycast(
                        previous + direction * fromClearance,
                        segment.Pos - direction * toClearance,
                        out _))
                {
                    blocked++;
                }
            }
            previous = segment.Pos;
            previousRadius = segment.Radius;
        }
        return blocked;
    }

    private static float MaximumLinkExcess(DaddyTentacle tentacle)
    {
        float maximum = 0f;
        Vector3 previous = tentacle.Anchor.Pos;
        foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
        {
            maximum = Math.Max(
                maximum,
                previous.DistanceTo(segment.Pos) - tentacle.LinkLength);
            previous = segment.Pos;
        }
        return maximum;
    }

    private static DaddyLongLegsParams ProbeParams()
    {
        DaddyLongLegsParams p = DaddyLongLegsFactory.Brother();
        p.MinimumBodyChunks = p.MaximumBodyChunks = 4;
        p.MinimumTentacles = p.MaximumTentacles = 4;
        p.MinimumLocomotionTentacles = 2;
        p.MaximumSegmentsPerTentacle = 12;
        p.MaximumTotalTentacleSegments = 48;
        p.MaximumTerrainQueriesPerTick = 1200;
        return p;
    }

    private static DaddyLongLegsLocomotionController NewWalker(Vector3 origin, ulong seed)
    {
        DaddyLongLegsParams p = DaddyLongLegsFactory.Brother();
        p.MaximumTerrainQueriesPerTick = Math.Max(p.MaximumTerrainQueriesPerTick, 620);
        return DaddyLongLegsFactory.CreateController(origin, p, seed);
    }

    private static void Tick(
        DaddyLongLegsLocomotionController daddy,
        ITerrainQuery terrain,
        ref long tick,
        Vector3? gravity = null)
    {
        daddy.Tick(new TickContext(gravity ?? GravityPerTick, terrain, tick++));
    }

    private static DaddyTentacle FirstLocomotion(DaddyLongLegsLocomotionController daddy) =>
        daddy.Tentacles.First(t => t.Role == DaddyTentacleRole.Locomotion);

    private static int LocomotionTentCount(this DaddyLongLegsLocomotionController daddy) =>
        daddy.Tentacles.Count(t => t.Role == DaddyTentacleRole.Locomotion);

    private static void PrepareSyntheticGrip(
        DaddyTentacle tentacle,
        Vector3 bodyCenter,
        int contactedSegments,
        bool arrived)
    {
        SetPrivateProperty(tentacle, "HasLandingTarget", true);
        SetPrivateProperty(tentacle, "LandingPoint", bodyCenter + Vector3.Right * 2f);
        SetPrivateProperty(tentacle, "LandingNormal", Vector3.Up);
        SetPrivateProperty(tentacle, "LandingColliderId", 1UL);
        SetPrivateField(tentacle, "_landingAge", tentacle.Segments.Count > 0 ? 8 : 0);
        Vector3 landingCenter = tentacle.LandingPoint + Vector3.Up *
            (tentacle.Segments[^1].Radius + 0.02f);
        for (int i = 0; i < tentacle.Segments.Count; i++)
        {
            DaddyTentacleSegmentState segment = tentacle.Segments[i];
            segment.TerrainContact = i >= tentacle.Segments.Count - contactedSegments;
            segment.ContactNormal = segment.TerrainContact ? Vector3.Up : Vector3.Zero;
            segment.ActiveGrip = segment.TerrainContact;
            segment.GripNormal = segment.ActiveGrip ? Vector3.Up : Vector3.Zero;
            segment.GripColliderId = segment.ActiveGrip ? 1UL : 0UL;
            if (i == tentacle.Segments.Count - 1)
                segment.Pos = arrived ? landingCenter : landingCenter + Vector3.Right * 3f;
        }
    }

    private static void ConfigureDirectionalSupports(
        DaddyLongLegsLocomotionController daddy,
        Vector3 direction,
        bool frontSide)
    {
        daddy.Launch(Vector3.Zero);
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            InvokePrivate(tentacle, "SetLocomotion");
            PrepareSyntheticGrip(tentacle, daddy.BodyCenter,
                tentacle.Segments.Count, true);
            SetPrivateProperty(tentacle, "LandingPoint",
                daddy.BodyCenter + direction * (frontSide ? 3f : -3f));
            Vector3 landingCenter = tentacle.LandingPoint + Vector3.Up *
                (tentacle.Segments[^1].Radius + 0.02f);
            tentacle.Segments[^1].Pos = landingCenter;
            InvokePrivate(tentacle, "UpdateSupport");
        }
        daddy.MoveDir = direction;
        daddy.RunSpeed = 1f;
    }

    private static Vector3 WorldPreference(
        DaddyLongLegsLocomotionController daddy,
        DaddyTentacle tentacle)
    {
        Vector3 local = tentacle.LocalPreference;
        return (daddy.MaterialAxisX * local.X
            + daddy.MaterialAxisY * local.Y
            + daddy.MaterialAxisZ * local.Z).Normalized();
    }

    private readonly record struct SurfaceResult(
        bool Pass,
        float Travel,
        float MaxSupport,
        int Landings,
        bool SawNormal,
        bool Finite)
    {
        public override string ToString() =>
            $"{Pass}/d{Travel:F2}/s{MaxSupport:F2}/l{Landings}/n{SawNormal}/f{Finite}";
    }

    private static SurfaceResult RunSurface(
        ITerrainQuery terrain,
        Vector3 origin,
        Vector3 moveDirection,
        Vector3 expectedNormal,
        Vector3 gravity,
        int ticks,
        ulong seed)
    {
        DaddyLongLegsLocomotionController daddy = NewWalker(origin, seed);
        Vector3 start = daddy.BodyCenter;
        long tick = 0;
        bool sawNormal = false;
        bool finite = true;
        float maxSupport = 0f;
        for (int i = 0; i < ticks; i++)
        {
            daddy.MoveDir = moveDirection;
            Tick(daddy, terrain, ref tick, gravity);
            finite &= IsFinite(daddy);
            maxSupport = Math.Max(maxSupport, daddy.EffectiveSupport);
            ObserveNormal(daddy, expectedNormal, ref sawNormal);
        }
        float travel = (daddy.BodyCenter - start).Dot(moveDirection.Normalized());
        int landings = daddy.Tentacles.Sum(t => t.LandingSerial);
        bool pass = finite && sawNormal && maxSupport > 0.05f && landings > 0 && travel > 0.08f
            && !daddy.QueryBudgetExceeded;
        return new SurfaceResult(pass, travel, maxSupport, landings, sawNormal, finite);
    }

    private static void ObserveNormal(
        DaddyLongLegsLocomotionController daddy,
        Vector3 expected,
        ref bool observed)
    {
        if (daddy.Tentacles.Any(t => t.Segments.Any(s =>
            s.TerrainContact && s.ContactNormal.Dot(expected) > 0.75f)))
        {
            observed = true;
        }
    }

    private static float ActualSelfSeparation(DaddyLongLegsLocomotionController daddy)
    {
        float minimum = float.PositiveInfinity;
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            for (int i = 0; i < tentacle.Segments.Count; i++)
                for (int j = i + 1; j < tentacle.Segments.Count; j++)
                    minimum = Math.Min(minimum,
                        tentacle.Segments[i].Pos.DistanceTo(tentacle.Segments[j].Pos));
        }
        return minimum;
    }

    private static float ActualMaximumLinkExcess(DaddyLongLegsLocomotionController daddy)
    {
        float maximum = 0f;
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            Vector3 previous = tentacle.Anchor.Pos;
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
            {
                maximum = Math.Max(
                    maximum, Math.Max(0f, previous.DistanceTo(segment.Pos) - tentacle.LinkLength));
                previous = segment.Pos;
            }
        }
        return maximum;
    }

    private static Vector3 AverageVelocityVector(Body body)
    {
        Vector3 sum = Vector3.Zero;
        foreach (BodyChunk chunk in body.Chunks)
            sum += chunk.Vel;
        return sum / body.Chunks.Count;
    }

    private static float VelocitySpread(Body body)
    {
        Vector3 mean = AverageVelocityVector(body);
        float maximum = 0f;
        foreach (BodyChunk chunk in body.Chunks)
            maximum = Math.Max(maximum, chunk.Vel.DistanceTo(mean));
        return maximum;
    }

    private static bool IsFinite(DaddyLongLegsLocomotionController daddy)
    {
        if (!Finite(daddy.BodyCenter) || !Finite(daddy.MaterialAxisX)
            || !Finite(daddy.MaterialAxisY) || !Finite(daddy.MaterialAxisZ)
            || !float.IsFinite(daddy.RawSupport) || !float.IsFinite(daddy.EffectiveSupport)
            || !float.IsFinite(daddy.ContinuousSupport)
            || !float.IsFinite(daddy.UnconditionalSupport))
        {
            return false;
        }
        foreach (BodyChunk chunk in daddy.Body.Chunks)
            if (!Finite(chunk.Pos) || !Finite(chunk.LastPos) || !Finite(chunk.Vel))
                return false;
        foreach (DaddyTentacle tentacle in daddy.Tentacles)
        {
            if (!float.IsFinite(tentacle.GripFraction)
                || !float.IsFinite(tentacle.SupportContribution)
                || !float.IsFinite(tentacle.MaximumConstraintError))
                return false;
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                if (!Finite(segment.Pos) || !Finite(segment.LastPos) || !Finite(segment.Vel))
                    return false;
        }
        return true;
    }

    private static bool MorphologyBitsEqual(
        DaddyLongLegsMorphology a,
        DaddyLongLegsMorphology b)
    {
        if (a.StableId != b.StableId || a.StableSeed != b.StableSeed
            || a.BodyChunks.Length != b.BodyChunks.Length
            || a.RestDistances.Length != b.RestDistances.Length
            || a.Tentacles.Length != b.Tentacles.Length
            || a.FrameLandmarkA != b.FrameLandmarkA
            || a.FrameLandmarkB != b.FrameLandmarkB
            || a.FrameLandmarkC != b.FrameLandmarkC)
        {
            return false;
        }
        for (int i = 0; i < a.BodyChunks.Length; i++)
        {
            DaddyBodyChunkSpec x = a.BodyChunkAt(i);
            DaddyBodyChunkSpec y = b.BodyChunkAt(i);
            if (!VectorBitsEqual(x.RestOffset, y.RestOffset)
                || !BitEqual(x.Radius, y.Radius) || !BitEqual(x.Mass, y.Mass))
                return false;
        }
        for (int i = 0; i < a.RestDistances.Length; i++)
            if (!BitEqual(a.RestDistanceAt(i), b.RestDistanceAt(i)))
                return false;
        for (int i = 0; i < a.Tentacles.Length; i++)
        {
            DaddyTentacleSpec x = a.TentacleAt(i);
            DaddyTentacleSpec y = b.TentacleAt(i);
            if (x.AnchorBodyIndex != y.AnchorBodyIndex || x.SegmentCount != y.SegmentCount
                || !BitEqual(x.Length, y.Length)
                || !VectorBitsEqual(x.LocalPreference, y.LocalPreference))
                return false;
        }
        return true;
    }

    private static ulong HashMorphology(DaddyLongLegsMorphology morphology)
    {
        var hasher = new DeterminismHasher();
        hasher.Fold(morphology.BodyChunks.Length);
        foreach (DaddyBodyChunkSpec body in morphology.BodyChunks)
        {
            hasher.Fold(body.RestOffset);
            hasher.Fold(body.Radius);
            hasher.Fold(body.Mass);
        }
        foreach (float distance in morphology.RestDistances)
            hasher.Fold(distance);
        foreach (DaddyTentacleSpec tentacle in morphology.Tentacles)
        {
            hasher.Fold(tentacle.AnchorBodyIndex);
            hasher.Fold(tentacle.Length);
            hasher.Fold(tentacle.SegmentCount);
            hasher.Fold(tentacle.LocalPreference);
        }
        return hasher.Value;
    }

    private static bool BitEqual(float a, float b) =>
        BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);

    private static bool VectorBitsEqual(Vector3 a, Vector3 b) =>
        BitEqual(a.X, b.X) && BitEqual(a.Y, b.Y) && BitEqual(a.Z, b.Z);

    private static bool Near(float a, float b, float tolerance = 1e-5f) =>
        Math.Abs(a - b) <= tolerance;

    private static bool NearVector(Vector3 a, Vector3 b, float tolerance) =>
        a.DistanceSquaredTo(b) <= tolerance * tolerance;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string Format(Vector3 value) =>
        $"({value.X:F2},{value.Y:F2},{value.Z:F2})";

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static void SetPrivateProperty(object target, string name, object value)
    {
        PropertyInfo property = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().Name, name);
        property.SetValue(target, value);
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().Name, name);
        field.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().Name, name);
        return field.GetValue(target)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} was null.");
    }

    private static object? InvokePrivate(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        return method.Invoke(target, args);
    }

    private sealed class EmptyTerrain : ITerrainQuery
    {
        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            return false;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }

    /// <summary>法线指向可活动半空间；signed distance &lt; 0 的一侧是实心。</summary>
    private sealed class HalfSpaceTerrain : ITerrainQuery
    {
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public ulong ColliderId { get; }

        public HalfSpaceTerrain(Vector3 point, Vector3 normal, ulong colliderId)
        {
            Point = point;
            Normal = normal.Normalized();
            ColliderId = colliderId;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            float a = (from - Point).Dot(Normal);
            float b = (to - Point).Dot(Normal);
            if (a < 0f)
            {
                hit = new TerrainHit(from, Vector3.Zero, ColliderId);
                return true;
            }
            if (b >= 0f || Math.Abs(a - b) <= 1e-9f)
            {
                hit = default;
                return false;
            }
            float t = a / (a - b);
            hit = new TerrainHit(from.Lerp(to, t), Normal, ColliderId);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            float signed = (center - Point).Dot(Normal);
            depth = radius - signed;
            pushDir = Normal;
            return depth > 0f;
        }
    }

    private sealed class UnionTerrain : ITerrainQuery
    {
        private readonly ITerrainQuery[] _parts;

        public UnionTerrain(params ITerrainQuery[] parts) => _parts = parts;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            bool found = false;
            float best = float.PositiveInfinity;
            foreach (ITerrainQuery part in _parts)
            {
                if (!part.Raycast(from, to, out TerrainHit candidate))
                    continue;
                if (candidate.Normal.LengthSquared() <= 1e-10f)
                {
                    hit = candidate;
                    return true;
                }
                float distance = from.DistanceSquaredTo(candidate.Point);
                if (distance < best)
                {
                    found = true;
                    best = distance;
                    hit = candidate;
                }
            }
            return found;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            Vector3 correction = Vector3.Zero;
            foreach (ITerrainQuery part in _parts)
            {
                if (part.SpherePenetration(center + correction, radius,
                        out Vector3 direction, out float partDepth))
                    correction += direction * partDepth;
            }
            depth = correction.Length();
            pushDir = depth > 1e-8f ? correction / depth : Vector3.Zero;
            return depth > 0f;
        }
    }

    /// <summary>
    /// 模拟 Jolt GetRestInfo 每次只返回一个 overlap。三面角必须靠固定次序的重复 MTD
    /// 逐面投影；单次查询会稳定留下其余两面的穿透。
    /// </summary>
    private sealed class SequentialContactTerrain : ITerrainQuery
    {
        private readonly ITerrainQuery[] _parts;

        public SequentialContactTerrain(params ITerrainQuery[] parts) => _parts = parts;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            bool found = false;
            float nearest = float.PositiveInfinity;
            foreach (ITerrainQuery part in _parts)
            {
                if (!part.Raycast(from, to, out TerrainHit candidate))
                    continue;
                float distance = from.DistanceSquaredTo(candidate.Point);
                if (distance >= nearest)
                    continue;
                nearest = distance;
                hit = candidate;
                found = true;
            }
            return found;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            foreach (ITerrainQuery part in _parts)
                if (part.SpherePenetration(center, radius, out pushDir, out depth))
                    return true;
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }

        public float MaximumPenetration(Vector3 center, float radius)
        {
            float maximum = 0f;
            foreach (ITerrainQuery part in _parts)
                if (part.SpherePenetration(center, radius, out _, out float depth))
                    maximum = Math.Max(maximum, depth);
            return maximum;
        }
    }

    /// <summary>模拟两个共面实体在接缝处让 Jolt MTD 精确上下往返；锚点区域保证 free。</summary>
    private sealed class OpposingContactTerrain : ITerrainQuery
    {
        private readonly Vector3 _safeCenter;
        public int CallCount { get; private set; }
        public int HitCount { get; private set; }

        public OpposingContactTerrain(Vector3 safeCenter) => _safeCenter = safeCenter;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            return false;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            CallCount++;
            if (center.DistanceSquaredTo(_safeCenter) <= radius * radius)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            HitCount++;
            pushDir = (CallCount & 1) == 1 ? Vector3.Up : Vector3.Down;
            depth = radius * 2f;
            return true;
        }
    }

    /// <summary>
    /// 以 X=0 为中心的无限薄实体墙。段球可以分别位于两侧且都没有 penetration，
    /// 因而只有逐相邻链边的 Raycast 才能观察到“绳子穿墙”的拓扑错误。
    /// </summary>
    private class ThinWallTerrain : ITerrainQuery
    {
        public const float HalfWidth = 0.025f;
        public const ulong ColliderId = 0xBA11UL;
        private readonly float _halfWidth;

        public float HalfWidthValue => _halfWidth;

        public ThinWallTerrain(float halfWidth = HalfWidth)
        {
            if (!float.IsFinite(halfWidth) || halfWidth <= 0f)
                throw new ArgumentOutOfRangeException(nameof(halfWidth));
            _halfWidth = halfWidth;
        }

        public virtual bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (Math.Abs(from.X) < _halfWidth)
            {
                hit = new TerrainHit(from, Vector3.Zero, ColliderId);
                return true;
            }

            float deltaX = to.X - from.X;
            if (Math.Abs(deltaX) <= 1e-10f)
                return false;

            float boundary;
            Vector3 normal;
            if (from.X >= _halfWidth && to.X < _halfWidth)
            {
                boundary = _halfWidth;
                normal = Vector3.Right;
            }
            else if (from.X <= -_halfWidth && to.X > -_halfWidth)
            {
                boundary = -_halfWidth;
                normal = Vector3.Left;
            }
            else
            {
                return false;
            }

            float amount = (boundary - from.X) / deltaX;
            if (amount < 0f || amount > 1f)
                return false;
            hit = new TerrainHit(from.Lerp(to, amount), normal, ColliderId);
            return true;
        }

        public virtual bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            float distance = Math.Abs(center.X) - _halfWidth;
            depth = radius - distance;
            if (depth <= 0f)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            pushDir = center.X < 0f ? Vector3.Left : Vector3.Right;
            return true;
        }
    }

    /// <summary>
    /// 只在目标段的第二次球查询（即首轮 ResolveTerrain 之后的
    /// residual pass）才把它推到薄墙另一侧。用于钉住链边审计必须
    /// 发生在最后一次段位置修正之后。
    /// </summary>
    private sealed class ResidualCrossingTerrain : ThinWallTerrain
    {
        private readonly Vector3 _targetCenter;
        private readonly float _destinationX;
        private int _targetSphereQueries;

        public bool Pushed { get; private set; }

        public ResidualCrossingTerrain(Vector3 targetCenter, float destinationX)
        {
            _targetCenter = targetCenter;
            _destinationX = destinationX;
        }

        public override bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            if (!Pushed && center.DistanceSquaredTo(_targetCenter) <= 1e-8f)
            {
                _targetSphereQueries++;
                if (_targetSphereQueries == 2)
                {
                    Pushed = true;
                    pushDir = Vector3.Left;
                    depth = center.X - _destinationX;
                    return depth > 0f;
                }
            }
            return base.SpherePenetration(center, radius, out pushDir, out depth);
        }
    }

    /// <summary>
    /// 恢复候选失败注入：先让指定数量的球查询按真实薄墙返回，再令恰好一个候选球失败。
    /// 只用于证明整条 suffix 的提交是原子的；失败后自动恢复普通静态薄墙语义。
    /// </summary>
    private sealed class RecoveryCandidateFailureTerrain : ThinWallTerrain
    {
        private int _clearCallsBeforeFailure = -1;

        public int InjectedFailures { get; private set; }

        public void FailAfterClearCalls(int clearCallsBeforeFailure)
        {
            if (clearCallsBeforeFailure < 0)
                throw new ArgumentOutOfRangeException(nameof(clearCallsBeforeFailure));
            _clearCallsBeforeFailure = clearCallsBeforeFailure;
        }

        public override bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            if (_clearCallsBeforeFailure == 0)
            {
                _clearCallsBeforeFailure = -1;
                InjectedFailures++;
                pushDir = Vector3.Right;
                depth = 0.01f;
                return true;
            }
            if (_clearCallsBeforeFailure > 0)
                _clearCallsBeforeFailure--;
            return base.SpherePenetration(center, radius, out pushDir, out depth);
        }
    }

    /// <summary>
    /// 两个有限薄 slab 之间的缝小于球直径。球在缝中会收到分别来自左右 collider 的
    /// 合法最小推出量并形成二周期；slab 外侧仍存在可行 LastPos。
    /// </summary>
    private sealed class NarrowGapSlabsTerrain : ITerrainQuery
    {
        public const float GapHalfWidth = 0.05f;
        public const float SlabThickness = 0.02f;
        private const ulong LeftId = 0xBA2901UL;
        private const ulong RightId = 0xBA2902UL;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            float best = float.PositiveInfinity;
            Vector3 normal = Vector3.Zero;
            ulong collider = 0UL;
            TryRaySlab(
                from.X, to.X,
                -GapHalfWidth - SlabThickness, -GapHalfWidth,
                LeftId, ref best, ref normal, ref collider);
            TryRaySlab(
                from.X, to.X,
                GapHalfWidth, GapHalfWidth + SlabThickness,
                RightId, ref best, ref normal, ref collider);
            if (!float.IsFinite(best))
                return false;
            hit = new TerrainHit(from.Lerp(to, best), normal, collider);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            float leftDepth = radius - (center.X + GapHalfWidth);
            float rightDepth = radius - (GapHalfWidth - center.X);
            bool left = center.X >= -GapHalfWidth && center.X <= 0f && leftDepth > 0f;
            bool right = center.X > 0f && center.X <= GapHalfWidth && rightDepth > 0f;
            if (left)
            {
                pushDir = Vector3.Right;
                depth = leftDepth;
                return true;
            }
            if (right)
            {
                pushDir = Vector3.Left;
                depth = rightDepth;
                return true;
            }

            // slab 内或各自外侧的通用有限厚度处理，供 LastPos 可行性复验使用。
            if (TrySphereSlab(
                    center.X, radius,
                    -GapHalfWidth - SlabThickness, -GapHalfWidth,
                    out float leftPush, out leftDepth))
            {
                pushDir = leftPush < 0f ? Vector3.Left : Vector3.Right;
                depth = leftDepth;
                return true;
            }
            if (TrySphereSlab(
                    center.X, radius,
                    GapHalfWidth, GapHalfWidth + SlabThickness,
                    out float rightPush, out rightDepth))
            {
                pushDir = rightPush < 0f ? Vector3.Left : Vector3.Right;
                depth = rightDepth;
                return true;
            }
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }

        private static bool TrySphereSlab(
            float center,
            float radius,
            float minimum,
            float maximum,
            out float push,
            out float depth)
        {
            if (center < minimum)
            {
                depth = radius - (minimum - center);
                push = -1f;
                return depth > 0f;
            }
            if (center > maximum)
            {
                depth = radius - (center - maximum);
                push = 1f;
                return depth > 0f;
            }
            float left = center - minimum;
            float right = maximum - center;
            bool leaveLeft = left <= right;
            push = leaveLeft ? -1f : 1f;
            depth = (leaveLeft ? left : right) + radius;
            return true;
        }

        private static void TryRaySlab(
            float from,
            float to,
            float minimum,
            float maximum,
            ulong colliderId,
            ref float best,
            ref Vector3 bestNormal,
            ref ulong bestCollider)
        {
            if (from > minimum && from < maximum)
            {
                best = 0f;
                bestNormal = Vector3.Zero;
                bestCollider = colliderId;
                return;
            }
            float delta = to - from;
            if (Math.Abs(delta) <= 1e-10f)
                return;
            float boundary;
            Vector3 normal;
            if (from <= minimum && to > minimum)
            {
                boundary = minimum;
                normal = Vector3.Left;
            }
            else if (from >= maximum && to < maximum)
            {
                boundary = maximum;
                normal = Vector3.Right;
            }
            else
            {
                return;
            }
            float amount = (boundary - from) / delta;
            if (amount < 0f || amount > 1f || amount >= best)
                return;
            best = amount;
            bestNormal = normal;
            bestCollider = colliderId;
        }
    }

    /// <summary>X=0 / Y=0 的两道正交薄墙；射线返回沿线最近的命中。</summary>
    private sealed class OrthogonalThinWallsTerrain : ITerrainQuery
    {
        public const float HalfWidth = 0.025f;
        public const ulong ColliderId = 0xBA23UL;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            Vector3 delta = to - from;
            float bestAmount = float.PositiveInfinity;
            Vector3 bestNormal = Vector3.Zero;
            TryAxis(from.X, delta.X, Vector3.Right, ref bestAmount, ref bestNormal);
            TryAxis(from.Y, delta.Y, Vector3.Up, ref bestAmount, ref bestNormal);
            if (!float.IsFinite(bestAmount))
                return false;
            hit = new TerrainHit(from.Lerp(to, bestAmount), bestNormal, ColliderId);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            float xDepth = radius - (Math.Abs(center.X) - HalfWidth);
            float yDepth = radius - (Math.Abs(center.Y) - HalfWidth);
            if (xDepth <= 0f && yDepth <= 0f)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            if (xDepth >= yDepth)
            {
                pushDir = center.X < 0f ? Vector3.Left : Vector3.Right;
                depth = xDepth;
            }
            else
            {
                pushDir = center.Y < 0f ? Vector3.Down : Vector3.Up;
                depth = yDepth;
            }
            return true;
        }

        private static void TryAxis(
            float from,
            float delta,
            Vector3 positiveNormal,
            ref float bestAmount,
            ref Vector3 bestNormal)
        {
            if (Math.Abs(from) < HalfWidth)
            {
                bestAmount = 0f;
                bestNormal = Vector3.Zero;
                return;
            }
            if (Math.Abs(delta) <= 1e-10f)
                return;
            float boundary;
            Vector3 normal;
            if (from >= HalfWidth && from + delta < HalfWidth)
            {
                boundary = HalfWidth;
                normal = positiveNormal;
            }
            else if (from <= -HalfWidth && from + delta > -HalfWidth)
            {
                boundary = -HalfWidth;
                normal = -positiveNormal;
            }
            else
            {
                return;
            }
            float amount = (boundary - from) / delta;
            if (amount < 0f || amount > 1f || amount >= bestAmount)
                return;
            bestAmount = amount;
            bestNormal = normal;
        }
    }

    private sealed class AabbTerrain : ITerrainQuery
    {
        private readonly Vector3 _minimum;
        private readonly Vector3 _maximum;
        private readonly ulong _colliderId;

        public AabbTerrain(Vector3 minimum, Vector3 maximum, ulong colliderId)
        {
            _minimum = minimum;
            _maximum = maximum;
            _colliderId = colliderId;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            if (Inside(from, _minimum, _maximum))
            {
                hit = new TerrainHit(from, Vector3.Zero, _colliderId);
                return true;
            }
            Vector3 delta = to - from;
            float enter = 0f;
            float exit = 1f;
            Vector3 normal = Vector3.Zero;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = Axis(from, axis);
                float direction = Axis(delta, axis);
                float minimum = Axis(_minimum, axis);
                float maximum = Axis(_maximum, axis);
                if (Math.Abs(direction) <= 1e-9f)
                {
                    if (origin < minimum || origin > maximum)
                    {
                        hit = default;
                        return false;
                    }
                    continue;
                }
                float near = (minimum - origin) / direction;
                float far = (maximum - origin) / direction;
                Vector3 nearNormal = AxisVector(axis, -Math.Sign(direction));
                if (near > far)
                    (near, far) = (far, near);
                if (near > enter)
                {
                    enter = near;
                    normal = nearNormal;
                }
                exit = Math.Min(exit, far);
                if (enter > exit)
                {
                    hit = default;
                    return false;
                }
            }
            if (enter < 0f || enter > 1f)
            {
                hit = default;
                return false;
            }
            hit = new TerrainHit(from + delta * enter, normal, _colliderId);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            Vector3 closest = new(
                Mathf.Clamp(center.X, _minimum.X, _maximum.X),
                Mathf.Clamp(center.Y, _minimum.Y, _maximum.Y),
                Mathf.Clamp(center.Z, _minimum.Z, _maximum.Z));
            Vector3 delta = center - closest;
            float distance = delta.Length();
            if (!Inside(center, _minimum, _maximum) && distance >= radius)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            if (distance > 1e-7f)
            {
                pushDir = delta / distance;
                depth = radius - distance;
                return depth > 0f;
            }

            float[] distances =
            {
                center.X - _minimum.X + radius,
                _maximum.X - center.X + radius,
                center.Y - _minimum.Y + radius,
                _maximum.Y - center.Y + radius,
                center.Z - _minimum.Z + radius,
                _maximum.Z - center.Z + radius,
            };
            int selected = 0;
            for (int i = 1; i < distances.Length; i++)
                if (distances[i] < distances[selected])
                    selected = i;
            pushDir = selected switch
            {
                0 => Vector3.Left,
                1 => Vector3.Right,
                2 => Vector3.Down,
                3 => Vector3.Up,
                4 => Vector3.Forward,
                _ => Vector3.Back,
            };
            depth = distances[selected];
            return depth > 0f;
        }

        private static bool Inside(Vector3 point, Vector3 minimum, Vector3 maximum) =>
            point.X >= minimum.X && point.X <= maximum.X
            && point.Y >= minimum.Y && point.Y <= maximum.Y
            && point.Z >= minimum.Z && point.Z <= maximum.Z;

        private static float Axis(Vector3 value, int axis) => axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z,
        };

        private static Vector3 AxisVector(int axis, float value) => axis switch
        {
            0 => new Vector3(value, 0f, 0f),
            1 => new Vector3(0f, value, 0f),
            _ => new Vector3(0f, 0f, value),
        };
    }
}
