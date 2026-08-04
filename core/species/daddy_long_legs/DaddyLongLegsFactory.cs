using System;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.DaddyLongLegs;

/// <summary>
/// DaddyLongLegs 稳定预设与确定性出生装配器。入口刻意不接收 forward：这类身体没有头尾轴。
/// 形态采样是 seed+domain+index 的无状态纯函数，不使用 System.Random、Godot RNG 或全局状态。
/// </summary>
public static class DaddyLongLegsFactory
{
    public const string BrotherId = "daddy-long-legs/brother";
    public const string DaddyId = "daddy-long-legs/daddy";
    public const string TerrorId = "daddy-long-legs/terror";

    /// <summary>
    /// 对应当前 DLL 的 BrotherLongLegs 档：身体 Range(4,7)=4..6、预算 8；触手
    /// Range(5,10)=5..9，总长 Lerp(1600px,N*300px,.5)。3D 接触与运动参数为本项目取值。
    /// </summary>
    public static DaddyLongLegsParams Brother() => new()
    {
        StableId = BrotherId,
        MinimumBodyChunks = 4,
        MaximumBodyChunks = 6,
        BodyBudgetUnits = 8f,
        MinimumTentacles = 5,
        MaximumTentacles = 9,
        TentacleBudgetBase = 40f,
        TentacleBudgetPerTentacle = 7.5f,
        MaximumSegmentsPerTentacle = 14,
        MaximumTotalTentacleSegments = 64,
        MaximumTerrainQueriesPerTick = 1700,
        SegmentPathServo = 0.038f,
        SegmentTargetServo = 0.062f,
        BaseDrive = 0.024f,
        MaxMoveSpeed = 0.13f,
        MoveTargetArriveRadius = 0.38f,
        MinimumLocomotionTentacles = 2,
    };

    /// <summary>
    /// 对应当前 DLL 的常规 DaddyLongLegs 档：身体 4..7、预算 12；触手 5..12，
    /// 总长 Lerp(3000px,N*400px,.5)。所有 px 均按 1px=0.025m 转换。
    /// </summary>
    public static DaddyLongLegsParams Daddy() => new()
    {
        StableId = DaddyId,
        MinimumBodyChunks = 4,
        MaximumBodyChunks = 7,
        BodyBudgetUnits = 12f,
        MinimumTentacles = 5,
        MaximumTentacles = 12,
        TentacleBudgetBase = 75f,
        TentacleBudgetPerTentacle = 10f,
        MaximumSegmentsPerTentacle = 18,
        MaximumTotalTentacleSegments = 120,
        MaximumTerrainQueriesPerTick = 2900,
    };

    /// <summary>
    /// TerrorLongLegs 的可控 3D 大型档。原 DLL 是身体 4..15、预算18、触手5..12、
    /// Lerp(3000px,N*1000px,.5)；那套极端尾部分布在连续 3D 球查询下可超过 300 段。
    /// 本项目改成身体8..12、触手9..12、每条至多20段且总段数≤144，长度预算的每触手项
    /// 从25m降到16m；保留“更大、更密、长触手”的手感，同时给查询量硬上限。
    /// </summary>
    public static DaddyLongLegsParams Terror() => new()
    {
        StableId = TerrorId,
        MinimumBodyChunks = 8,
        MaximumBodyChunks = 12,
        BodyBudgetUnits = 18f,
        MinimumTentacles = 9,
        MaximumTentacles = 12,
        TentacleBudgetBase = 75f,
        TentacleBudgetPerTentacle = 16f,
        MaximumSegmentsPerTentacle = 20,
        MaximumTotalTentacleSegments = 144,
        MaximumTerrainQueriesPerTick = 4050,
        BodyConstraintIterations = 9,
        SegmentRadius = 0.0825f,
        SegmentMass = 0.052f,
        SegmentPathServo = 0.029f,
        SegmentTargetServo = 0.050f,
        BaseDrive = 0.017f,
        MaxMoveSpeed = 0.095f,
        MoveTargetArriveRadius = 0.58f,
        MinimumLocomotionTentacles = 3,
        DutyChangeCooldownTicks = 7,
    };

    public static DaddyLongLegsParams[] AllPresets() => [Brother(), Daddy(), Terror()];

