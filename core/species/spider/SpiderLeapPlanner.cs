using Godot;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Spider;

/// <summary>
/// 一次跳跃攻击的规划输入（纯值）。所有速度单位都是米/tick、重力是米/tick²，
/// 与内核积分口径一致；宿主只负责把「攻击距离」的裁决结果翻译成这份请求。
/// </summary>
public readonly struct SpiderLeapRequest
{
    /// <summary>起跳点（主身体节球心）。</summary>
    public readonly Vector3 From;

    /// <summary>规划时刻的瞄准点（猎物身上某点）。</summary>
    public readonly Vector3 Target;

    /// <summary>瞄准点的每 tick 位移（提前量；零 = 不预判）。</summary>
    public readonly Vector3 TargetVelocity;

    public readonly Vector3 GravityPerTick;

    /// <summary>飞行期身体每 tick 的空气阻力系数（≙ Body.AirFriction）。</summary>
    public readonly float AirFriction;

    /// <summary>起跳面法线：起跳速度必须至少以 <see cref="MinOutwardSpeed"/> 离开该面。</summary>
    public readonly Vector3 SurfaceNormal;
    public readonly float MinOutwardSpeed;

    /// <summary>名义扑击速度（米/tick）：飞行 tick 数以「距离 ÷ 该速度」为中心搜索。</summary>
    public readonly float PreferredSpeed;
    public readonly int MinTicks;
    public readonly int MaxTicks;

    /// <summary>起跳速度模长上限（米/tick）。</summary>
    public readonly float MaxSpeed;

    /// <summary>弹道相对起跳点沿世界上方向的最大抬升（米）。</summary>
    public readonly float MaxRise;

    /// <summary>路径扫掠半径（米，≈ 主身体节半径）；≤0 时不做地形扫掠。</summary>
    public readonly float ClearanceRadius;

    public SpiderLeapRequest(
        Vector3 from,
        Vector3 target,
        Vector3 targetVelocity,
        Vector3 gravityPerTick,
        float airFriction,
        Vector3 surfaceNormal,
        float minOutwardSpeed,
        float preferredSpeed,
        int minTicks,
        int maxTicks,
        float maxSpeed,
        float maxRise,
        float clearanceRadius)
    {
        From = from;
        Target = target;
        TargetVelocity = targetVelocity;
        GravityPerTick = gravityPerTick;
        AirFriction = airFriction;
        SurfaceNormal = surfaceNormal;
        MinOutwardSpeed = minOutwardSpeed;
        PreferredSpeed = preferredSpeed;
        MinTicks = minTicks;
        MaxTicks = maxTicks;
        MaxSpeed = maxSpeed;
        MaxRise = maxRise;
        ClearanceRadius = clearanceRadius;
    }
}

/// <summary>规划结果：起跳速度（直接写入全部身体节）+ 飞行 tick 数 + 预计落点。</summary>
public readonly struct SpiderLeapPlan
{
    public readonly Vector3 LaunchVelocity;
    public readonly int FlightTicks;
    public readonly Vector3 PredictedLanding;
    public readonly float PeakRise;

    public float Speed => LaunchVelocity.Length();

    public SpiderLeapPlan(Vector3 launchVelocity, int flightTicks, Vector3 predictedLanding,
        float peakRise)
    {
        LaunchVelocity = launchVelocity;
        FlightTicks = flightTicks;
        PredictedLanding = predictedLanding;
        PeakRise = peakRise;
    }
}

/// <summary>
/// 跳跃攻击弹道规划（≙ RW BigSpider.Attack/Jump 的「朝猎物起跳」，3D 化并改成**精确命中**）。
/// 原作按固定 16px/tick 起跳速度只调方向，攻击距离由跳速隐式决定；本实现反过来——
/// 攻击距离是宿主裁决的设计量，跳速按「N tick 后主身体节恰好落在瞄准点」反解：
/// 内核积分是 <c>Vel += g; Pos += Vel; Vel *= f</c>（Body.Tick 固定序），N 固定时位移对
/// 起跳速度是线性的，闭式可解；N 在名义速度给出的中心附近交替搜索，取第一个满足
/// 「离面、限速、限抬升、路径无地形」的解。飞行期控制器关闭拖尾姿态与推进，两节同速
/// 出发，因此实飞轨迹与规划逐 tick 一致（smoke 钉住误差）。
/// 纯静态数学 + 可选地形扫掠，不持状态、不读随机数。
/// </summary>
public static class SpiderLeapPlanner
{
    /// <summary>用内核相同的积分顺序模拟 N tick 后的位置（与 Body.Tick 逐位同序）。</summary>
    public static Vector3 PositionAfter(Vector3 from, Vector3 launchVelocity,
        Vector3 gravityPerTick, float airFriction, int ticks)
    {
        Vector3 pos = from;
        Vector3 vel = launchVelocity;
        for (int n = 0; n < ticks; n++)
        {
            vel += gravityPerTick;
            pos += vel;
            vel *= airFriction;
        }
        return pos;
    }

