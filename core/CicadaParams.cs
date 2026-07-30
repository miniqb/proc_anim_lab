using Godot;

namespace ProcAnim.Core;

/// <summary>
/// Cicada 的纯出生配置。控制器在构造时读取并保存这些数值；运行时不按预设名分支。
/// 单位与其余内核一致：米、米/tick、固定 40 tick/s。
/// </summary>
public sealed class CicadaParams
{
    public string Name = "light";

    // —— 双 chunk 身体（RW 7.5px / 7px / 14px，1px = 0.025m）——
    public float FrontRadius = 0.1875f;
    public float RearRadius = 0.175f;
    public float BodyLength = 0.35f;
    public float BodyMass = 0.55f;

    // —— 自由飞行：两端差分升力、阻尼和方向推力 ——
    public float FrontLift = 0.020f;
    public float RearLift = 0.030f;
    public float FrontSteer = 0.0275f;
    public float RearSteer = 0.01625f;
    public float FrontFlightDamping = 0.98f;
    public float RearFlightDamping = 0.94f;
    public float MaxFlightSpeed = 0.18f;
    public float FlightPowerRise = 0.10f;
    public float FlightPowerFall = 0.05f;
    public float MoveTargetArriveRadius = 0.30f;
    public float HoverGain = 0.055f;
    public float HoverDamping = 0.10f;
    public float HoverMaxCorrection = 0.035f;

    // —— 固定顺序避障射线扇；Charge 时关闭 ——
    public float AvoidanceProbeDistance = 0.75f;
    public float AvoidanceGain = 0.45f;

    // —— 显式停驻 ——
    public float PerchClearance = 0.22f;
    public float LandingGain = 0.22f;
    public float LandingDamping = 0.32f;
    public float PerchedGain = 0.30f;
    public float PerchedDamping = 0.50f;
    public float PerchMaxCorrection = 0.10f;
    public float PerchSettleDistance = 0.075f;
    public float PerchSettleSpeed = 0.035f;
    public int PerchSettleTicks = 4;
    public float PerchValidationDistance = 0.16f;
    public int AutoTakeOffTicks = 30;

    // —— 起飞 / Charge ——
    public float FrontTakeOffImpulse = 0.225f;
    public float RearTakeOffImpulse = 0.175f;
    public float ChargeWindupBackstep = 0.020f;
    public float ChargeThrust = 0.10f;
    public float ChargeMaxSpeed = 0.65f;
    public float ChargeHardImpactSpeed = 0.50f;
    public int StunTicks = 20;

    // —— 3D 姿态和表现 ——
    public float BankMaxRadians = 0.55f;
    public float BankResponse = 0.16f;
    public float WingLength = 0.80f;
    public float WingPeriodTicks = 5.5f;
    public float TentacleLength = 0.58f;
    public Vector3 BodyColor = new(0.78f, 0.72f, 0.50f);
    public Vector3 AccentColor = new(0.95f, 0.54f, 0.18f);
    public Vector3 WingColor = new(0.70f, 0.88f, 0.86f);
    public float InitialFlapPhase = 0.17f;

    /// <summary>
    /// 工厂在出生时冻结一份浅拷贝；当前字段全是值类型，因此不会与调用侧共享可变状态。
    /// </summary>
    internal CicadaParams Snapshot() => (CicadaParams)MemberwiseClone();
}
