using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Terrain;

namespace ProcAnimLab.SpiderSandbox;

/// <summary>蜘蛛所在/候选落点所在的表面类别（按法线与世界上方向的夹角三分）。</summary>
public enum SpiderSurfaceKind
{
    Ground,
    Wall,
    Ceiling,
}

/// <summary>一个接近路径点：表面点 + 法线 + 类别 + 评分（调试显示用）。</summary>
public readonly struct SpiderStalkWaypoint
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly SpiderSurfaceKind Kind;
    public readonly float Score;
    public readonly float Progress;

    public SpiderStalkWaypoint(Vector3 point, Vector3 normal, SpiderSurfaceKind kind,
        float score, float progress)
    {
        Point = point;
        Normal = normal;
        Kind = kind;
        Score = score;
        Progress = progress;
    }
}

/// <summary>
/// 潜行接近规划器（宿主层 AI，不进内核——CLAUDE.md §4：寻路归宿主，内核只吃邻近可达点）。
/// 每次只产出**一个**邻近路径点（默认 2.6m 内），由候选采样 + 打分选出：
/// ① 沿当前支撑面切平面 12 向采样、投影到真实表面；② 水平扫墙 8 向，命中竖直面即给
/// 「爬上去」的墙面候选；③ 已在墙/顶时向上探天花板，给顶面候选。
/// 打分 = 朝猎物的水平推进 + 表面加成（墙 &lt; 顶）+ 之字项（期望侧逐点交替）− 直线惩罚
/// + 确定性抖动。猎物很近时切换为绕行模式（推进权重降、之字权重翻倍）——
/// 冷却期在猎物身边转圈而不是撞上去。全部随机数来自本类 xorshift32，seed 可复现。
/// 表面类别、攻击范围裁决都不在这里：这里只回答「下一步往哪爬」。
/// </summary>
public sealed class SpiderStalkPlanner
{
    public float StepRadius = 2.6f;
    public float ClimbScanRadius = 4.5f;
    public float WallBonus = 0.5f;
    public float CeilingBonus = 0.8f;
    public float ZigzagWeight = 0.5f;
    public float StraightPenalty = 0.35f;
    public float StraightAngleDegrees = 18f;
    public float MinProgress = 0.12f;
    public float CircleRadius = 3.0f;
    public float JitterWeight = 0.15f;

    /// <summary>墙面候选的目标高度带（相对地面）。</summary>
    public float ClimbMinHeight = 1.2f;
    public float ClimbMaxHeight = 2.6f;

    private const int TangentSamples = 12;
    private const int ClimbSamples = 8;
    private const int CeilingSamples = 6;

    private readonly List<SpiderStalkWaypoint> _candidates = new();
    private uint _rng;
    private int _zigzagSign = 1;

    /// <summary>上次规划的全部候选（调试绘制/日志用，规划后有效）。</summary>
    public IReadOnlyList<SpiderStalkWaypoint> LastCandidates => _candidates;

    public SpiderStalkPlanner(int seed)
    {
        Reseed(seed);
    }

    public void Reseed(int seed)
    {
        _rng = unchecked((uint)seed) | 1u;
        _zigzagSign = 1;
    }

    public static SpiderSurfaceKind ClassifySurface(Vector3 normal, Vector3 worldUp)
    {
        float dot = normal.Dot(worldUp);
        if (dot > 0.5f)
        {
            return SpiderSurfaceKind.Ground;
        }
        return dot < -0.5f ? SpiderSurfaceKind.Ceiling : SpiderSurfaceKind.Wall;
    }