    public static bool TryByStableId(string? stableId, out DaddyLongLegsParams parameters)
    {
        parameters = stableId switch
        {
            BrotherId => Brother(),
            DaddyId => Daddy(),
            TerrorId => Terror(),
            _ => null!,
        };
        return parameters is not null;
    }

    public static DaddyLongLegsParams ByStableId(string stableId)
    {
        if (TryByStableId(stableId, out DaddyLongLegsParams parameters))
            return parameters;
        throw new ArgumentException($"Unknown DaddyLongLegs stable ID '{stableId}'.", nameof(stableId));
    }

    public static DaddyLongLegsLocomotionController CreateController(
        Vector3 origin,
        ulong stableSeed) => CreateController(origin, Daddy(), stableSeed);

    public static DaddyLongLegsLocomotionController CreateController(
        Vector3 origin,
        string stableId,
        ulong stableSeed) => CreateController(origin, ByStableId(stableId), stableSeed);

    public static DaddyLongLegsLocomotionController CreateController(
        Vector3 origin,
        DaddyLongLegsParams parameters,
        ulong stableSeed)
    {
        if (!Finite(origin))
            throw new ArgumentException("Origin must be finite.", nameof(origin));
        ArgumentNullException.ThrowIfNull(parameters);
        DaddyLongLegsParams snapshot = parameters.Snapshot();
        DaddyLongLegsMorphology morphology = GenerateMorphology(snapshot, stableSeed);

        var body = new Body
        {
            GravityScale = 1f,
            AirFriction = snapshot.UnsupportedAirFriction,
            SurfaceFriction = snapshot.UnsupportedSurfaceFriction,
            ConstraintIterations = snapshot.BodyConstraintIterations,
            Skin = snapshot.TerrainSkin,
            EnablePostCollisionStructureRecovery = true,
            SnagStretchRatio = 4f,
            SnagReleaseTicks = 120,
        };

        for (int i = 0; i < morphology.BodyChunks.Length; i++)
        {
            DaddyBodyChunkSpec spec = morphology.BodyChunkAt(i);
            body.Chunks.Add(new BodyChunk(origin + spec.RestOffset, spec.Radius, spec.Mass));
        }

        int connectionIndex = 0;
        for (int i = 0; i < body.Chunks.Count; i++)
        {
            for (int j = i + 1; j < body.Chunks.Count; j++)
            {
                BodyChunk a = body.Chunks[i];
                BodyChunk b = body.Chunks[j];
                float weightA = b.Mass / (a.Mass + b.Mass);
                var connection = new ChunkConnection(
                    a, b, morphology.RestDistanceAt(connectionIndex++), weightA)
                {
                    ConstraintMode = ChunkConnection.Mode.Rigid,
                    TerrainCoupled = true,
                };
                body.Connections.Add(connection);
            }
        }

        // ChunkConnection 的通用副作用会设置 RotationChunk；本物种没有朝向语义，明确清空。
        foreach (BodyChunk chunk in body.Chunks)
            chunk.RotationChunk = null;

        var tentacles = new DaddyTentacle[morphology.Tentacles.Length];
        for (int i = 0; i < tentacles.Length; i++)
        {
            DaddyTentacleSpec spec = morphology.TentacleAt(i);
            tentacles[i] = new DaddyTentacle(
                i,
                body.Chunks[spec.AnchorBodyIndex],
                spec,
                snapshot,
                stableSeed);
        }

        return new DaddyLongLegsLocomotionController(
            snapshot, morphology, body, tentacles);
    }