    /// <summary>
    /// 反解：N tick 内恰好位移 <paramref name="displacement"/> 所需的起跳速度。
    /// 位移 = v0·A(N) + g·C(N)，A/C 用与积分相同的累加得到（f=1 时退化为 N 与 N(N+1)/2）。
    /// </summary>
    public static Vector3 VelocityFor(Vector3 displacement, Vector3 gravityPerTick,
        float airFriction, int ticks)
    {
        float a = 0f;
        float factor = 1f;
        Vector3 gravityOnly = PositionAfter(Vector3.Zero, Vector3.Zero,
            gravityPerTick, airFriction, ticks);
        for (int n = 0; n < ticks; n++)
        {
            a += factor;
            factor *= airFriction;
        }
        if (a <= 1e-8f)
        {
            return Vector3.Zero;
        }
        return (displacement - gravityOnly) / a;
    }

    /// <summary>
    /// 规划一跳。失败（任何 N 都不满足约束，或路径被地形挡住）返回 false，宿主应视为
    /// 「此刻不可攻击」而不是硬跳。terrain 为 null 时跳过路径扫掠。
    /// </summary>
    public static bool TryPlan(in SpiderLeapRequest request, ITerrainQuery? terrain,
        out SpiderLeapPlan plan)
    {
        plan = default;
        int minTicks = Mathf.Max(1, request.MinTicks);
        int maxTicks = Mathf.Max(minTicks, request.MaxTicks);
        if (!IsFinite(request.From) || !IsFinite(request.Target)
            || !float.IsFinite(request.AirFriction) || request.AirFriction <= 0f)
        {
            return false;
        }

        Vector3 normal = request.SurfaceNormal.LengthSquared() > 1e-12f
            ? request.SurfaceNormal.Normalized()
            : Vector3.Up;
        Vector3 worldUp = request.GravityPerTick.LengthSquared() > 1e-12f
            ? -request.GravityPerTick.Normalized()
            : Vector3.Up;
        float distance = (request.Target - request.From).Length();
        float preferred = Mathf.Max(1e-4f, request.PreferredSpeed);
        int center = Mathf.Clamp(Mathf.RoundToInt(distance / preferred), minTicks, maxTicks);

        // 交替扫描：center, center+1, center-1, center+2, ...；先命中的解就是答案，
        // 顺序固定 = 结果确定。
        for (int step = 0; step <= maxTicks - minTicks; step++)
        {
            for (int sign = 0; sign < 2; sign++)
            {
                if (step == 0 && sign == 1)
                {
                    continue;
                }
                int n = sign == 0 ? center + step : center - step;
                if (n < minTicks || n > maxTicks)
                {
                    continue;
                }
                if (TryTicks(request, terrain, normal, worldUp, n, out plan))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryTicks(in SpiderLeapRequest request, ITerrainQuery? terrain,
        Vector3 normal, Vector3 worldUp, int ticks, out SpiderLeapPlan plan)
    {
        plan = default;
        Vector3 target = request.Target + request.TargetVelocity * ticks;
        Vector3 velocity = VelocityFor(target - request.From,
            request.GravityPerTick, request.AirFriction, ticks);
        if (!IsFinite(velocity))
        {
            return false;
        }
        float speed = velocity.Length();
        if (speed > request.MaxSpeed || speed < 1e-6f)
        {
            return false;
        }
        if (velocity.Dot(normal) < request.MinOutwardSpeed)
        {
            return false;
        }

        // 逐 tick 复演：抬升上限 + 地形扫掠（射线段 + 球体重叠双保险）。
        Vector3 pos = request.From;
        Vector3 vel = velocity;
        float peakRise = 0f;
        float clearance = request.ClearanceRadius;
        for (int n = 0; n < ticks; n++)
        {
            Vector3 previous = pos;
            vel += request.GravityPerTick;
            pos += vel;
            vel *= request.AirFriction;
            peakRise = Mathf.Max(peakRise, (pos - request.From).Dot(worldUp));
            if (peakRise > request.MaxRise)
            {
                return false;
            }
            if (terrain is null || clearance <= 0f)
            {
                continue;
            }
            Vector3 motion = pos - previous;
            float motionLength = motion.Length();
            if (motionLength > 1e-6f)
            {
                Vector3 dir = motion / motionLength;
                if (terrain.Raycast(previous, pos + dir * clearance, out _))
                {
                    return false;
                }
            }
            if (terrain.SpherePenetration(pos, clearance, out _, out _))
            {
                return false;
            }
        }

        plan = new SpiderLeapPlan(velocity, ticks, pos, peakRise);
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
