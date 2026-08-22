using System;
using Godot;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 竞技场命中判定的共享射线数学（从 Daddy 抓取竞技场提升共享）：宿主侧解析求交，
/// 不建物理形体。球 = 精确二次方程；胶囊 = 射线与线段最近点对的近似（命中距离用
/// 最近点的射线参数，够「大致符合命中位置」的定位精度）。
/// </summary>
internal static class RayHitMath
{
    /// <summary>射线 vs 球：返回最近的非负相交距离。</summary>
    public static bool RayHitsSphere(
        Vector3 from, Vector3 direction, Vector3 center, float radius, out float distance)
    {
        distance = 0f;
        Vector3 offset = from - center;
        float b = offset.Dot(direction);
        float c = offset.LengthSquared() - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0f)
            return false;
        float root = MathF.Sqrt(discriminant);
        float t = -b - root;
        if (t < 0f)
            t = -b + root;
        if (t < 0f)
            return false;
        distance = t;
        return true;
    }

    /// <summary>
    /// 射线 vs 胶囊（链边两端 + 半径）：取射线与线段的最近点对，距离 ≤ 半径即命中；
    /// 命中距离用最近点的射线参数近似（够「大致符合命中位置」的断点定位精度）。
    /// </summary>
    public static bool RayHitsCapsule(
        Vector3 from,
        Vector3 direction,
        Vector3 capsuleA,
        Vector3 capsuleB,
        float radius,
        out float distance)
    {
        distance = 0f;
        Vector3 segment = capsuleB - capsuleA;
        Vector3 offset = from - capsuleA;
        float segmentDot = segment.LengthSquared();
        float segmentAlongRay = segment.Dot(direction);
        float offsetAlongSegment = offset.Dot(segment);
        float offsetAlongRay = offset.Dot(direction);
        // 最近点对：s|S|² − t(S·D) = offset·S 与 t = s(S·D) − offset·D 联立
        // （offset = 射线原点 − 胶囊端 A；D 单位向量）。
        float denominator = segmentDot - segmentAlongRay * segmentAlongRay;
        float s = denominator > 1e-8f
            ? Mathf.Clamp(
                (offsetAlongSegment - segmentAlongRay * offsetAlongRay) / denominator, 0f, 1f)
            : 0f;
        float t = MathF.Max(0f, s * segmentAlongRay - offsetAlongRay);
        Vector3 closestOnSegment = capsuleA + segment * s;
        Vector3 closestOnRay = from + direction * t;
        if (closestOnRay.DistanceSquaredTo(closestOnSegment) > radius * radius)
            return false;
        distance = t;
        return true;
    }
}
