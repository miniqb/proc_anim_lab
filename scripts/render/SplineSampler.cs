using System.Collections.Generic;
using Godot;

namespace ProcAnimLab.Render;

/// <summary>扫管的一个采样站：位置 + 显示半径 + 顶点色。半径/颜色是**解析剖面**，与物理
/// chunk 半径解耦（≙ 渲染研究 §1.4——RW 的显示半径从不是 chunk.rad）。</summary>
internal readonly struct TubeStation
{
    public readonly Vector3 Pos;
    public readonly float Radius;
    public readonly Color Color;

    public TubeStation(Vector3 pos, float radius, Color color)
    {
        Pos = pos;
        Radius = radius;
        Color = color;
    }
}

/// <summary>
/// Catmull-Rom 采样器：稀疏控制点（chunk / 关节）→ 稠密管站序列。
/// ≙ RW 三种密化策略（插值中点 / 贝塞尔控制点 / 弧长 pin，渲染研究 §1.3）的 3D 统一形态：
/// RW 的自动柄（(入向+弦向).normalized × 弦/3）本质就是 Catmull-Rom 切线。
/// 半径/颜色沿段用 smoothstep 过渡（线性会在控制点处留下折痕）。
/// </summary>
internal static class SplineSampler
{
    /// <summary>把控制点采样成管站写入 outStations（复用调用方列表，零逐帧分配增长）。
    /// samplesPerSegment ≥ 1：每段内部采样数（端点各站必含）。</summary>
    public static void Sample(List<Vector3> points, List<float> radii, List<Color> colors,
        int samplesPerSegment, List<TubeStation> outStations)
    {
        outStations.Clear();
        int n = points.Count;
        if (n == 0)
        {
            return;
        }
        if (n == 1)
        {
            outStations.Add(new TubeStation(points[0], radii[0], colors[0]));
            return;
        }

        for (int i = 0; i < n - 1; i++)
        {
            Vector3 p0 = points[i];
            Vector3 p1 = points[i + 1];
            // Catmull-Rom 切线：内点用中心差分，端点单边（≙ RW 混合切线 Lerp(B-A, C-B, t) 的连续版）。
            Vector3 m0 = i > 0 ? (p1 - points[i - 1]) * 0.5f : p1 - p0;
            Vector3 m1 = i + 2 < n ? (points[i + 2] - p0) * 0.5f : p1 - p0;

            int last = i == n - 2 ? samplesPerSegment : samplesPerSegment - 1;
            for (int k = 0; k <= last; k++)
            {
                float t = k / (float)samplesPerSegment;
                float t2 = t * t;
                float t3 = t2 * t;
                Vector3 pos = p0 * (2f * t3 - 3f * t2 + 1f)
                    + m0 * (t3 - 2f * t2 + t)
                    + p1 * (-2f * t3 + 3f * t2)
                    + m1 * (t3 - t2);
                float s = t * t * (3f - 2f * t); // smoothstep：半径/颜色在控制点处 C1 连续
                float radius = Mathf.Lerp(radii[i], radii[i + 1], s);
                Color color = colors[i].Lerp(colors[i + 1], s);
                outStations.Add(new TubeStation(pos, radius, color));
            }
        }
    }
}
