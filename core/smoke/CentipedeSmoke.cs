using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core.Smoke;

/// <summary>
/// 蜈蚣内核的无引擎场景回归。与 Program 中既有蜥蜴门分开，避免新物种的装配、
/// 表面路径或步态基线反向污染 Lizard 的历史哈希。
/// </summary>
public static class CentipedeSmoke
{
    private const float TickDt = 0.025f;
    private const float GravityMps2 = 36f;
    private const float Epsilon = 1e-4f;
    private const int DeterminismTicks = 480;
    private const ulong ExpectedShortHash = 0x655A21496C00E86AUL;
    private const ulong ExpectedLongHash = 0x59CBCF993DF8ACD8UL;

    /// <summary>供 Program.Main 调用的单一入口。</summary>
    public static bool RunAll(out string message)
    {
        bool assemblyOk = CheckAssembly(out string assemblyMessage);
        bool deterministicOk = CheckDeterminism(out string deterministicMessage);
        bool leadSelectionOk = CheckExplicitLeadSelection(out string leadSelectionMessage);
        bool lifecycleOk = CheckLifecycle(out string lifecycleMessage);
        bool moveTargetOk = CheckMoveTarget(out string moveTargetMessage);
        bool embedOk = CheckEmbedRecovery(out string embedMessage);
        bool idleOk = CheckIdleHold(out string idleMessage);
        bool avoidanceOk = CheckSelfAvoidance(out string avoidanceMessage);
        bool legBarrierOk = CheckLegBarrierRecovery(out string legBarrierMessage);
        bool scalingOk = CheckFiniteAndQueryScaling(out string scalingMessage);
        bool terrainOk = CheckTerrainPrimitives(out string terrainMessage);
        bool courseOk = CheckCourseMotion(out string courseMessage);
        bool fixedLeadDescentOk = CheckFixedLeadDescent(out string fixedLeadDescentMessage);
        bool narrowWallOk = CheckNarrowWallTraverse(out string narrowWallMessage);
        message = $"assembly=({assemblyMessage}); det=({deterministicMessage}); " +
                  $"lead=({leadSelectionMessage}); lifecycle=({lifecycleMessage}); " +
                  $"target=({moveTargetMessage}); " +
                  $"embed=({embedMessage}); idle=({idleMessage}); " +
                  $"avoid=({avoidanceMessage}); leg-barrier=({legBarrierMessage}); " +
                  $"scale=({scalingMessage}); " +
                  $"terrain-primitives=({terrainMessage}); course-motion=({courseMessage}); " +
                  $"fixed-lead-descent=({fixedLeadDescentMessage}); " +
                  $"narrow-wall=({narrowWallMessage})";
        return assemblyOk && deterministicOk && leadSelectionOk && lifecycleOk
            && moveTargetOk && embedOk && idleOk && avoidanceOk && legBarrierOk
            && scalingOk && terrainOk && courseOk && fixedLeadDescentOk
            && narrowWallOk;
    }

