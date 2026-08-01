using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace ProcAnim.Core.TentaclePlantSmoke;

internal static class Program
{
    private const ulong ExpectedHash = 0x06F0D3E1B3D2B044UL;
    private static readonly Vector3 NoGravity = Vector3.Zero;

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var failures = new List<string>();

        Check("FACTORY", CheckFactoryAndValidation, failures);
        Check("MOUNTS", CheckThreeDimensionalMounts, failures);
        Check("ATTACK", CheckAttackTimelineAndForceOrder, failures);
        Check("STRIKE-GEOMETRY", CheckStrikeGeometry, failures);
        Check("ENVELOPE", CheckTargetVolumeAttackEnvelope, failures);
        Check("GRASP", CheckGraspAndHostContract, failures);
        Check("CARRY-LOOP", CheckClosedHostCarryLoop, failures);
        Check("CAPTURE-LOS", CheckCaptureLineOfSight, failures);
        Check("ROUTING", CheckRoutingBacktrackAndQueries, failures);
        Check("FAR-GUIDE", CheckFarTargetGuideContinuity, failures);
        Check("SPAWN-CLEARANCE", CheckSpawnAndRemountClearance, failures);
        Check("LIFECYCLE", CheckLifecycle, failures);

        DeterminismResult a = RunDeterminism();
        DeterminismResult b = RunDeterminism();
        DeterminismResult differentSeed = RunDeterminism(0x5EED1235UL);
        bool deterministic = a.Hash == b.Hash &&
                             a.Hash == ExpectedHash &&
                             a.Hash != differentSeed.Hash &&
                             a.Finite &&
                             a.PeakQueries <= 96 &&
                             a.MaxPenetration <= 0.002f;
        Report(
            "DET",
            deterministic,
            $"run1={a.Hash:X16} run2={b.Hash:X16} expected={ExpectedHash:X16} " +
            $"otherSeed={differentSeed.Hash:X16} " +
            $"finite={a.Finite} peakQueries={a.PeakQueries} " +
            $"maxPen={a.MaxPenetration:F6}m attacks={a.Attacks}",
            failures);

        bool pass = failures.Count == 0;
        Console.WriteLine(pass
            ? "[TENTACLE-PLANT-CORE-SMOKE] PASS：预设、三向安装、攻击时序、抓持、3D绕障、回卷、生命周期与固定哈希均通过"
            : $"[TENTACLE-PLANT-CORE-SMOKE] FAIL：{string.Join("；", failures)}");
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
        Console.WriteLine($"[TENTACLE-PLANT-CORE-{name}] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok)
        {
            failures.Add(name);
        }
    }

    private static (bool, string) CheckFactoryAndValidation()
    {
        TentaclePlantParams original = TentaclePlantFactory.Original();
        TentaclePlantParams shortPlant = TentaclePlantFactory.Short();
        TentaclePlantParams hunter = TentaclePlantFactory.Hunter();
        TentaclePlantParams[] all = TentaclePlantFactory.AllPresets();

        bool ids = all.Length == 3 &&
                   original.Name == "tentacle-plant/original" &&
                   shortPlant.Name == "tentacle-plant/short" &&
                   hunter.Name == "tentacle-plant/hunter" &&
                   TentaclePlantFactory.ByName("TENTACLE-PLANT/HUNTER").Name == hunter.Name;
        bool originalEvidence = Near(original.Length, 7.5f) &&
                                original.SegmentCount == 8 &&
                                original.ChargeTicks == 90 &&
                                original.LungeTicks == 10 &&
                                original.GrabWindowTicks == 40 &&
                                original.RetractTicks == 80;
        bool variants = Near(shortPlant.Length, 5f) &&
                        shortPlant.SegmentCount == 6 &&
                        shortPlant.ChargeTicks == 110 &&
                        Near(hunter.Length, 9f) &&
                        hunter.SegmentCount == 10 &&
                        hunter.ChargeTicks == 70;

        var mount = new TentaclePlantMount(
            new Vector3(1f, 2f, 3f),
            new Vector3(0f, 4f, 0f),
            new Vector3(0f, 2f, 0f),
            77UL);
        TentaclePlantController plant = TentaclePlantFactory.CreateController(
            mount,
            original,
            1234UL);
        bool topology = plant.Body.Chunks.Count == 2 &&
                        plant.Body.Connections.Count == 0 &&
                        ReferenceEquals(plant.Root, plant.Body.Chunks[0]) &&
                        ReferenceEquals(plant.Hand, plant.Body.Chunks[1]) &&
                        plant.Segments.Count == 8 &&
                        Near(plant.Segments[0].Radius, original.RootRadius) &&
                        Near(plant.Segments[^1].Radius, original.TipRadius) &&
                        Near(plant.Segments[^1].Mass, original.TipMass) &&
                        !plant.Root.CollideWithTerrain &&
                        !plant.Hand.CollideWithTerrain;
        bool frame = Near(plant.Outward.Length(), 1f) &&
                     Near(plant.Tangent.Length(), 1f) &&
                     Near(plant.Bitangent.Length(), 1f) &&
                     Math.Abs(plant.Outward.Dot(plant.Tangent)) < 1e-5f &&
                     Math.Abs(plant.Outward.Dot(plant.Bitangent)) < 1e-5f &&
                     Math.Abs(plant.Tangent.Dot(plant.Bitangent)) < 1e-5f &&
                     plant.Mount.ColliderId == 77UL;

        TentaclePlantParams mutable = TentaclePlantFactory.Original();
        TentaclePlantController frozen = NewPlant(mutable, 9UL);
        mutable.Length = 99f;
        mutable.ChargeTicks = 1;
        bool snapshot = Near(frozen.Params.Length, 7.5f) &&
                        frozen.Params.ChargeTicks == 90;

        bool throwsUnknown = Throws<ArgumentException>(
            () => TentaclePlantFactory.ByName("original"));
        bool throwsZeroNormal = Throws<ArgumentException>(() =>
            TentaclePlantFactory.CreateController(
                new TentaclePlantMount(Vector3.Zero, Vector3.Zero, Vector3.Right, 0UL),
                TentaclePlantFactory.Original(),
                1UL));
        bool throwsNarrowGuide = Throws<ArgumentOutOfRangeException>(() =>
        {
            TentaclePlantParams invalid = TentaclePlantFactory.Original();
            invalid.GuideClearanceRadius = invalid.RootRadius * 0.5f;
            invalid.Validate();
        });
        bool throwsBuriedSpawn = Throws<ArgumentOutOfRangeException>(() =>
        {
            TentaclePlantParams invalid = TentaclePlantFactory.Original();
            invalid.SpawnExtension = 0.01f;
            invalid.Validate();
        });
        bool throwsBuriedRetract = Throws<ArgumentOutOfRangeException>(() =>
        {
            TentaclePlantParams invalid = TentaclePlantFactory.Original();
            invalid.RetractedLengthFraction = 0.01f;
            invalid.Validate();
        });
        bool throwsNonFiniteTarget = Throws<ArgumentException>(() =>
            new TentaclePlantTargetSnapshot(
                1UL,
                new Vector3(float.NaN, 0f, 0f),
                Vector3.Zero,
                0.1f,
                1f,
                true,
                true));

        bool ok = ids && originalEvidence && variants && topology && frame && snapshot &&
                  throwsUnknown && throwsZeroNormal && throwsNarrowGuide &&
                  throwsBuriedSpawn && throwsBuriedRetract && throwsNonFiniteTarget;
        return (
            ok,
            $"ids={ids} original={originalEvidence} variants={variants} topology={topology} " +
            $"frame={frame} snapshot={snapshot} failFast=" +
            $"{throwsUnknown}/{throwsZeroNormal}/{throwsNarrowGuide}/{throwsBuriedSpawn}/" +
            $"{throwsBuriedRetract}/{throwsNonFiniteTarget}");
    }

    private static (bool, string) CheckThreeDimensionalMounts()
    {
        (Vector3 Outward, Vector3 Tangent)[] frames =
        {
            (Vector3.Up, Vector3.Right),
            (Vector3.Right, Vector3.Back),
            (Vector3.Down, Vector3.Left),
        };

        bool finite = true;
        bool bounded = true;
        bool clear = true;
        bool falseBacktrack = false;
        int peak = 0;
        float maxPenetration = 0f;
        float maxLinkRatio = 0f;
        int maxStretchRun = 0;
        foreach ((Vector3 outward, Vector3 tangent) in frames)
        {
            var terrain = new PlaneTerrain(Vector3.Zero, outward);
            TentaclePlantController plant = TentaclePlantFactory.CreateController(
                new TentaclePlantMount(Vector3.Zero, outward, tangent, 1UL),
                TentaclePlantFactory.Original(),
                0x10203040UL);
            long tick = 0;
            int stretchRun = 0;
            for (int i = 0; i < 500; i++)
            {
                Tick(plant, terrain, ref tick);
                finite &= IsFinite(plant);
                Vector3 center = plant.Root.Pos +
                                 plant.Outward * plant.Params.WanderCenterDistance;
                bounded &= plant.WanderGoal.DistanceTo(center) <=
                           plant.Params.WanderRadius + 1e-4f;
                bounded &= (plant.WanderGoal - plant.Root.Pos).Dot(plant.Outward) > 0f;
                falseBacktrack |= plant.BacktrackFrom >= 0;
                peak = Math.Max(peak, plant.TickQueryCount);
                bool stretched = false;
                Vector3 previous = plant.Root.Pos;
                float linkLength = plant.Params.Length / plant.Segments.Count;
                foreach (TentacleSegmentState segment in plant.Segments)
                {
                    float penetration = terrain.PenetrationDepth(segment.Pos, segment.Radius);
                    maxPenetration = Math.Max(maxPenetration, penetration);
                    clear &= penetration <= 0.002f;
                    bounded &= segment.Pos.DistanceTo(plant.Root.Pos) <=
                               plant.Params.Length + 0.75f;
                    float ratio = previous.DistanceTo(segment.Pos) / linkLength;
                    maxLinkRatio = Math.Max(maxLinkRatio, ratio);
                    stretched |= ratio > 1.25f;
                    previous = segment.Pos;
                }
                stretchRun = stretched ? stretchRun + 1 : 0;
                maxStretchRun = Math.Max(maxStretchRun, stretchRun);
            }
        }

        bool ok = finite && bounded && clear && !falseBacktrack &&
                  peak <= 96 && maxStretchRun <= 10;
        return (
            ok,
            $"finite={finite} bounded={bounded} clear={clear} falseBacktrack={falseBacktrack} " +
            $"peakQueries={peak} maxPen={maxPenetration:F6}m " +
            $"maxLink={maxLinkRatio:F3}x maxStretchRun={maxStretchRun}");
    }

