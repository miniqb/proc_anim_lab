using Godot;

namespace ProcAnimLab.Render;

/// <summary>
/// 渲染期两骨 IK（≙ RW Custom.InverseKinematic，Custom.cs:1201）。膝/肘是纯渲染派生，
/// 物理层只有足端粒子。RW 的关键手感细节原样保留：余弦钳制在 [0.2, 0.98]——
/// 关节**永远不完全伸直也不完全对折**，这是"有机感"的暗门。
/// </summary>
internal static class TwoBoneIk
{
    /// <summary>解出中间关节位置。poleDir 决定弯折侧（会被投影到垂直于 root→target 轴的平面）。</summary>
    public static Vector3 Solve(Vector3 root, Vector3 target, float upperLen, float lowerLen, Vector3 poleDir)
    {
        Vector3 to = target - root;
        float dist = to.Length();
        if (dist < 1e-5f)
        {
            return root + poleDir.Normalized() * upperLen;
        }
        Vector3 axis = to / dist;

        // 余弦定理 + RW 钳制（0.2~0.98：不伸直、不对折）。
        float cos = (dist * dist + upperLen * upperLen - lowerLen * lowerLen) / (2f * dist * upperLen);
        cos = Mathf.Clamp(cos, 0.2f, 0.98f);
        float along = cos * upperLen;
        float outward = Mathf.Sqrt(Mathf.Max(0f, upperLen * upperLen - along * along));

        Vector3 side = poleDir - axis * poleDir.Dot(axis);
        if (side.LengthSquared() < 1e-8f)
        {
            // pole 与轴共线的退化：任选稳定正交向量（轴近竖直时换 Right 基）。
            side = axis.Cross(Vector3.Up);
            if (side.LengthSquared() < 1e-8f)
            {
                side = axis.Cross(Vector3.Right);
            }
        }
        return root + axis * along + side.Normalized() * outward;
    }
}