    /// <summary>
    /// 规划下一路径点。失败（周围没有任何可投影到真实表面的候选）返回 false，
    /// 宿主可退化为直接朝猎物走。
    /// </summary>
    public bool TryPlan(ITerrainQuery terrain, Vector3 spiderPos, Vector3 supportNormal,
        Vector3 worldUp, Vector3 preyPos, float roomHeight, out SpiderStalkWaypoint waypoint)
    {
        _candidates.Clear();
        Vector3 n = supportNormal.LengthSquared() > 1e-8f ? supportNormal.Normalized() : worldUp;
        SpiderSurfaceKind currentKind = ClassifySurface(n, worldUp);
        Vector3 toPreyH = Horizontal(preyPos - spiderPos, worldUp);
        float preyDistance = toPreyH.Length();
        Vector3 preyDir = preyDistance > 1e-4f ? toPreyH / preyDistance : Vector3.Right;
        Vector3 side = worldUp.Cross(preyDir);
        side = side.LengthSquared() > 1e-8f ? side.Normalized() : Vector3.Forward;
        bool circling = preyDistance < CircleRadius;

        CollectTangentSamples(terrain, spiderPos, n, worldUp);
        CollectClimbSamples(terrain, spiderPos, worldUp, preyPos, roomHeight);
        if (currentKind != SpiderSurfaceKind.Ground)
        {
            CollectCeilingSamples(terrain, spiderPos, worldUp, preyDir);
        }

        int best = -1;
        float bestScore = float.NegativeInfinity;
        float cosStraight = Mathf.Cos(Mathf.DegToRad(StraightAngleDegrees));
        for (int i = 0; i < _candidates.Count; i++)
        {
            SpiderStalkWaypoint c = _candidates[i];
            Vector3 offset = c.Point - spiderPos;
            if (offset.Length() < 0.6f)
            {
                continue; // 原地踏步的候选没有意义
            }
            float candidateDistance = Horizontal(preyPos - c.Point, worldUp).Length();
            float progress = (preyDistance - candidateDistance) / Mathf.Max(0.1f, StepRadius);
            Vector3 offsetH = Horizontal(offset, worldUp);
            float lateral = offsetH.Dot(side) / Mathf.Max(0.1f, StepRadius);
            float zigzag = ZigzagWeight * lateral * _zigzagSign;
            float straight = 0f;
            if (offsetH.LengthSquared() > 1e-6f
                && offsetH.Normalized().Dot(preyDir) > cosStraight)
            {
                straight = -StraightPenalty;
            }
            float bonus = c.Kind switch
            {
                SpiderSurfaceKind.Wall => WallBonus,
                SpiderSurfaceKind.Ceiling => CeilingBonus,
                _ => 0f,
            };
            float jitter = JitterWeight * (NextUnit() - 0.5f);
            float score;
            if (circling)
            {
                if (progress < -0.5f)
                {
                    continue;
                }
                score = progress * 0.15f + bonus + zigzag * 2f + jitter;
            }
            else
            {
                if (progress < MinProgress)
                {
                    continue;
                }
                score = progress + bonus + zigzag + straight + jitter;
            }
            _candidates[i] = new SpiderStalkWaypoint(c.Point, c.Normal, c.Kind, score, progress);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0)
        {
            // 没有满足推进门的候选：放宽到「任何候选里最接近猎物的」。
            float bestFallback = float.PositiveInfinity;
            for (int i = 0; i < _candidates.Count; i++)
            {
                float d = Horizontal(preyPos - _candidates[i].Point, worldUp).Length();
                if (d < bestFallback && (_candidates[i].Point - spiderPos).Length() >= 0.6f)
                {
                    bestFallback = d;
                    best = i;
                }
            }
        }
        if (best < 0)
        {
            waypoint = default;
            return false;
        }
        waypoint = _candidates[best];
        _zigzagSign = -_zigzagSign;
        return true;
    }

    private void CollectTangentSamples(ITerrainQuery terrain, Vector3 spiderPos, Vector3 n,
        Vector3 worldUp)
    {
        Vector3 t1 = Mathf.Abs(n.Dot(worldUp)) < 0.9f ? worldUp.Cross(n) : Vector3.Right.Cross(n);
        if (t1.LengthSquared() < 1e-8f)
        {
            t1 = Vector3.Forward.Cross(n);
        }
        t1 = t1.Normalized();
        Vector3 t2 = n.Cross(t1).Normalized();
        for (int k = 0; k < TangentSamples; k++)
        {
            float angle = k * (Mathf.Tau / TangentSamples);
            Vector3 dir = t1 * Mathf.Cos(angle) + t2 * Mathf.Sin(angle);
            Vector3 probe = spiderPos + dir * StepRadius + n * 0.35f;
            if (terrain.Raycast(probe, probe - n * 1.4f, out TerrainHit hit)
                && hit.Normal.LengthSquared() > 1e-8f)
            {
                AddCandidate(hit, worldUp);
                continue;
            }
            // 越过当前面的边缘：改用世界向下探地板。
            if (terrain.Raycast(probe, probe - worldUp * 4.5f, out TerrainHit floor)
                && floor.Normal.LengthSquared() > 1e-8f)
            {
                AddCandidate(floor, worldUp);
            }
        }
    }

