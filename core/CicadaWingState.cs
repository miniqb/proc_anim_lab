using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 一片 Cicada 翼的固定 tick 表现状态。翅膀不产生身体升力；Renderer 只读并插值。
/// </summary>
public sealed class CicadaWingState
{
    public readonly int Side;
    public readonly int Pair;
    public readonly float PhaseOffset;

    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Tip;
    public Vector3 LastTip;

    public CicadaWingState(int side, int pair, float phaseOffset)
    {
        Side = side;
        Pair = pair;
        PhaseOffset = phaseOffset;
    }

    internal void Reset(Vector3 root, Vector3 tip)
    {
        Pos = LastPos = root;
        Tip = LastTip = tip;
    }

    internal void Shift(Vector3 delta)
    {
        Pos += delta;
        LastPos += delta;
        Tip += delta;
        LastTip += delta;
    }
}
