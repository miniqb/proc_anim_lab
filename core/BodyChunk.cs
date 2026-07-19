using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 身体的最小物理单元：带质量的球形粒子（对标雨世界 BodyChunk）。
/// 纯数据容器——积分/约束/碰撞逻辑全部在 <see cref="Body"/>，保证 tick 顺序唯一。
/// Vel 的语义是「米/tick 位移」：积分时直接 Pos += Vel，不乘 dt（确定性来源）。
/// LastPos 仅用于渲染插值与碰撞方向判定，不参与速度推算。
/// </summary>
public sealed class BodyChunk
{
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public readonly float Radius;
    public readonly float Mass;

    public bool CollideWithTerrain = true;

    /// <summary>本 tick 是否接触地形（碰撞阶段开头清 false，命中后置 true）。</summary>
    public bool TerrainContact;

    /// <summary>最近一次接触的表面法线（斜坡探针与 M2 落脚复用）。</summary>
    public Vector3 ContactNormal;

    /// <summary>上一 tick 是否有接触（触发接触法向探针射线）。</summary>
    public bool HadContactLastTick;

    public BodyChunk(Vector3 pos, float radius, float mass)
    {
        Pos = pos;
        LastPos = pos;
        Vel = Vector3.Zero;
        Radius = radius;
        Mass = mass;
    }

    /// <summary>渲染插值位置：t = 物理插值分数 ∈ [0,1)。</summary>
    public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
}
