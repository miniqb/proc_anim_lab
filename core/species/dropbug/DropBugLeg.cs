using Godot;

namespace ProcAnim.Core.Species.DropBug;

/// <summary>
/// 纯表现腿（≙ DropBugGraphics.legs：Limb[2,2] 全部挂在 chunk0 上，不参与物理、
/// 不向身体回传力）。本轮不做渲染美化，只维持「步频由身体实际位移驱动」的观测量：
/// 足端位置、是否踩住、累计换步数。落点由控制器用一根支撑向射线钉在真实表面。
/// </summary>
public sealed class DropBugLeg
{
    /// <summary>左 -1 / 右 +1。</summary>
    public readonly int Side;

    /// <summary>0 = 前对，1 = 后对。</summary>
    public readonly int Pair;

    /// <summary>相位序号（≙ DropBugGraphics 步态相位 num7×0.25）。</summary>
    public readonly int Index;

    public Vector3 Pos;
    public Vector3 LastPos;
    public Vector3 Vel;

    /// <summary>足端当前踩住真实表面（悬挂时踩住悬挂面）。</summary>
    public bool Planted;

    /// <summary>踩住时的表面法线（调试渲染用）。</summary>
    public Vector3 PlantNormal = Vector3.Up;

    /// <summary>累计换步数——静止时必须不增长（步频驱动关系的回归观测量）。</summary>
    public long StepSerial;

    /// <summary>逐腿平滑的行进方向（≙ legsTravelDirs；原作用随机权重 Lerp，
    /// 本项目改为 lift 驱动的确定性权重）。</summary>
    public Vector3 TravelDir;

    public DropBugLeg(int side, int pair, int index, Vector3 pos)
    {
        Side = side;
        Pair = pair;
        Index = index;
        Pos = pos;
        LastPos = pos;
    }

    internal void Shift(Vector3 delta)
    {
        Pos += delta;
        LastPos += delta;
    }
}
