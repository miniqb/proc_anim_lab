using Godot;

namespace ProcAnimLab.Physics;

/// <summary>
/// 球 vs 地形命中的共用解算：Body 的 chunk 碰撞与 Limb 的脚部出地形走同一份语义，
/// 保证「无反弹 + 切向摩擦」的手感只有一个实现。只推 Pos 不注入反向速度（静息防抖）。
/// </summary>
public static class SphereTerrain
{
    /// <summary>
    /// 解穿透 + 速度响应。返回是否真的产生了接触（normal 输出接触法线）。
    /// HitFromInside（零法线）时回退到 fallbackPos 并清零速度——归一化零向量会 NaN，必须特判。
    /// </summary>
    public static bool Resolve(in TerrainHit hit, float radius, float surfaceFriction,
        ref Vector3 pos, ref Vector3 vel, in Vector3 fallbackPos, out Vector3 normal)
    {
        if (hit.Normal.LengthSquared() < 1e-12f)
        {
            pos = fallbackPos;
            vel = Vector3.Zero;
            normal = Vector3.Zero;
            return true;
        }

        normal = hit.Normal;
        float depth = radius - (pos - hit.Point).Dot(normal);
        if (depth <= 0f)
        {
            return false;
        }

        pos += normal * depth;
        float vn = vel.Dot(normal);
        if (vn < 0f)
        {
            vel -= normal * vn;
        }
        Vector3 vt = vel - normal * vel.Dot(normal);
        vel -= vt * (1f - surfaceFriction);
        return true;
    }
}
