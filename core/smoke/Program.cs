using System;
using Godot;

namespace ProcAnim.Core.Smoke;

/// <summary>
/// 无引擎冒烟回归：内核完全脱离 Godot 运行时跑在纯 .NET 进程里（M5「与引擎解耦」
/// 的实证），也是回迁后目标仓库最快的内核回归入口。
/// 纯解析平面地形 + default 品种平地巡走（前半直行、后半 45° 转向）：
/// 进程内双跑哈希必须 bit-exact、行走里程过阈、终态无 NaN。
/// </summary>
internal static class Program
{
    private const int Ticks = 1000;

    /// <summary>与沙盒一致：40 tick/s、重力 36 m/s²（≙ RW 0.9px/tick²）。</summary>
    private const float TickDt = 0.025f;
    private const float GravityMps2 = 36f;

    private static int Main()
    {
        (ulong Hash, float Walk, float Grip, float RaysPerTick, bool Nan) a = Run();
        (ulong Hash, float Walk, float Grip, float RaysPerTick, bool Nan) b = Run();

        Console.WriteLine($"[CORE-DET] ticks={Ticks} run1={a.Hash:X16} run2={b.Hash:X16}");
        Console.WriteLine($"[CORE-METRIC] walkDistance={a.Walk:F2}m avgLegsGripping={a.Grip:F2}/4 " +
                          $"raysPerTick={a.RaysPerTick:F1} nan={a.Nan}");

        bool pass = a.Hash == b.Hash && a.Walk > 15f && !a.Nan;
        Console.WriteLine(pass
            ? "[CORE-SMOKE] PASS：双跑 bit-exact、平地巡走正常、无 NaN——内核在纯 .NET 进程内确定运行"
            : "[CORE-SMOKE] FAIL");
        return pass ? 0 : 1;
    }

    private static (ulong, float, float, float, bool) Run()
    {
        var terrain = new PlaneTerrainQuery(0f);
        Walker walker = BodyFactory.CreateWalker(new Vector3(0f, 0.6f, 0f), BodyFactory.Default());
        var hasher = new DeterminismHasher();
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);

        float walk = 0f;
        long gripSum = 0;
        Vector3 lastHead = walker.Head.Pos;
        for (long tick = 1; tick <= Ticks; tick++)
        {
            // 脚本化路线：把「直行步态」与「转弯换步」都纳入哈希。
            walker.MoveDir = tick <= Ticks / 2
                ? new Vector3(1f, 0f, 0f)
                : new Vector3(1f, 0f, 1f).Normalized();
            walker.RunSpeed = 1f;
            walker.Tick(new TickContext(gravityPerTick, terrain, tick));

            hasher.FoldBody(walker.Body);
            hasher.FoldLimbs(walker.Limbs);

            Vector3 step = walker.Head.Pos - lastHead;
            step.Y = 0f;
            walk += step.Length();
            lastHead = walker.Head.Pos;
            gripSum += walker.LegsGripping;
        }

        bool nan = false;
        foreach (BodyChunk c in walker.Body.Chunks)
        {
            nan |= !c.Pos.IsFinite();
        }
        foreach (Limb l in walker.Limbs)
        {
            nan |= !l.Pos.IsFinite();
        }
        return (hasher.Value, walk, (float)gripSum / Ticks, (float)terrain.RayCount / Ticks, nan);
    }
}