    /// <summary>
    /// 2/5/18/32 节、逐节全字段覆写、质量加权连接和隔节防折叠支柱。这里还特意修改
    /// 装配后的源参数，确认控制器只持有出生快照而不会在运行时回读配置。
    /// </summary>
    private static bool CheckAssembly(out string message)
    {
        CentipedeParams two = CustomParams(2);
        CentipedeParams five = CustomParams(5);
        CentipedeParams eighteen = CentipedeFactory.Long();
        CentipedeParams thirtyTwo = CustomParams(32);

        bool counts = CheckTopology(two, 2)
            && CheckTopology(five, 5)
            && CheckTopology(eighteen, 18)
            && CheckTopology(thirtyTwo, 32);

        CentipedeSegmentParams[] resolved = five.ResolveSegments();
        CentipedeSegmentParams first = resolved[0];
        bool allOverrideFields = Near(first.Radius, 0.123f)
            && Near(first.Mass, 1.5f)
            && Near(first.LinkLengthToNext, 0.31f)
            && Near(first.BendStiffness, 0.8f)
            && Near(first.DriveWeight, 0.2f)
            && Near(first.AdhesionWeight, 1.4f)
            && first.LegPairs == 2
            && Near(first.LegLength, 0.44f)
            && Near(first.FootRadius, 0.03f)
            && Near(first.LegHuntSpeed, 0.12f)
            && Near(first.LegQuickness, 0.4f)
            && first.LegGripDelay == 5
            && Near(first.LegStride, 0.5f)
            && Near(first.LegLateral, 0.18f);

        var snapshotController = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), five);
        float bornRadius = snapshotController.Body.Chunks[0].Radius;
        five.EndRadius = 0.9f;
        five.BaseSegment.Mass = 9f;
        five.Overrides[0]!.Radius = 0.8f;
        bool birthSnapshot = Near(snapshotController.Body.Chunks[0].Radius, bornRadius)
            && Near(snapshotController.Body.Chunks[0].Mass, 1.5f);

        bool minRejected = false;
        try
        {
            _ = CustomParams(1).ResolveSegments();
        }
        catch (ArgumentOutOfRangeException)
        {
            minRejected = true;
        }

        bool unknownRejected = false;
        try
        {
            _ = CentipedeFactory.ByStableId("centipede/unknown");
        }
        catch (ArgumentException)
        {
            unknownRejected = true;
        }
        bool invalidLeadEndRejected = false;
        try
        {
            snapshotController.RequestedLeadEnd = (CentipedeLeadEnd)int.MaxValue;
        }
        catch (ArgumentOutOfRangeException)
        {
            invalidLeadEndRejected = true;
        }
        bool iterationProfiles = CentipedeFactory.Short().ConstraintIterations == 3
            && CentipedeFactory.Long().ConstraintIterations == 3
            && CentipedeFactory.Armored().ConstraintIterations == 6
            && CentipedeFactory.Ribbon().ConstraintIterations == 6
            && snapshotController.Body.ConstraintIterations == five.ConstraintIterations;

        message = $"2/5/18/32 topology={counts}, override15={allOverrideFields}, " +
                  $"iterations={iterationProfiles}, birthSnapshot={birthSnapshot}, " +
                  $"reject<2/unknown/lead={minRejected}/{unknownRejected}/{invalidLeadEndRejected}";
        return counts && allOverrideFields && iterationProfiles && birthSnapshot
            && minRejected && unknownRejected && invalidLeadEndRejected;
    }

    private static CentipedeParams CustomParams(int segmentCount)
    {
        return new CentipedeParams
        {
            StableId = $"centipede/smoke-{segmentCount}",
            SegmentCount = segmentCount,
            EndRadius = 0.1f,
            MiddleRadius = 0.16f,
            BaseSegment = new CentipedeSegmentParams
            {
                Radius = 0.12f,
                Mass = 0.4f,
                LinkLengthToNext = 0.24f,
                BendStiffness = 0.55f,
                DriveWeight = 1f,
                AdhesionWeight = 1f,
                LegPairs = 1,
                LegLength = 0.34f,
                FootRadius = 0.04f,
                LegHuntSpeed = 0.14f,
                LegQuickness = 0.6f,
                LegGripDelay = 3,
                LegStride = 0.3f,
                LegLateral = 0.21f,
            },
            Overrides =
            [
                new CentipedeSegmentOverride
                {
                    SegmentIndex = 0,
                    Radius = 0.123f,
                    Mass = 1.5f,
                    LinkLengthToNext = 0.31f,
                    BendStiffness = 0.8f,
                    DriveWeight = 0.2f,
                    AdhesionWeight = 1.4f,
                    LegPairs = 2,
                    LegLength = 0.44f,
                    FootRadius = 0.03f,
                    LegHuntSpeed = 0.12f,
                    LegQuickness = 0.4f,
                    LegGripDelay = 5,
                    LegStride = 0.5f,
                    LegLateral = 0.18f,
                },
                new CentipedeSegmentOverride
                {
                    SegmentIndex = Math.Min(1, segmentCount - 1),
                    Mass = 0.5f,
                    LinkLengthToNext = 0.22f,
                },
            ],
        };
    }

    private static bool CheckTopology(CentipedeParams parameters, int expectedCount)
    {
        CentipedeSegmentParams[] specs = parameters.ResolveSegments();
        var controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), parameters);
        Body body = controller.Body;
        if (specs.Length != expectedCount
            || body.Chunks.Count != expectedCount
            || body.Connections.Count != expectedCount - 1 + Math.Max(0, expectedCount - 2)
            || !body.EnablePostCollisionStructureRecovery)
        {
            return false;
        }

        for (int i = 0; i < expectedCount; i++)
        {
            if (!Near(body.Chunks[i].Radius, specs[i].Radius)
                || !Near(body.Chunks[i].Mass, specs[i].Mass))
            {
                return false;
            }
        }

        for (int i = 0; i + 1 < expectedCount; i++)
        {
            ChunkConnection adjacent = body.Connections[i];
            float expectedWeightA = specs[i + 1].Mass / (specs[i].Mass + specs[i + 1].Mass);
            if (adjacent.A != body.Chunks[i]
                || adjacent.B != body.Chunks[i + 1]
                || adjacent.ConstraintMode != ChunkConnection.Mode.Rigid
                || adjacent.SoftOnly
                || !adjacent.TerrainCoupled
                || !Near(adjacent.RestLength, specs[i].LinkLengthToNext)
                || !Near(adjacent.WeightA, expectedWeightA))
            {
                return false;
            }
        }

        for (int i = expectedCount - 1; i < body.Connections.Count; i++)
        {
            ChunkConnection brace = body.Connections[i];
            if (!brace.SoftOnly
                || brace.ConstraintMode != ChunkConnection.Mode.PushOnly
                || !brace.TerrainCoupled)
            {
                return false;
            }
        }
        return true;
    }

    private static bool CheckDeterminism(out string message)
    {
        HashRun shortA = RunHash(CentipedeFactory.Short());
        HashRun shortB = RunHash(CentipedeFactory.Short());
        HashRun longA = RunHash(CentipedeFactory.Long());
        HashRun longB = RunHash(CentipedeFactory.Long());

        bool exact = shortA.Hash == shortB.Hash && longA.Hash == longB.Hash;
        bool independent = shortA.Hash != longA.Hash;
        bool baselines = shortA.Hash == ExpectedShortHash
            && longA.Hash == ExpectedLongHash;
        bool behavior = shortA.Finite && longA.Finite
            && shortA.WalkDistance > 2f && longA.WalkDistance > 2f
            && shortA.EndDeviationRatio <= 0.1f
            && longA.EndDeviationRatio <= 0.1f
            && shortA.MaxDisconnectTicks <= 20
            && longA.MaxDisconnectTicks <= 20;

        message = $"short={shortA.Hash:X16}/{shortB.Hash:X16} " +
                  $"long={longA.Hash:X16}/{longB.Hash:X16}, distinct={independent}, " +
                  $"walk={shortA.WalkDistance:F2}/{longA.WalkDistance:F2}m, " +
                  $"endDev={shortA.EndDeviationRatio:P1}/{longA.EndDeviationRatio:P1}, " +
                  $"disconnectMax={shortA.MaxDisconnectTicks}/{longA.MaxDisconnectTicks}tick, " +
                  $"finite={shortA.Finite}/{longA.Finite}, baseline={baselines}";
        return exact && independent && baselines && behavior;
    }

    private static HashRun RunHash(CentipedeParams parameters)
    {
        var terrain = new PlaneTerrainQuery(0f);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), parameters);
        var hasher = new DeterminismHasher();
        Vector3 gravity = GravityPerTick();
        Vector3 previous = controller.LeadChunk.Pos;
        float walk = 0f;
        int maxDisconnectTicks = 0;
        var disconnectRuns = new int[controller.Body.Connections.Count];

        for (long tick = 1; tick <= DeterminismTicks; tick++)
        {
            controller.MoveDir = tick <= DeterminismTicks / 2
                ? Vector3.Right
                : new Vector3(1f, 0f, 1f).Normalized();
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, tick));

            hasher.FoldBody(controller.Body);
            controller.FoldDeterministicState(hasher);

            Vector3 step = controller.LeadChunk.Pos - previous;
            step.Y = 0f;
            walk += step.Length();
            previous = controller.LeadChunk.Pos;
            maxDisconnectTicks = Math.Max(maxDisconnectTicks,
                UpdatePerConnectionDisconnectRuns(controller.Body, disconnectRuns));
        }

        return new HashRun(hasher.Value, walk,
            MaxConnectionDeviationRatio(controller.Body), maxDisconnectTicks,
            AllFinite(controller));
    }

    private static bool CheckExplicitLeadSelection(out string message)
    {
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Short());
        var terrain = new PlaneTerrainQuery(0f);
        Vector3 gravity = GravityPerTick();
        long tick = 0;

        controller.MoveDir = Vector3.Right;
        controller.RunSpeed = 1f;
        for (int i = 0; i < 12; i++)
        {
            controller.Tick(new TickContext(gravity, terrain, ++tick));
        }
        bool start = controller.RequestedLeadEnd == CentipedeLeadEnd.Start
            && controller.LeadEnd == CentipedeLeadEnd.Start;
        var trailBefore = new Vector3[controller.SurfaceTrail.Count];
        bool realTrail = trailBefore.Length >= controller.Segments.Count;
        for (int i = 0; i < trailBefore.Length; i++)
        {
            CentipedeSurfaceSample sample = controller.SurfaceTrail[i];
            trailBefore[i] = sample.Point;
            realTrail &= sample.ColliderId != 0;
        }

        var previousPhases = new float[controller.Legs.Count];
        for (int i = 0; i < previousPhases.Length; i++)
        {
            previousPhases[i] = controller.Legs[i].Phase;
        }
        bool phaseContinuous = true;
        float maxPhaseError = 0f;

        void TickAndCheckPhase()
        {
            controller.Tick(new TickContext(gravity, terrain, ++tick));
            for (int i = 0; i < previousPhases.Length; i++)
            {
                float expected = previousPhases[i] + controller.GaitFrequency;
                expected -= Mathf.Floor(expected);
                float error = Mathf.Abs(controller.Legs[i].Phase - expected);
                error = Mathf.Min(error, 1f - error);
                maxPhaseError = Mathf.Max(maxPhaseError, error);
                phaseContinuous &= error <= 2e-5f;
                previousPhases[i] = controller.Legs[i].Phase;
            }
        }

        void TickDirection(Vector3 direction)
        {
            controller.MoveTarget = null;
            controller.MoveDir = direction;
            TickAndCheckPhase();
        }

        void TickTarget(Vector3 target)
        {
            controller.MoveDir = Vector3.Zero;
            controller.MoveTarget = target;
            TickAndCheckPhase();
        }

        Vector3[] SnapshotTrail()
        {
            var snapshot = new Vector3[controller.SurfaceTrail.Count];
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = controller.SurfaceTrail[i].Point;
            }
            return snapshot;
        }

        int CountPreserved(Vector3[] snapshot)
        {
            int preserved = 0;
            foreach (CentipedeSurfaceSample sample in controller.SurfaceTrail)
            {
                foreach (Vector3 oldPoint in snapshot)
                {
                    if (sample.Point.DistanceSquaredTo(oldPoint) <= Epsilon * Epsilon)
                    {
                        preserved++;
                        break;
                    }
                }
            }
            return preserved;
        }

        // MoveDir 和 MoveTarget 只改变移动意图；宿主没有写请求时，领航端必须保持 Start。
        for (int i = 0; i < 3; i++)
        {
            TickDirection(Vector3.Left);
        }
        bool moveDirKeptStart = controller.RequestedLeadEnd == CentipedeLeadEnd.Start
            && controller.LeadEnd == CentipedeLeadEnd.Start;
        Vector3 targetBehindStart = controller.Segments[^1].Chunk.Pos + Vector3.Left * 3f;
        TickTarget(targetBehindStart);
        bool moveTargetKeptStart = controller.RequestedLeadEnd == CentipedeLeadEnd.Start
            && controller.LeadEnd == CentipedeLeadEnd.Start;

        controller.MoveTarget = null;
        Vector3[] beforeEndRequest = SnapshotTrail();
        float minBeforeEnd = float.PositiveInfinity;
        foreach (CentipedeSurfaceSample sample in controller.SurfaceTrail)
        {
            minBeforeEnd = Mathf.Min(minBeforeEnd, sample.Point.X);
        }
        controller.RequestedLeadEnd = CentipedeLeadEnd.End;
        bool endQueued = controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.Start
            && CountPreserved(beforeEndRequest) == beforeEndRequest.Length;
        TickDirection(Vector3.Left);
        bool endAppliedNextTick = controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.End;
        int keptAtEndSwitch = CountPreserved(beforeEndRequest);
        for (int i = 0; i < 6; i++)
        {
            TickDirection(Vector3.Left);
        }
        float minAfterReverse = float.PositiveInfinity;
        foreach (CentipedeSurfaceSample sample in controller.SurfaceTrail)
        {
            minAfterReverse = Mathf.Min(minAfterReverse, sample.Point.X);
            realTrail &= sample.ColliderId != 0;
        }
        bool extendedEnd = minAfterReverse
            < minBeforeEnd - controller.TrailSampleSpacing * 0.5f;

        Vector3[] beforeStartRequest = SnapshotTrail();
        float maxBeforeStart = float.NegativeInfinity;
        foreach (CentipedeSurfaceSample sample in controller.SurfaceTrail)
        {
            maxBeforeStart = Mathf.Max(maxBeforeStart, sample.Point.X);
        }
        controller.RequestedLeadEnd = CentipedeLeadEnd.Start;
        bool startQueued = controller.RequestedLeadEnd == CentipedeLeadEnd.Start
            && controller.LeadEnd == CentipedeLeadEnd.End
            && CountPreserved(beforeStartRequest) == beforeStartRequest.Length;
        TickDirection(Vector3.Right);
        bool startAppliedNextTick = controller.RequestedLeadEnd == CentipedeLeadEnd.Start
            && controller.LeadEnd == CentipedeLeadEnd.Start;
        int keptAtStartSwitch = CountPreserved(beforeStartRequest);
        for (int i = 0; i < 6; i++)
        {
            TickDirection(Vector3.Right);
        }
        float maxAfterReturn = float.NegativeInfinity;
        int preservedSamples = 0;
        foreach (CentipedeSurfaceSample sample in controller.SurfaceTrail)
        {
            maxAfterReturn = Mathf.Max(maxAfterReturn, sample.Point.X);
            realTrail &= sample.ColliderId != 0;
            foreach (Vector3 oldPoint in trailBefore)
            {
                if (sample.Point.DistanceSquaredTo(oldPoint) <= Epsilon * Epsilon)
                {
                    preservedSamples++;
                    break;
                }
            }
        }
        bool extendedStart = maxAfterReturn
            > maxBeforeStart + controller.TrailSampleSpacing * 0.5f;
        bool trailKept = controller.SurfaceTrail.Count >= controller.Segments.Count
            && keptAtEndSwitch > 0 && keptAtStartSwitch > 0 && preservedSamples > 0;

        message = $"start={start}, input-kept dir/target={moveDirKeptStart}/{moveTargetKeptStart}, " +
                  $"request queued/applied end={endQueued}/{endAppliedNextTick} " +
                  $"start={startQueued}/{startAppliedNextTick}, " +
                  $"real/kept={realTrail}/{trailKept} " +
                  $"({trailBefore.Length}->{controller.SurfaceTrail.Count}, " +
                  $"switch-kept={keptAtEndSwitch}/{keptAtStartSwitch}, final-kept={preservedSamples}), " +
                  $"extend end/start={extendedEnd}/{extendedStart}, " +
                  $"phase continuous={phaseContinuous} (err={maxPhaseError:E2})";
        return start && moveDirKeptStart && moveTargetKeptStart
            && endQueued && endAppliedNextTick && startQueued && startAppliedNextTick
            && realTrail && trailKept && extendedEnd && extendedStart && phaseContinuous
            && AllFinite(controller);
    }

    private static bool CheckLifecycle(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Short());
        Vector3 gravity = GravityPerTick();
        long tick = 0;
        controller.RequestedLeadEnd = CentipedeLeadEnd.End;
        for (int i = 0; i < 160; i++)
        {
            controller.MoveDir = Vector3.Left;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, ++tick));
        }
        bool endApplied = controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.End;

        controller.MoveTarget = controller.LeadChunk.Pos + new Vector3(-5f, 0f, 1f);
        controller.RunSpeed = 1f;
        controller.Tick(new TickContext(gravity, terrain, ++tick));

        var chunks = new (Vector3 Pos, Vector3 Last)[controller.Body.Chunks.Count];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = (controller.Body.Chunks[i].Pos, controller.Body.Chunks[i].LastPos);
        }
        var legs = new (Vector3 Pos, Vector3 Last, Vector3 Grip)[controller.Legs.Count];
        for (int i = 0; i < legs.Length; i++)
        {
            legs[i] = (controller.Legs[i].Pos, controller.Legs[i].LastPos,
                controller.Legs[i].GripPoint);
        }
        var segments = new (Vector3 Support, Vector3 Target)[controller.Segments.Count];
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = (controller.Segments[i].SupportPoint,
                controller.Segments[i].TargetCenter);
        }
        var trail = new Vector3[controller.SurfaceTrail.Count];
        for (int i = 0; i < trail.Length; i++)
        {
            trail[i] = controller.SurfaceTrail[i].Point;
        }
        Vector3 fedTarget = controller.MoveTarget!.Value;
        Vector3 lastTarget = controller.LastMoveTarget;

        Vector3 shift = new(256f, 0f, -384f);
        controller.Shift(shift);
        bool shiftExact = controller.MoveTarget == fedTarget + shift
            && controller.LastMoveTarget == lastTarget + shift
            && controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.End;
        for (int i = 0; i < chunks.Length; i++)
        {
            shiftExact &= controller.Body.Chunks[i].Pos == chunks[i].Pos + shift
                && controller.Body.Chunks[i].LastPos == chunks[i].Last + shift;
        }
        for (int i = 0; i < legs.Length; i++)
        {
            shiftExact &= controller.Legs[i].Pos == legs[i].Pos + shift
                && controller.Legs[i].LastPos == legs[i].Last + shift
                && controller.Legs[i].GripPoint == legs[i].Grip + shift;
        }
        for (int i = 0; i < segments.Length; i++)
        {
            shiftExact &= controller.Segments[i].SupportPoint == segments[i].Support + shift
                && controller.Segments[i].TargetCenter == segments[i].Target + shift;
        }
        for (int i = 0; i < trail.Length; i++)
        {
            shiftExact &= controller.SurfaceTrail[i].Point == trail[i] + shift;
        }

        Vector3 beforeTeleport = controller.Body.Chunks[0].Pos;
        Vector3 teleport = new(-256f, 1.5f, 384f);
        controller.Teleport(teleport);
        bool teleportReset = controller.Body.Chunks[0].Pos == beforeTeleport + teleport
            && controller.MoveTarget is null
            && controller.MoveDir == Vector3.Zero
            && !controller.AtMoveTarget
            && controller.LastMoveTargetKind == CentipedeMoveTargetKind.None
            && controller.SurfaceTrail.Count == 0
            && controller.SupportRatio == 0f
            && controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.End;
        foreach (CentipedeLeg leg in controller.Legs)
        {
            teleportReset &= !leg.HasGrip && !leg.Gripping;
        }

        // 重新落地取得支撑，再验证 Launch 对全部 chunk 的统一冲量与表面状态作废。
        for (int i = 0; i < 220; i++)
        {
            controller.MoveDir = Vector3.Left;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, ++tick));
        }
        var velocities = new Vector3[controller.Body.Chunks.Count];
        for (int i = 0; i < velocities.Length; i++)
        {
            velocities[i] = controller.Body.Chunks[i].Vel;
        }
        Vector3 impulse = new(0.03f, 0.16f, -0.02f);
        controller.Launch(impulse);
        bool launchExact = controller.SurfaceTrail.Count == 0
            && controller.SupportRatio == 0f
            && controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.End;
        for (int i = 0; i < velocities.Length; i++)
        {
            launchExact &= controller.Body.Chunks[i].Vel == velocities[i] + impulse;
        }
        foreach (CentipedeLeg leg in controller.Legs)
        {
            launchExact &= !leg.HasGrip && !leg.Gripping;
        }

        bool recovered = false;
        for (int i = 0; i < 300; i++)
        {
            controller.MoveDir = Vector3.Left;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, ++tick));
            recovered |= controller.SupportRatio >= 0.4f;
        }
        bool endRetained = controller.RequestedLeadEnd == CentipedeLeadEnd.End
            && controller.LeadEnd == CentipedeLeadEnd.End;

        message = $"End applied/retained={endApplied}/{endRetained}, " +
                  $"Shift exact+End={shiftExact}, Teleport reset+End={teleportReset}, " +
                  $"Launch exact={launchExact}, support recovered={recovered}, " +
                  $"finite={AllFinite(controller)}";
        return endApplied && endRetained && shiftExact && teleportReset
            && launchExact && recovered
            && AllFinite(controller);
    }

    private static bool CheckMoveTarget(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Short());
        Vector3 gravity = GravityPerTick();
        Vector3 target = new(2.5f, 0.12f, 0f);
        controller.MoveTarget = target;
        controller.RunSpeed = 1f;
        int arrivedAt = -1;
        long tick = 0;
        bool externalObserved = false;
        for (int i = 0; i < 240; i++)
        {
            controller.Tick(new TickContext(gravity, terrain, ++tick));
            externalObserved |= controller.LastMoveTargetKind
                == CentipedeMoveTargetKind.External;
            if (controller.AtMoveTarget)
            {
                arrivedAt = i + 1;
                break;
            }
        }
        float distance = controller.LeadChunk.Pos.DistanceTo(target);

        controller.MoveTarget = null;
        controller.RunSpeed = 0f;
        controller.Tick(new TickContext(gravity, terrain, ++tick));
        bool cleared = !controller.AtMoveTarget && !controller.HasMoveIntent;
        message = $"arrivedAt={arrivedAt}/240, distance={distance:F3}/{controller.ArriveRadius:F3}m, " +
                  $"external={externalObserved}, clear-stop={cleared}, finite={AllFinite(controller)}";
        return arrivedAt > 0 && distance <= controller.ArriveRadius + Epsilon
            && externalObserved && cleared && AllFinite(controller);
    }

    /// <summary>
    /// 18 节身体整体出生在解析地板内，验证球体 MTD、长链约束与腿端都能在有限预算内
    /// 恢复；脱困后继续按正式 2 mm / 10% / 20 tick 门观测，不能只检查某一节被推出。
    /// </summary>
    private static bool CheckEmbedRecovery(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, -0.1f, 0f), CentipedeFactory.Long());
        bool initiallyEmbedded = false;
        foreach (BodyChunk chunk in controller.Body.Chunks)
        {
            initiallyEmbedded |= terrain.SpherePenetration(
                chunk.Pos, chunk.Radius, out _, out float depth) && depth > 0.002f;
        }

        int escapedAt = -1;
        float maxPostEscapePenetration = 0f;
        int maxPostEscapeDisconnect = 0;
        var disconnectRuns = new int[controller.Body.Connections.Count];
        Vector3 gravity = GravityPerTick();
        for (long tick = 1; tick <= 100; tick++)
        {
            controller.MoveDir = Vector3.Zero;
            controller.MoveTarget = null;
            controller.RunSpeed = 0f;
            controller.Tick(new TickContext(gravity, terrain, tick));

            float penetration = 0f;
            foreach (BodyChunk chunk in controller.Body.Chunks)
            {
                if (terrain.SpherePenetration(chunk.Pos, chunk.Radius,
                    out _, out float depth))
                {
                    penetration = Mathf.Max(penetration, depth);
                }
            }
            foreach (CentipedeLeg leg in controller.Legs)
            {
                if (terrain.SpherePenetration(leg.Pos, leg.Radius,
                    out _, out float depth))
                {
                    penetration = Mathf.Max(penetration, depth);
                }
            }

            if (escapedAt < 0 && penetration <= 0.002f + Epsilon
                && MaxConnectionDeviationRatio(controller.Body) <= 0.1f)
            {
                escapedAt = (int)tick;
                Array.Clear(disconnectRuns);
            }
            if (escapedAt >= 0)
            {
                maxPostEscapePenetration = Mathf.Max(
                    maxPostEscapePenetration, penetration);
                maxPostEscapeDisconnect = Math.Max(maxPostEscapeDisconnect,
                    UpdatePerConnectionDisconnectRuns(controller.Body, disconnectRuns));
            }
        }

        float finalDeviation = MaxConnectionDeviationRatio(controller.Body);
        bool escaped = escapedAt is >= 1 and <= 40;
        bool finite = AllFinite(controller);
        message = $"initial={initiallyEmbedded}, escapedAt={escapedAt}/40, " +
                  $"postPenetration={maxPostEscapePenetration:F4}/0.002m, " +
                  $"disconnectMax={maxPostEscapeDisconnect}/20, " +
                  $"finalDev={finalDeviation:P1}, finite={finite}";
        return initiallyEmbedded && escaped
            && maxPostEscapePenetration <= 0.002f + Epsilon
            && maxPostEscapeDisconnect <= 20
            && finalDeviation <= 0.1f
            && finite;
    }

    private static bool CheckSelfAvoidance(out string message)
    {
        CentipedeParams parameters = CustomParams(5);
        CentipedeSegmentParams[] specs = parameters.ResolveSegments();
        var body = new Body();
        for (int i = 0; i < specs.Length; i++)
        {
            Vector3 position = i switch
            {
                0 or 3 => Vector3.Zero,
                1 => new Vector3(5f, 0f, 0f),
                2 => new Vector3(10f, 0f, 0f),
                _ => new Vector3(15f, 0f, 0f),
            };
            body.Chunks.Add(new BodyChunk(position, specs[i].Radius, specs[i].Mass)
            {
                CollideWithTerrain = false,
            });
        }
        var controller = new CentipedeLocomotionController(body, specs)
        {
            SurfaceServo = 0f,
            SelfAvoidanceStrength = 1f,
            SelfAvoidanceCellSize = 1f,
            MaxMoveSpeed = 1f,
        };
        Vector3 centerBefore = (body.Chunks[0].Pos + body.Chunks[3].Pos) * 0.5f;
        controller.Tick(new TickContext(Vector3.Zero, new EmptyTerrain(), 1));
        float separation = body.Chunks[0].Pos.DistanceTo(body.Chunks[3].Pos);
        Vector3 centerAfter = (body.Chunks[0].Pos + body.Chunks[3].Pos) * 0.5f;
        bool symmetric = centerAfter.DistanceTo(centerBefore) <= 1e-6f
            && (body.Chunks[0].Vel + body.Chunks[3].Vel).Length() <= 1e-6f;

        message = $"overlap separation={separation:F4}m, symmetric={symmetric}, " +
                  $"finite={AllFinite(controller)}";
        return separation >= 0.05f && symmetric && AllFinite(controller);
    }

    /// <summary>
    /// 薄墙把足端和锚点隔开时，普通摆动会反复被同一墙面扫掠挡回错误一侧。
    /// 足端必须在有限 tick 内穿墙复位到锚点侧；同侧正常摆动不得误触发传送。
    /// </summary>
    private static bool CheckLegBarrierRecovery(out string message)
    {
        var terrain = new ThinWallTerrain();
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Short());
        CentipedeLeg leg = controller.Legs[0];
        BodyChunk anchor = leg.Anchor.Chunk;
        Vector3 anchorPos = new(0.22f, 0.4f, 0f);
        anchor.Pos = anchorPos;
        anchor.LastPos = anchorPos;
        anchor.Vel = Vector3.Zero;
        leg.Anchor.Forward = Vector3.Right;
        leg.Anchor.Side = Vector3.Back;
        leg.Anchor.SupportNormal = Vector3.Up;

        Vector3 trappedPos = new(-0.08f, 0.4f, 0f);
        leg.Pos = trappedPos;
        leg.LastPos = trappedPos;
        leg.Vel = Vector3.Zero;
        leg.ForceRelease();
        bool initiallySeparated = terrain.Raycast(anchor.Pos, leg.Pos, out TerrainHit barrier)
            && barrier.Normal.Dot(Vector3.Right) >= 0.999f;
        bool clear = !terrain.SpherePenetration(anchor.Pos, anchor.Radius, out _, out _)
            && !terrain.SpherePenetration(leg.Pos, leg.Radius, out _, out _);
        bool finite = true;
        int recoveredAt = -1;
        bool resetExact = false;
        bool stayedRecovered = true;
        for (int tick = 1; tick <= 12; tick++)
        {
            int recoveriesBefore = leg.TerrainBarrierRecoveries;
            leg.Tick(new TickContext(Vector3.Zero, terrain, tick),
                phase: 0.8f, stanceFraction: 0.66f, runSpeed: 1f);
            finite &= leg.DeterministicStateIsFinite;
            clear &= !terrain.SpherePenetration(leg.Pos, leg.Radius, out _, out _);
            if (leg.TerrainBarrierRecoveries > recoveriesBefore)
            {
                recoveredAt = tick;
                resetExact = Near(leg.Pos, anchor.Pos)
                    && Near(leg.LastPos, anchor.Pos)
                    && Near(leg.Vel, anchor.Vel)
                    && Near(leg.GripPoint, leg.Pos)
                    && leg.GripColliderId == 0
                    && !leg.HasGrip && !leg.Gripping && leg.IsSwinging;
            }
            if (recoveredAt > 0)
            {
                stayedRecovered &= leg.Pos.X
                    >= ThinWallTerrain.HalfWidth + leg.Radius - Epsilon;
                stayedRecovered &= !terrain.Raycast(anchor.Pos, leg.Pos, out _);
            }
        }
        bool anchorUnmoved = Near(anchor.Pos, anchorPos) && Near(anchor.LastPos, anchorPos)
            && Near(anchor.Vel, Vector3.Zero);
        bool connected = leg.Pos.DistanceTo(anchor.Pos) <= leg.Length + Epsilon;
        int recoveriesAfterTrap = leg.TerrainBarrierRecoveries;

        // 低速大脚的中心永远到不了墙面，只会“球壳侵入→MTD 推回”；这条钉住 MTD 阻挡
        // 也必须累计，不能只覆盖 short 的高速中心扫掠。
        var slowTerrain = new ThinWallTerrain();
        CentipedeLocomotionController slowController = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Armored());
        CentipedeLeg slowLeg = slowController.Legs[0];
        BodyChunk slowAnchor = slowLeg.Anchor.Chunk;
        slowAnchor.Pos = anchorPos;
        slowAnchor.LastPos = anchorPos;
        slowAnchor.Vel = Vector3.Zero;
        slowLeg.Anchor.Forward = Vector3.Right;
        slowLeg.Anchor.Side = Vector3.Back;
        slowLeg.Anchor.SupportNormal = Vector3.Up;
        Vector3 slowTrappedPos = new(
            -(ThinWallTerrain.HalfWidth + slowLeg.Radius), 0.4f, 0f);
        slowLeg.Pos = slowTrappedPos;
        slowLeg.LastPos = slowTrappedPos;
        slowLeg.Vel = Vector3.Zero;
        slowLeg.HuntSpeed = 0.02f;
        slowLeg.Quickness = 0.25f;
        slowLeg.ForceRelease();
        int slowRecoveredAt = -1;
        bool slowResetExact = false;
        bool slowClear = true;
        for (int tick = 1; tick <= 12; tick++)
        {
            int recoveriesBefore = slowLeg.TerrainBarrierRecoveries;
            slowLeg.Tick(new TickContext(Vector3.Zero, slowTerrain, tick),
                phase: 0.8f, stanceFraction: 0.66f, runSpeed: 1f);
            slowClear &= !slowTerrain.SpherePenetration(
                slowLeg.Pos, slowLeg.Radius, out _, out _);
            finite &= slowLeg.DeterministicStateIsFinite;
            if (slowLeg.TerrainBarrierRecoveries > recoveriesBefore)
            {
                slowRecoveredAt = tick;
                slowResetExact = Near(slowLeg.Pos, slowAnchor.Pos)
                    && Near(slowLeg.LastPos, slowAnchor.Pos)
                    && Near(slowLeg.Vel, slowAnchor.Vel)
                    && !slowLeg.HasGrip;
            }
        }
        bool slowRecovered = slowRecoveredAt is >= 1 and <= 12
            && slowLeg.TerrainBarrierRecoveries == 1
            && slowTerrain.PenetrationHitCount > 0
            && slowLeg.Pos.X >= ThinWallTerrain.HalfWidth + slowLeg.Radius - Epsilon
            && slowLeg.Pos.DistanceTo(slowAnchor.Pos) <= slowLeg.Length + Epsilon
            && slowResetExact && slowClear;

        // 同侧脚故意连续撞向同一墙面：必须确实发生球壳碰撞，但锚点没有隔在墙后，
        // 因而不能把“普通撞墙”误判成需要穿墙复位。
        Vector3 sameSidePos = new(
            ThinWallTerrain.HalfWidth + leg.Radius + 0.005f, 0.4f, 0f);
        leg.Pos = sameSidePos;
        leg.LastPos = sameSidePos;
        leg.Vel = Vector3.Zero;
        leg.Anchor.Forward = Vector3.Back;
        leg.Anchor.Side = Vector3.Right * -leg.Side;
        leg.ForceRelease();
        float minimumSameSideX = leg.Pos.X;
        int penetrationHitsBefore = terrain.PenetrationHitCount;
        for (int tick = 13; tick <= 20; tick++)
        {
            leg.Tick(new TickContext(Vector3.Zero, terrain, tick),
                phase: 0.8f, stanceFraction: 0.66f, runSpeed: 1f);
            minimumSameSideX = Mathf.Min(minimumSameSideX, leg.Pos.X);
            finite &= leg.DeterministicStateIsFinite;
            clear &= !terrain.SpherePenetration(leg.Pos, leg.Radius, out _, out _);
        }
        bool sameSideStable = minimumSameSideX
            >= ThinWallTerrain.HalfWidth + leg.Radius - Epsilon;
        bool oneRecoveryOnly = recoveriesAfterTrap == 1
            && leg.TerrainBarrierRecoveries == recoveriesAfterTrap;
        bool sameSideHitWall = terrain.PenetrationHitCount > penetrationHitsBefore;
        bool occludedGrip = CheckOccludedStanceGrip(out string occludedGripMessage);

        message = $"separated={initiallySeparated}, recoveredAt={recoveredAt}/12, " +
                  $"recoveries={recoveriesAfterTrap}/1, " +
                  $"resetExact/stayed={resetExact}/{stayedRecovered}, " +
                  $"slowMTD={slowRecoveredAt}/12 exact={slowResetExact}, " +
                  $"sameSideHit/minX={sameSideHitWall}/{minimumSameSideX:F3}, " +
                  $"occludedGrip=({occludedGripMessage}), anchorStill={anchorUnmoved}, " +
                  $"connected={connected}, clear={clear && slowClear}, finite={finite}";
        return initiallySeparated && recoveredAt is >= 1 and <= 12
            && resetExact && stayedRecovered && slowRecovered
            && oneRecoveryOnly && sameSideHitWall && sameSideStable
            && occludedGrip && anchorUnmoved && connected && clear && finite;
    }

    private static bool CheckOccludedStanceGrip(out string message)
    {
        var terrain = new OccludableFloorTerrain();
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Armored());
        CentipedeLeg leg = controller.Legs[0];
        BodyChunk anchor = leg.Anchor.Chunk;
        Vector3 initialAnchor = new(-0.22f, 0.34f, 0f);
        anchor.Pos = initialAnchor;
        anchor.LastPos = initialAnchor;
        anchor.Vel = Vector3.Zero;
        leg.Anchor.Forward = Vector3.Right;
        leg.Anchor.Side = Vector3.Back;
        leg.Anchor.SupportNormal = Vector3.Up;
        leg.Pos = initialAnchor;
        leg.LastPos = initialAnchor;
        leg.Vel = Vector3.Zero;
        leg.ForceRelease();

        for (int tick = 1; tick <= 12; tick++)
        {
            leg.Tick(new TickContext(Vector3.Zero, terrain, tick),
                phase: 0f, stanceFraction: 1f, runSpeed: 0f);
        }
        bool planted = leg.HasGrip;
        Vector3 oldGripCenter = leg.GripPoint
            + leg.GripNormal * (leg.Radius + 0.005f);

        Vector3 movedAnchor = new(0.22f, 0.34f, 0f);
        anchor.Pos = movedAnchor;
        anchor.LastPos = movedAnchor;
        anchor.Vel = Vector3.Zero;
        terrain.WallEnabled = true;
        bool occluded = terrain.Raycast(anchor.Pos, oldGripCenter, out TerrainHit obstruction)
            && obstruction.Normal.Dot(Vector3.Right) >= 0.999f;
        bool stillWithinReach = leg.GripPoint.DistanceTo(anchor.Pos)
            <= leg.Length + leg.Radius + 0.05f;
        int recoveredAfter = -1;
        bool resetExact = false;
        int recoveriesBeforeOcclusion = leg.TerrainBarrierRecoveries;
        for (int tick = 13; tick <= 20; tick++)
        {
            int before = leg.TerrainBarrierRecoveries;
            leg.Tick(new TickContext(Vector3.Zero, terrain, tick),
                phase: 0f, stanceFraction: 1f, runSpeed: 0f);
            if (leg.TerrainBarrierRecoveries > before)
            {
                recoveredAfter = tick - 12;
                resetExact = Near(leg.Pos, anchor.Pos)
                    && Near(leg.LastPos, anchor.Pos)
                    && Near(leg.Vel, anchor.Vel)
                    && Near(leg.GripPoint, leg.Pos)
                    && leg.GripColliderId == 0
                    && !leg.HasGrip && !leg.Gripping && leg.IsSwinging;
                break;
            }
        }
        bool once = leg.TerrainBarrierRecoveries == recoveriesBeforeOcclusion + 1;
        message = $"planted/blocked/reachable={planted}/{occluded}/{stillWithinReach}, " +
                  $"resetAt={recoveredAfter}/4 exact={resetExact}";
        return planted && occluded && stillWithinReach
            && recoveredAfter is >= 1 and <= 4 && once && resetExact;
    }

    private static bool CheckIdleHold(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Short());
        Vector3 gravity = GravityPerTick();
        long tick = 0;
        for (int i = 0; i < 160; i++)
        {
            controller.MoveDir = Vector3.Right;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, ++tick));
        }

        controller.MoveDir = Vector3.Zero;
        controller.MoveTarget = null;
        controller.RunSpeed = 0f;
        for (int i = 0; i < 80; i++)
        {
            controller.Tick(new TickContext(gravity, terrain, ++tick));
        }

        int gripping = 0;
        bool settled = true;
        foreach (CentipedeLeg leg in controller.Legs)
        {
            gripping += leg.Gripping ? 1 : 0;
            settled &= !leg.IsSwinging && Mathf.Abs(leg.Phase) <= Epsilon;
        }
        bool supported = controller.SupportRatio >= 0.8f;
        message = $"80tick phase-held={settled}, gripping={gripping}/{controller.Legs.Count}, " +
                  $"support={controller.SupportRatio:P0}, finite={controller.DeterministicStateIsFinite}";
        return settled && gripping > 0 && supported
            && controller.DeterministicStateIsFinite;
    }

    private static bool CheckFiniteAndQueryScaling(out string message)
    {
        ScaleRun sixteen = RunScale(16);
        ScaleRun thirtyTwo = RunScale(32);
        float ratio = sixteen.Queries == 0 ? float.PositiveInfinity
            : thirtyTwo.Queries / (float)sixteen.Queries;
        bool scale = ratio <= 2.25f;
        bool finite = sixteen.Finite && thirtyTwo.Finite;
        bool constraints = sixteen.EndDeviationRatio <= 0.1f
            && thirtyTwo.EndDeviationRatio <= 0.1f;

        message = $"q16/q32={sixteen.Queries}/{thirtyTwo.Queries} " +
                  $"ratio={ratio:F2} (<=2.25), finite={finite}, " +
                  $"endDev={sixteen.EndDeviationRatio:P1}/{thirtyTwo.EndDeviationRatio:P1}";
        return scale && finite && constraints;
    }

    private static ScaleRun RunScale(int count)
    {
        CentipedeParams parameters = CustomParams(count);
        parameters.Overrides = Array.Empty<CentipedeSegmentOverride?>();
        parameters.BaseSegment.LegPairs = 1;
        var terrain = new PlaneTerrainQuery(0f);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), parameters);
        Vector3 gravity = GravityPerTick();
        const int ticks = 180;
        for (long tick = 1; tick <= ticks; tick++)
        {
            bool firstHalf = tick < ticks / 2;
            controller.RequestedLeadEnd = firstHalf
                ? CentipedeLeadEnd.Start : CentipedeLeadEnd.End;
            controller.MoveDir = firstHalf ? Vector3.Right : Vector3.Left;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, tick));
        }
        return new ScaleRun(terrain.RayCount + terrain.ShapeQueryCount,
            MaxConnectionDeviationRatio(controller.Body), AllFinite(controller));
    }

    private static bool AllFinite(CentipedeLocomotionController controller)
    {
        return controller.DeterministicStateIsFinite
            && float.IsFinite(controller.SupportRatio)
            && float.IsFinite(controller.Body.CurrentMaxDeviation());
    }

    private static float MaxConnectionDeviationRatio(Body body)
    {
        WorstRigidConnection(body, out _, out float maximum);
        return maximum;
    }

    private static int UpdatePerConnectionDisconnectRuns(Body body, int[] runs)
    {
        int maximum = 0;
        for (int i = 0; i < body.Connections.Count; i++)
        {
            ChunkConnection connection = body.Connections[i];
            if (connection.SoftOnly)
            {
                runs[i] = 0;
                continue;
            }
            runs[i] = ConnectionDeviationRatio(connection) > 0.1f
                ? runs[i] + 1 : 0;
            maximum = Math.Max(maximum, runs[i]);
        }
        return maximum;
    }

    private static void WorstRigidConnection(Body body,
        out int worstIndex, out float worstRatio)
    {
        worstIndex = -1;
        worstRatio = 0f;
        for (int i = 0; i < body.Connections.Count; i++)
        {
            ChunkConnection connection = body.Connections[i];
            if (connection.SoftOnly || connection.RestLength <= 0f)
            {
                continue;
            }
            float ratio = ConnectionDeviationRatio(connection);
            if (ratio > worstRatio)
            {
                worstRatio = ratio;
                worstIndex = i;
            }
        }
    }

    private static float ConnectionDeviationRatio(ChunkConnection connection)
    {
        if (connection.RestLength <= 0f)
        {
            return 0f;
        }
        float error = connection.B.Pos.DistanceTo(connection.A.Pos)
            - connection.RestLength;
        float deviation = connection.ConstraintMode switch
        {
            ChunkConnection.Mode.PullOnly => Mathf.Max(0f, error),
            ChunkConnection.Mode.PushOnly => Mathf.Max(0f, -error),
            _ => Mathf.Abs(error),
        };
        return deviation / connection.RestLength;
    }

    private static Vector3 GravityPerTick() =>
        new(0f, -GravityMps2 * TickDt * TickDt, 0f);

    private readonly record struct HashRun(ulong Hash, float WalkDistance,
        float EndDeviationRatio, int MaxDisconnectTicks, bool Finite);

    private readonly record struct ScaleRun(long Queries, float EndDeviationRatio, bool Finite);

    /// <summary>
    /// 0.4m 窄墙双端镜像回归。Start 从墙左侧向 +X、End 从墙右侧向 -X，均让出生时
    /// 其余节自然拖在领端后方；宿主全程只给恒定水平输入，不替控制器补上墙/下墙方向。
    /// 通过判据读取实体球心与真实抓足，不能由提前翻面的 SurfaceTrail/SupportRatio 假造。
    /// </summary>
    private static bool CheckNarrowWallTraverse(out string message)
    {
        NarrowWallRun start = RunNarrowWall(CentipedeLeadEnd.Start);
        NarrowWallRun end = RunNarrowWall(CentipedeLeadEnd.End);
        bool startOk = NarrowWallPass(start);
        bool endOk = NarrowWallPass(end);
        message = $"start[{FormatNarrowWall(start)}] end[{FormatNarrowWall(end)}]";
        return startOk && endOk;
    }

    private static NarrowWallRun RunNarrowWall(CentipedeLeadEnd leadEnd)
    {
        float direction = leadEnd == CentipedeLeadEnd.Start ? 1f : -1f;
        var terrain = new NarrowWallTerrain(direction * NarrowWallTerrain.WallOffset);
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.5f, 0f), CentipedeFactory.Long());
        controller.RequestedLeadEnd = leadEnd;
        Vector3 gravity = GravityPerTick();
        Vector3 moveDir = Vector3.Right * direction;
        Vector3 farNormal = moveDir;
        float farFaceX = terrain.CenterX + direction * NarrowWallTerrain.HalfWidth;

        int leadFarAt = -1;
        int tailFarAt = -1;
        int farGripAt = -1;
        int continuedAt = -1;
        int wrongSideRun = 0;
        int maxWrongSideRun = 0;
        int maxDisconnectRun = 0;
        var disconnectRuns = new int[controller.Body.Connections.Count];
        float maxPenetration = 0f;
        float maxLeadY = float.NegativeInfinity;
        float maxForwardProgress = float.NegativeInfinity;
        bool fixedLead = true;
        bool finite = true;
        int ticksRun = 0;

        const int maxTicks = 2400;
        for (int tick = 1; tick <= maxTicks; tick++)
        {
            bool settling = continuedAt > 0;
            controller.RequestedLeadEnd = leadEnd;
            controller.MoveDir = settling ? Vector3.Zero : moveDir;
            controller.RunSpeed = settling ? 0f : 1f;
            controller.Tick(new TickContext(gravity, terrain, tick));
            ticksRun = tick;

            fixedLead &= controller.RequestedLeadEnd == leadEnd
                && controller.LeadEnd == leadEnd;
            finite &= AllFinite(controller);
            maxDisconnectRun = Math.Max(maxDisconnectRun,
                UpdatePerConnectionDisconnectRuns(controller.Body, disconnectRuns));

            CentipedeSegment lead = leadEnd == CentipedeLeadEnd.Start
                ? controller.Segments[0] : controller.Segments[^1];
            CentipedeSegment tail = leadEnd == CentipedeLeadEnd.Start
                ? controller.Segments[^1] : controller.Segments[0];
            maxLeadY = Mathf.Max(maxLeadY, lead.Chunk.Pos.Y);
            maxForwardProgress = Mathf.Max(maxForwardProgress, lead.Chunk.Pos.X * direction);
            if (leadFarAt < 0 && WholeSphereBeyondFarFace(lead.Chunk, farFaceX, direction))
            {
                leadFarAt = tick;
            }
            if (tailFarAt < 0 && WholeSphereBeyondFarFace(tail.Chunk, farFaceX, direction))
            {
                tailFarAt = tick;
            }
            if (continuedAt < 0 && tailFarAt > 0
                && direction * (lead.Chunk.Pos.X - farFaceX)
                    - lead.Chunk.Radius >= 1.2f)
            {
                continuedAt = tick;
            }

            bool wrongSide = lead.ColliderId == NarrowWallTerrain.WallColliderId
                && lead.SupportConfidence >= 0.25f
                && lead.SupportNormal.Dot(farNormal) >= 0.75f
                && (lead.Chunk.Pos - lead.SupportPoint).Dot(lead.SupportNormal)
                    < -lead.Chunk.Radius * 0.5f;
            wrongSideRun = wrongSide ? wrongSideRun + 1 : 0;
            maxWrongSideRun = Math.Max(maxWrongSideRun, wrongSideRun);

            if (farGripAt < 0)
            {
                foreach (CentipedeLeg leg in controller.Legs)
                {
                    if (leg.Gripping
                        && leg.GripColliderId == NarrowWallTerrain.WallColliderId
                        && leg.GripNormal.Dot(farNormal) >= 0.75f)
                    {
                        farGripAt = tick;
                        break;
                    }
                }
            }

            foreach (BodyChunk chunk in controller.Body.Chunks)
            {
                if (terrain.SpherePenetration(chunk.Pos, chunk.Radius,
                    out _, out float depth))
                {
                    maxPenetration = Mathf.Max(maxPenetration, depth);
                }
            }
            foreach (CentipedeLeg leg in controller.Legs)
            {
                if (terrain.SpherePenetration(leg.Pos, leg.Radius,
                    out _, out float depth))
                {
                    maxPenetration = Mathf.Max(maxPenetration, depth);
                }
            }

            if (continuedAt > 0 && farGripAt > 0 && tick >= continuedAt + 120)
            {
                break;
            }
        }

        return new NarrowWallRun(leadEnd, fixedLead, leadFarAt, tailFarAt,
            farGripAt, continuedAt, maxWrongSideRun, maxDisconnectRun, maxPenetration,
            maxLeadY, maxForwardProgress, controller.LeadChunk.Pos,
            MaxConnectionDeviationRatio(controller.Body), finite, ticksRun);
    }

    private static bool WholeSphereBeyondFarFace(
        BodyChunk chunk, float farFaceX, float direction) =>
        direction * (chunk.Pos.X - farFaceX) - chunk.Radius >= 0.02f;

    private static bool NarrowWallPass(in NarrowWallRun run)
    {
        const int wrongSideBudget = 8;
        int tailBudget = 40 + 8 * 18;
        int tailLag = run.LeadFarAt > 0 && run.TailFarAt > 0
            ? run.TailFarAt - run.LeadFarAt : -1;
        return run.FixedLead
            && run.LeadFarAt > 0
            && run.TailFarAt >= run.LeadFarAt
            && tailLag <= tailBudget
            && run.FarGripAt > 0
            && run.ContinuedAt >= run.TailFarAt
            && run.TicksRun - run.ContinuedAt >= 120
            && run.MaxLeadY >= NarrowWallTerrain.TopY + 0.05f
            && run.MaxWrongSideRun <= wrongSideBudget
            && run.MaxDisconnectRun <= 20
            && run.MaxPenetration <= 0.002f
            && run.FinalDeviationRatio <= 0.1f
            && run.Finite;
    }

    private static string FormatNarrowWall(in NarrowWallRun run)
    {
        int tailBudget = 40 + 8 * 18;
        int tailLag = run.LeadFarAt > 0 && run.TailFarAt > 0
            ? run.TailFarAt - run.LeadFarAt : -1;
        return $"fixed={run.FixedLead}, cross={run.LeadFarAt}/{run.TailFarAt}, " +
               $"lag={tailLag}/{tailBudget}, farGrip={run.FarGripAt}, " +
               $"continued/settled={run.ContinuedAt}/{run.TicksRun - run.ContinuedAt}, " +
               $"wrongSide={run.MaxWrongSideRun}/8, disconnect={run.MaxDisconnectRun}/20, " +
               $"penetration={run.MaxPenetration:F4}/0.0020m, " +
               $"maxY={run.MaxLeadY:F2}/{NarrowWallTerrain.TopY + 0.05f:F2}, " +
               $"progress={run.MaxForwardProgress:F2}, " +
               $"finalLead={run.FinalLeadPos}, " +
               $"finalDev={run.FinalDeviationRatio:P3}, finite={run.Finite}, ticks={run.TicksRun}";
    }

    private readonly record struct NarrowWallRun(
        CentipedeLeadEnd LeadEnd,
        bool FixedLead,
        int LeadFarAt,
        int TailFarAt,
        int FarGripAt,
        int ContinuedAt,
        int MaxWrongSideRun,
        int MaxDisconnectRun,
        float MaxPenetration,
        float MaxLeadY,
        float MaxForwardProgress,
        Vector3 FinalLeadPos,
        float FinalDeviationRatio,
        bool Finite,
        int TicksRun);

    /// <summary>
    /// 宿主始终用 Start 端领航并保持世界向右输入。该输入在台阶顶和下层地面都明确
    /// 指向前方；只在外侧立面上退化为零，控制器必须延续过角时平行运输得到的向下
    /// 切向，而不能自行换头或回退为向上。最终同一物理头尾都须落到下层并继续向右，
    /// 同时拒绝在墙唇附近靠反复换法线“制造弧长”的自交路径。
    /// </summary>
    private static bool CheckFixedLeadDescent(out string message)
    {
        var terrain = new StepDownTerrain();
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(-1.5f, StepDownTerrain.TopY + 0.35f, 0f),
            CentipedeFactory.Armored());
        controller.RequestedLeadEnd = CentipedeLeadEnd.Start;
        Vector3 gravity = GravityPerTick();

        int wallLeadAt = -1;
        int wallTailAt = -1;
        int lowerLeadAt = -1;
        int lowerTailAt = -1;
        float leadYAtWall = float.NaN;
        float minimumLeadYAfterWall = float.PositiveInfinity;
        float leadXAtLowerTail = float.NaN;
        float minimumNonAdjacentSeparationRatio = float.PositiveInfinity;
        int blockedRun = 0;
        int maxBlockedRun = 0;
        int maxDisconnectRun = 0;
        var disconnectRuns = new int[controller.Body.Connections.Count];
        bool fixedLead = true;
        bool finite = true;
        bool trailAlias = false;
        int ticksRun = 0;

        const int maxTicks = 1800;
        for (int tick = 1; tick <= maxTicks; tick++)
        {
            controller.RequestedLeadEnd = CentipedeLeadEnd.Start;
            controller.MoveDir = Vector3.Right;
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, tick));
            ticksRun = tick;

            fixedLead &= controller.RequestedLeadEnd == CentipedeLeadEnd.Start
                && controller.LeadEnd == CentipedeLeadEnd.Start;
            finite &= AllFinite(controller);
            blockedRun = controller.LeadSurfaceBlocked ? blockedRun + 1 : 0;
            maxBlockedRun = Math.Max(maxBlockedRun, blockedRun);
            maxDisconnectRun = Math.Max(maxDisconnectRun,
                UpdatePerConnectionDisconnectRuns(controller.Body, disconnectRuns));

            CentipedeSegment lead = controller.Segments[0];
            CentipedeSegment tail = controller.Segments[^1];
            StepDownPhase leadPhase = ClassifyStepDownPhase(lead);
            StepDownPhase tailPhase = ClassifyStepDownPhase(tail);
            if (leadPhase == StepDownPhase.Wall && wallLeadAt < 0)
            {
                wallLeadAt = tick;
                leadYAtWall = lead.Chunk.Pos.Y;
            }
            if (tailPhase == StepDownPhase.Wall && wallTailAt < 0)
            {
                wallTailAt = tick;
            }
            if (leadPhase == StepDownPhase.LowerFloor && lowerLeadAt < 0)
            {
                lowerLeadAt = tick;
            }
            if (tailPhase == StepDownPhase.LowerFloor && lowerTailAt < 0)
            {
                lowerTailAt = tick;
                leadXAtLowerTail = lead.Chunk.Pos.X;
            }

            if (wallLeadAt > 0)
            {
                minimumLeadYAfterWall = Mathf.Min(
                    minimumLeadYAfterWall, lead.Chunk.Pos.Y);
                minimumNonAdjacentSeparationRatio = Mathf.Min(
                    minimumNonAdjacentSeparationRatio,
                    MinimumNonAdjacentSeparationRatio(controller));
                if (!trailAlias)
                {
                    trailAlias = HasNonLocalTrailAlias(controller.SurfaceTrail);
                }
            }

            if (lowerTailAt > 0 && tick >= lowerTailAt + 120)
            {
                break;
            }
        }

        int tailBudget = 40 + 8 * controller.Segments.Count;
        float netDescent = float.IsFinite(leadYAtWall)
            && float.IsFinite(minimumLeadYAfterWall)
                ? leadYAtWall - minimumLeadYAfterWall
                : float.NegativeInfinity;
        float continuedX = float.IsFinite(leadXAtLowerTail)
            ? controller.Segments[0].Chunk.Pos.X - leadXAtLowerTail
            : float.NegativeInfinity;
        bool traversed = wallLeadAt > 0
            && wallTailAt >= wallLeadAt
            && lowerLeadAt > wallLeadAt
            && lowerTailAt >= lowerLeadAt
            && lowerTailAt > wallTailAt
            && lowerTailAt - lowerLeadAt <= tailBudget;
        bool structure = maxBlockedRun <= 40
            && maxDisconnectRun <= 20
            && MaxConnectionDeviationRatio(controller.Body) <= 0.1f;
        bool separated = minimumNonAdjacentSeparationRatio >= 0.35f;

        message = $"fixedStart={fixedLead}, wall={wallLeadAt}/{wallTailAt}, " +
                  $"lower={lowerLeadAt}/{lowerTailAt}, " +
                  $"tailLag={(lowerLeadAt > 0 && lowerTailAt > 0 ? lowerTailAt - lowerLeadAt : -1)}" +
                  $"/{tailBudget}, descent={netDescent:F2}/1.00m, " +
                  $"continuedX={continuedX:F2}/0.50m, " +
                  $"trailAlias={trailAlias}, nonAdjacent={minimumNonAdjacentSeparationRatio:F2}/0.35, " +
                  $"blocked={maxBlockedRun}/40, disconnect={maxDisconnectRun}/20, " +
                  $"finalDev={MaxConnectionDeviationRatio(controller.Body):P1}, " +
                  $"finite={finite}, ticks={ticksRun}";
        return fixedLead && traversed && netDescent >= 1f && continuedX >= 0.5f
            && !trailAlias && separated && structure && finite;
    }

    private static float MinimumNonAdjacentSeparationRatio(
        CentipedeLocomotionController controller)
    {
        float minimum = float.PositiveInfinity;
        for (int i = 0; i < controller.Segments.Count; i++)
        {
            BodyChunk a = controller.Segments[i].Chunk;
            for (int j = i + 3; j < controller.Segments.Count; j++)
            {
                BodyChunk b = controller.Segments[j].Chunk;
                float radii = a.Radius + b.Radius;
                if (radii > Epsilon)
                {
                    minimum = Mathf.Min(minimum, a.Pos.DistanceTo(b.Pos) / radii);
                }
            }
        }
        return minimum;
    }

    /// <summary>
    /// 单次 90° 锐角允许同一点附近用半径圆弧累计约 0.4m；只有相隔超过 0.55m
    /// 弧长后又回到 6cm 内，才判为墙唇折返。该门不依赖采样数或确定性哈希。
    /// </summary>
    private static bool HasNonLocalTrailAlias(
        IReadOnlyList<CentipedeSurfaceSample> trail)
    {
        for (int i = 0; i < trail.Count; i++)
        {
            for (int j = i + 1; j < trail.Count; j++)
            {
                if (trail[j].ArcLength - trail[i].ArcLength <= 0.55f)
                {
                    continue;
                }
                if (trail[i].Point.DistanceSquaredTo(trail[j].Point) <= 0.06f * 0.06f)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool CheckCourseMotion(out string message)
    {
        CourseRun shortRun = RunCourse(CentipedeFactory.Short(), maxTicks: 2600);
        CourseRun longRun = RunCourse(CentipedeFactory.Long(), maxTicks: 3600);

        bool shortOk = CoursePass(shortRun, 5);
        bool longOk = CoursePass(longRun, 18);
        message = $"short[{FormatCourse(shortRun, 5)}] long[{FormatCourse(longRun, 18)}]";
        return shortOk && longOk;
    }

    private static CourseRun RunCourse(CentipedeParams parameters, int maxTicks)
    {
        var terrain = new CentipedeCourseTerrain();
        CentipedeLocomotionController controller = CentipedeFactory.CreateController(
            new Vector3(0f, 0.45f, 0f), parameters);
        controller.RequestedLeadEnd = CentipedeLeadEnd.Start;
        Vector3 gravity = GravityPerTick();
        int phaseCount = Enum.GetValues<CoursePhase>().Length;
        var leadFirst = new int[phaseCount];
        var tailFirst = new int[phaseCount];
        Array.Fill(leadFirst, -1);
        Array.Fill(tailFirst, -1);

        int steeringStage = 0;
        int transitionTicks = 0;
        int maxTransitionTicks = 0;
        int blockedTicks = 0;
        int maxBlockedTicks = 0;
        int maxDisconnectTicks = 0;
        int maxDisconnectStart = -1;
        int maxDisconnectEnd = -1;
        int maxDisconnectConnection = -1;
        var disconnectRuns = new int[controller.Body.Connections.Count];
        var disconnectStarts = new int[controller.Body.Connections.Count];
        Array.Fill(disconnectStarts, -1);
        int collisionDrivenDisconnectTicks = 0;
        int worstDisconnectTick = -1;
        int worstConnectionIndex = -1;
        float worstConnectionRatio = 0f;
        float worstRelaxRatio = 0f;
        Vector3 worstAPos = Vector3.Zero;
        Vector3 worstBPos = Vector3.Zero;
        Vector3 worstANormal = Vector3.Zero;
        Vector3 worstBNormal = Vector3.Zero;
        bool worstAContact = false;
        bool worstBContact = false;
        CoursePhase worstAPhase = CoursePhase.None;
        CoursePhase worstBPhase = CoursePhase.None;
        float maxPenetration = 0f;
        float maxLeadX = float.NegativeInfinity;
        float maxLeadY = float.NegativeInfinity;
        bool finite = true;
        int ticksRun = 0;

        for (int tick = 1; tick <= maxTicks; tick++)
        {
            controller.MoveDir = steeringStage switch
            {
                0 => Vector3.Right,
                1 => Vector3.Down,
                _ => Vector3.Left,
            };
            controller.RunSpeed = 1f;
            controller.Tick(new TickContext(gravity, terrain, tick));
            ticksRun = tick;

            // 本课程由宿主显式保持 Start 端领航；过角预算跟踪同一物理端到另一物理端，
            // 不允许通过换头把旧 leader 当成“刚通过的 tail”制造假绿。
            CentipedeSegment lead = controller.Segments[0];
            CentipedeSegment tail = controller.Segments[^1];
            CoursePhase leadPhase = ClassifyCoursePhase(lead);
            CoursePhase tailPhase = ClassifyCoursePhase(tail);
            RecordFirst(leadFirst, leadPhase, tick);
            RecordFirst(tailFirst, tailPhase, tick);
            maxLeadX = Mathf.Max(maxLeadX, lead.Chunk.Pos.X);
            maxLeadY = Mathf.Max(maxLeadY, lead.Chunk.Pos.Y);

            if (steeringStage == 0 && leadPhase == CoursePhase.OuterWall)
            {
                steeringStage = 1;
            }
            else if (steeringStage == 1 && leadPhase == CoursePhase.Ceiling)
            {
                steeringStage = 2;
            }

            transitionTicks = leadPhase == CoursePhase.None
                ? transitionTicks + 1 : 0;
            maxTransitionTicks = Math.Max(maxTransitionTicks, transitionTicks);
            blockedTicks = controller.LeadSurfaceBlocked ? blockedTicks + 1 : 0;
            maxBlockedTicks = Math.Max(maxBlockedTicks, blockedTicks);

            for (int connectionIndex = 0;
                 connectionIndex < controller.Body.Connections.Count;
                 connectionIndex++)
            {
                ChunkConnection current = controller.Body.Connections[connectionIndex];
                if (current.SoftOnly)
                {
                    continue;
                }
                float ratio = ConnectionDeviationRatio(current);
                if (ratio > 0.1f)
                {
                    if (disconnectRuns[connectionIndex] == 0)
                    {
                        disconnectStarts[connectionIndex] = tick;
                    }
                    disconnectRuns[connectionIndex]++;
                    if (disconnectRuns[connectionIndex] > maxDisconnectTicks)
                    {
                        maxDisconnectTicks = disconnectRuns[connectionIndex];
                        maxDisconnectStart = disconnectStarts[connectionIndex];
                        maxDisconnectEnd = tick;
                        maxDisconnectConnection = connectionIndex;
                    }
                }
                else
                {
                    disconnectRuns[connectionIndex] = 0;
                    disconnectStarts[connectionIndex] = -1;
                }
            }

            WorstRigidConnection(controller.Body,
                out int worstThisTick, out float connectionRatio);
            if (connectionRatio > 0.1f && worstThisTick >= 0)
            {
                ChunkConnection connection = controller.Body.Connections[worstThisTick];
                float relaxRatio = controller.Body.LastRelaxDeviation / connection.RestLength;
                if (relaxRatio <= 0.1f)
                {
                    collisionDrivenDisconnectTicks++;
                }
                if (connectionRatio > worstConnectionRatio)
                {
                    worstConnectionRatio = connectionRatio;
                    worstRelaxRatio = relaxRatio;
                    worstDisconnectTick = tick;
                    worstConnectionIndex = worstThisTick;
                    worstAPos = connection.A.Pos;
                    worstBPos = connection.B.Pos;
                    worstAContact = connection.A.TerrainContact;
                    worstBContact = connection.B.TerrainContact;
                    worstANormal = connection.A.ContactNormal;
                    worstBNormal = connection.B.ContactNormal;
                    worstAPhase = worstThisTick < controller.Segments.Count
                        ? ClassifyCoursePhase(controller.Segments[worstThisTick])
                        : CoursePhase.None;
                    worstBPhase = worstThisTick + 1 < controller.Segments.Count
                        ? ClassifyCoursePhase(controller.Segments[worstThisTick + 1])
                        : CoursePhase.None;
                }
            }
            finite &= AllFinite(controller);

            foreach (BodyChunk chunk in controller.Body.Chunks)
            {
                if (terrain.SpherePenetration(chunk.Pos, chunk.Radius,
                    out _, out float depth))
                {
                    maxPenetration = Mathf.Max(maxPenetration, depth);
                }
            }
            foreach (CentipedeLeg leg in controller.Legs)
            {
                if (terrain.SpherePenetration(leg.Pos, leg.Radius,
                    out _, out float depth))
                {
                    maxPenetration = Mathf.Max(maxPenetration, depth);
                }
            }

            int ceilingTail = tailFirst[(int)CoursePhase.Ceiling];
            if (ceilingTail > 0 && tick >= ceilingTail + 20)
            {
                break;
            }
        }

        CentipedeSegment finalLeadSegment = controller.Segments[0];
        int trailLeadIndex = controller.LeadEnd == CentipedeLeadEnd.Start
            ? 0 : controller.SurfaceTrail.Count - 1;
        CentipedeSurfaceSample finalLeadSample = trailLeadIndex >= 0
            && trailLeadIndex < controller.SurfaceTrail.Count
                ? controller.SurfaceTrail[trailLeadIndex] : default;
        return new CourseRun(leadFirst, tailFirst, maxTransitionTicks, maxBlockedTicks,
            maxDisconnectTicks, maxPenetration, finite, ticksRun,
            controller.Segments[0].Chunk.Pos, controller.Segments[^1].Chunk.Pos,
            maxLeadX, maxLeadY, MaxConnectionDeviationRatio(controller.Body),
            finalLeadSegment.SupportPoint, finalLeadSegment.SupportNormal,
            finalLeadSegment.ColliderId, finalLeadSample,
            maxDisconnectStart, maxDisconnectEnd, maxDisconnectConnection,
            collisionDrivenDisconnectTicks, controller.Body.SnagReleases,
            worstDisconnectTick, worstConnectionIndex, worstConnectionRatio,
            worstRelaxRatio, worstAPos, worstBPos, worstAContact, worstBContact,
            worstANormal, worstBNormal, worstAPhase, worstBPhase);
    }

    private static bool CoursePass(CourseRun run, int segmentCount)
    {
        int budget = 40 + 8 * segmentCount;
        CoursePhase[] required =
        [
            CoursePhase.Floor,
            CoursePhase.Slope,
            CoursePhase.InnerWall,
            CoursePhase.Top,
            CoursePhase.OuterWall,
            CoursePhase.Ceiling,
        ];
        foreach (CoursePhase phase in required)
        {
            int lead = run.LeadFirst[(int)phase];
            int tail = run.TailFirst[(int)phase];
            if (lead < 0 || tail < lead || tail - lead > budget)
            {
                return false;
            }
        }
        return run.MaxTransitionTicks <= 40
            && run.MaxBlockedTicks <= 40
            && run.MaxDisconnectTicks <= 20
            && run.SnagReleases <= SnagReleaseBudget(segmentCount)
            && run.MaxPenetration <= 0.002f + Epsilon
            && run.FinalDeviationRatio <= 0.1f
            && run.Finite;
    }

    private static string FormatCourse(CourseRun run, int segmentCount)
    {
        int budget = 40 + 8 * segmentCount;
        string Times(CoursePhase phase) =>
            $"{run.LeadFirst[(int)phase]}/{run.TailFirst[(int)phase]}";
        int maxLag = 0;
        foreach (CoursePhase phase in Enum.GetValues<CoursePhase>())
        {
            if (phase == CoursePhase.None)
            {
                continue;
            }
            int lead = run.LeadFirst[(int)phase];
            int tail = run.TailFirst[(int)phase];
            if (lead >= 0 && tail >= lead)
            {
                maxLag = Math.Max(maxLag, tail - lead);
            }
        }
        return $"lead/tail F={Times(CoursePhase.Floor)} S={Times(CoursePhase.Slope)} " +
               $"W={Times(CoursePhase.InnerWall)} T={Times(CoursePhase.Top)} " +
               $"O={Times(CoursePhase.OuterWall)} C={Times(CoursePhase.Ceiling)}, " +
               $"lagMax={maxLag}/{budget}, transitionMax={run.MaxTransitionTicks}/40, " +
               $"blockedMax={run.MaxBlockedTicks}/40, " +
               $"disconnectMax={run.MaxDisconnectTicks}/20, " +
               $"window=c{run.MaxDisconnectConnection}@" +
               $"{run.MaxDisconnectStart}-{run.MaxDisconnectEnd}, " +
               $"snagReleases={run.SnagReleases}/{SnagReleaseBudget(segmentCount)}, " +
               $"collisionDriven={run.CollisionDrivenDisconnectTicks}, " +
               $"worst=t{run.WorstDisconnectTick} c{run.WorstConnectionIndex} " +
               $"{run.WorstConnectionRatio:P1}(relax={run.WorstRelaxRatio:P1}) " +
               $"A={run.WorstAPhase}/{run.WorstAContact}@{run.WorstAPos} " +
               $"n={run.WorstANormal} B={run.WorstBPhase}/{run.WorstBContact}@" +
               $"{run.WorstBPos} n={run.WorstBNormal}, " +
               $"penetration={run.MaxPenetration:F4}/0.002m, " +
               $"maxXY={run.MaxLeadX:F2}/{run.MaxLeadY:F2}, " +
               $"final={run.FinalLead}/{run.FinalTail}, finalDev={run.FinalDeviationRatio:P1}, " +
               $"support={run.FinalSupportPoint} n={run.FinalSupportNormal} " +
               $"id={run.FinalColliderId}, trail={run.FinalLeadSample.Point} " +
               $"n={run.FinalLeadSample.Normal} id={run.FinalLeadSample.ColliderId}, " +
               $"finite={run.Finite}, ticks={run.TicksRun}";
    }

    private static CoursePhase ClassifyCoursePhase(CentipedeSegment segment)
    {
        if (segment.ColliderId != CentipedeCourseTerrain.CourseColliderId
            || segment.SupportNormal.LengthSquared() < 1e-10f)
        {
            return CoursePhase.None;
        }
        Vector3 normal = segment.SupportNormal.Normalized();
        Vector3 point = segment.SupportPoint;
        if (normal.Dot(Vector3.Down) >= 0.78f
            && Mathf.Abs(point.Y - CentipedeCourseTerrain.CeilingY) <= 0.45f)
        {
            return CoursePhase.Ceiling;
        }
        if (normal.Dot(Vector3.Right) >= 0.78f
            && Mathf.Abs(point.X - CentipedeCourseTerrain.OuterWallX) <= 0.45f)
        {
            return CoursePhase.OuterWall;
        }
        if (normal.Dot(Vector3.Left) >= 0.78f
            && Mathf.Abs(point.X - CentipedeCourseTerrain.WallX) <= 0.45f)
        {
            return CoursePhase.InnerWall;
        }
        if (normal.Dot(CentipedeCourseTerrain.SlopeNormal) >= 0.985f
            && point.X >= 1.7f && point.X <= 5.3f)
        {
            return CoursePhase.Slope;
        }
        if (normal.Dot(Vector3.Up) >= 0.78f
            && point.Y >= CentipedeCourseTerrain.WallTopY - 0.45f)
        {
            return CoursePhase.Top;
        }
        if (normal.Dot(Vector3.Up) >= 0.78f)
        {
            return CoursePhase.Floor;
        }
        return CoursePhase.None;
    }

    private static void RecordFirst(int[] ticks, CoursePhase phase, int tick)
    {
        int index = (int)phase;
        if (phase != CoursePhase.None && ticks[index] < 0)
        {
            ticks[index] = tick;
        }
    }

    private static int SnagReleaseBudget(int segmentCount) =>
        segmentCount <= 5 ? 4 : 40;

    private static StepDownPhase ClassifyStepDownPhase(CentipedeSegment segment)
    {
        if (segment.ColliderId != StepDownTerrain.ColliderId
            || segment.SupportNormal.LengthSquared() < 1e-10f)
        {
            return StepDownPhase.None;
        }
        Vector3 normal = segment.SupportNormal.Normalized();
        Vector3 point = segment.SupportPoint;
        if (normal.Dot(Vector3.Right) >= 0.78f
            && Mathf.Abs(point.X) <= 0.25f
            && point.Y >= -0.25f && point.Y <= StepDownTerrain.TopY + 0.25f)
        {
            return StepDownPhase.Wall;
        }
        if (normal.Dot(Vector3.Up) >= 0.78f
            && Mathf.Abs(point.Y - StepDownTerrain.TopY) <= 0.25f
            && point.X <= 0.25f)
        {
            return StepDownPhase.Top;
        }
        if (normal.Dot(Vector3.Up) >= 0.78f
            && Mathf.Abs(point.Y) <= 0.25f
            && point.X >= -0.25f)
        {
            return StepDownPhase.LowerFloor;
        }
        return StepDownPhase.None;
    }

    private enum StepDownPhase
    {
        None,
        Top,
        Wall,
        LowerFloor,
    }

    private enum CoursePhase
    {
        None,
        Floor,
        Slope,
        InnerWall,
        Top,
        OuterWall,
        Ceiling,
    }

    private readonly record struct CourseRun(int[] LeadFirst, int[] TailFirst,
        int MaxTransitionTicks, int MaxBlockedTicks,
        int MaxDisconnectTicks, float MaxPenetration,
        bool Finite, int TicksRun, Vector3 FinalLead, Vector3 FinalTail,
        float MaxLeadX, float MaxLeadY, float FinalDeviationRatio,
        Vector3 FinalSupportPoint, Vector3 FinalSupportNormal,
        ulong FinalColliderId, CentipedeSurfaceSample FinalLeadSample,
        int MaxDisconnectStart, int MaxDisconnectEnd,
        int MaxDisconnectConnection, int CollisionDrivenDisconnectTicks,
        long SnagReleases, int WorstDisconnectTick,
        int WorstConnectionIndex, float WorstConnectionRatio,
        float WorstRelaxRatio, Vector3 WorstAPos, Vector3 WorstBPos,
        bool WorstAContact, bool WorstBContact,
        Vector3 WorstANormal, Vector3 WorstBNormal,
        CoursePhase WorstAPhase, CoursePhase WorstBPhase);

    /// <summary>
    /// 同一个解析碰撞体依次给出地板、18° 斜坡、墙脚内角与墙顶外角；第二块碰撞体
    /// 给出天花板底面。这里先钉 ITerrainQuery 几何语义，再由运动回归验证控制器是否
    /// 真正沿这些面延伸路径，避免「路线没走到但射线实现也错了」的混合归因。
    /// </summary>
    private static bool CheckTerrainPrimitives(out string message)
    {
        var terrain = new CentipedeCourseTerrain();
        bool floor = terrain.Raycast(new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f),
            out TerrainHit floorHit)
            && Near(floorHit.Point, Vector3.Zero)
            && Near(floorHit.Normal, Vector3.Up)
            && floorHit.ColliderId == CentipedeCourseTerrain.CourseColliderId;

        float slopeY = CentipedeCourseTerrain.SlopeHeightAt(3f);
        bool slope = terrain.Raycast(new Vector3(3f, slopeY + 1f, 0f),
                new Vector3(3f, slopeY - 1f, 0f), out TerrainHit slopeHit)
            && Mathf.Abs(slopeHit.Point.Y - slopeY) <= Epsilon
            && slopeHit.Normal.Dot(CentipedeCourseTerrain.SlopeNormal) >= 0.9999f;

        bool wall = terrain.Raycast(new Vector3(6f, 2f, 0f), new Vector3(8f, 2f, 0f),
                out TerrainHit wallHit)
            && Mathf.Abs(wallHit.Point.X - CentipedeCourseTerrain.WallX) <= Epsilon
            && Near(wallHit.Normal, Vector3.Left);

        Vector3 corner = new(CentipedeCourseTerrain.WallX, CentipedeCourseTerrain.WallTopY, 0f);
        Vector3 cornerCenter = corner + new Vector3(-0.05f, 0.05f, 0f);
        bool edge = terrain.SpherePenetration(cornerCenter, 0.1f,
                out Vector3 edgePush, out float edgeDepth)
            && edgePush.Dot(new Vector3(-1f, 1f, 0f).Normalized()) >= 0.999f
            && Mathf.Abs(edgeDepth - (0.1f - Mathf.Sqrt(0.005f))) <= Epsilon;

        bool ceiling = terrain.Raycast(new Vector3(10f, 3.5f, 0f),
                new Vector3(10f, 4.5f, 0f), out TerrainHit ceilingHit)
            && Mathf.Abs(ceilingHit.Point.Y - CentipedeCourseTerrain.CeilingY) <= Epsilon
            && Near(ceilingHit.Normal, Vector3.Down)
            && ceilingHit.ColliderId == CentipedeCourseTerrain.CourseColliderId;

        bool inside = terrain.Raycast(new Vector3(10f, 4.5f, 0f),
                new Vector3(10f, 3.5f, 0f), out TerrainHit insideHit)
            && insideHit.Normal == Vector3.Zero;

        message = $"floor/slope/wall/edge/ceiling/inside=" +
                  $"{floor}/{slope}/{wall}/{edge}/{ceiling}/{inside}";
        return floor && slope && wall && edge && ceiling && inside;
    }

    private static bool Near(Vector3 a, Vector3 b) => a.DistanceSquaredTo(b) <= Epsilon * Epsilon;
    private static bool Near(float a, float b) => Mathf.Abs(a - b) <= Epsilon;

    /// <summary>无地形：自避、纯装配与 lifecycle 状态测试用。</summary>
    private sealed class EmptyTerrain : ITerrainQuery
    {
        public long RayCount { get; private set; }
        public long ShapeQueryCount { get; private set; }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            RayCount++;
            hit = default;
            return false;
        }

        public bool SpherePenetration(Vector3 center, float radius,
            out Vector3 pushDir, out float depth)
        {
            ShapeQueryCount++;
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }

    /// <summary>以 X=0 为中心的无限薄墙实体，用来隔离足端跨墙恢复而不引入地面抓点。</summary>
    private sealed class ThinWallTerrain : ITerrainQuery
    {
        public const float HalfWidth = 0.025f;
        private const ulong ColliderId = 404UL;
        public int PenetrationHitCount { get; private set; }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (Mathf.Abs(from.X) < HalfWidth)
            {
                hit = new TerrainHit(from, Vector3.Zero, ColliderId);
                return true;
            }

            float deltaX = to.X - from.X;
            if (Mathf.Abs(deltaX) <= 1e-10f)
            {
                return false;
            }

            float boundary;
            Vector3 normal;
            if (from.X >= HalfWidth && to.X < HalfWidth)
            {
                boundary = HalfWidth;
                normal = Vector3.Right;
            }
            else if (from.X <= -HalfWidth && to.X > -HalfWidth)
            {
                boundary = -HalfWidth;
                normal = Vector3.Left;
            }
            else
            {
                return false;
            }

            float t = (boundary - from.X) / deltaX;
            if (t < 0f || t > 1f)
            {
                return false;
            }
            hit = new TerrainHit(from.Lerp(to, t), normal, ColliderId);
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius,
            out Vector3 pushDir, out float depth)
        {
            float distanceFromSurface = Mathf.Abs(center.X) - HalfWidth;
            depth = radius - distanceFromSurface;
            if (depth <= 0f)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            PenetrationHitCount++;
            pushDir = center.X < 0f ? Vector3.Left : Vector3.Right;
            return true;
        }
    }

    /// <summary>先在连续地板上种脚，再按需打开无限薄墙，复现停驶 stance 抓点被隔断。</summary>
    private sealed class OccludableFloorTerrain : ITerrainQuery
    {
        private const ulong FloorColliderId = 405UL;
        private readonly ThinWallTerrain _wall = new();

        public bool WallEnabled { get; set; }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            TerrainHit wallHit = default;
            bool wallFound = WallEnabled && _wall.Raycast(from, to, out wallHit);
            bool floorFound = RaycastFloor(from, to, out TerrainHit floorHit);
            if (!wallFound)
            {
                hit = floorHit;
                return floorFound;
            }
            if (!floorFound
                || from.DistanceSquaredTo(wallHit.Point)
                <= from.DistanceSquaredTo(floorHit.Point))
            {
                hit = wallHit;
                return true;
            }
            hit = floorHit;
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius,
            out Vector3 pushDir, out float depth)
        {
            if (WallEnabled && _wall.SpherePenetration(
                    center, radius, out pushDir, out depth))
            {
                return true;
            }
            depth = radius - center.Y;
            if (depth <= 0f)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            pushDir = Vector3.Up;
            return true;
        }

        private static bool RaycastFloor(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (from.Y < 0f)
            {
                hit = new TerrainHit(from, Vector3.Zero, FloorColliderId);
                return true;
            }
            float deltaY = to.Y - from.Y;
            if (deltaY >= -1e-10f || to.Y > 0f)
            {
                return false;
            }
            float t = -from.Y / deltaY;
            hit = new TerrainHit(from.Lerp(to, t), Vector3.Up, FloorColliderId);
            return true;
        }
    }

    /// <summary>
    /// 无限地板与 3m×0.4m 墙盒的 XY 截面；沿 Z 无限延伸。地板和墙保留独立 collider
    /// ID，与 Godot sandbox 的两个 StaticBody3D 一致。碰撞边界只暴露实体并集的外轮廓，
    /// 不会把墙底与地板重合处当成可抓取表面。
    /// </summary>
    private sealed class NarrowWallTerrain : ITerrainQuery
    {
        public const ulong FloorColliderId = 505UL;
        public const ulong WallColliderId = 506UL;
        public const float WallOffset = 6f;
        public const float HalfWidth = 0.2f;
        public const float TopY = 3f;
        private const float Extent = 1000f;

        private readonly Boundary[] _boundaries;
        public float CenterX { get; }
        private float MinX => CenterX - HalfWidth;
        private float MaxX => CenterX + HalfWidth;

        public long RayCount { get; private set; }
        public long ShapeQueryCount { get; private set; }

        public NarrowWallTerrain(float centerX)
        {
            CenterX = centerX;
            _boundaries =
            [
                new Boundary(new Vector2(-Extent, 0f), new Vector2(MinX, 0f),
                    new Vector2(0f, 1f), FloorColliderId),
                new Boundary(new Vector2(MinX, 0f), new Vector2(MinX, TopY),
                    Vector2.Left, WallColliderId),
                new Boundary(new Vector2(MinX, TopY), new Vector2(MaxX, TopY),
                    new Vector2(0f, 1f), WallColliderId),
                new Boundary(new Vector2(MaxX, TopY), new Vector2(MaxX, 0f),
                    Vector2.Right, WallColliderId),
                new Boundary(new Vector2(MaxX, 0f), new Vector2(Extent, 0f),
                    new Vector2(0f, 1f), FloorColliderId),
            ];
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            RayCount++;
            hit = default;
            Vector2 start = new(from.X, from.Y);
            if (InsideFloor(start))
            {
                hit = new TerrainHit(from, Vector3.Zero, FloorColliderId);
                return true;
            }
            if (InsideWall(start))
            {
                hit = new TerrainHit(from, Vector3.Zero, WallColliderId);
                return true;
            }

            Vector2 end = new(to.X, to.Y);
            Vector2 ray = end - start;
            bool found = false;
            float bestT = float.MaxValue;
            Boundary best = default;
            foreach (Boundary boundary in _boundaries)
            {
                Vector2 edge = boundary.B - boundary.A;
                float denominator = Cross(ray, edge);
                if (Mathf.Abs(denominator) <= 1e-8f)
                {
                    continue;
                }
                Vector2 relative = boundary.A - start;
                float t = Cross(relative, edge) / denominator;
                float u = Cross(relative, ray) / denominator;
                if (t < -Epsilon || t > 1f + Epsilon
                    || u < -Epsilon || u > 1f + Epsilon
                    || ray.Dot(boundary.Normal) >= 0f || t >= bestT)
                {
                    continue;
                }
                found = true;
                bestT = Mathf.Clamp(t, 0f, 1f);
                best = boundary;
            }
            if (!found)
            {
                return false;
            }

            Vector3 point = from.Lerp(to, bestT);
            hit = new TerrainHit(point,
                new Vector3(best.Normal.X, best.Normal.Y, 0f), best.ColliderId);
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius,
            out Vector3 pushDir, out float depth)
        {
            ShapeQueryCount++;
            Vector2 point = new(center.X, center.Y);
            bool inside = InsideFloor(point) || InsideWall(point);
            float bestDistanceSquared = float.MaxValue;
            Vector2 closest = Vector2.Zero;
            Vector2 closestNormal = new(0f, 1f);
            foreach (Boundary boundary in _boundaries)
            {
                Vector2 edge = boundary.B - boundary.A;
                float edgeLengthSquared = edge.LengthSquared();
                float t = edgeLengthSquared <= 1e-12f
                    ? 0f
                    : Mathf.Clamp((point - boundary.A).Dot(edge)
                        / edgeLengthSquared, 0f, 1f);
                Vector2 candidate = boundary.A + edge * t;
                float distanceSquared = point.DistanceSquaredTo(candidate);
                if (distanceSquared >= bestDistanceSquared)
                {
                    continue;
                }
                bestDistanceSquared = distanceSquared;
                closest = candidate;
                closestNormal = boundary.Normal;
            }

            float distance = Mathf.Sqrt(bestDistanceSquared);
            depth = inside ? radius + distance : radius - distance;
            if (depth <= 0f)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            Vector2 delta = inside ? closest - point : point - closest;
            Vector2 push = delta.LengthSquared() <= 1e-12f
                ? closestNormal : delta.Normalized();
            pushDir = new Vector3(push.X, push.Y, 0f);
            return true;
        }

        private bool InsideFloor(Vector2 point) => point.Y < 0f;

        private bool InsideWall(Vector2 point) =>
            point.X > MinX && point.X < MaxX
            && point.Y >= 0f && point.Y < TopY;

        private static float Cross(Vector2 a, Vector2 b) =>
            a.X * b.Y - a.Y * b.X;

        private readonly record struct Boundary(
            Vector2 A, Vector2 B, Vector2 Normal, ulong ColliderId);
    }

    /// <summary>
    /// XY 截面的单碰撞体台阶：x&lt;0 是高台，x&gt;0 是低地，中间外墙连接两面。
    /// 固定 +X 输入在两块水平面上都有明确意义，只在外墙上退化，恰好隔离切向续接契约。
    /// </summary>
    private sealed class StepDownTerrain : ITerrainQuery
    {
        public const ulong ColliderId = 303UL;
        public const float TopY = 1.6f;
        private readonly Vector2[] _vertices =
        [
            new Vector2(-1000f, -1000f),
            new Vector2(1000f, -1000f),
            new Vector2(1000f, 0f),
            new Vector2(0f, 0f),
            new Vector2(0f, TopY),
            new Vector2(-1000f, TopY),
        ];

        public long RayCount { get; private set; }
        public long ShapeQueryCount { get; private set; }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            RayCount++;
            hit = default;
            Vector2 start = new(from.X, from.Y);
            Vector2 end = new(to.X, to.Y);
            Vector2 ray = end - start;
            if (Contains(start))
            {
                hit = new TerrainHit(from, Vector3.Zero, ColliderId);
                return true;
            }

            bool found = false;
            float bestT = float.MaxValue;
            Vector2 bestNormal = Vector2.Zero;
            for (int i = 0; i < _vertices.Length; i++)
            {
                Vector2 a = _vertices[i];
                Vector2 edge = _vertices[(i + 1) % _vertices.Length] - a;
                float denominator = Cross(ray, edge);
                if (Mathf.Abs(denominator) <= 1e-8f)
                {
                    continue;
                }
                Vector2 relative = a - start;
                float t = Cross(relative, edge) / denominator;
                float u = Cross(relative, ray) / denominator;
                Vector2 outward = new(edge.Y, -edge.X);
                if (t < -Epsilon || t > 1f + Epsilon
                    || u < -Epsilon || u > 1f + Epsilon
                    || ray.Dot(outward) >= 0f || t >= bestT)
                {
                    continue;
                }
                found = true;
                bestT = Mathf.Clamp(t, 0f, 1f);
                bestNormal = outward.Normalized();
            }
            if (!found)
            {
                return false;
            }
            Vector3 point = from.Lerp(to, bestT);
            hit = new TerrainHit(point,
                new Vector3(bestNormal.X, bestNormal.Y, 0f), ColliderId);
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius,
            out Vector3 pushDir, out float depth)
        {
            ShapeQueryCount++;
            Vector2 point = new(center.X, center.Y);
            bool inside = Contains(point);
            float minimumDistanceSquared = float.MaxValue;
            Vector2 closest = Vector2.Zero;
            Vector2 closestNormal = Vector2.Up;
            for (int i = 0; i < _vertices.Length; i++)
            {
                Vector2 a = _vertices[i];
                Vector2 edge = _vertices[(i + 1) % _vertices.Length] - a;
                float edgeLengthSquared = edge.LengthSquared();
                float t = edgeLengthSquared <= 1e-12f
                    ? 0f
                    : Mathf.Clamp((point - a).Dot(edge) / edgeLengthSquared, 0f, 1f);
                Vector2 candidate = a + edge * t;
                float distanceSquared = point.DistanceSquaredTo(candidate);
                if (distanceSquared >= minimumDistanceSquared)
                {
                    continue;
                }
                minimumDistanceSquared = distanceSquared;
                closest = candidate;
                closestNormal = new Vector2(edge.Y, -edge.X).Normalized();
            }

            float distance = Mathf.Sqrt(minimumDistanceSquared);
            depth = inside ? radius + distance : radius - distance;
            if (depth <= 0f)
            {
                pushDir = Vector3.Zero;
                depth = 0f;
                return false;
            }
            Vector2 delta = inside ? closest - point : point - closest;
            Vector2 push = delta.LengthSquared() <= 1e-12f
                ? closestNormal : delta.Normalized();
            pushDir = new Vector3(push.X, push.Y, 0f);
            return true;
        }

        private bool Contains(Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = _vertices.Length - 1; i < _vertices.Length; j = i++)
            {
                Vector2 a = _vertices[i];
                Vector2 b = _vertices[j];
                bool crosses = (a.Y > point.Y) != (b.Y > point.Y)
                    && point.X < (b.X - a.X) * (point.Y - a.Y)
                    / (b.Y - a.Y) + a.X;
                if (crosses)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static float Cross(Vector2 a, Vector2 b) =>
            a.X * b.Y - a.Y * b.X;
    }

    /// <summary>
    /// XY 截面的无限 Z 棱柱解析地形。course 是一个凹多边形：左侧低地经 18° 斜坡
    /// 到墙脚，墙顶横梁提供上表面、右侧外角与向下法线的天花板底面。整条路线保持
    /// 同一 ColliderId，额外钉住跨角时碰撞体连续性。
    /// </summary>
    private sealed class CentipedeCourseTerrain : ITerrainQuery
    {
        public const ulong CourseColliderId = 101UL;
        public const float WallX = 7f;
        public const float WallTopY = 5f;
        public const float CeilingY = 4f;
        public const float OuterWallX = 15f;

        private const float SlopeStartX = 2f;
        private const float SlopeEndX = 5f;
        private const float ColumnRightX = 8f;
        private static readonly float SlopeRise = Mathf.Tan(Mathf.DegToRad(18f));
        public static readonly Vector3 SlopeNormal =
            new Vector3(-SlopeRise, 1f, 0f).Normalized();

        private readonly Polygon[] _solids;

        public long RayCount { get; private set; }
        public long ShapeQueryCount { get; private set; }

        public CentipedeCourseTerrain()
        {
            float slopeTop = SlopeHeightAt(SlopeEndX);
            _solids =
            [
                new Polygon(CourseColliderId,
                [
                    new Vector2(-1000f, -1000f),
                    new Vector2(1000f, -1000f),
                    new Vector2(1000f, slopeTop),
                    new Vector2(ColumnRightX, slopeTop),
                    new Vector2(ColumnRightX, CeilingY),
                    new Vector2(OuterWallX, CeilingY),
                    new Vector2(OuterWallX, WallTopY),
                    new Vector2(WallX, WallTopY),
                    new Vector2(WallX, slopeTop),
                    new Vector2(SlopeEndX, slopeTop),
                    new Vector2(SlopeStartX, 0f),
                    new Vector2(-1000f, 0f),
                ]),
            ];
        }

        public static float SlopeHeightAt(float x) =>
            (x - SlopeStartX) * SlopeRise;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            RayCount++;
            hit = default;
            Vector2 start = new(from.X, from.Y);
            Vector2 end = new(to.X, to.Y);
            Vector2 ray = end - start;

            foreach (Polygon solid in _solids)
            {
                if (Contains(solid.Vertices, start))
                {
                    hit = new TerrainHit(from, Vector3.Zero, solid.ColliderId);
                    return true;
                }
            }

            bool found = false;
            float bestT = float.MaxValue;
            Vector2 bestNormal = Vector2.Zero;
            ulong bestCollider = 0UL;
            foreach (Polygon solid in _solids)
            {
                Vector2[] vertices = solid.Vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector2 a = vertices[i];
                    Vector2 edge = vertices[(i + 1) % vertices.Length] - a;
                    float denominator = Cross(ray, edge);
                    if (Mathf.Abs(denominator) <= 1e-8f)
                    {
                        continue;
                    }

                    Vector2 relative = a - start;
                    float t = Cross(relative, edge) / denominator;
                    float u = Cross(relative, ray) / denominator;
                    Vector2 outward = new(edge.Y, -edge.X);
                    if (t < -Epsilon || t > 1f + Epsilon
                        || u < -Epsilon || u > 1f + Epsilon
                        || ray.Dot(outward) >= 0f
                        || t >= bestT)
                    {
                        continue;
                    }

                    found = true;
                    bestT = Mathf.Clamp(t, 0f, 1f);
                    bestNormal = outward.Normalized();
                    bestCollider = solid.ColliderId;
                }
            }

            if (!found)
            {
                return false;
            }

            Vector3 point = from.Lerp(to, bestT);
            hit = new TerrainHit(point,
                new Vector3(bestNormal.X, bestNormal.Y, 0f), bestCollider);
            return true;
        }

        public bool SpherePenetration(Vector3 center, float radius,
            out Vector3 pushDir, out float depth)
        {
            ShapeQueryCount++;
            Vector2 point = new(center.X, center.Y);
            bool found = false;
            float bestDepth = 0f;
            Vector2 bestPush = Vector2.Zero;

            foreach (Polygon solid in _solids)
            {
                bool inside = Contains(solid.Vertices, point);
                float minDistanceSquared = float.MaxValue;
                Vector2 closest = Vector2.Zero;
                Vector2 closestNormal = Vector2.Up;
                Vector2[] vertices = solid.Vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector2 a = vertices[i];
                    Vector2 edge = vertices[(i + 1) % vertices.Length] - a;
                    float edgeLengthSquared = edge.LengthSquared();
                    float t = edgeLengthSquared <= 1e-12f
                        ? 0f
                        : Mathf.Clamp((point - a).Dot(edge) / edgeLengthSquared, 0f, 1f);
                    Vector2 candidate = a + edge * t;
                    float distanceSquared = point.DistanceSquaredTo(candidate);
                    if (distanceSquared >= minDistanceSquared)
                    {
                        continue;
                    }
                    minDistanceSquared = distanceSquared;
                    closest = candidate;
                    closestNormal = new Vector2(edge.Y, -edge.X).Normalized();
                }

                float distance = Mathf.Sqrt(minDistanceSquared);
                float candidateDepth = inside ? radius + distance : radius - distance;
                if (candidateDepth <= 0f || candidateDepth <= bestDepth)
                {
                    continue;
                }

                Vector2 delta = inside ? closest - point : point - closest;
                Vector2 candidatePush = delta.LengthSquared() <= 1e-12f
                    ? closestNormal
                    : delta.Normalized();
                found = true;
                bestDepth = candidateDepth;
                bestPush = candidatePush;
            }

            pushDir = new Vector3(bestPush.X, bestPush.Y, 0f);
            depth = bestDepth;
            return found;
        }

        private static bool Contains(Vector2[] polygon, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                bool crosses = (a.Y > point.Y) != (b.Y > point.Y)
                    && point.X < (b.X - a.X) * (point.Y - a.Y)
                    / (b.Y - a.Y) + a.X;
                if (crosses)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private readonly record struct Polygon(ulong ColliderId, Vector2[] Vertices);
    }
}