    private static (bool, string) CheckAttackTimelineAndForceOrder()
    {
        TentaclePlantParams strongParams = TentaclePlantFactory.Original();
        TentaclePlantParams weakParams = TentaclePlantFactory.Original();
        weakParams.LungeImpulse = 0.01f;
        TentaclePlantController strong = NewPlant(strongParams, 44UL);
        TentaclePlantController weak = NewPlant(weakParams, 44UL);
        var terrain = new OpenTerrain();
        long strongTick = 0;
        long weakTick = 0;
        TentaclePlantTargetSnapshot target = NewTarget(
            7UL,
            strong.Root.Pos + strong.Outward * 6f,
            Vector3.Zero,
            visible: true,
            grabbable: false);

        int firstWindup = -1;
        int firstStrike = -1;
        int strikingTicks = 0;
        Vector3 strongAt90 = default;
        Vector3 weakAt90 = default;
        Vector3 strongVelocity90 = default;
        Vector3 weakVelocity90 = default;
        Vector3 oldDirection = (target.Position - strong.Hand.Pos).Normalized();
        for (int i = 1; i <= 90; i++)
        {
            strong.Target = target;
            weak.Target = target;
            Tick(strong, terrain, ref strongTick);
            Tick(weak, terrain, ref weakTick);
            if (firstWindup < 0 && strong.Phase == TentaclePlantPhase.Windup)
            {
                firstWindup = i;
            }
            if (firstStrike < 0 && strong.Phase == TentaclePlantPhase.Striking)
            {
                firstStrike = i;
            }
            if (strong.Phase == TentaclePlantPhase.Striking)
            {
                strikingTicks++;
            }
            if (i == 90)
            {
                strongAt90 = strong.Hand.Pos;
                weakAt90 = weak.Hand.Pos;
                strongVelocity90 = strong.Hand.Vel;
                weakVelocity90 = weak.Hand.Vel;
                oldDirection = (target.Position - strong.Hand.Pos).Normalized();
            }
        }

        bool exactStart = firstWindup == 45 &&
                          firstStrike == 90 &&
                          strong.AttackSerial == 1 &&
                          strong.Phase == TentaclePlantPhase.Striking;
        bool nextTickForce = strongAt90.DistanceTo(weakAt90) <= 1e-6f &&
                             strongVelocity90.DistanceTo(weakVelocity90) > 0.05f;

        Vector3 newDirection = strong.Tangent;
        target = NewTarget(
            7UL,
            strong.Root.Pos + newDirection * 3f + strong.Outward * 2f,
            Vector3.Zero,
            visible: true,
            grabbable: false);
        Vector3 before91 = strong.Hand.Pos;
        strong.Target = target;
        weak.Target = target;
        Tick(strong, terrain, ref strongTick);
        Tick(weak, terrain, ref weakTick);
        if (strong.Phase == TentaclePlantPhase.Striking)
        {
            strikingTicks++;
        }
        Vector3 displacement91 = strong.Hand.Pos - before91;
        Vector3 isolatedLungeDisplacement = strong.Hand.Pos - weak.Hand.Pos;
        bool directionLocked = isolatedLungeDisplacement.Dot(oldDirection) >
                               Math.Abs(isolatedLungeDisplacement.Dot(newDirection));
        float nextTickPositionDelta = strong.Hand.Pos.DistanceTo(weak.Hand.Pos);
        bool forceConsumedNextTick = nextTickPositionDelta > 0.01f;

        for (int i = 92; i <= 99; i++)
        {
            strong.Target = target;
            Tick(strong, terrain, ref strongTick);
            if (strong.Phase == TentaclePlantPhase.Striking)
            {
                strikingTicks++;
            }
        }
        bool firstLungeLength = strikingTicks == strong.Params.LungeTicks;
        bool grabWindowExact = true;
        for (int i = 100; i <= 138; i++)
        {
            strong.Target = target;
            Tick(strong, terrain, ref strongTick);
            grabWindowExact &= strong.CanGrab > 0f &&
                               strong.Phase == TentaclePlantPhase.Recovering;
        }
        strong.Target = target;
        Tick(strong, terrain, ref strongTick); // tick 139: final 1/40 decays to zero
        grabWindowExact &= Near(strong.CanGrab, 0f);
        bool noEarlySecondStrike = true;
        for (int i = 140; i <= 189; i++)
        {
            strong.Target = target;
            Tick(strong, terrain, ref strongTick);
            if (i < 189)
            {
                noEarlySecondStrike &= strong.AttackSerial == 1;
            }
        }
        bool cadence = firstLungeLength &&
                       grabWindowExact &&
                       noEarlySecondStrike &&
                       strong.AttackSerial == 2 &&
                       strong.Phase == TentaclePlantPhase.Striking;

        TentaclePlantController decay = NewPlant(
            TentaclePlantFactory.Original(),
            99UL);
        long decayTick = 0;
        for (int i = 0; i < 45; i++)
        {
            decay.Target = NewTarget(
                9UL,
                decay.Root.Pos + decay.Outward * 5f,
                Vector3.Zero,
                visible: true,
                grabbable: false);
            Tick(decay, terrain, ref decayTick);
        }
        float halfCharge = decay.AttackCharge;
        for (int i = 0; i < 90; i++)
        {
            decay.Target = null;
            Tick(decay, terrain, ref decayTick);
        }
        bool decayExact = Near(halfCharge, 0.5f) && Near(decay.AttackCharge, 0f);

        TentaclePlantController outOfRange = NewPlant(
            TentaclePlantFactory.Original(),
            100UL);
        long rangeTick = 0;
        for (int i = 0; i < 100; i++)
        {
            outOfRange.Target = NewTarget(
                10UL,
                outOfRange.Root.Pos +
                outOfRange.Outward * (outOfRange.Params.Length + 1f),
                Vector3.Zero,
                visible: true,
                grabbable: false);
            Tick(outOfRange, terrain, ref rangeTick);
        }

        var occluder = new BoxTerrain(
            new Box(
                new Vector3(-20f, 3f, -20f),
                new Vector3(20f, 3.5f, 20f),
                10UL));
        TentaclePlantController occluded = NewPlant(
            TentaclePlantFactory.Original(),
            101UL);
        long occludedTick = 0;
        for (int i = 0; i < 100; i++)
        {
            occluded.Target = NewTarget(
                11UL,
                occluded.Root.Pos + occluded.Outward * 5f,
                Vector3.Zero,
                visible: true,
                grabbable: false);
            Tick(occluded, occluder, ref occludedTick);
        }
        bool gates = Near(outOfRange.AttackCharge, 0f) &&
                     outOfRange.AttackSerial == 0 &&
                     Near(occluded.AttackCharge, 0f) &&
                     occluded.AttackSerial == 0;

        bool ok = exactStart && nextTickForce && forceConsumedNextTick &&
                  directionLocked && cadence && decayExact && gates;
        return (
            ok,
            $"windup={firstWindup} strike={firstStrike} serial={strong.AttackSerial} " +
            $"strikeTicks={strikingTicks} samePosAtStrike={strongAt90.DistanceTo(weakAt90):F6} " +
            $"velocityDelta={strongVelocity90.DistanceTo(weakVelocity90):F4} " +
            $"nextPosDelta={nextTickPositionDelta:F4} " +
            $"lockProj={isolatedLungeDisplacement.Dot(oldDirection):F4}/" +
            $"{isolatedLungeDisplacement.Dot(newDirection):F4} window={grabWindowExact} " +
            $"noEarly={noEarlySecondStrike} decay={halfCharge:F3}->{decay.AttackCharge:F3} " +
            $"gates={outOfRange.AttackCharge:F3}/{occluded.AttackCharge:F3}");
    }

    private static (bool, string) CheckStrikeGeometry()
    {
        var terrain = new OpenTerrain();
        bool finite = true;
        bool handTipSynced = true;
        bool velocityBounded = true;
        bool allAttacked = true;
        float maxLinkRatio = 0f;
        float maxReachRatio = 0f;
        float maxTipStepRatio = 0f;

        foreach (TentaclePlantParams parameters in TentaclePlantFactory.AllPresets())
        {
            TentaclePlantController plant = NewPlant(parameters, 0xA770UL);
            long tick = 0;
            Tick(plant, terrain, ref tick); // consume terrain-safe birth seeding
            Vector3 previousTip = plant.Chain.Tip.Pos;
            Vector3 targetPosition = plant.Root.Pos +
                                     plant.Outward * (plant.Params.Length * 0.78f) +
                                     plant.Tangent * 0.15f;
            int duration = plant.Params.ChargeTicks + plant.Params.LungeTicks + 5;
            for (int i = 0; i < duration; i++)
            {
                plant.Target = NewTarget(
                    0xA771UL,
                    targetPosition,
                    Vector3.Zero,
                    visible: true,
                    grabbable: false);
                Tick(plant, terrain, ref tick);

                finite &= IsFinite(plant);
                handTipSynced &= plant.Hand.Pos.DistanceTo(plant.Chain.Tip.Pos) <= 1e-5f;
                velocityBounded &= plant.Hand.Vel.Length() <=
                                   plant.Params.SegmentVelocityCap + 1e-4f;
                velocityBounded &= plant.Chain.Tip.Vel.Length() <=
                                   plant.Params.SegmentVelocityCap + 1e-4f;
                maxTipStepRatio = Math.Max(
                    maxTipStepRatio,
                    previousTip.DistanceTo(plant.Chain.Tip.Pos) /
                    plant.Params.SegmentVelocityCap);
                previousTip = plant.Chain.Tip.Pos;

                float effectiveLength = plant.Params.Length * Mathf.Lerp(
                    plant.Params.RetractedLengthFraction,
                    1f,
                    plant.Extension);
                float linkLength = effectiveLength / plant.Segments.Count;
                Vector3 previous = plant.Root.Pos;
                foreach (TentacleSegmentState segment in plant.Segments)
                {
                    maxLinkRatio = Math.Max(
                        maxLinkRatio,
                        previous.DistanceTo(segment.Pos) / linkLength);
                    previous = segment.Pos;
                }
                maxReachRatio = Math.Max(
                    maxReachRatio,
                    plant.Chain.Tip.Pos.DistanceTo(plant.Root.Pos) /
                    plant.Params.Length);
            }
            allAttacked &= plant.AttackSerial == 1;
        }

        bool ok = finite && handTipSynced && velocityBounded && allAttacked &&
                  maxLinkRatio <= 1.25f &&
                  // PullOnly 是有限迭代软约束；5% 只容纳正常柔顺性，仍会把旧 2x
                  // 扑击伸长稳定打红。
                  maxReachRatio <= 1.05f &&
                  maxTipStepRatio <= 1.05f;
        return (
            ok,
            $"finite={finite} synced={handTipSynced} velocity={velocityBounded} " +
            $"attacked={allAttacked} maxLink={maxLinkRatio:F3}x " +
            $"maxReach={maxReachRatio:F3}x maxTipStep={maxTipStepRatio:F3}xCap");
    }

