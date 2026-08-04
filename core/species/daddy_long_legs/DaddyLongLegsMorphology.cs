using System;
using Godot;

namespace ProcAnim.Core.Species.DaddyLongLegs;

/// <summary>一只 DaddyLongLegs 已冻结的身体球出生数据。</summary>
public readonly struct DaddyBodyChunkSpec
{
    public readonly Vector3 RestOffset;
    public readonly float Radius;
    public readonly float Mass;

    public DaddyBodyChunkSpec(Vector3 restOffset, float radius, float mass)
    {
        RestOffset = restOffset;
        Radius = radius;
        Mass = mass;
    }
}

/// <summary>一条已冻结触手的锚点、长度、段数和无前向语义的材料局部偏好。</summary>
public readonly struct DaddyTentacleSpec
{
    public readonly int AnchorBodyIndex;
    public readonly float Length;
    public readonly int SegmentCount;
    public readonly Vector3 LocalPreference;

    public DaddyTentacleSpec(
        int anchorBodyIndex,
        float length,
        int segmentCount,
        Vector3 localPreference)
    {
        AnchorBodyIndex = anchorBodyIndex;
        Length = length;
        SegmentCount = segmentCount;
        LocalPreference = localPreference;
    }
}

/// <summary>
/// 工厂按 stable seed 生成后冻结的个体形态。数组不会暴露可写引用；同预设+同 seed
/// 必须逐位相同，不同 seed 的差异由 smoke 覆盖。
/// </summary>
public sealed class DaddyLongLegsMorphology
{
    private readonly DaddyBodyChunkSpec[] _bodyChunks;
    private readonly float[] _restDistances;
    private readonly DaddyTentacleSpec[] _tentacles;

    public string StableId { get; }
    public ulong StableSeed { get; }
    public ReadOnlySpan<DaddyBodyChunkSpec> BodyChunks => _bodyChunks;
    public ReadOnlySpan<float> RestDistances => _restDistances;
    public ReadOnlySpan<DaddyTentacleSpec> Tentacles => _tentacles;
    public int FrameLandmarkA { get; }
    public int FrameLandmarkB { get; }
    public int FrameLandmarkC { get; }
    public int ConnectionCount => _restDistances.Length;
    public int TotalTentacleSegments { get; }
    public float TotalTentacleLength { get; }

    internal DaddyLongLegsMorphology(
        string stableId,
        ulong stableSeed,
        DaddyBodyChunkSpec[] bodyChunks,
        float[] restDistances,
        DaddyTentacleSpec[] tentacles,
        int frameLandmarkA,
        int frameLandmarkB,
        int frameLandmarkC)
    {
        StableId = stableId;
        StableSeed = stableSeed;
        _bodyChunks = (DaddyBodyChunkSpec[])bodyChunks.Clone();
        _restDistances = (float[])restDistances.Clone();
        _tentacles = (DaddyTentacleSpec[])tentacles.Clone();
        FrameLandmarkA = frameLandmarkA;
        FrameLandmarkB = frameLandmarkB;
        FrameLandmarkC = frameLandmarkC;
        int segments = 0;
        float length = 0f;
        foreach (DaddyTentacleSpec spec in _tentacles)
        {
            segments += spec.SegmentCount;
            length += spec.Length;
        }
        TotalTentacleSegments = segments;
        TotalTentacleLength = length;
    }

    public DaddyBodyChunkSpec BodyChunkAt(int index) => _bodyChunks[index];
    public float RestDistanceAt(int connectionIndex) => _restDistances[connectionIndex];
    public DaddyTentacleSpec TentacleAt(int index) => _tentacles[index];
}
