using System.Collections.Generic;
using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 确定性探针：按固定顺序把所有 chunk 的 Pos/Vel 六个 float 的原始位折叠进 FNV-1a 64 哈希，
/// 每隔 checkpoint 打印一次，跑满 runTicks 后打终值。目标是同机同构建 bit-exact——
/// 不量化，任何一位漂移（偷读 delta/墙钟/迭代顺序不稳）都会被抓到。
/// </summary>
public sealed class DeterminismProbe
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly int _runTicks;
    private readonly int _checkpointInterval;
    private ulong _hash = FnvOffset;

    public bool Finished { get; private set; }

    public DeterminismProbe(int runTicks, int checkpointInterval = 100)
    {
        _runTicks = runTicks;
        _checkpointInterval = checkpointInterval;
    }

    public void Record(long tick, IReadOnlyList<Body> bodies)
    {
        if (Finished)
        {
            return;
        }

        foreach (Body body in bodies)
        {
            foreach (BodyChunk c in body.Chunks)
            {
                Fold(c.Pos);
                Fold(c.Vel);
            }
        }

        if (tick % _checkpointInterval == 0 || tick >= _runTicks)
        {
            GD.Print($"[DET] tick={tick} hash={_hash:X16}");
        }
        if (tick >= _runTicks)
        {
            Finished = true;
        }
    }

    private void Fold(Vector3 v)
    {
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.X));
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.Y));
        FoldBits(System.BitConverter.SingleToUInt32Bits(v.Z));
    }

    private void FoldBits(uint bits)
    {
        for (int i = 0; i < 4; i++)
        {
            _hash = (_hash ^ ((bits >> (i * 8)) & 0xFF)) * FnvPrime;
        }
    }
}
