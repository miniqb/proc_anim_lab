using Godot;

namespace ProcAnim.Core.Species.Cicada;

/// <summary>
/// CicadaGraphics 式单粒子触须末端。只做拖曳/停驻表现，不计支撑且不向 Body 回传力。
/// </summary>
public sealed class CicadaTentacleState
{
    public readonly int Side;
    public readonly int Pair;
    public readonly float Length;

    public Vector3 Anchor;
    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;
    public bool Attached;

    public CicadaTentacleState(int side, int pair, float length)
    {
        Side = side;
        Pair = pair;
        Length = length;
    }

    internal void Reset(Vector3 anchor, Vector3 pos)
    {
        Anchor = anchor;
        Pos = LastPos = pos;
        Vel = Vector3.Zero;
        Attached = false;
    }

    internal void Shift(Vector3 delta)
    {
        Anchor += delta;
        Pos += delta;
        LastPos += delta;
    }
}
