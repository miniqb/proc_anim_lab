using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 确定性探针：每 tick 把所有 chunk/limb 状态喂给内核的 <see cref="DeterminismHasher"/>
/// （FNV-1a 64 bit-exact，算法与 smoke/ 无引擎回归共用同一实现），
/// 每隔 checkpoint 打印一次，跑满 runTicks 后打终值。
/// </summary>
public sealed class DeterminismProbe
{
    private readonly int _runTicks;
    private readonly int _checkpointInterval;
    private readonly DeterminismHasher _hasher = new();

    public bool Finished { get; private set; }

    /// <summary>当前折叠哈希（跑满后 = 终值,供 --expect-hash 断言)。</summary>
    public ulong Hash => _hasher.Value;

    public DeterminismProbe(int runTicks, int checkpointInterval = 100)
    {
        _runTicks = runTicks;
        _checkpointInterval = checkpointInterval;
    }

    public void Record(long tick, IReadOnlyList<Body> bodies, IReadOnlyList<Limb>? limbs = null)
    {
        if (Finished)
        {
            return;
        }

        foreach (Body body in bodies)
        {
            _hasher.FoldBody(body);
        }
        if (limbs is not null)
        {
            _hasher.FoldLimbs(limbs);
        }

        if (tick % _checkpointInterval == 0 || tick >= _runTicks)
        {
            GD.Print($"[DET] tick={tick} hash={_hasher.Value:X16}");
        }
        if (tick >= _runTicks)
        {
            Finished = true;
        }
    }
}
