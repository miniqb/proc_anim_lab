using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 状态哈希器：按调用顺序把 Pos/Vel 的 float 原始位（小端逐字节）折叠进 FNV-1a 64。
/// 不量化——任何一位漂移（偷读 delta/墙钟/迭代顺序不稳）都会被抓到。
/// 沙盒确定性探针与 smoke/ 无引擎回归共用此实现：同一具身体同一路线下
/// 折叠序一致（先逐 chunk Pos/Vel、再逐 limb Pos/Vel），两边哈希可直接互证。
/// </summary>
public sealed class DeterminismHasher
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private ulong _hash = FnvOffset;

    public ulong Value => _hash;

    public void Fold(in Vector3 v)
    {
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.X));
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.Y));
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.Z));
    }

    /// <summary>逐 chunk 折叠 Pos/Vel（Chunks 列表序 = 装配序，固定）。</summary>
    public void FoldBody(Body body)
    {
        foreach (BodyChunk c in body.Chunks)
        {
            Fold(c.Pos);
            Fold(c.Vel);
        }
    }

    public void FoldLimbs(IReadOnlyList<Limb> limbs)
    {
        foreach (Limb limb in limbs)
        {
            Fold(limb.Pos);
            Fold(limb.Vel);
        }
    }

    /// <summary>逐翅逐节折叠 Pos/Vel（Wings/Segments 列表序 = 装配序，固定）。
    /// 拍翅相位等控制器标量不进哈希（同守则：影响轨迹的状态由轨迹哈希抓到）。</summary>
    public void FoldWings(IReadOnlyList<VultureWing> wings)
    {
        foreach (VultureWing wing in wings)
        {
            foreach (VultureWing.WingSegment seg in wing.Segments)
            {
                Fold(seg.Pos);
                Fold(seg.Vel);
            }
        }
    }

    private void FoldBits(uint bits)
    {
        for (int i = 0; i < 4; i++)
        {
            _hash = (_hash ^ ((bits >> (i * 8)) & 0xFF)) * FnvPrime;
        }
    }
}