    public static DaddyLongLegsMorphology GenerateMorphology(
        DaddyLongLegsParams parameters,
        ulong stableSeed)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DaddyLongLegsParams p = parameters.Snapshot();
        int bodyCount = SampleInt(stableSeed, 0xB001UL, 0,
            p.MinimumBodyChunks, p.MaximumBodyChunks);
        var radii = new float[bodyCount];
        var masses = new float[bodyCount];
        var offsets = new Vector3[bodyCount];
        float remaining = p.BodyBudgetUnits;
        for (int i = 0; i < bodyCount; i++)
        {
            float t = bodyCount == 1 ? 1f : (float)i / (bodyCount - 1);
            float lower = remaining * 0.20f;
            float upper = remaining * Mathf.Lerp(0.30f, 1f, t);
            float exponent = 1f - t;
            float sample = Sample01(stableSeed, 0xB010UL, i);
            float allocation = Mathf.Lerp(lower, upper,
                exponent <= 0f ? 1f : Mathf.Pow(sample, exponent));
            remaining -= allocation;
            radii[i] = p.BodyRadiusBase + allocation * p.BodyRadiusPerBudgetUnit;
            masses[i] = Mathf.Max(0.01f, allocation * p.BodyMassPerBudgetUnit);
            offsets[i] = SampleUnitVector(stableSeed, 0xB020UL, i) * radii[i];
        }

        for (int iteration = 0; iteration < p.RestLayoutIterations; iteration++)
        {
            for (int a = 0; a < bodyCount; a++)
            {
                for (int b = 0; b < bodyCount; b++)
                {
                    if (a == b)
                        continue;
                    Vector3 delta = offsets[a] - offsets[b];
                    float distance = delta.Length();
                    float minimum = (radii[a] + radii[b]) * p.RestOverlapRatio;
                    if (distance < minimum)
                    {
                        // DLL: list[l] -= DirVec(list[l], list[k]) * overlap。
                        // DirVec(l,k) 指向 k，因此这里必须用 b→a；减去它才把 b 推离 a。
                        Vector3 direction = distance > 1e-6f
                            ? delta / distance
                            : SampleUnitVector(stableSeed,
                                0xB030UL + (ulong)iteration,
                                a * bodyCount + b);
                        offsets[b] -= direction * (minimum - distance);
                    }
                }
            }
            for (int i = 0; i < bodyCount; i++)
                offsets[i] *= p.RestContraction;
        }

        Vector3 weightedCenter = Vector3.Zero;
        float totalMass = 0f;
        for (int i = 0; i < bodyCount; i++)
        {
            weightedCenter += offsets[i] * masses[i];
            totalMass += masses[i];
        }
        weightedCenter /= totalMass;
        var bodySpecs = new DaddyBodyChunkSpec[bodyCount];
        for (int i = 0; i < bodyCount; i++)
        {
            offsets[i] -= weightedCenter;
            bodySpecs[i] = new DaddyBodyChunkSpec(offsets[i], radii[i], masses[i]);
        }

        var restDistances = new float[bodyCount * (bodyCount - 1) / 2];
        int connection = 0;
        for (int i = 0; i < bodyCount; i++)
            for (int j = i + 1; j < bodyCount; j++)
                restDistances[connection++] = offsets[i].DistanceTo(offsets[j]);

        SelectFrameLandmarks(offsets, out int landmarkA, out int landmarkB, out int landmarkC);
        int tentacleCount = SampleInt(stableSeed, 0xD001UL, 0,
            p.MinimumTentacles, p.MaximumTentacles);
        float totalLength = Mathf.Lerp(
            p.TentacleBudgetBase,
            tentacleCount * p.TentacleBudgetPerTentacle,
            p.TentacleBudgetBlend);
        var lengths = new float[tentacleCount];
        for (int i = 0; i < tentacleCount; i++)
            lengths[i] = totalLength / tentacleCount;

        int passes = p.LengthRedistributionPassesPerTentacle * tentacleCount;
        for (int pass = 0; pass < passes; pass++)
        {
            int donor = SampleInt(stableSeed, 0xD010UL, pass, 0, tentacleCount - 1);
            float transfer = lengths[donor]
                * Sample01(stableSeed, 0xD011UL, pass)
                * p.MaximumRedistributionFraction;
            if (lengths[donor] - transfer > p.MinimumTentacleLength)
            {
                int recipient = SampleInt(stableSeed, 0xD012UL, pass, 0, tentacleCount - 1);
                lengths[recipient] += transfer;
                lengths[donor] -= transfer;
            }
        }

