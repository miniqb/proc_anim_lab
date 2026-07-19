using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Godot;

namespace ProcAnim.Core.Smoke;

/// <summary>
/// 无引擎冒烟回归：内核完全脱离 Godot 运行时跑在纯 .NET 进程里（M5「与引擎解耦」
/// 的实证），也是回迁后目标仓库最快的内核回归入口。
/// 纯解析平面地形 + default 品种平地巡走（前半直行、后半 45° 转向）。
/// 断言：双跑 bit-exact、哈希对基线（防「确定但错误」）、里程过阈、终态约束收敛、
/// 无 NaN、内核程序集引擎边界干净（TypeRef 扫描）。
/// </summary>
internal static class Program
{
    private const int Ticks = 1000;

    /// <summary>与沙盒一致：40 tick/s、重力 36 m/s²（≙ RW 0.9px/tick²）。</summary>
    private const float TickDt = 0.025f;
    private const float GravityMps2 = 36f;

    /// <summary>基线哈希 = 内核行为的指纹。**只有有意改物理时才允许更新**（与 CLAUDE.md §5
    /// 的哈希表同步改）——只比双跑一致会漏掉「确定但错误」的行为漂移。</summary>
    private const ulong ExpectedHash = 0x653886DEBB5B3F60UL;

    private static int Main()
    {
        (ulong Hash, float Walk, float Grip, float RaysPerTick, bool Nan, float EndDev) a = Run();
        (ulong Hash, float Walk, float Grip, float RaysPerTick, bool Nan, float EndDev) b = Run();
        bool boundaryOk = CheckEngineBoundary(out string boundaryMsg);
        bool embedOk = CheckEmbedRecovery(out string embedMsg);
        bool shiftOk = CheckShiftContinuity(out string shiftMsg);

        Console.WriteLine($"[CORE-DET] ticks={Ticks} run1={a.Hash:X16} run2={b.Hash:X16} expected={ExpectedHash:X16}");
        Console.WriteLine($"[CORE-METRIC] walkDistance={a.Walk:F2}m avgLegsGripping={a.Grip:F2}/4 " +
                          $"raysPerTick={a.RaysPerTick:F1} endDev={a.EndDev:F4}m nan={a.Nan}");
        Console.WriteLine($"[CORE-BOUNDARY] {boundaryMsg}");
        Console.WriteLine($"[CORE-EMBED] {embedMsg}");
        Console.WriteLine($"[CORE-SHIFT] {shiftMsg}");

        var reasons = new List<string>();
        if (a.Hash != b.Hash)
        {
            reasons.Add("双跑哈希不一致");
        }
        if (a.Hash != ExpectedHash)
        {
            reasons.Add("哈希偏离基线（有意改内核请同步更新 ExpectedHash 与 CLAUDE.md §5）");
        }
        if (a.Walk <= 15f)
        {
            reasons.Add($"行走里程不足（{a.Walk:F2}m）");
        }
        if (a.Nan)
        {
            reasons.Add("状态出现 NaN/Inf");
        }
        if (a.EndDev >= 0.05f)
        {
            reasons.Add($"终态约束偏差过大（{a.EndDev:F4}m，碰撞后量取）");
        }
        if (!boundaryOk)
        {
            reasons.Add("内核程序集越出引擎边界");
        }
        if (!embedOk)
        {
            reasons.Add("嵌入恢复失败（出生在地形内没有被推出）");
        }
        if (!shiftOk)
        {
            reasons.Add("Shift 后步态中断（rebase 契约被破坏）");
        }

        bool pass = reasons.Count == 0;
        Console.WriteLine(pass
            ? "[CORE-SMOKE] PASS：双跑 bit-exact、哈希对基线、约束收敛、边界干净、无 NaN"
            : $"[CORE-SMOKE] FAIL：{string.Join("；", reasons)}");
        return pass ? 0 : 1;
    }