    private static (bool, string) CheckTargetVolumeAttackEnvelope()
    {
        const float radius = 0.16f;
        TentaclePlantMount[] mounts =
        {
            new(Vector3.Zero, Vector3.Up, Vector3.Right, 201UL),
            new(Vector3.Zero, Vector3.Forward, Vector3.Up, 202UL),
            new(Vector3.Zero, Vector3.Down, Vector3.Right, 203UL),
        };
        var terrain = new OpenTerrain();
        bool rotatedEdgeHits = true;
        float minimumEdgeExcess = float.MaxValue;
        int latestWindup = -1;
        int latestStrike = -1;

        for (int mountIndex = 0; mountIndex < mounts.Length; mountIndex++)
        {
            TentaclePlantController edge = TentaclePlantFactory.CreateController(
                mounts[mountIndex],
                TentaclePlantFactory.Hunter(),
                (ulong)(220 + mountIndex));
            Vector3 edgePosition = edge.Mount.Point +
                                   edge.Outward * edge.Params.Length +
                                   edge.Tangent * 0.90f +
                                   edge.Bitangent * 0.45f;
            float centerDistance = edgePosition.DistanceTo(edge.Root.Pos);
            minimumEdgeExcess = Math.Min(
                minimumEdgeExcess,
                centerDistance - edge.Params.Length);
            bool surfaceIntersects = centerDistance > edge.Params.Length &&
                                     centerDistance < edge.Params.Length + radius;
            long tick = 0;
            int firstWindup = -1;
            int firstStrike = -1;
            bool chargeableBeforeWindup = true;
            for (int i = 1; i <= edge.Params.ChargeTicks; i++)
            {
                edge.Target = NewTarget(
                    (ulong)(230 + mountIndex),
                    edgePosition,
                    Vector3.Zero,
                    visible: true,
                    grabbable: false,
                    radius: radius);
                Tick(edge, terrain, ref tick);
                if (i < edge.Params.ChargeTicks / 2)
                {
                    chargeableBeforeWindup &=
                        edge.TargetStatus == TentaclePlantTargetStatus.Chargeable;
                }
                if (firstWindup < 0 && edge.Phase == TentaclePlantPhase.Windup)
                {
                    firstWindup = i;
                }
                if (firstStrike < 0 && edge.Phase == TentaclePlantPhase.Striking)
                {
                    firstStrike = i;
                }
            }
            latestWindup = Math.Max(latestWindup, firstWindup);
            latestStrike = Math.Max(latestStrike, firstStrike);
            rotatedEdgeHits &= surfaceIntersects && chargeableBeforeWindup &&
                               firstWindup == 35 && firstStrike == 70 &&
                               edge.AttackSerial == 1;
        }

        TentaclePlantController planeEdge = NewPlant(
            TentaclePlantFactory.Hunter(),
            240UL);
        long planeTick = 0;
        planeEdge.Target = NewTarget(
            241UL,
            planeEdge.Mount.Point - planeEdge.Outward * (radius * 0.5f) +
            planeEdge.Tangent * 2f,
            Vector3.Zero,
            visible: true,
            grabbable: false,
            radius: radius);
        Tick(planeEdge, terrain, ref planeTick);
        bool planeSurfaceIntersects =
            planeEdge.TargetStatus == TentaclePlantTargetStatus.Chargeable &&
            planeEdge.AttackCharge > 0f;

        TentaclePlantController capSeamInside = NewPlant(
            TentaclePlantFactory.Hunter(),
            246UL);
        long capSeamInsideTick = 0;
        capSeamInside.Target = NewTarget(
            247UL,
            capSeamInside.Mount.Point - capSeamInside.Outward * 0.10f +
            capSeamInside.Tangent * 9.05f,
            Vector3.Zero,
            visible: true,
            grabbable: false,
            radius: radius);
        Tick(capSeamInside, terrain, ref capSeamInsideTick);
        bool capSeamIntersectionAccepted =
            capSeamInside.TargetStatus == TentaclePlantTargetStatus.Chargeable &&
            capSeamInside.AttackCharge > 0f;

        TentaclePlantController capSeamOutside = NewPlant(
            TentaclePlantFactory.Hunter(),
            248UL);
        long capSeamOutsideTick = 0;
        capSeamOutside.Target = NewTarget(
            249UL,
            capSeamOutside.Mount.Point - capSeamOutside.Outward * 0.15f +
            capSeamOutside.Tangent * 9.15f,
            Vector3.Zero,
            visible: true,
            grabbable: false,
            radius: radius);
        Tick(capSeamOutside, terrain, ref capSeamOutsideTick);
        bool capSeamDisjointRejected =
            capSeamOutside.TargetStatus == TentaclePlantTargetStatus.OutOfRange &&
            Near(capSeamOutside.AttackCharge, 0f);

        TentaclePlantController behind = NewPlant(
            TentaclePlantFactory.Hunter(),
            242UL);
        long behindTick = 0;
        behind.Target = NewTarget(
            243UL,
            behind.Mount.Point - behind.Outward * (radius + 0.001f) +
            behind.Tangent * 2f,
            Vector3.Zero,
            visible: true,
            grabbable: false,
            radius: radius);
        Tick(behind, terrain, ref behindTick);
        bool fullyBehindRejected =
            behind.TargetStatus == TentaclePlantTargetStatus.BehindMount &&
            Near(behind.AttackCharge, 0f);

        TentaclePlantController outside = NewPlant(
            TentaclePlantFactory.Hunter(),
            244UL);
        long outsideTick = 0;
        outside.Target = NewTarget(
            245UL,
            outside.Root.Pos + outside.Outward *
            (outside.Params.Length + radius + 0.001f),
            Vector3.Zero,
            visible: true,
            grabbable: false,
            radius: radius);
        Tick(outside, terrain, ref outsideTick);
        bool fullyOutsideRejected =
            outside.TargetStatus == TentaclePlantTargetStatus.OutOfRange &&
            Near(outside.AttackCharge, 0f);

        bool ok = rotatedEdgeHits && planeSurfaceIntersects &&
                  capSeamIntersectionAccepted && capSeamDisjointRejected &&
                  fullyBehindRejected && fullyOutsideRejected;
        return (
            ok,
            $"edgeExcess={minimumEdgeExcess:F4}m windup/strike=" +
            $"{latestWindup}/{latestStrike} rotated={rotatedEdgeHits} " +
            $"plane={planeSurfaceIntersects} seam=" +
            $"{capSeamIntersectionAccepted}/{capSeamDisjointRejected} " +
            $"behind={fullyBehindRejected} " +
            $"outside={fullyOutsideRejected}");
    }