    private void CollectClimbSamples(ITerrainQuery terrain, Vector3 spiderPos, Vector3 worldUp,
        Vector3 preyPos, float roomHeight)
    {
        Vector3 origin = spiderPos + worldUp * 0.2f;
        Vector3 axisA = Mathf.Abs(worldUp.Dot(Vector3.Right)) < 0.9f
            ? worldUp.Cross(Vector3.Right).Normalized()
            : worldUp.Cross(Vector3.Forward).Normalized();
        Vector3 axisB = worldUp.Cross(axisA).Normalized();
        float preyHeight = (preyPos - spiderPos).Dot(worldUp) + spiderPos.Dot(worldUp);
        for (int k = 0; k < ClimbSamples; k++)
        {
            float angle = k * (Mathf.Tau / ClimbSamples) + 0.2f;
            Vector3 dir = axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle);
            if (!terrain.Raycast(origin, origin + dir * ClimbScanRadius, out TerrainHit hit)
                || hit.Normal.LengthSquared() < 1e-8f
                || Mathf.Abs(hit.Normal.Dot(worldUp)) > 0.5f)
            {
                continue;
            }
            float maxHeight = Mathf.Min(ClimbMaxHeight, roomHeight - 0.6f);
            float targetHeight = Mathf.Clamp(
                preyHeight + 1.6f + (NextUnit() - 0.5f) * 0.8f, ClimbMinHeight, maxHeight);
            Vector3 footOfWall = hit.Point;
            float currentHeight = footOfWall.Dot(worldUp);
            Vector3 wallPoint = footOfWall + worldUp * (targetHeight - currentHeight);
            Vector3 probe = wallPoint + hit.Normal * 0.4f;
            if (terrain.Raycast(probe, probe - hit.Normal * 0.8f, out TerrainHit wall)
                && wall.Normal.LengthSquared() > 1e-8f
                && Mathf.Abs(wall.Normal.Dot(worldUp)) <= 0.5f)
            {
                AddCandidate(wall, worldUp);
            }
        }
    }

    private void CollectCeilingSamples(ITerrainQuery terrain, Vector3 spiderPos, Vector3 worldUp,
        Vector3 preyDir)
    {
        if (!terrain.Raycast(spiderPos, spiderPos + worldUp * ClimbScanRadius, out TerrainHit ceiling)
            || ceiling.Normal.LengthSquared() < 1e-8f
            || ceiling.Normal.Dot(worldUp) > -0.5f)
        {
            return;
        }
        Vector3 side = worldUp.Cross(preyDir);
        side = side.LengthSquared() > 1e-8f ? side.Normalized() : Vector3.Forward;
        for (int k = 0; k < CeilingSamples; k++)
        {
            float angle = -Mathf.Pi * 0.5f + k * (Mathf.Pi / (CeilingSamples - 1));
            Vector3 dir = preyDir * Mathf.Cos(angle) + side * Mathf.Sin(angle);
            Vector3 point = ceiling.Point + dir * StepRadius;
            Vector3 probe = point - worldUp * 0.4f;
            if (terrain.Raycast(probe, probe + worldUp * 0.8f, out TerrainHit hit)
                && hit.Normal.LengthSquared() > 1e-8f
                && hit.Normal.Dot(worldUp) < -0.5f)
            {
                AddCandidate(hit, worldUp);
            }
        }
    }

    private void AddCandidate(in TerrainHit hit, Vector3 worldUp)
    {
        Vector3 normal = hit.Normal.Normalized();
        _candidates.Add(new SpiderStalkWaypoint(hit.Point, normal,
            ClassifySurface(normal, worldUp), 0f, 0f));
    }

    private static Vector3 Horizontal(Vector3 v, Vector3 worldUp) => v - worldUp * v.Dot(worldUp);

    private float NextUnit()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng & 0xFFFFFFu) / (float)0x1000000;
    }
}