    private static (ulong, float, float, float, bool, float) Run()
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
        return (hasher.Value, walk, (float)gripSum / Ticks, (float)terrain.RayCount / Ticks,
            nan, walker.Body.CurrentMaxDeviation());
    }

    /// <summary>
    /// 嵌入恢复：出生在地板下，SpherePenetration 的 MTD 必须在数 tick 内把全身推出
    /// （外部评审 P1-3：旧版 HitFromInside 回退 LastPos + 清速 = 出生嵌入永久冻结）。
    /// </summary>
    private static bool CheckEmbedRecovery(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        Walker walker = BodyFactory.CreateWalker(new Vector3(0f, -0.1f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        for (long tick = 1; tick <= 50; tick++)
        {
            walker.MoveDir = Vector3.Zero;
            walker.RunSpeed = 0f;
            walker.Tick(new TickContext(gravityPerTick, terrain, tick));
        }
        float minY = float.MaxValue;
        foreach (BodyChunk c in walker.Body.Chunks)
        {
            minY = Math.Min(minY, c.Pos.Y);
        }
        message = $"出生 y=-0.1 嵌入地板，50 tick 后最低 chunk y={minY:F3}（须 ≥ 0）";
        return minY > -1e-3f;
    }

    /// <summary>
    /// rebase 连续性：行走中整体 Shift(+512,0,+512)（宿主 teleport/浮点原点重置的契约入口），
    /// 步态必须无缝续走。坐标进入不同浮点尾数区间，bit-exact 不可期待——断言的是
    /// 「平移不打断动力学」：续走里程过阈、无 NaN、约束收敛。
    /// </summary>
    private static bool CheckShiftContinuity(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        Walker walker = BodyFactory.CreateWalker(new Vector3(0f, 0.6f, 0f), BodyFactory.Default());
        var gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        long t = 0;
        for (int i = 0; i < 300; i++)
        {
            t++;
            walker.MoveDir = new Vector3(1f, 0f, 0f);
            walker.RunSpeed = 1f;
            walker.Tick(new TickContext(gravityPerTick, terrain, t));
        }
        walker.Shift(new Vector3(512f, 0f, 512f));
        Vector3 start = walker.Head.Pos;
        for (int i = 0; i < 300; i++)
        {
            t++;
            walker.MoveDir = new Vector3(1f, 0f, 0f);
            walker.RunSpeed = 1f;
            walker.Tick(new TickContext(gravityPerTick, terrain, t));
        }
        Vector3 d = walker.Head.Pos - start;
        d.Y = 0f;
        bool nan = !walker.Head.Pos.IsFinite();
        float dev = walker.Body.CurrentMaxDeviation();
        message = $"Shift(+512,0,+512) 后 300 tick 续走 {d.Length():F2}m，endDev={dev:F4}m（须 >15m 且收敛）";
        return d.Length() > 15f && !nan && dev < 0.05f;
    }

    /// <summary>
    /// 内核程序集的引擎边界扫描：除数学允许清单外出现任何 Godot.* 类型引用即 FAIL。
    /// GodotSharp 包里 Node/GD/PhysicsServer3D 全部编译期可达——「不引 Godot.NET.Sdk」
    /// 只挡住了场景树源生成器，真正的强制靠这道 TypeRef 扫描（离线、秒级，回迁后照跑）。
    /// </summary>
    private static bool CheckEngineBoundary(out string message)
    {
        var allowed = new HashSet<string> { "Vector3", "Mathf" };
        using FileStream fs = File.OpenRead(typeof(Body).Assembly.Location);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var offenders = new List<string>();
        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference tr = md.GetTypeReference(handle);
            string ns = md.GetString(tr.Namespace);
            if (!ns.StartsWith("Godot", StringComparison.Ordinal))
            {
                continue;
            }
            string name = md.GetString(tr.Name);
            if (ns == "Godot" && allowed.Contains(name))
            {
                continue;
            }
            offenders.Add($"{ns}.{name}");
        }
        message = offenders.Count == 0
            ? $"ProcAnim.Core 引擎类型引用 = 允许清单 [{string.Join(", ", allowed)}]，边界干净"
            : $"越界引用: {string.Join(", ", offenders)}";
        return offenders.Count == 0;
    }
}