        var segmentCounts = new int[tentacleCount];
        int totalSegments = 0;
        for (int i = 0; i < tentacleCount; i++)
        {
            segmentCounts[i] = Math.Clamp(
                (int)(lengths[i] / p.NominalSegmentLength),
                p.MinimumSegmentsPerTentacle,
                p.MaximumSegmentsPerTentacle);
            totalSegments += segmentCounts[i];
        }
        while (totalSegments > p.MaximumTotalTentacleSegments)
        {
            int reduce = -1;
            float longestLink = float.NegativeInfinity;
            for (int i = 0; i < tentacleCount; i++)
            {
                if (segmentCounts[i] <= p.MinimumSegmentsPerTentacle)
                    continue;
                float link = lengths[i] / segmentCounts[i];
                if (link > longestLink)
                {
                    longestLink = link;
                    reduce = i;
                }
            }
            if (reduce < 0)
                throw new InvalidOperationException("Total tentacle segment cap cannot satisfy minimums.");
            segmentCounts[reduce]--;
            totalSegments--;
        }

        float phase = Sample01(stableSeed, 0xD020UL, 0) * Mathf.Tau;
        float goldenAngle = Mathf.Pi * (3f - Mathf.Sqrt(5f));
        var tentacleSpecs = new DaddyTentacleSpec[tentacleCount];
        for (int i = 0; i < tentacleCount; i++)
        {
            float y = 1f - 2f * ((i + 0.5f) / tentacleCount);
            float radial = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float angle = phase + goldenAngle * i;
            var preference = new Vector3(
                Mathf.Cos(angle) * radial,
                y,
                Mathf.Sin(angle) * radial);
            tentacleSpecs[i] = new DaddyTentacleSpec(
                i % bodyCount,
                lengths[i],
                segmentCounts[i],
                preference);
        }

        return new DaddyLongLegsMorphology(
            p.StableId,
            stableSeed,
            bodySpecs,
            restDistances,
            tentacleSpecs,
            landmarkA,
            landmarkB,
            landmarkC);
    }

    internal static float Sample01(ulong seed, ulong domain, int index)
    {
        ulong bits = Mix(seed ^ domain ^ unchecked((ulong)(uint)index * 0x9E3779B97F4A7C15UL));
        return (float)((bits >> 40) * (1.0 / 16777216.0));
    }

    internal static Vector3 SampleUnitVector(ulong seed, ulong domain, int index)
    {
        float z = 1f - 2f * Sample01(seed, domain, index * 2);
        float angle = Mathf.Tau * Sample01(seed, domain, index * 2 + 1);
        float radial = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
        return new Vector3(radial * Mathf.Cos(angle), z, radial * Mathf.Sin(angle));
    }

    private static int SampleInt(
        ulong seed,
        ulong domain,
        int index,
        int minimumInclusive,
        int maximumInclusive)
    {
        int count = maximumInclusive - minimumInclusive + 1;
        int offset = Math.Min(count - 1, (int)(Sample01(seed, domain, index) * count));
        return minimumInclusive + offset;
    }

    private static ulong Mix(ulong value)
    {
        ulong z = value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static void SelectFrameLandmarks(
        Vector3[] offsets,
        out int landmarkA,
        out int landmarkB,
        out int landmarkC)
    {
        landmarkA = 0;
        landmarkB = 1;
        float bestDistance = -1f;
        for (int i = 0; i < offsets.Length; i++)
        {
            for (int j = i + 1; j < offsets.Length; j++)
            {
                float distance = offsets[i].DistanceSquaredTo(offsets[j]);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    landmarkA = i;
                    landmarkB = j;
                }
            }
        }

        Vector3 axis = offsets[landmarkB] - offsets[landmarkA];
        float axisLengthSquared = axis.LengthSquared();
        landmarkC = -1;
        float bestOffAxis = -1f;
        for (int i = 0; i < offsets.Length; i++)
        {
            if (i == landmarkA || i == landmarkB)
                continue;
            float t = axisLengthSquared > 1e-10f
                ? Mathf.Clamp((offsets[i] - offsets[landmarkA]).Dot(axis) / axisLengthSquared, 0f, 1f)
                : 0f;
            float offAxis = offsets[i].DistanceSquaredTo(offsets[landmarkA] + axis * t);
            if (offAxis > bestOffAxis)
            {
                bestOffAxis = offAxis;
                landmarkC = i;
            }
        }
        if (landmarkC < 0)
            landmarkC = (landmarkB + 1) % offsets.Length;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
