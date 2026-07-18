using Godot;

namespace ProcAnimLab.Physics;

/// <summary>
/// 一个物理 tick 的环境包（传值）：内核不持有任何引擎对象。
/// GravityPerTick 已含步长平方（单位 米/tick²），内核里永远见不到 dt。
/// </summary>
public readonly struct TickContext
{
    public readonly Vector3 GravityPerTick;
    public readonly ITerrainQuery Terrain;
    public readonly long TickIndex;

    public TickContext(Vector3 gravityPerTick, ITerrainQuery terrain, long tickIndex)
    {
        GravityPerTick = gravityPerTick;
        Terrain = terrain;
        TickIndex = tickIndex;
    }
}