    private static (bool, string) CheckGraspAndHostContract()
    {
        var terrain = new OpenTerrain();
        TentaclePlantController plant = NewPlant(TentaclePlantFactory.Original(), 77UL);
        long tick = 0;
        for (int i = 0; i < 8; i++)
        {
            Tick(plant, terrain, ref tick);
        }

        ulong id = 0xABCUL;
        plant.Target = NewTarget(
            id,
            plant.Hand.Pos,
            plant.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(plant, terrain, ref tick);
        bool captured = plant.HeldTargetId == id &&
                        plant.TargetEffect.TargetId == id &&
                        plant.TargetEffect.CaptureStarted &&
                        plant.TargetEffect.Held &&
                        plant.TargetStatus == TentaclePlantTargetStatus.Held &&
                        plant.Phase == TentaclePlantPhase.Holding &&
                        plant.PhaseTick == 0;
        bool captureKeepsFullExtension = Near(plant.Extension, 1f);
        bool holdingPhaseAge = true;

        int consumeTick = -1;
        int consumeEvents = 0;
        int ticksSinceCapture = 0;
        int firstZeroTick = -1;
        bool positiveBeforeTick80 = true;
        bool withinConsumeAtZero = false;
        for (int i = 1; i <= plant.Params.RetractTicks + plant.Params.ConsumeForceTicks + 5; i++)
        {
            Vector3 heldPosition = plant.Hand.Pos;
            plant.Target = NewTarget(
                id,
                heldPosition,
                plant.Hand.Vel,
                visible: false,
                grabbable: true,
                radius: 0.20f);
            Tick(plant, terrain, ref tick);
            ticksSinceCapture = i;
            holdingPhaseAge &= plant.Phase == TentaclePlantPhase.Holding &&
                               plant.PhaseTick == i;
            if (i < plant.Params.RetractTicks)
            {
                positiveBeforeTick80 &= plant.Extension > 0f;
            }
            if (firstZeroTick < 0 && Near(plant.Extension, 0f))
            {
                firstZeroTick = i;
                withinConsumeAtZero = plant.Hand.Pos.DistanceTo(plant.Root.Pos) <=
                                      plant.Params.ConsumeDistance + 1e-4f;
            }
            if (plant.TargetEffect.ConsumeRequested)
            {
                consumeEvents++;
                consumeTick = consumeTick < 0 ? i : consumeTick;
            }
        }
        int expectedZeroTick = plant.Params.RetractTicks;
        int latestConsumeTick = expectedZeroTick +
                                plant.Params.ConsumeForceTicks - 1;
        bool retract = captureKeepsFullExtension &&
                       positiveBeforeTick80 &&
                       firstZeroTick == expectedZeroTick &&
                       withinConsumeAtZero &&
                       consumeTick >= expectedZeroTick &&
                       consumeTick <= latestConsumeTick &&
                       consumeEvents == 1;

        plant.ReleaseHeldTarget();
        plant.Target = NewTarget(
            id,
            plant.Hand.Pos,
            plant.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(plant, terrain, ref tick);
        bool releaseEvent = plant.TargetEffect.Released &&
                            plant.TargetEffect.TargetId == id &&
                            plant.HeldTargetId is null;
        bool suppressed = true;
        for (int i = 0; i < 6; i++)
        {
            plant.Target = NewTarget(
                id,
                plant.Hand.Pos,
                plant.Hand.Vel,
                visible: false,
                grabbable: true,
                radius: 0.20f);
            Tick(plant, terrain, ref tick);
            suppressed &= plant.HeldTargetId is null &&
                          !plant.TargetEffect.CaptureStarted;
        }
        plant.Target = NewTarget(
            id,
            plant.Root.Pos + plant.Outward * (plant.Params.Length + 2f),
            Vector3.Zero,
            visible: false,
            grabbable: true);
        Tick(plant, terrain, ref tick);
        bool recapturedAfterSeparation = false;
        for (int i = 0; i < 6 && !recapturedAfterSeparation; i++)
        {
            plant.Target = NewTarget(
                id,
                plant.Hand.Pos,
                plant.Hand.Vel,
                visible: false,
                grabbable: true,
                radius: 0.20f);
            Tick(plant, terrain, ref tick);
            recapturedAfterSeparation = plant.HeldTargetId == id &&
                                        plant.TargetEffect.CaptureStarted;
        }

        TentaclePlantController missing = NewPlant(
            TentaclePlantFactory.Original(),
            78UL);
        long missingTick = 0;
        Tick(missing, terrain, ref missingTick);
        missing.Target = NewTarget(
            55UL,
            missing.Hand.Pos,
            missing.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(missing, terrain, ref missingTick);
        missing.Target = null;
        Tick(missing, terrain, ref missingTick);
        bool missingRelease = missing.TargetEffect.Released &&
                              missing.TargetEffect.TargetId == 55UL &&
                              missing.HeldTargetId is null;

        TentaclePlantController changed = NewPlant(
            TentaclePlantFactory.Original(),
            79UL);
        long changedTick = 0;
        Tick(changed, terrain, ref changedTick);
        changed.Target = NewTarget(
            66UL,
            changed.Hand.Pos,
            changed.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(changed, terrain, ref changedTick);
        changed.Target = NewTarget(
            67UL,
            changed.Hand.Pos,
            changed.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(changed, terrain, ref changedTick);
        bool changedRelease = changed.TargetEffect.Released &&
                              changed.TargetEffect.TargetId == 66UL &&
                              changed.HeldTargetId is null;

        TentaclePlantController fast = NewPlant(
            TentaclePlantFactory.Original(),
            80UL);
        long fastTick = 0;
        fast.Target = NewTarget(
            80UL,
            fast.Hand.Pos,
            fast.Hand.Vel + Vector3.Right * 0.20f,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(fast, terrain, ref fastTick);
        bool highSpeedRejected = fast.HeldTargetId is null &&
                                 !fast.TargetEffect.CaptureStarted;

        TentaclePlantController lightTarget = NewPlant(
            TentaclePlantFactory.Original(),
            81UL);
        TentaclePlantController heavyTarget = NewPlant(
            TentaclePlantFactory.Original(),
            81UL);
        long lightTick = 0;
        long heavyTick = 0;
        lightTarget.Target = NewTarget(
            81UL,
            lightTarget.Hand.Pos,
            lightTarget.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f,
            mass: 0.10f);
        heavyTarget.Target = NewTarget(
            81UL,
            heavyTarget.Hand.Pos,
            heavyTarget.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f,
            mass: 10f);
        Tick(lightTarget, terrain, ref lightTick);
        Tick(heavyTarget, terrain, ref heavyTick);
        lightTarget.Target = NewTarget(
            81UL,
            lightTarget.Hand.Pos + lightTarget.Tangent * 0.20f,
            lightTarget.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f,
            mass: 0.10f);
        heavyTarget.Target = NewTarget(
            81UL,
            heavyTarget.Hand.Pos + heavyTarget.Tangent * 0.20f,
            heavyTarget.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f,
            mass: 10f);
        Tick(lightTarget, terrain, ref lightTick);
        Tick(heavyTarget, terrain, ref heavyTick);
        float lightPull = lightTarget.TargetEffect.PositionCorrection.Length();
        float heavyPull = heavyTarget.TargetEffect.PositionCorrection.Length();
        float lightVelocityPull = lightTarget.TargetEffect.VelocityDelta.Dot(
            lightTarget.TargetEffect.PositionCorrection.Normalized());
        float heavyVelocityPull = heavyTarget.TargetEffect.VelocityDelta.Dot(
            heavyTarget.TargetEffect.PositionCorrection.Normalized());
        bool rigidPosition = Math.Abs(lightPull - heavyPull) <= 0.01f;
        bool massWeighted = lightVelocityPull > heavyVelocityPull + 0.05f;

        bool ok = captured && retract && holdingPhaseAge &&
                  releaseEvent && suppressed &&
                  recapturedAfterSeparation && missingRelease && changedRelease &&
                  highSpeedRejected && rigidPosition && massWeighted;
        return (
            ok,
            $"captured={captured} retract={retract} phaseAge={holdingPhaseAge} " +
            $"zeroTick={firstZeroTick} " +
            $"consumeTick={consumeTick}<={latestConsumeTick} " +
            $"consumeEvents={consumeEvents} release={releaseEvent} suppressed={suppressed} " +
            $"recaptured={recapturedAfterSeparation} missing={missingRelease} changed={changedRelease} " +
            $"fastRejected={highSpeedRejected} rigidPull={lightPull:F5}/{heavyPull:F5} " +
            $"massVelocity={lightVelocityPull:F5}/{heavyVelocityPull:F5} " +
            $"loop={ticksSinceCapture}");
    }

    private static (bool, string) CheckClosedHostCarryLoop()
    {
        float[] masses = { 0.25f, 1f, 4f, 10f };
        var terrain = new OpenTerrain();
        bool allCaptured = true;
        bool allConsumed = true;
        bool finite = true;
        float maxPreEffectGap = 0f;
        float maxPostEffectGap = 0f;
        float maxTargetSpeed = 0f;
        float maxConsumeDistance = 0f;
        float maxInternalSpeed = 0f;
        float maxLinkRatio = 0f;
        float maxTetherExcess = 0f;
        int latestConsume = 0;
        int peakQueries = 0;
        bool handTipSynced = true;
        var massDetails = new List<string>();

        for (int massIndex = 0; massIndex < masses.Length; massIndex++)
        {
            float mass = masses[massIndex];
            TentaclePlantController plant = NewPlant(
                TentaclePlantFactory.Original(),
                0xCA770UL + (ulong)massIndex);
            long tick = 0;
            Tick(plant, terrain, ref tick);

            ulong id = 0xCA880UL + (ulong)massIndex;
            Vector3 targetPosition = plant.Hand.Pos;
            Vector3 targetVelocity = plant.Hand.Vel;
            bool captured = false;
            bool consumed = false;
            int consumeTick = -1;
            float massMaxPreGap = 0f;
            float massMaxPostGap = 0f;
            float massMaxSpeed = 0f;
            int budget = plant.Params.RetractTicks +
                         plant.Params.ConsumeForceTicks + 25;
            for (int i = 0; i < budget; i++)
            {
                // 宿主先积分自己的权威实体，再把同一 tick 的纯值快照交给内核。
                targetPosition += targetVelocity;
                plant.Target = NewTarget(
                    id,
                    targetPosition,
                    targetVelocity,
                    visible: false,
                    grabbable: true,
                    radius: 0.20f,
                    mass: mass);
                Tick(plant, terrain, ref tick);

                handTipSynced &= plant.Hand.Pos.DistanceTo(plant.Chain.Tip.Pos) <= 1e-5f;
                float maximumReach = plant.Params.Length * 2f * plant.Extension;
                maxTetherExcess = Math.Max(
                    maxTetherExcess,
                    plant.Hand.Pos.DistanceTo(plant.Root.Pos) - maximumReach);
                maxInternalSpeed = Math.Max(
                    maxInternalSpeed,
                    Math.Max(plant.Hand.Vel.Length(), plant.Chain.Tip.Vel.Length()));
                peakQueries = Math.Max(peakQueries, plant.PeakQueryCount);
                float effectiveLength = plant.Params.Length * Mathf.Lerp(
                    plant.Params.RetractedLengthFraction,
                    1f,
                    plant.Extension);
                float linkLength = effectiveLength / plant.Segments.Count;
                Vector3 previous = plant.Root.Pos;
                foreach (TentacleSegmentState segment in plant.Segments)
                {
                    maxInternalSpeed = Math.Max(maxInternalSpeed, segment.Vel.Length());
                    maxLinkRatio = Math.Max(
                        maxLinkRatio,
                        previous.DistanceTo(segment.Pos) / linkLength);
                    previous = segment.Pos;
                }

                TentaclePlantTargetEffect effect = plant.TargetEffect;
                captured |= effect.CaptureStarted;
                float preEffectGap = targetPosition.DistanceTo(plant.Hand.Pos);
                if (effect.TargetId == id && effect.Held)
                {
                    targetPosition += effect.PositionCorrection;
                    targetVelocity += effect.VelocityDelta;
                }

                finite &= IsFinite(plant) &&
                          Finite(targetPosition) &&
                          Finite(targetVelocity) &&
                          Finite(effect.PositionCorrection) &&
                          Finite(effect.VelocityDelta);
                float postEffectGap = targetPosition.DistanceTo(plant.Hand.Pos);
                maxPreEffectGap = Math.Max(maxPreEffectGap, preEffectGap);
                maxPostEffectGap = Math.Max(maxPostEffectGap, postEffectGap);
                maxTargetSpeed = Math.Max(maxTargetSpeed, targetVelocity.Length());
                massMaxPreGap = Math.Max(massMaxPreGap, preEffectGap);
                massMaxPostGap = Math.Max(massMaxPostGap, postEffectGap);
                massMaxSpeed = Math.Max(massMaxSpeed, targetVelocity.Length());
                if (effect.ConsumeRequested)
                {
                    consumed = true;
                    consumeTick = i + 1;
                    maxConsumeDistance = Math.Max(
                        maxConsumeDistance,
                        targetPosition.DistanceTo(plant.Root.Pos));
                    break;
                }
            }

            allCaptured &= captured;
            allConsumed &= consumed;
            latestConsume = Math.Max(latestConsume, consumeTick);
            massDetails.Add(
                $"m{mass:G}:{massMaxPreGap:F3}>{massMaxPostGap:F5}m/" +
                $"{massMaxSpeed:F3}v/t{consumeTick}");
        }

        TentaclePlantParams original = TentaclePlantFactory.Original();
        float perTickJointBudget = original.SegmentVelocityCap + 0.20f + 0.01f;
        bool bounded = maxPreEffectGap <= perTickJointBudget &&
                       maxPostEffectGap <= 1e-4f &&
                       maxTargetSpeed <= perTickJointBudget &&
                       maxConsumeDistance <= original.ConsumeDistance + 1e-4f &&
                       maxInternalSpeed <= original.SegmentVelocityCap + 1e-4f &&
                       maxLinkRatio <= 1.25f &&
                       maxTetherExcess <= 1e-4f &&
                       handTipSynced &&
                       peakQueries <= 96;
        bool ok = allCaptured && allConsumed && finite && bounded;
        return (
            ok,
            $"captured={allCaptured} consumed={allConsumed} finite={finite} " +
            $"gap={maxPreEffectGap:F4}>{maxPostEffectGap:F6}m " +
            $"maxSpeed={maxTargetSpeed:F4}m/tick " +
            $"internal={maxInternalSpeed:F4}m/tick link={maxLinkRatio:F3}x " +
            $"tetherExcess={maxTetherExcess:F6}m " +
            $"synced={handTipSynced} queries={peakQueries} " +
            $"consumeDistance={maxConsumeDistance:F4}m latestTick={latestConsume} " +
            $"[{string.Join(",", massDetails)}]");
    }

    private static (bool, string) CheckCaptureLineOfSight()
    {
        var open = new OpenTerrain();
        TentaclePlantController blocked = NewPlant(
            TentaclePlantFactory.Original(),
            0x105UL);
        TentaclePlantController control = NewPlant(
            TentaclePlantFactory.Original(),
            0x105UL);
        long blockedTick = 0;
        long controlTick = 0;
        Vector3 attackTarget = blocked.Root.Pos + blocked.Outward * 5.0f;
        int primeTicks = blocked.Params.ChargeTicks + blocked.Params.LungeTicks;
        for (int i = 0; i < primeTicks; i++)
        {
            TentaclePlantTargetSnapshot target = NewTarget(
                0x106UL,
                attackTarget,
                Vector3.Zero,
                visible: true,
                grabbable: false);
            blocked.Target = target;
            control.Target = target;
            Tick(blocked, open, ref blockedTick);
            Tick(control, open, ref controlTick);
        }

        bool windowPrimed = blocked.CanGrab > 0f &&
                            control.CanGrab > 0f &&
                            SameKinematics(blocked, control);
        const float targetRadius = 0.10f;
        float nearX = blocked.Hand.Pos.X + 0.04f;
        float farX = nearX + 0.15f;
        var thinWall = new BoxTerrain(
            new Box(
                new Vector3(nearX, -20f, -20f),
                new Vector3(farX, 20f, 20f),
                0x107UL));
        Vector3 passiveTarget = new(
            farX + targetRadius + 0.01f,
            blocked.Hand.Pos.Y,
            blocked.Hand.Pos.Z);
        TentaclePlantTargetSnapshot candidate = NewTarget(
            0x108UL,
            passiveTarget,
            blocked.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: targetRadius);
        blocked.Target = candidate;
        control.Target = candidate;
        Tick(blocked, thinWall, ref blockedTick);
        Tick(control, open, ref controlTick);
        bool blockedRejected = blocked.HeldTargetId is null &&
                               !blocked.TargetEffect.CaptureStarted;
        bool openCaptured = control.HeldTargetId == candidate.StableId &&
                            control.TargetEffect.CaptureStarted;

        TentaclePlantController tolerance = NewPlant(
            TentaclePlantFactory.Original(),
            0x109UL);
        Vector3 volumeTarget = tolerance.Root.Pos + tolerance.Outward * 5f;
        var toleranceWall = new BoxTerrain(
            new Box(
                new Vector3(-20f, 4.85f, -20f),
                new Vector3(20f, 4.90f, 20f),
                0x10AUL));
        tolerance.Target = NewTarget(
            0x10BUL,
            volumeTarget,
            Vector3.Zero,
            visible: true,
            grabbable: false,
            radius: targetRadius);
        long toleranceTick = 0;
        Tick(tolerance, toleranceWall, ref toleranceTick);
        bool targetRadiusOnly = tolerance.TargetStatus ==
                                TentaclePlantTargetStatus.Occluded &&
                                Near(tolerance.AttackCharge, 0f);

        bool budget = blocked.TickQueryCount <= 96;
        bool ok = windowPrimed && blockedRejected && openCaptured &&
                  targetRadiusOnly && budget;
        return (
            ok,
            $"window={windowPrimed} blocked={blockedRejected} open={openCaptured} " +
            $"targetRadiusOnly={targetRadiusOnly} queries={blocked.TickQueryCount}");
    }

    private static (bool, string) CheckRoutingBacktrackAndQueries()
    {
        var obstacle = new BoxTerrain(
            new Box(
                new Vector3(-0.30f, 2.0f, -0.30f),
                new Vector3(0.30f, 3.6f, 0.30f),
                20UL));
        TentaclePlantController routing = NewPlant(
            TentaclePlantFactory.Original(),
            111UL);
        routing.Target = NewTarget(
            1UL,
            routing.Root.Pos + routing.Outward * 6f,
            Vector3.Zero,
            visible: false,
            grabbable: false);
        long routingTick = 0;
        int maxBends = 0;
        bool sawBend = false;
        float maxPen = 0f;
        for (int i = 0; i < 240; i++)
        {
            Tick(routing, obstacle, ref routingTick);
            maxBends = Math.Max(maxBends, routing.GuidePoints.Count);
            sawBend |= routing.GuidePoints.Count > 0;
            foreach (TentacleSegmentState segment in routing.Segments)
            {
                maxPen = Math.Max(maxPen, obstacle.PenetrationDepth(segment.Pos, segment.Radius));
            }
        }
        bool route = sawBend && maxBends <= 2 &&
                     maxPen <= 0.002f &&
                     routing.PeakQueryCount <= 96;

        var countedTerrain = new CountingOpenTerrain();
        TentaclePlantController counted = NewPlant(
            TentaclePlantFactory.Original(),
            112UL);
        long countedTick = 0;
        bool exactCount = true;
        for (int i = 0; i < 40; i++)
        {
            int before = countedTerrain.TotalQueries;
            Tick(counted, countedTerrain, ref countedTick);
            exactCount &= counted.TickQueryCount ==
                          countedTerrain.TotalQueries - before;
        }

        var blocker = new LinkBlockTerrain { Enabled = true };
        TentaclePlantController backtrack = NewPlant(
            TentaclePlantFactory.Original(),
            113UL);
        long backtrackTick = 0;
        blocker.Enabled = false;
        Tick(backtrack, blocker, ref backtrackTick);
        blocker.Enabled = true;
        int triggerTick = -1;
        bool early = false;
        for (int i = 1; i <= backtrack.Params.BlockedSuffixTicks; i++)
        {
            ArrangeBlockedPose(backtrack);
            Tick(backtrack, blocker, ref backtrackTick);
            if (backtrack.BacktrackFrom >= 0 && triggerTick < 0)
            {
                triggerTick = i;
            }
            if (i < backtrack.Params.BlockedSuffixTicks &&
                backtrack.BacktrackFrom >= 0)
            {
                early = true;
            }
        }
        blocker.Enabled = false;
        bool heldForWindow = backtrack.BacktrackFrom >= 0;
        int recoveryTicks = 0;
        for (int i = 1; i < backtrack.Params.RebuildTicks; i++)
        {
            ArrangeBlockedPose(backtrack);
            Tick(backtrack, blocker, ref backtrackTick);
            recoveryTicks++;
            heldForWindow &= backtrack.BacktrackFrom >= 0;
        }
        ArrangeBlockedPose(backtrack);
        Tick(backtrack, blocker, ref backtrackTick);
        recoveryTicks++;
        bool clearedOnBudget = backtrack.BacktrackFrom < 0;

        var narrow = new BoxTerrain(
            new Box(
                new Vector3(-0.36f, 3.2f, -0.60f),
                new Vector3(-0.15f, 5.2f, 0.60f),
                31UL),
            new Box(
                new Vector3(0.15f, 3.2f, -0.60f),
                new Vector3(0.36f, 5.2f, 0.60f),
                32UL));
        TentaclePlantController clearance = NewPlant(
            TentaclePlantFactory.Original(),
            114UL);
        clearance.Target = NewTarget(
            2UL,
            clearance.Root.Pos + clearance.Outward * 6f,
            Vector3.Zero,
            visible: false,
            grabbable: false);
        long clearanceTick = 0;
        bool narrowSawBend = false;
        bool narrowSawBacktrack = false;
        float narrowMaxPenetration = 0f;
        float narrowMaxTipForward = 0f;
        float narrowBestBendForward = 0f;
        float narrowBestBendLateral = 0f;
        for (int i = 0; i < 160; i++)
        {
            Tick(clearance, narrow, ref clearanceTick);
            narrowSawBend |= clearance.GuidePoints.Count > 0;
            narrowSawBacktrack |= clearance.BacktrackFrom >= 0;
            narrowMaxTipForward = Math.Max(
                narrowMaxTipForward,
                (clearance.Chain.Tip.Pos - clearance.Root.Pos).Dot(clearance.Outward));
            foreach (Vector3 bend in clearance.GuidePoints)
            {
                Vector3 offset = bend - clearance.Root.Pos;
                narrowBestBendForward = Math.Max(
                    narrowBestBendForward,
                    offset.Dot(clearance.Outward));
                narrowBestBendLateral = Math.Max(
                    narrowBestBendLateral,
                    Mathf.Max(
                        Mathf.Abs(offset.Dot(clearance.Tangent)),
                        Mathf.Abs(offset.Dot(clearance.Bitangent))));
            }
            foreach (TentacleSegmentState segment in clearance.Segments)
            {
                narrowMaxPenetration = Math.Max(
                    narrowMaxPenetration,
                    narrow.PenetrationDepth(segment.Pos, segment.Radius));
            }
        }
        bool sphereDetected = narrow.ClearanceProbeQueries > 0;
        bool narrowDetouredForward = narrowSawBend &&
            narrowBestBendForward > 3.2f &&
            narrowBestBendLateral > 0.60f;
        bool narrowStoppedAtEntrance =
            narrowMaxTipForward <=
            3.2f + clearance.Params.GuideClearanceRadius + 0.002f;
        bool narrowHandled = narrowMaxPenetration <= 0.002f &&
            (narrowDetouredForward || narrowStoppedAtEntrance);

        TentaclePlantController query8 = NewPlant(
            TentaclePlantFactory.Original(),
            115UL);
        TentaclePlantParams sixteenParams = TentaclePlantFactory.Original();
        sixteenParams.Name = "tentacle-plant/test-16";
        sixteenParams.SegmentCount = 16;
        sixteenParams.RetractedLengthFraction = 0.35f;
        TentaclePlantController query16 = NewPlant(sixteenParams, 115UL);
        var queryTerrain = new OpenTerrain();
        long query8Tick = 0;
        long query16Tick = 0;
        for (int i = 0; i < 160; i++)
        {
            Tick(query8, queryTerrain, ref query8Tick);
            Tick(query16, queryTerrain, ref query16Tick);
        }
        bool queryGrowth = query8.PeakQueryCount <= 96 &&
                           query16.PeakQueryCount <=
                           Mathf.CeilToInt(query8.PeakQueryCount * 2.25f);

        bool timing = !early &&
                      triggerTick == backtrack.Params.BlockedSuffixTicks &&
                      heldForWindow &&
                      clearedOnBudget &&
                      recoveryTicks <= 60;
        bool ok = route && exactCount && timing && sphereDetected &&
                  narrowHandled && queryGrowth;
        return (
            ok,
            $"route={route} bends={maxBends} sawBend={sawBend} maxPen={maxPen:F6} " +
            $"peak={routing.PeakQueryCount} exactCount={exactCount} trigger={triggerTick} " +
            $"window={heldForWindow} recovery={recoveryTicks} clear={clearedOnBudget} " +
            $"sphereClearance={sphereDetected} narrowHandled={narrowHandled} " +
            $"narrow=bend:{narrowSawBend}/backtrack:{narrowSawBacktrack}/" +
            $"stopped:{narrowStoppedAtEntrance}/tip:{narrowMaxTipForward:F3}/" +
            $"bendProgress:{narrowBestBendForward:F3}/" +
            $"bendLateral:{narrowBestBendLateral:F3}/pen:{narrowMaxPenetration:F6} " +
            $"queries8/16=" +
            $"{query8.PeakQueryCount}/{query16.PeakQueryCount}");
    }

    private static (bool, string) CheckFarTargetGuideContinuity()
    {
        float[] distances = { 15.1f, 15.2f, 25f };
        var plants = new TentaclePlantController[distances.Length];
        var ticks = new long[distances.Length];
        var minimum = new float[distances.Length];
        var maximum = new float[distances.Length];
        Array.Fill(minimum, float.MaxValue);
        Array.Fill(maximum, float.MinValue);
        var terrain = new OpenTerrain();
        bool finite = true;
        int peakQueries = 0;
        float maxDivergence = 0f;

        for (int i = 0; i < plants.Length; i++)
        {
            plants[i] = NewPlant(TentaclePlantFactory.Original(), 0xFA12UL);
        }

        for (int frame = 0; frame < 240; frame++)
        {
            for (int i = 0; i < plants.Length; i++)
            {
                TentaclePlantController plant = plants[i];
                Vector3 direction = (plant.Tangent + plant.Outward * 0.2f).Normalized();
                plant.Target = NewTarget(
                    0xFA13UL,
                    plant.Root.Pos + direction * distances[i],
                    Vector3.Zero,
                    visible: false,
                    grabbable: false);
                Tick(plant, terrain, ref ticks[i]);
                float lateral = (plant.Chain.Tip.Pos - plant.Root.Pos).Dot(plant.Tangent);
                minimum[i] = Math.Min(minimum[i], lateral);
                maximum[i] = Math.Max(maximum[i], lateral);
                finite &= IsFinite(plant);
                peakQueries = Math.Max(peakQueries, plant.PeakQueryCount);
            }

            Vector3 reference = plants[0].Chain.Tip.Pos - plants[0].Root.Pos;
            for (int i = 1; i < plants.Length; i++)
            {
                Vector3 candidate = plants[i].Chain.Tip.Pos - plants[i].Root.Pos;
                maxDivergence = Math.Max(
                    maxDivergence,
                    reference.DistanceTo(candidate));
            }
        }

        float minSpan = float.MaxValue;
        float maxSpan = 0f;
        foreach (int index in new[] { 0, 1, 2 })
        {
            float span = maximum[index] - minimum[index];
            minSpan = Math.Min(minSpan, span);
            maxSpan = Math.Max(maxSpan, span);
        }
        bool continuous = minSpan >= plants[0].Params.Length * 0.75f &&
                          minSpan >= maxSpan * 0.95f &&
                          maxDivergence <= 0.01f;

        TentaclePlantParams exhaustedParams = TentaclePlantFactory.Original();
        exhaustedParams.Name = "tentacle-plant/test-budget-exhausted";
        exhaustedParams.RoutingQueryBudget = 1;
        TentaclePlantController exhausted = NewPlant(exhaustedParams, 0xFA14UL);
        long exhaustedTick = 0;
        for (int i = 0; i < 240; i++)
        {
            exhausted.Target = NewTarget(
                0xFA15UL,
                exhausted.Root.Pos + exhausted.Tangent * 25f,
                Vector3.Zero,
                visible: false,
                grabbable: false);
            Tick(exhausted, terrain, ref exhaustedTick);
            finite &= IsFinite(exhausted);
        }
        float exhaustedReach = exhausted.Chain.Tip.Pos.DistanceTo(exhausted.Root.Pos);
        bool conservativeFallback = exhaustedReach <= 1.50f;

        bool ok = finite && continuous && conservativeFallback && peakQueries <= 96;
        return (
            ok,
            $"finite={finite} span={minimum[0]:F3}..{maximum[0]:F3}/" +
            $"{minimum[1]:F3}..{maximum[1]:F3}/" +
            $"{minimum[2]:F3}..{maximum[2]:F3} " +
            $"min/max={minSpan:F3}/{maxSpan:F3} divergence={maxDivergence:F5} " +
            $"budgetFallback={conservativeFallback}/{exhaustedReach:F3}m " +
            $"peakQueries={peakQueries}");
    }

    private static (bool, string) CheckSpawnAndRemountClearance()
    {
        const float slabNear = 2.12f;
        const float slabFar = 2.37f;
        (Vector3 Outward, Vector3 Tangent)[] frames =
        {
            (Vector3.Up, Vector3.Right),
            (Vector3.Right, Vector3.Back),
            (Vector3.Down, Vector3.Left),
        };
        TentaclePlantParams[] presets =
        {
            TentaclePlantFactory.Original(),
            TentaclePlantFactory.Hunter(),
        };

        var openTerrain = new OpenTerrain();
        var openMount = new TentaclePlantMount(
            Vector3.Zero,
            Vector3.Up,
            Vector3.Right,
            0x51A8UL);
        TentaclePlantController openPlant = TentaclePlantFactory.CreateController(
            openMount,
            TentaclePlantFactory.Original(),
            0x51A9UL);
        long openTick = 0;
        Tick(openPlant, openTerrain, ref openTick);
        float expectedSpawn = openPlant.Params.Length * openPlant.Params.SpawnExtension;
        Vector3 openOffset = openPlant.Chain.Tip.Pos - openPlant.Root.Pos;
        float openForward = openOffset.Dot(openPlant.Outward);
        bool openExpanded = Math.Abs(openForward - expectedSpawn) <= 1e-4f &&
                            (openOffset - openPlant.Outward * openForward).Length() <= 1e-4f;

        var rotatedMount = new TentaclePlantMount(
            new Vector3(1f, 2f, 3f),
            Vector3.Right,
            Vector3.Up,
            0x51AAUL);
        openPlant.Remount(rotatedMount);
        Tick(openPlant, openTerrain, ref openTick);
        Vector3 rotatedOffset = openPlant.Chain.Tip.Pos - openPlant.Root.Pos;
        float rotatedForward = rotatedOffset.Dot(openPlant.Outward);
        openExpanded &= openPlant.Outward == Vector3.Right &&
                        Math.Abs(rotatedForward - expectedSpawn) <= 1e-4f &&
                        (rotatedOffset - openPlant.Outward * rotatedForward).Length() <= 1e-4f;

        bool immediateFolded = true;
        bool finite = true;
        bool noFarSide = true;
        bool noCrossing = true;
        bool clear = true;
        float maxPenetration = 0f;
        int maxBacktrackRun = 0;
        int peakQueries = 0;

        foreach (TentaclePlantParams parameters in presets)
        {
            foreach ((Vector3 outward, Vector3 tangent) in frames)
            {
                var mount = new TentaclePlantMount(
                    Vector3.Zero,
                    outward,
                    tangent,
                    0x51ABUL);
                var terrain = new BoxTerrain(
                    SlabForOutward(outward, slabNear, slabFar, 0x51ACUL));
                TentaclePlantController plant = TentaclePlantFactory.CreateController(
                    mount,
                    parameters,
                    0x51ADUL);
                long tick = 0;
                int backtrackRun = 0;

                for (int phase = 0; phase < 2; phase++)
                {
                    if (phase == 1)
                    {
                        mount = new TentaclePlantMount(
                            tangent * 0.5f,
                            outward,
                            tangent,
                            0x51AEUL);
                        plant.Remount(mount);
                        backtrackRun = 0;
                    }

                    Vector3 folded = mount.Point + outward * plant.Params.RootRadius;
                    immediateFolded &= plant.Hand.Pos.DistanceTo(folded) <= 1e-5f &&
                                       plant.Hand.Pos == plant.Hand.LastPos &&
                                       plant.Hand.Vel == Vector3.Zero;
                    foreach (TentacleSegmentState segment in plant.Segments)
                    {
                        immediateFolded &= segment.Pos.DistanceTo(folded) <= 1e-5f &&
                                           segment.Pos == segment.LastPos &&
                                           segment.Vel == Vector3.Zero;
                    }

                    for (int frame = 0; frame < 2000; frame++)
                    {
                        Tick(plant, terrain, ref tick);
                        finite &= IsFinite(plant);
                        peakQueries = Math.Max(peakQueries, plant.PeakQueryCount);
                        backtrackRun = plant.BacktrackFrom >= 0
                            ? backtrackRun + 1
                            : 0;
                        maxBacktrackRun = Math.Max(maxBacktrackRun, backtrackRun);

                        Vector3 previous = mount.Point + outward * plant.Params.RootRadius;
                        foreach (TentacleSegmentState segment in plant.Segments)
                        {
                            float forward = (segment.Pos - mount.Point).Dot(outward);
                            noFarSide &= forward <= slabFar + segment.Radius + 1e-4f;
                            float penetration = terrain.PenetrationDepth(
                                segment.Pos,
                                segment.Radius);
                            maxPenetration = Math.Max(maxPenetration, penetration);
                            clear &= penetration <= 0.002f;
                            if (terrain.Raycast(previous, segment.Pos, out TerrainHit hit))
                            {
                                float endpointTolerance = segment.Radius + 0.002f;
                                noCrossing &= hit.Point.DistanceSquaredTo(segment.Pos) <=
                                              endpointTolerance * endpointTolerance;
                            }
                            previous = segment.Pos;
                        }
                    }
                }
            }
        }

        bool recovered = maxBacktrackRun <= 60;
        bool ok = openExpanded && immediateFolded && finite && noFarSide &&
                  noCrossing && clear && recovered && peakQueries <= 96;
        return (
            ok,
            $"openExpanded={openExpanded} folded={immediateFolded} " +
            $"finite={finite} farSide={!noFarSide} " +
            $"crossing={!noCrossing} clear={clear} maxPen={maxPenetration:F6}m " +
            $"backtrackRun={maxBacktrackRun} peakQueries={peakQueries}");
    }

    private static (bool, string) CheckLifecycle()
    {
        var terrain = new OpenTerrain();
        TentaclePlantController plant = NewPlant(
            TentaclePlantFactory.Original(),
            222UL);
        long tick = 0;
        for (int i = 0; i < 20; i++)
        {
            Tick(plant, terrain, ref tick);
        }
        plant.Target = NewTarget(
            500UL,
            plant.Root.Pos + plant.Outward * 4f,
            Vector3.Zero,
            visible: true,
            grabbable: false);
        for (int i = 0; i < 50; i++)
        {
            Tick(plant, terrain, ref tick);
        }

        Vector3 rootBefore = plant.Root.Pos;
        Vector3 handBefore = plant.Hand.Pos;
        Vector3 handLastBefore = plant.Hand.LastPos;
        Vector3 firstBefore = plant.Segments[0].Pos;
        Vector3 firstLastBefore = plant.Segments[0].LastPos;
        Vector3 wanderBefore = plant.WanderGoal;
        Vector3 targetBefore = plant.Target!.Value.Position;
        Vector3[] guideBefore = Copy(plant.GuidePoints);
        Vector3 delta = new(17f, -4f, 9f);
        plant.Shift(delta);
        bool shifted = plant.Root.Pos == rootBefore + delta &&
                       plant.Root.LastPos == rootBefore + delta &&
                       plant.Hand.Pos == handBefore + delta &&
                       plant.Hand.LastPos == handLastBefore + delta &&
                       plant.Segments[0].Pos == firstBefore + delta &&
                       plant.Segments[0].LastPos == firstLastBefore + delta &&
                       plant.WanderGoal == wanderBefore + delta &&
                       plant.Target!.Value.Position == targetBefore + delta &&
                       plant.Mount.Point == delta;
        for (int i = 0; i < guideBefore.Length; i++)
        {
            shifted &= plant.GuidePoints[i] == guideBefore[i] + delta;
        }

        var remount = new TentaclePlantMount(
            new Vector3(-3f, 8f, 2f),
            Vector3.Left,
            Vector3.Up,
            999UL);
        plant.Remount(remount);
        Vector3 expectedRoot = remount.Point +
                               Vector3.Left * plant.Params.RootSurfaceOffset;
        bool remounted = plant.Root.Pos == expectedRoot &&
                         plant.Root.Pos == plant.Root.LastPos &&
                         plant.Hand.Pos == plant.Hand.LastPos &&
                         plant.Outward == Vector3.Left &&
                         plant.Mount.ColliderId == 999UL &&
                         plant.Target is null &&
                         plant.HeldTargetId is null &&
                         plant.AttackSerial == 0 &&
                         Near(plant.AttackCharge, 0f) &&
                         Near(plant.CanGrab, 0f) &&
                         Near(plant.Extension, 1f) &&
                         plant.BacktrackFrom < 0 &&
                         plant.Phase == TentaclePlantPhase.Wandering &&
                         plant.PeakQueryCount == 0;
        bool interpolationReset = true;
        foreach (TentacleSegmentState segment in plant.Segments)
        {
            interpolationReset &= segment.Pos == segment.LastPos &&
                                  Near(segment.Vel.Length(), 0f);
        }

        TentaclePlantController held = NewPlant(
            TentaclePlantFactory.Original(),
            333UL);
        long heldTick = 0;
        for (int i = 0; i < 25; i++)
        {
            Tick(held, terrain, ref heldTick);
        }
        held.Target = NewTarget(
            333UL,
            held.Hand.Pos,
            held.Hand.Vel,
            visible: false,
            grabbable: true,
            radius: 0.20f);
        Tick(held, terrain, ref heldTick);
        bool wasHeld = held.HeldTargetId == 333UL;
        held.Remount(remount);
        bool phaseReset = held.Phase == TentaclePlantPhase.Wandering &&
                          held.PhaseTick == 0;
        TentaclePlantController fresh = TentaclePlantFactory.CreateController(
            remount,
            TentaclePlantFactory.Original(),
            333UL);
        long freshTick = 0;
        bool randomAgeReset = true;
        bool remountReleased = false;
        for (int i = 0; i < 30; i++)
        {
            Tick(held, terrain, ref heldTick);
            Tick(fresh, terrain, ref freshTick);
            if (i == 0)
            {
                remountReleased = held.TargetEffect.Released &&
                                  held.TargetEffect.TargetId == 333UL;
            }
            randomAgeReset &= SameKinematics(held, fresh);
        }

        bool ok = shifted && remounted && interpolationReset &&
                  wasHeld && phaseReset && remountReleased && randomAgeReset;
        return (
            ok,
            $"shift={shifted} remount={remounted} interpolation={interpolationReset} " +
            $"held={wasHeld} phaseReset={phaseReset} release={remountReleased} " +
            $"randomReset={randomAgeReset} root={plant.Root.Pos} " +
            $"frame={plant.Outward}/{plant.Tangent}/{plant.Bitangent}");
    }

    private readonly record struct DeterminismResult(
        ulong Hash,
        bool Finite,
        int PeakQueries,
        float MaxPenetration,
        long Attacks);

    private static DeterminismResult RunDeterminism(ulong seed = 0x5EED1234UL)
    {
        var terrain = new PlaneTerrain(Vector3.Zero, Vector3.Up);
        TentaclePlantController plant = NewPlant(
            TentaclePlantFactory.Original(),
            seed);
        var hasher = new DeterminismHasher();
        long tick = 0;
        float maxPenetration = 0f;
        for (int i = 1; i <= 700; i++)
        {
            if (i >= 100 && i < 330)
            {
                float phase = (i - 100) * 0.035f;
                Vector3 position = plant.Root.Pos +
                                   plant.Outward * 5.2f +
                                   plant.Tangent * (Mathf.Sin(phase) * 0.8f) +
                                   plant.Bitangent * (Mathf.Cos(phase * 0.7f) * 0.55f);
                Vector3 velocity = plant.Tangent * (Mathf.Cos(phase) * 0.028f);
                plant.Target = NewTarget(
                    42UL,
                    position,
                    velocity,
                    visible: true,
                    grabbable: false);
            }
            else
            {
                plant.Target = null;
            }
            Tick(plant, terrain, ref tick);
            FoldPlant(hasher, plant);
            foreach (TentacleSegmentState segment in plant.Segments)
            {
                maxPenetration = Math.Max(
                    maxPenetration,
                    terrain.PenetrationDepth(segment.Pos, segment.Radius));
            }
        }
        return new DeterminismResult(
            hasher.Value,
            IsFinite(plant),
            plant.PeakQueryCount,
            maxPenetration,
            plant.AttackSerial);
    }

    private static void FoldPlant(
        DeterminismHasher hasher,
        TentaclePlantController plant)
    {
        hasher.FoldBody(plant.Body);
        foreach (TentacleSegmentState segment in plant.Segments)
        {
            hasher.Fold(segment.Pos);
            hasher.Fold(segment.Vel);
            hasher.Fold(segment.ContactNormal);
            hasher.Fold(segment.TerrainContact);
        }
        hasher.Fold(plant.WanderGoal);
        hasher.Fold(plant.AttackCharge);
        hasher.Fold(plant.CanGrab);
        hasher.Fold(plant.Extension);
        hasher.Fold((int)plant.Phase);
        hasher.Fold(plant.PhaseTick);
        hasher.Fold(unchecked((ulong)plant.AttackSerial));
        hasher.Fold(plant.HeldTargetId is not null);
        if (plant.HeldTargetId is { } held)
        {
            hasher.Fold(held);
        }
        hasher.Fold(plant.TargetEffect.TargetId);
        hasher.Fold(plant.TargetEffect.CaptureStarted);
        hasher.Fold(plant.TargetEffect.Held);
        hasher.Fold(plant.TargetEffect.Released);
        hasher.Fold(plant.TargetEffect.ConsumeRequested);
        hasher.Fold(plant.TargetEffect.PositionCorrection);
        hasher.Fold(plant.TargetEffect.VelocityDelta);
        hasher.Fold(plant.BacktrackFrom);
        hasher.Fold(plant.TickQueryCount);
        hasher.Fold(plant.GuidePoints.Count);
        foreach (Vector3 point in plant.GuidePoints)
        {
            hasher.Fold(point);
        }
    }

    private static TentaclePlantController NewPlant(
        TentaclePlantParams parameters,
        ulong seed) =>
        TentaclePlantFactory.CreateController(
            new TentaclePlantMount(
                Vector3.Zero,
                Vector3.Up,
                Vector3.Right,
                1UL),
            parameters,
            seed);

    private static Box SlabForOutward(
        Vector3 outward,
        float near,
        float far,
        ulong id)
    {
        if (outward.Dot(Vector3.Up) > 0.99f)
        {
            return new Box(
                new Vector3(-20f, near, -20f),
                new Vector3(20f, far, 20f),
                id);
        }
        if (outward.Dot(Vector3.Down) > 0.99f)
        {
            return new Box(
                new Vector3(-20f, -far, -20f),
                new Vector3(20f, -near, 20f),
                id);
        }
        if (outward.Dot(Vector3.Right) > 0.99f)
        {
            return new Box(
                new Vector3(near, -20f, -20f),
                new Vector3(far, 20f, 20f),
                id);
        }
        throw new ArgumentException("Smoke slab only supports the three tested mount axes.");
    }

    private static TentaclePlantTargetSnapshot NewTarget(
        ulong id,
        Vector3 position,
        Vector3 velocity,
        bool visible,
        bool grabbable,
        float radius = 0.10f,
        float mass = 1f) =>
        new(
            id,
            position,
            velocity,
            radius,
            mass,
            visible,
            grabbable);

    private static void Tick(
        TentaclePlantController plant,
        ITerrainQuery terrain,
        ref long tick)
    {
        tick++;
        plant.Tick(new TickContext(NoGravity, terrain, tick));
    }

    private static void ArrangeBlockedPose(TentaclePlantController plant)
    {
        const float spacing = 0.70f;
        for (int i = 0; i < plant.Segments.Count; i++)
        {
            TentacleSegmentState segment = plant.Segments[i];
            Vector3 position = plant.Root.Pos + plant.Outward * (spacing * (i + 1));
            segment.Pos = position;
            segment.LastPos = position;
            segment.Vel = Vector3.Zero;
        }
        plant.Hand.Pos = plant.Chain.Tip.Pos;
        plant.Hand.LastPos = plant.Hand.Pos;
        plant.Hand.Vel = Vector3.Zero;
    }

    private static bool SameKinematics(
        TentaclePlantController a,
        TentaclePlantController b)
    {
        if (a.Root.Pos != b.Root.Pos ||
            a.Root.LastPos != b.Root.LastPos ||
            a.Root.Vel != b.Root.Vel ||
            a.Hand.Pos != b.Hand.Pos ||
            a.Hand.LastPos != b.Hand.LastPos ||
            a.Hand.Vel != b.Hand.Vel ||
            a.WanderGoal != b.WanderGoal ||
            a.Phase != b.Phase ||
            a.PhaseTick != b.PhaseTick ||
            a.AttackCharge != b.AttackCharge ||
            a.Extension != b.Extension ||
            a.Segments.Count != b.Segments.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Segments.Count; i++)
        {
            TentacleSegmentState left = a.Segments[i];
            TentacleSegmentState right = b.Segments[i];
            if (left.Pos != right.Pos ||
                left.LastPos != right.LastPos ||
                left.Vel != right.Vel)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsFinite(TentaclePlantController plant)
    {
        if (!Finite(plant.Root.Pos) || !Finite(plant.Root.Vel) ||
            !Finite(plant.Hand.Pos) || !Finite(plant.Hand.Vel) ||
            !Finite(plant.WanderGoal) || !Finite(plant.Outward) ||
            !Finite(plant.Tangent) || !Finite(plant.Bitangent))
        {
            return false;
        }
        foreach (TentacleSegmentState segment in plant.Segments)
        {
            if (!Finite(segment.Pos) || !Finite(segment.Vel) ||
                !Finite(segment.ContactNormal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool Near(float a, float b, float tolerance = 1e-5f) =>
        Math.Abs(a - b) <= tolerance;

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

    private static Vector3[] Copy(IReadOnlyList<Vector3> source)
    {
        var result = new Vector3[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            result[i] = source[i];
        }
        return result;
    }

    private class OpenTerrain : ITerrainQuery
    {
        public virtual bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            return false;
        }

        public virtual bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }

    private sealed class CountingOpenTerrain : OpenTerrain
    {
        public int TotalQueries { get; private set; }

        public override bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            TotalQueries++;
            return base.Raycast(from, to, out hit);
        }

        public override bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            TotalQueries++;
            return base.SpherePenetration(center, radius, out pushDir, out depth);
        }
    }

    private sealed class PlaneTerrain : ITerrainQuery
    {
        private readonly Vector3 _point;
        private readonly Vector3 _outward;

        public PlaneTerrain(Vector3 point, Vector3 outward)
        {
            _point = point;
            _outward = outward.Normalized();
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            float a = SignedDistance(from);
            float b = SignedDistance(to);
            if (a <= 0f)
            {
                hit = new TerrainHit(from, Vector3.Zero, 1UL);
                return true;
            }
            if (b > 0f || Near(a, b))
            {
                hit = default;
                return false;
            }
            float t = a / (a - b);
            hit = new TerrainHit(from.Lerp(to, t), _outward, 1UL);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            depth = radius - SignedDistance(center);
            if (depth > 0f)
            {
                pushDir = _outward;
                return true;
            }
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }

        public float PenetrationDepth(Vector3 center, float radius) =>
            Math.Max(0f, radius - SignedDistance(center));

        private float SignedDistance(Vector3 point) =>
            (point - _point).Dot(_outward);
    }

    private readonly record struct Box(Vector3 Min, Vector3 Max, ulong Id);

    private sealed class BoxTerrain : ITerrainQuery
    {
        private readonly Box[] _boxes;
        public int ClearanceProbeQueries { get; private set; }

        public BoxTerrain(params Box[] boxes)
        {
            _boxes = boxes;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            bool found = false;
            float bestT = float.MaxValue;
            TerrainHit best = default;
            foreach (Box box in _boxes)
            {
                if (RayBox(from, to, box, out float t, out Vector3 normal) &&
                    t < bestT)
                {
                    bestT = t;
                    best = new TerrainHit(from.Lerp(to, t), normal, box.Id);
                    found = true;
                }
            }
            hit = best;
            return found;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            if (radius >= 0.205f)
            {
                ClearanceProbeQueries++;
            }
            pushDir = Vector3.Zero;
            depth = 0f;
            foreach (Box box in _boxes)
            {
                if (SphereBox(center, radius, box, out Vector3 candidateDir, out float candidateDepth) &&
                    candidateDepth > depth)
                {
                    pushDir = candidateDir;
                    depth = candidateDepth;
                }
            }
            return depth > 0f;
        }

        public float PenetrationDepth(Vector3 center, float radius)
        {
            float best = 0f;
            foreach (Box box in _boxes)
            {
                if (SphereBox(center, radius, box, out _, out float depth))
                {
                    best = Math.Max(best, depth);
                }
            }
            return best;
        }

        private static bool RayBox(
            Vector3 from,
            Vector3 to,
            in Box box,
            out float entry,
            out Vector3 normal)
        {
            if (Inside(from, box))
            {
                entry = 0f;
                normal = Vector3.Zero;
                return true;
            }

            Vector3 direction = to - from;
            float tMin = 0f;
            float tMax = 1f;
            Vector3 entryNormal = Vector3.Zero;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = Component(from, axis);
                float delta = Component(direction, axis);
                float min = Component(box.Min, axis);
                float max = Component(box.Max, axis);
                if (Math.Abs(delta) <= 1e-8f)
                {
                    if (origin < min || origin > max)
                    {
                        entry = 0f;
                        normal = Vector3.Zero;
                        return false;
                    }
                    continue;
                }

                float near = (min - origin) / delta;
                float far = (max - origin) / delta;
                Vector3 nearNormal = Axis(axis) * -Math.Sign(delta);
                if (near > far)
                {
                    (near, far) = (far, near);
                }
                if (near > tMin)
                {
                    tMin = near;
                    entryNormal = nearNormal;
                }
                tMax = Math.Min(tMax, far);
                if (tMin > tMax)
                {
                    entry = 0f;
                    normal = Vector3.Zero;
                    return false;
                }
            }
            entry = tMin;
            normal = entryNormal;
            return entry >= 0f && entry <= 1f;
        }

        private static bool SphereBox(
            Vector3 center,
            float radius,
            in Box box,
            out Vector3 pushDir,
            out float depth)
        {
            Vector3 closest = new(
                Mathf.Clamp(center.X, box.Min.X, box.Max.X),
                Mathf.Clamp(center.Y, box.Min.Y, box.Max.Y),
                Mathf.Clamp(center.Z, box.Min.Z, box.Max.Z));
            Vector3 delta = center - closest;
            float distance = delta.Length();
            if (distance > 1e-7f)
            {
                depth = radius - distance;
                if (depth > 0f)
                {
                    pushDir = delta / distance;
                    return true;
                }
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }

            float left = center.X - box.Min.X;
            float right = box.Max.X - center.X;
            float down = center.Y - box.Min.Y;
            float up = box.Max.Y - center.Y;
            float back = center.Z - box.Min.Z;
            float forward = box.Max.Z - center.Z;
            depth = left;
            pushDir = Vector3.Left;
            if (right < depth) { depth = right; pushDir = Vector3.Right; }
            if (down < depth) { depth = down; pushDir = Vector3.Down; }
            if (up < depth) { depth = up; pushDir = Vector3.Up; }
            if (back < depth) { depth = back; pushDir = Vector3.Back; }
            if (forward < depth) { depth = forward; pushDir = Vector3.Forward; }
            depth += radius;
            return true;
        }

        private static bool Inside(Vector3 point, in Box box) =>
            point.X >= box.Min.X && point.X <= box.Max.X &&
            point.Y >= box.Min.Y && point.Y <= box.Max.Y &&
            point.Z >= box.Min.Z && point.Z <= box.Max.Z;

        private static float Component(Vector3 value, int axis) => axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z,
        };

        private static Vector3 Axis(int axis) => axis switch
        {
            0 => Vector3.Right,
            1 => Vector3.Up,
            _ => Vector3.Back,
        };
    }

    private sealed class LinkBlockTerrain : ITerrainQuery
    {
        public bool Enabled;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            float length = from.DistanceTo(to);
            if (Enabled && length >= 0.45f && length <= 1.05f)
            {
                Vector3 direction = (to - from).Normalized();
                Vector3 normal = direction.Cross(Vector3.Back);
                if (normal.LengthSquared() <= 1e-8f)
                {
                    normal = Vector3.Right;
                }
                hit = new TerrainHit(from.Lerp(to, 0.25f), normal.Normalized(), 88UL);
                return true;
            }
            hit = default;
            return false;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }
}
