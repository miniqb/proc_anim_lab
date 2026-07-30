using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnim.Core.SpiderSmoke;

internal static class Program
{
    private const int Ticks = 900;
    private const ulong ExpectedSmall = 0xF78323EEB0882985UL;
    private const ulong ExpectedLarge = 0xF7AB5619F6E6928FUL;

    private readonly record struct RunResult(
        ulong Hash,
        float Distance,
        float MaxConstraintError,
        float MaxUpperError,
        float MaxLowerError,
        int MaxNoGrip,
        float MinBendHeight,
        float MaxKneeStepRatio,
        float MinPoleDot,
        float MaxReachExcess,
        float MaxFootPenetration,
        int MaxSideRecoveryTicks,
        bool Finite,
        bool PoleStable,
        bool SidesCorrect);

    private readonly record struct SyntheticResult(
        ulong Hash,
        bool Finite,
        bool LocalRootsCorrect,
        float MaxConstraintError);

    private readonly record struct TrailingResult(
        ulong Hash,
        bool InitiallyReversed,
        bool Recovered,
        int RecoveryTick,
        float InitialAlignment,
        float FinalAlignment,
        float MaxConstraintError,
        bool Finite);

    private readonly record struct AcquisitionResult(
        ulong Hash,
        bool SawBlockedCandidate,
        bool RejectedWithoutFalseGrip,
        bool RecoveredOnAlternateSurface,
        int MaxAcquisitionTicks,
        bool Finite);

    private readonly record struct GaitActivityResult(
        int CompletedSteps,
        int MinStepsPerLeg,
        int MaxStepsPerLeg,
        float MeanForwardGainRatio,
        float MinForwardGainRatio,
        float MinLegMeanForwardGainRatio,
        float MeanPlantedTrailRatio,
        float MaxLegMeanPlantedTrailRatio,
        float SevereTrailFraction,
        float MaxLegSevereTrailFraction,
        int MaxSevereTrailRun,
        float MinLegForwardSwingFraction,
        float ForwardSwingFraction,
        int DirectRetargets,
        int EmergencySteps,
        int MaxEmergencyStepsPerLeg,
        int RearEmergencySteps,
        float MicroStepFraction,
        float RearMeanForwardGainRatio,
        float RearMicroStepFraction,
        float RearMeanClearanceRatio,
        float RearMeanStanceAdvanceRatio,
        bool Finite);

    private readonly record struct TurnLaneResult(
        ulong Hash,
        bool PreconditionMet,
        int BodyAlignmentTick,
        int AllLanesRecoveryTick,
        int BalanceRecoveryTick,
        float MinSettledFootLane,
        float MinSettledTargetLane,
        float MinSettledPairSeparation,
        float SettledSideGapP95,
        float SettledPairMeanGapP95,
        float SettledPairMaxGapP95,
        float SettledTargetSideGapP95,
        float SettledTargetPairMaxGapP95,
        float SettledPairRatioP05,
        float SettledFootNominalRatioP05,
        float SettledTargetNominalRatioP05,
        float FinalSideGap,
        float FinalPairMaxGap,
        float FinalTargetSideGap,
        float FinalTargetPairMaxGap,
        float FinalMinPairRatio,
        float FinalMinFootNominalRatio,
        float FinalMinTargetNominalRatio,
        int MaxWrongLegsAfterBudget,
        int MaxSameHalfPairsAfterBudget,
        int MaxInvertedPairsAfterBudget,
        int FinalWrongLegs,
        int FinalSameHalfPairs,
        int FinalInvertedPairs,
        int MaxNoGrip,
        float MaxUpperError,
        float MaxLowerError,
        float MaxKneeStepRatio,
        float MinPoleDot,
        float PostTurnProgress,
        bool PoleSidesCorrect,
        bool Finite);

    private readonly record struct TurnBalanceSample(
        int Tick,
        float SideGap,
        float PairMeanGap,
        float PairMaxGap,
        float TargetSideGap,
        float TargetPairMaxGap,
        float MinPairRatio,
        float MinFootNominalRatio,
        float MinTargetNominalRatio);

    private readonly record struct LegState(
        Vector3 Pos,
        Vector3 LastPos,
        Vector3 Vel,
        Vector3 HuntPos,
        Vector3 RootPos,
        Vector3 LastRootPos,
        Vector3 KneePos,
        Vector3 LastKneePos,
        Vector3 BendPole,
        Vector3 GripNormal,
        Vector3 LastGripNormal,
        Vector3 LastIkAxis,
        Vector3 SwingLandingGoal,
        Vector3 SwingHuntTarget,
        bool HasGrip,
        bool ReachingForTerrain,
        bool TerrainContact,
        bool TargetSurfaceContact,
        bool HasFrozenSwingTarget,
        int GripCounter,
        int AcquisitionTicks,
        int SwingTicks,
        int StepSerial,
        int EmergencyStepCount,
        int LandingSerial,
        SpiderStepReason LastStepReason);

    public static int Main()
    {
        SpiderBreedParams smallBreed = SpiderFactory.SmallSpider();
        SpiderBreedParams largeBreed = SpiderFactory.LargeSpider();

        RunResult smallA = Run(smallBreed);
        RunResult smallB = Run(SpiderFactory.SmallSpider());
        RunResult largeA = Run(largeBreed);
        RunResult largeB = Run(SpiderFactory.LargeSpider());

        bool topologyOk = CheckTopology(out string topologyMessage);
        bool lifecycleOk = CheckLifecycle(out string lifecycleMessage);
        bool moveTargetOk = CheckMoveTarget(out string moveTargetMessage);
        bool annulusOk = CheckUnequalLegGeometry(out string annulusMessage);
        bool trailingOk = CheckTrailingRecovery(out string trailingMessage);
        bool configOk = CheckConfigValidation(out string configMessage);
        bool acquisitionOk = CheckAcquisitionTimeout(out string acquisitionMessage);
        bool antipodalOk = CheckAntipodalDirections(out string antipodalMessage);
        bool turnOk = CheckTurnLaneRecovery(out string[] turnMessages);
        GaitActivityResult smallGait = RunGaitActivity(SpiderFactory.SmallSpider());
        GaitActivityResult largeGait = RunGaitActivity(SpiderFactory.LargeSpider());
        bool smallDeterministic = smallA.Hash == smallB.Hash;
        bool largeDeterministic = largeA.Hash == largeB.Hash;
        bool smallExpected = smallA.Hash == ExpectedSmall;
        bool largeExpected = largeA.Hash == ExpectedLarge;
        bool behaviorOk = Healthy(smallA, minimumDistance: 4f) && Healthy(largeA, minimumDistance: 2f);
        // 稳定平地专门钉住用户看到的失败：后腿必须完成可见的 AEP→PEP 复位，
        // 不得靠“稳定抓点同 tick 直接换 HuntPos”或大量超距恢复伪装成高步频。
        bool gaitOk = largeGait.Finite
            && smallGait.Finite
            && smallGait.CompletedSteps >= 400
            && smallGait.MinStepsPerLeg >= 40
            && smallGait.MeanForwardGainRatio >= 0.25f
            && smallGait.MinLegMeanForwardGainRatio >= 0.20f
            && smallGait.RearMeanForwardGainRatio >= 0.24f
            && smallGait.MicroStepFraction <= 0.20f
            && smallGait.RearMicroStepFraction <= 0.20f
            && smallGait.DirectRetargets == 0
            && smallGait.RearEmergencySteps <= 2
            && smallGait.MaxEmergencyStepsPerLeg <= 8
            && smallGait.RearMeanClearanceRatio >= 0.04f
            && smallGait.RearMeanStanceAdvanceRatio >= 0.18f
            && smallGait.MinLegForwardSwingFraction >= 0.9f
            && smallGait.ForwardSwingFraction >= 0.9f
            && largeGait.CompletedSteps >= 180
            && largeGait.MinStepsPerLeg >= 20
            && largeGait.MeanForwardGainRatio >= 0.25f
            && largeGait.MinLegMeanForwardGainRatio >= 0.20f
            && largeGait.RearMeanForwardGainRatio >= 0.24f
            && largeGait.MicroStepFraction <= 0.20f
            && largeGait.RearMicroStepFraction <= 0.20f
            && largeGait.DirectRetargets == 0
            && largeGait.RearEmergencySteps <= 2
            && largeGait.MaxEmergencyStepsPerLeg <= 8
            && largeGait.RearMeanClearanceRatio >= 0.04f
            && largeGait.RearMeanStanceAdvanceRatio >= 0.18f
            && largeGait.MeanPlantedTrailRatio <= 0.39f
            && largeGait.MaxLegMeanPlantedTrailRatio <= 0.54f
            && largeGait.SevereTrailFraction <= 0.64f
            && largeGait.MaxSevereTrailRun <= 24
            && largeGait.MinLegForwardSwingFraction >= 0.9f
            && largeGait.ForwardSwingFraction >= 0.9f;

        Console.WriteLine(
            $"[SPIDER-DET] small={smallA.Hash:X16}/{smallB.Hash:X16} expected={ExpectedSmall:X16}; " +
            $"large={largeA.Hash:X16}/{largeB.Hash:X16} expected={ExpectedLarge:X16}");
        Console.WriteLine(
            $"[SPIDER-METRIC] small distance={smallA.Distance:F2}m dev={smallA.MaxConstraintError:F4}m " +
            $"ik={smallA.MaxUpperError:E2}/{smallA.MaxLowerError:E2} noGrip={smallA.MaxNoGrip} " +
            $"bendMin={smallA.MinBendHeight:F3}m kneeStep={smallA.MaxKneeStepRatio:F3} " +
            $"poleDot={smallA.MinPoleDot:F3} reach={smallA.MaxReachExcess:E2} " +
            $"footPen={smallA.MaxFootPenetration:E2} sideRecovery={smallA.MaxSideRecoveryTicks}; " +
            $"large distance={largeA.Distance:F2}m dev={largeA.MaxConstraintError:F4}m " +
            $"ik={largeA.MaxUpperError:E2}/{largeA.MaxLowerError:E2} noGrip={largeA.MaxNoGrip} " +
            $"bendMin={largeA.MinBendHeight:F3}m kneeStep={largeA.MaxKneeStepRatio:F3} " +
            $"poleDot={largeA.MinPoleDot:F3} reach={largeA.MaxReachExcess:E2} " +
            $"footPen={largeA.MaxFootPenetration:E2} sideRecovery={largeA.MaxSideRecoveryTicks}; " +
            $"flags={smallA.Finite}/{smallA.PoleStable}/{smallA.SidesCorrect}," +
            $"{largeA.Finite}/{largeA.PoleStable}/{largeA.SidesCorrect}");
        Console.WriteLine($"[SPIDER-TOPOLOGY] {topologyMessage}");
        Console.WriteLine($"[SPIDER-LIFECYCLE] {lifecycleMessage}");
        Console.WriteLine($"[SPIDER-TARGET] {moveTargetMessage}");
        Console.WriteLine($"[SPIDER-ANNULUS] {annulusMessage}");
        Console.WriteLine($"[SPIDER-TRAILING] {trailingMessage}");
        Console.WriteLine($"[SPIDER-CONFIG] {configMessage}");
        Console.WriteLine($"[SPIDER-ACQUISITION] {acquisitionMessage}");
        Console.WriteLine($"[SPIDER-ANTIPODAL] {antipodalMessage}");
        foreach (string turnMessage in turnMessages)
        {
            Console.WriteLine($"[SPIDER-TURN] {turnMessage}");
        }
        Console.WriteLine(
            $"[SPIDER-GAIT] small steps={smallGait.CompletedSteps}/" +
            $"{smallGait.MinStepsPerLeg}..{smallGait.MaxStepsPerLeg} " +
            $"gain={smallGait.MeanForwardGainRatio:F3}/{smallGait.MinForwardGainRatio:F3} " +
            $"legGainMin={smallGait.MinLegMeanForwardGainRatio:F3} " +
            $"trail={smallGait.MeanPlantedTrailRatio:F3}/{smallGait.MaxLegMeanPlantedTrailRatio:F3} " +
            $"severe={smallGait.SevereTrailFraction:P1}/{smallGait.MaxLegSevereTrailFraction:P1}/" +
            $"{smallGait.MaxSevereTrailRun} swingForward=" +
            $"{smallGait.ForwardSwingFraction:P1}/{smallGait.MinLegForwardSwingFraction:P1} " +
            $"retarget/emergency={smallGait.DirectRetargets}/{smallGait.EmergencySteps} " +
            $"micro={smallGait.MicroStepFraction:P1} rear=" +
            $"{smallGait.RearMeanForwardGainRatio:F3}/{smallGait.RearMicroStepFraction:P1}/" +
            $"{smallGait.RearMeanClearanceRatio:F3}/{smallGait.RearMeanStanceAdvanceRatio:F3}; " +
            $"large steps={largeGait.CompletedSteps}/" +
            $"{largeGait.MinStepsPerLeg}..{largeGait.MaxStepsPerLeg} " +
            $"gain={largeGait.MeanForwardGainRatio:F3}/{largeGait.MinForwardGainRatio:F3} " +
            $"legGainMin={largeGait.MinLegMeanForwardGainRatio:F3} " +
            $"trail={largeGait.MeanPlantedTrailRatio:F3}/{largeGait.MaxLegMeanPlantedTrailRatio:F3} " +
            $"severe={largeGait.SevereTrailFraction:P1}/{largeGait.MaxLegSevereTrailFraction:P1}/" +
            $"{largeGait.MaxSevereTrailRun} swingForward=" +
            $"{largeGait.ForwardSwingFraction:P1}/{largeGait.MinLegForwardSwingFraction:P1} " +
            $"retarget/emergency={largeGait.DirectRetargets}/{largeGait.EmergencySteps}" +
            $"(max/rear={largeGait.MaxEmergencyStepsPerLeg}/{largeGait.RearEmergencySteps}) " +
            $"micro={largeGait.MicroStepFraction:P1} rear=" +
            $"{largeGait.RearMeanForwardGainRatio:F3}/{largeGait.RearMicroStepFraction:P1}/" +
            $"{largeGait.RearMeanClearanceRatio:F3}/{largeGait.RearMeanStanceAdvanceRatio:F3}");

        var failures = new List<string>();
        if (!smallDeterministic) failures.Add("small 双跑不一致");
        if (!largeDeterministic) failures.Add("large 双跑不一致");
        if (!smallExpected) failures.Add("small 哈希偏离基线");
        if (!largeExpected) failures.Add("large 哈希偏离基线");
        if (!behaviorOk) failures.Add("行走/约束/IK 健康门未通过");
        if (!topologyOk) failures.Add("拓扑配置不符合契约");
        if (!lifecycleOk) failures.Add("Shift/Teleport/Launch 生命周期失败");
        if (!moveTargetOk) failures.Add("MoveTarget 输入契约失败");
        if (!annulusOk) failures.Add("极不等长腿可达环契约失败");
        if (!trailingOk) failures.Add("多节身体 trailing 反折恢复失败");
        if (!configOk) failures.Add("配置派生溢出/预算上限校验失败");
        if (!acquisitionOk) failures.Add("未确认抓点超时/目标面背书失败");
        if (!antipodalOk) failures.Add("180° 朝向/直接天花板重抓失败");
        if (!turnOk) failures.Add("平地急转后足端/落脚目标没有回到各自身体侧");
        if (!gaitOk) failures.Add("蜘蛛 AEP/PEP 步幅、抬腿净空或紧急换点门失败");

        if (failures.Count == 0)
        {
            Console.WriteLine(
                "[SPIDER-SMOKE] PASS：双跑确定、拓扑通用、弯腿稳定、" +
                "生命周期完整、急转腿槽/站距恢复");
            return 0;
        }

        Console.WriteLine($"[SPIDER-SMOKE] FAIL：{string.Join("；", failures)}");
        return 1;
    }

    private static bool Healthy(in RunResult result, float minimumDistance)
    {
        return result.Finite
            && result.PoleStable
            && result.SidesCorrect
            && result.Distance >= minimumDistance
            && result.MaxConstraintError <= 0.08f
            && result.MaxUpperError <= 1e-3f
            && result.MaxLowerError <= 1e-3f
            && result.MaxNoGrip <= 40
            && result.MinBendHeight >= 0.005f
            && result.MaxKneeStepRatio <= 0.75f
            && result.MinPoleDot >= -0.01f
            && result.MaxReachExcess <= 1e-3f
            && result.MaxFootPenetration <= 1e-3f;
    }

    private static GaitActivityResult RunGaitActivity(SpiderBreedParams breed)
    {
        const int ticks = 700;
        const int settleTicks = 120;
        const float severeTrailRatio = 0.35f;
        const float microStepRatio = 0.12f;
        Vector3 forward = Vector3.Right;
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), breed);
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);
        int count = spider.Legs.Count;
        int rearPairIndex = -1;
        foreach (SpiderLeg leg in spider.Legs)
        {
            rearPairIndex = Math.Max(rearPairIndex, leg.PairIndex);
        }
        var previousStepSerial = new int[count];
        var previousLandingSerial = new int[count];
        var previousEmergencySteps = new int[count];
        var previousGripping = new bool[count];
        var stepActive = new bool[count];
        var stepStartRelative = new float[count];
        var stepLandingSerial = new int[count];
        var stepPeakClearance = new float[count];
        var stepsPerLeg = new int[count];
        var forwardGainPerLeg = new float[count];
        var microStepsPerLeg = new int[count];
        var clearancePerLeg = new float[count];
        var emergencyStepsPerLeg = new int[count];
        var stanceActive = new bool[count];
        var stanceStartRoot = new Vector3[count];
        var stanceAdvancePerLeg = new float[count];
        var stanceCountPerLeg = new int[count];
        var plantedTrailPerLeg = new float[count];
        var plantedSamplesPerLeg = new int[count];
        var severeSamplesPerLeg = new int[count];
        var severeRuns = new int[count];
        var swingSamplesPerLeg = new int[count];
        var forwardSwingSamplesPerLeg = new int[count];
        float forwardGainSum = 0f;
        float minForwardGain = float.PositiveInfinity;
        float plantedTrailSum = 0f;
        int plantedSamples = 0;
        int severeSamples = 0;
        int maxSevereRun = 0;
        int completedSteps = 0;
        int microSteps = 0;
        int directRetargets = 0;
        int emergencySteps = 0;
        int swingSamples = 0;
        int forwardSwingSamples = 0;
        bool finite = true;

        for (int i = 0; i < count; i++)
        {
            SpiderLeg leg = spider.Legs[i];
            previousStepSerial[i] = leg.StepSerial;
            previousLandingSerial[i] = leg.LandingSerial;
            previousEmergencySteps[i] = leg.EmergencyStepCount;
            previousGripping[i] = leg.Gripping;
        }

        for (long tick = 1; tick <= ticks; tick++)
        {
            spider.MoveDir = forward;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, tick));

            for (int i = 0; i < count; i++)
            {
                SpiderLeg leg = spider.Legs[i];
                float reach = Mathf.Max(leg.MaxReach, 1e-5f);
                float relative = (leg.Pos - leg.RootPos).Dot(forward) / reach;
                bool stepEvent = leg.StepSerial != previousStepSerial[i];
                bool landingEvent = leg.LandingSerial != previousLandingSerial[i];

                if (tick >= settleTicks && stepEvent)
                {
                    if (stanceActive[i])
                    {
                        float stanceAdvance =
                            (leg.RootPos - stanceStartRoot[i]).Dot(forward) / reach;
                        stanceAdvancePerLeg[i] += stanceAdvance;
                        stanceCountPerLeg[i]++;
                        stanceActive[i] = false;
                    }
                    stepActive[i] = true;
                    stepStartRelative[i] = relative;
                    stepLandingSerial[i] = leg.LandingSerial;
                    stepPeakClearance[i] = Mathf.Max(
                        0f, (leg.Pos.Y - leg.Radius) / reach);
                }
                if (tick >= settleTicks && landingEvent
                    && previousGripping[i] && !stepEvent && !stepActive[i])
                {
                    // 稳定抓地足没有进入完整摆腿却换了 HuntPos，就是旧碎步路径。
                    directRetargets++;
                }
                if (tick >= settleTicks)
                {
                    int emergencyDelta =
                        leg.EmergencyStepCount - previousEmergencySteps[i];
                    if (emergencyDelta > 0)
                    {
                        emergencySteps += emergencyDelta;
                        emergencyStepsPerLeg[i] += emergencyDelta;
                    }
                }
                if (stepActive[i])
                {
                    stepPeakClearance[i] = Mathf.Max(
                        stepPeakClearance[i],
                        Mathf.Max(0f, (leg.Pos.Y - leg.Radius) / reach));
                }
                if (stepActive[i] && leg.Gripping
                    && leg.LandingSerial != stepLandingSerial[i])
                {
                    float gain = relative - stepStartRelative[i];
                    forwardGainSum += gain;
                    minForwardGain = Mathf.Min(minForwardGain, gain);
                    completedSteps++;
                    stepsPerLeg[i]++;
                    forwardGainPerLeg[i] += gain;
                    clearancePerLeg[i] += stepPeakClearance[i];
                    if (gain < microStepRatio)
                    {
                        microSteps++;
                        microStepsPerLeg[i]++;
                    }
                    stepActive[i] = false;
                    stanceActive[i] = true;
                    stanceStartRoot[i] = leg.RootPos;
                }

                if (tick >= settleTicks && !leg.ReachingForTerrain)
                {
                    swingSamples++;
                    swingSamplesPerLeg[i]++;
                    if ((leg.HuntPos - leg.Pos).Dot(forward) > 0f)
                    {
                        forwardSwingSamples++;
                        forwardSwingSamplesPerLeg[i]++;
                    }
                }

                if (tick >= settleTicks && leg.Gripping)
                {
                    float trail = -relative;
                    plantedTrailSum += trail;
                    plantedSamples++;
                    plantedTrailPerLeg[i] += trail;
                    plantedSamplesPerLeg[i]++;
                    if (trail > severeTrailRatio)
                    {
                        severeSamples++;
                        severeSamplesPerLeg[i]++;
                        severeRuns[i]++;
                        maxSevereRun = Math.Max(maxSevereRun, severeRuns[i]);
                    }
                    else
                    {
                        severeRuns[i] = 0;
                    }
                }
                else
                {
                    severeRuns[i] = 0;
                }

                finite &= leg.Pos.IsFinite() && leg.RootPos.IsFinite()
                    && leg.HuntPos.IsFinite()
                    && leg.SwingLandingGoal.IsFinite()
                    && leg.SwingHuntTarget.IsFinite();
                previousStepSerial[i] = leg.StepSerial;
                previousLandingSerial[i] = leg.LandingSerial;
                previousEmergencySteps[i] = leg.EmergencyStepCount;
                previousGripping[i] = leg.Gripping;
            }
            foreach (BodyChunk chunk in spider.Body.Chunks)
            {
                finite &= chunk.Pos.IsFinite() && chunk.Vel.IsFinite();
            }
        }

        int minStepsPerLeg = int.MaxValue;
        int maxStepsPerLeg = 0;
        float minLegMeanForwardGain = float.PositiveInfinity;
        float maxLegMeanPlantedTrail = float.NegativeInfinity;
        float maxLegSevereTrailFraction = 0f;
        float minLegForwardSwingFraction = float.PositiveInfinity;
        int maxEmergencyStepsPerLeg = 0;
        int rearEmergencySteps = 0;
        int rearSteps = 0;
        int rearMicroSteps = 0;
        float rearForwardGain = 0f;
        float rearClearance = 0f;
        float rearStanceAdvance = 0f;
        int rearStanceCount = 0;
        var legSummary = new string[count];
        for (int i = 0; i < count; i++)
        {
            int steps = stepsPerLeg[i];
            SpiderLeg leg = spider.Legs[i];
            minStepsPerLeg = Math.Min(minStepsPerLeg, steps);
            maxStepsPerLeg = Math.Max(maxStepsPerLeg, steps);
            maxEmergencyStepsPerLeg = Math.Max(
                maxEmergencyStepsPerLeg, emergencyStepsPerLeg[i]);
            if (steps > 0)
            {
                minLegMeanForwardGain = Mathf.Min(
                    minLegMeanForwardGain, forwardGainPerLeg[i] / steps);
            }
            if (plantedSamplesPerLeg[i] > 0)
            {
                maxLegMeanPlantedTrail = Mathf.Max(
                    maxLegMeanPlantedTrail,
                    plantedTrailPerLeg[i] / plantedSamplesPerLeg[i]);
                maxLegSevereTrailFraction = Mathf.Max(
                    maxLegSevereTrailFraction,
                    severeSamplesPerLeg[i] / (float)plantedSamplesPerLeg[i]);
            }
            if (swingSamplesPerLeg[i] > 0)
            {
                minLegForwardSwingFraction = Mathf.Min(
                    minLegForwardSwingFraction,
                    forwardSwingSamplesPerLeg[i] / (float)swingSamplesPerLeg[i]);
            }
            float legGain = steps > 0 ? forwardGainPerLeg[i] / steps : 0f;
            float legTrail = plantedSamplesPerLeg[i] > 0
                ? plantedTrailPerLeg[i] / plantedSamplesPerLeg[i]
                : 0f;
            float legClearance = steps > 0 ? clearancePerLeg[i] / steps : 0f;
            float legStance = stanceCountPerLeg[i] > 0
                ? stanceAdvancePerLeg[i] / stanceCountPerLeg[i]
                : 0f;
            if (leg.PairIndex == rearPairIndex)
            {
                rearSteps += steps;
                rearMicroSteps += microStepsPerLeg[i];
                rearForwardGain += forwardGainPerLeg[i];
                rearClearance += clearancePerLeg[i];
                rearEmergencySteps += emergencyStepsPerLeg[i];
                rearStanceAdvance += stanceAdvancePerLeg[i];
                rearStanceCount += stanceCountPerLeg[i];
            }
            legSummary[i] =
                $"{i}:{steps}/{legGain:F3}/{legTrail:F3}/" +
                $"{emergencyStepsPerLeg[i]}/{legClearance:F3}/{legStance:F3}";
        }
        Console.WriteLine($"[SPIDER-GAIT-LEGS] {breed.Name} " +
            $"slot:steps/gain/trail/emergency/clearance/stance={string.Join(",", legSummary)}");
        return new GaitActivityResult(
            completedSteps,
            minStepsPerLeg == int.MaxValue ? 0 : minStepsPerLeg,
            maxStepsPerLeg,
            completedSteps > 0 ? forwardGainSum / completedSteps : 0f,
            float.IsPositiveInfinity(minForwardGain) ? 0f : minForwardGain,
            float.IsPositiveInfinity(minLegMeanForwardGain) ? 0f : minLegMeanForwardGain,
            plantedSamples > 0 ? plantedTrailSum / plantedSamples : 0f,
            float.IsNegativeInfinity(maxLegMeanPlantedTrail) ? 0f : maxLegMeanPlantedTrail,
            plantedSamples > 0 ? severeSamples / (float)plantedSamples : 0f,
            maxLegSevereTrailFraction,
            maxSevereRun,
            float.IsPositiveInfinity(minLegForwardSwingFraction)
                ? 0f
                : minLegForwardSwingFraction,
            swingSamples > 0 ? forwardSwingSamples / (float)swingSamples : 0f,
            directRetargets,
            emergencySteps,
            maxEmergencyStepsPerLeg,
            rearEmergencySteps,
            completedSteps > 0 ? microSteps / (float)completedSteps : 0f,
            rearSteps > 0 ? rearForwardGain / rearSteps : 0f,
            rearSteps > 0 ? rearMicroSteps / (float)rearSteps : 0f,
            rearSteps > 0 ? rearClearance / rearSteps : 0f,
            rearStanceCount > 0 ? rearStanceAdvance / rearStanceCount : 0f,
            finite);
    }

    /// <summary>
    /// 平地急转专项：身体局部轴改变后，旧抓点允许短暂落到新的身体内侧，但每条腿的
    /// 足端与下一落脚目标都必须在固定预算内回到自己 Side 对应的半平面。该门刻意不用
    /// controller.Forward 量 lane；SpiderLeg 实际由锚点所在链段定向，测试必须复现同一轴。
    /// 除左右差外还钉住每腿 lane/自身名义工作区，防止两侧一起贴身形成对称假绿。
    /// </summary>
    private static bool CheckTurnLaneRecovery(out string[] messages)
    {
        const int rightAngleBodyBudget = 20;
        const int halfTurnBodyBudget = 30;
        const int rightAngleLaneBudget = 60;
        const int halfTurnLaneBudget = 80;
        const int rightAngleBalanceBudget = 80;
        const int halfTurnBalanceBudget = 100;
        var output = new List<string>();
        bool allOk = true;

        foreach (string breedName in new[] { "spider-small", "spider-large" })
        {
            foreach ((string Name, Vector3 Target, bool HalfTurn) turn in new[]
            {
                ("90-forward", Vector3.Forward, false),
                ("90-back", Vector3.Back, false),
                ("180-right", Vector3.Right, true),
            })
            {
                int bodyBudget = turn.HalfTurn
                    ? halfTurnBodyBudget
                    : rightAngleBodyBudget;
                int laneBudget = turn.HalfTurn
                    ? halfTurnLaneBudget
                    : rightAngleLaneBudget;
                int balanceBudget = turn.HalfTurn
                    ? halfTurnBalanceBudget
                    : rightAngleBalanceBudget;
                SpiderBreedParams Breed() => breedName == "spider-small"
                    ? SpiderFactory.SmallSpider()
                    : SpiderFactory.LargeSpider();
                TurnLaneResult first = RunTurnLane(
                    Breed(), turn.Target, laneBudget, balanceBudget);
                TurnLaneResult second = RunTurnLane(
                    Breed(), turn.Target, laneBudget, balanceBudget);
                bool deterministic = first.Hash == second.Hash;
                bool healthy = deterministic
                    && HealthyTurn(
                        first, bodyBudget, laneBudget, balanceBudget);
                allOk &= healthy;
                output.Add(
                    $"{breedName}/{turn.Name}={healthy} " +
                    $"hash={first.Hash:X16}/{second.Hash:X16} " +
                    $"pre={first.PreconditionMet} align={first.BodyAlignmentTick}/{bodyBudget} " +
                    $"recover={first.AllLanesRecoveryTick}/{laneBudget} " +
                    $"balance={first.BalanceRecoveryTick}/{balanceBudget} " +
                    $"lane={first.MinSettledFootLane:F3}/" +
                    $"{first.MinSettledTargetLane:F3}/" +
                    $"{first.MinSettledPairSeparation:F3} " +
                    $"gapP95={first.SettledSideGapP95:F3}/" +
                    $"{first.SettledPairMeanGapP95:F3}/" +
                    $"{first.SettledPairMaxGapP95:F3} target=" +
                    $"{first.SettledTargetSideGapP95:F3}/" +
                    $"{first.SettledTargetPairMaxGapP95:F3} " +
                    $"ratioP05={first.SettledPairRatioP05:F3} " +
                    $"nominalP05={first.SettledFootNominalRatioP05:F3}/" +
                    $"{first.SettledTargetNominalRatioP05:F3} " +
                    $"finalGap={first.FinalSideGap:F3}/" +
                    $"{first.FinalPairMaxGap:F3}/" +
                    $"{first.FinalTargetSideGap:F3}/" +
                    $"{first.FinalTargetPairMaxGap:F3}/" +
                    $"{first.FinalMinPairRatio:F3} nominal=" +
                    $"{first.FinalMinFootNominalRatio:F3}/" +
                    $"{first.FinalMinTargetNominalRatio:F3} " +
                    $"wrong={first.MaxWrongLegsAfterBudget}/{first.FinalWrongLegs} " +
                    $"same={first.MaxSameHalfPairsAfterBudget}/{first.FinalSameHalfPairs} " +
                    $"invert={first.MaxInvertedPairsAfterBudget}/{first.FinalInvertedPairs} " +
                    $"noGrip={first.MaxNoGrip} " +
                    $"ik={first.MaxUpperError:E2}/{first.MaxLowerError:E2} " +
                    $"knee={first.MaxKneeStepRatio:F3} pole={first.MinPoleDot:F3}/" +
                    $"{first.PoleSidesCorrect} progress={first.PostTurnProgress:F2}m " +
                    $"finite={first.Finite}");
            }
        }

        messages = output.ToArray();
        return allOk;
    }

    private static bool HealthyTurn(
        in TurnLaneResult result,
        int bodyBudget,
        int laneBudget,
        int balanceBudget)
    {
        const float laneMargin = 0.08f;
        const float pairSeparationMargin = 0.16f;
        return result.PreconditionMet
            && result.BodyAlignmentTick >= 0
            && result.BodyAlignmentTick <= bodyBudget
            && result.AllLanesRecoveryTick >= 0
            && result.AllLanesRecoveryTick <= laneBudget
            && result.BalanceRecoveryTick >= 0
            && result.BalanceRecoveryTick <= balanceBudget
            && result.MinSettledFootLane >= laneMargin
            && result.MinSettledTargetLane >= laneMargin
            && result.MinSettledPairSeparation >= pairSeparationMargin
            && result.SettledSideGapP95 <= 0.10f
            && result.SettledPairMeanGapP95 <= 0.12f
            && result.SettledPairMaxGapP95 <= 0.15f
            && result.SettledTargetSideGapP95 <= 0.10f
            && result.SettledTargetPairMaxGapP95 <= 0.15f
            && result.SettledPairRatioP05 >= 0.80f
            && result.SettledFootNominalRatioP05 >= 0.80f
            && result.SettledTargetNominalRatioP05 >= 0.80f
            && result.FinalSideGap <= 0.10f
            && result.FinalPairMaxGap <= 0.15f
            && result.FinalTargetSideGap <= 0.10f
            && result.FinalTargetPairMaxGap <= 0.15f
            && result.FinalMinPairRatio >= 0.80f
            && result.FinalMinFootNominalRatio >= 0.80f
            && result.FinalMinTargetNominalRatio >= 0.80f
            && result.MaxWrongLegsAfterBudget == 0
            && result.MaxSameHalfPairsAfterBudget == 0
            && result.MaxInvertedPairsAfterBudget == 0
            && result.FinalWrongLegs == 0
            && result.FinalSameHalfPairs == 0
            && result.FinalInvertedPairs == 0
            && result.MaxNoGrip <= 40
            && result.MaxUpperError <= 1e-3f
            && result.MaxLowerError <= 1e-3f
            && result.MaxKneeStepRatio <= 0.88f
            && result.MinPoleDot >= -0.01f
            && result.PostTurnProgress >= 1f
            && result.PoleSidesCorrect
            && result.Finite;
    }

    private static TurnLaneResult RunTurnLane(
        SpiderBreedParams breed,
        Vector3 turnTarget,
        int laneBudget,
        int balanceBudget)
    {
        const int preTurnTicks = 120;
        const int postTurnTicks = 180;
        const int stableTicks = 20;
        const float laneMargin = 0.08f;
        int balanceWindow = Math.Max(16, 2 * breed.GaitPhaseTicks);
        var terrain = new PlaneTerrainQuery(0f);
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), breed);
        var hasher = new DeterminismHasher();
        var previousPoles = new Vector3[spider.Legs.Count];
        var footLaneWindows = new Queue<float>[spider.Legs.Count];
        var latestTargetLanes = new float[spider.Legs.Count];
        var targetLaneSeen = new bool[spider.Legs.Count];
        var alignedTargetLaneSeen = new bool[spider.Legs.Count];
        var lastStepSerial = new int[spider.Legs.Count];
        var balanceSamples = new List<TurnBalanceSample>();
        for (int i = 0; i < footLaneWindows.Length; i++)
        {
            footLaneWindows[i] = new Queue<float>(balanceWindow);
        }
        bool precondition = false;
        bool finite = true;
        Vector3 turnStart = spider.Primary.Pos;
        int bodyAlignmentTick = -1;
        int allLanesRecoveryTick = -1;
        int balanceRecoveryTick = -1;
        int allCorrectRun = 0;
        int balanceCorrectRun = 0;
        float minSettledFootLane = float.PositiveInfinity;
        float minSettledTargetLane = float.PositiveInfinity;
        float minSettledPairSeparation = float.PositiveInfinity;
        int maxWrongAfterBudget = 0;
        int maxSameHalfAfterBudget = 0;
        int maxInvertedAfterBudget = 0;
        int finalWrong = int.MaxValue;
        int finalSameHalf = int.MaxValue;
        int finalInverted = int.MaxValue;
        int maxNoGrip = 0;
        float maxUpperError = 0f;
        float maxLowerError = 0f;
        float maxKneeStepRatio = 0f;
        float minPoleDot = 1f;

        for (long tick = 1; tick <= preTurnTicks + postTurnTicks; tick++)
        {
            Vector3 input = tick <= preTurnTicks ? Vector3.Left : turnTarget;
            spider.MoveDir = input;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, tick));
            FoldSpiderState(hasher, spider);
            maxNoGrip = Math.Max(maxNoGrip, spider.NoGripCounter);

            for (int i = 0; i < spider.Legs.Count; i++)
            {
                SpiderLeg leg = spider.Legs[i];
                maxUpperError = Mathf.Max(maxUpperError,
                    Mathf.Abs((leg.KneePos - leg.RootPos).Length() - leg.UpperLength));
                maxLowerError = Mathf.Max(maxLowerError,
                    Mathf.Abs((leg.Pos - leg.KneePos).Length() - leg.LowerLength));
                maxKneeStepRatio = Mathf.Max(maxKneeStepRatio,
                    (leg.KneePos - leg.LastKneePos).Length()
                    / Mathf.Max(leg.UpperLength + leg.LowerLength, 1e-5f));
                if (previousPoles[i].LengthSquared() > 1e-8f
                    && leg.BendPole.LengthSquared() > 1e-8f)
                {
                    minPoleDot = Mathf.Min(minPoleDot,
                        previousPoles[i].Normalized().Dot(leg.BendPole.Normalized()));
                }
                previousPoles[i] = leg.BendPole;
                finite &= leg.SwingLandingGoal.IsFinite()
                    && leg.SwingHuntTarget.IsFinite();
            }
            finite &= AllSpiderStateFinite(spider);

            Vector3 support = spider.SupportNormal.LengthSquared() > 1e-10f
                ? spider.SupportNormal.Normalized()
                : Vector3.Up;
            if (tick == preTurnTicks)
            {
                turnStart = spider.Primary.Pos;
                precondition = support.Dot(Vector3.Up) > 0.95f
                    && spider.LegsGripping >= spider.MinPlantedLegs
                    && TryTurnFrame(spider, spider.Primary, support, Vector3.Left,
                        out Vector3 preForward, out _)
                    && preForward.Dot(Vector3.Left) > 0.95f;
                foreach (SpiderLeg leg in spider.Legs)
                {
                    precondition &= TryTurnLane(
                        spider, leg, support, out float footLane, out _)
                        && footLane >= laneMargin;
                }
                for (int i = 0; i < spider.Legs.Count; i++)
                {
                    lastStepSerial[i] = spider.Legs[i].StepSerial;
                }
            }

            if (tick <= preTurnTicks)
            {
                continue;
            }

            int postTick = (int)tick - preTurnTicks;
            if (bodyAlignmentTick < 0
                && TryTurnFrame(spider, spider.Primary, support, turnTarget,
                    out Vector3 bodyForward, out _)
                && bodyForward.Dot(turnTarget) > 0.95f)
            {
                bodyAlignmentTick = postTick;
            }

            bool allCorrect = bodyAlignmentTick >= 0;
            float tickMinFootLane = float.PositiveInfinity;
            float tickMinTargetLane = float.PositiveInfinity;
            for (int i = 0; i < spider.Legs.Count; i++)
            {
                SpiderLeg leg = spider.Legs[i];
                bool validLane = TryTurnLane(
                    spider, leg, support, out float footLane, out float targetLane);
                allCorrect &= validLane
                    && footLane >= laneMargin
                    && targetLane >= laneMargin;
                tickMinFootLane = Mathf.Min(tickMinFootLane, footLane);
                tickMinTargetLane = Mathf.Min(tickMinTargetLane, targetLane);

                if (validLane)
                {
                    Queue<float> window = footLaneWindows[i];
                    window.Enqueue(footLane);
                    while (window.Count > balanceWindow)
                    {
                        window.Dequeue();
                    }
                }
                else
                {
                    footLaneWindows[i].Clear();
                }

                if (leg.StepSerial > lastStepSerial[i])
                {
                    if (leg.HasFrozenSwingTarget
                        && TryTurnPointLane(
                            spider,
                            leg,
                            support,
                            leg.SwingLandingGoal + support * leg.Radius,
                            out float capturedTargetLane))
                    {
                        latestTargetLanes[i] = capturedTargetLane;
                        targetLaneSeen[i] = true;
                        if (bodyAlignmentTick >= 0)
                        {
                            alignedTargetLaneSeen[i] = true;
                        }
                    }
                    lastStepSerial[i] = leg.StepSerial;
                }
            }
            allCorrectRun = allCorrect ? allCorrectRun + 1 : 0;
            if (allLanesRecoveryTick < 0 && allCorrectRun >= stableTicks)
            {
                allLanesRecoveryTick = postTick - stableTicks + 1;
            }

            if (TryMeasureTurnBalance(
                spider,
                postTick,
                balanceWindow,
                footLaneWindows,
                latestTargetLanes,
                targetLaneSeen,
                alignedTargetLaneSeen,
                out TurnBalanceSample balance))
            {
                balanceSamples.Add(balance);
                bool balanced = bodyAlignmentTick >= 0
                    && balance.SideGap <= 0.10f
                    && balance.PairMeanGap <= 0.12f
                    && balance.PairMaxGap <= 0.15f
                    && balance.TargetSideGap <= 0.10f
                    && balance.TargetPairMaxGap <= 0.15f
                    && balance.MinPairRatio >= 0.80f
                    && balance.MinFootNominalRatio >= 0.80f
                    && balance.MinTargetNominalRatio >= 0.80f;
                balanceCorrectRun = balanced ? balanceCorrectRun + 1 : 0;
                if (balanceRecoveryTick < 0
                    && balanceCorrectRun >= stableTicks)
                {
                    balanceRecoveryTick = postTick - stableTicks + 1;
                }
            }
            else
            {
                balanceCorrectRun = 0;
            }

            CountTurnLayout(
                spider, support, laneMargin,
                out int wrongLegs,
                out int sameHalfPairs,
                out int invertedPairs,
                out float minPairSeparation);
            if (postTick > laneBudget)
            {
                minSettledFootLane = Mathf.Min(
                    minSettledFootLane, tickMinFootLane);
                minSettledTargetLane = Mathf.Min(
                    minSettledTargetLane, tickMinTargetLane);
                minSettledPairSeparation = Mathf.Min(
                    minSettledPairSeparation, minPairSeparation);
                maxWrongAfterBudget = Math.Max(maxWrongAfterBudget, wrongLegs);
                maxSameHalfAfterBudget = Math.Max(
                    maxSameHalfAfterBudget, sameHalfPairs);
                maxInvertedAfterBudget = Math.Max(
                    maxInvertedAfterBudget, invertedPairs);
            }
            if (tick == preTurnTicks + postTurnTicks)
            {
                finalWrong = wrongLegs;
                finalSameHalf = sameHalfPairs;
                finalInverted = invertedPairs;
            }
        }

        bool poleSidesCorrect = true;
        foreach (SpiderLeg leg in spider.Legs)
        {
            if (!leg.Gripping || leg.Pair is not { } pair)
            {
                continue;
            }
            Vector3 outward = leg.RootPos - pair.RootPos;
            poleSidesCorrect &= outward.LengthSquared() > 1e-8f
                && leg.BendPole.Dot(outward.Normalized()) > 0.02f;
        }

        float progress = (spider.Primary.Pos - turnStart).Dot(turnTarget);
        var settledSideGaps = new List<float>();
        var settledPairMeanGaps = new List<float>();
        var settledPairMaxGaps = new List<float>();
        var settledTargetSideGaps = new List<float>();
        var settledTargetPairMaxGaps = new List<float>();
        var settledPairRatios = new List<float>();
        var settledFootNominalRatios = new List<float>();
        var settledTargetNominalRatios = new List<float>();
        foreach (TurnBalanceSample sample in balanceSamples)
        {
            if (sample.Tick <= balanceBudget)
            {
                continue;
            }
            settledSideGaps.Add(sample.SideGap);
            settledPairMeanGaps.Add(sample.PairMeanGap);
            settledPairMaxGaps.Add(sample.PairMaxGap);
            settledTargetSideGaps.Add(sample.TargetSideGap);
            settledTargetPairMaxGaps.Add(sample.TargetPairMaxGap);
            settledPairRatios.Add(sample.MinPairRatio);
            settledFootNominalRatios.Add(sample.MinFootNominalRatio);
            settledTargetNominalRatios.Add(sample.MinTargetNominalRatio);
        }
        TurnBalanceSample finalBalance = balanceSamples.Count > 0
            ? balanceSamples[^1]
            : new TurnBalanceSample(
                postTurnTicks,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity);
        return new TurnLaneResult(
            hasher.Value,
            precondition,
            bodyAlignmentTick,
            allLanesRecoveryTick,
            balanceRecoveryTick,
            minSettledFootLane,
            minSettledTargetLane,
            minSettledPairSeparation,
            PercentileOr(settledSideGaps, 0.95f, float.PositiveInfinity),
            PercentileOr(settledPairMeanGaps, 0.95f, float.PositiveInfinity),
            PercentileOr(settledPairMaxGaps, 0.95f, float.PositiveInfinity),
            PercentileOr(
                settledTargetSideGaps, 0.95f, float.PositiveInfinity),
            PercentileOr(
                settledTargetPairMaxGaps, 0.95f, float.PositiveInfinity),
            PercentileOr(settledPairRatios, 0.05f, float.NegativeInfinity),
            PercentileOr(
                settledFootNominalRatios, 0.05f, float.NegativeInfinity),
            PercentileOr(
                settledTargetNominalRatios, 0.05f, float.NegativeInfinity),
            finalBalance.SideGap,
            finalBalance.PairMaxGap,
            finalBalance.TargetSideGap,
            finalBalance.TargetPairMaxGap,
            finalBalance.MinPairRatio,
            finalBalance.MinFootNominalRatio,
            finalBalance.MinTargetNominalRatio,
            maxWrongAfterBudget,
            maxSameHalfAfterBudget,
            maxInvertedAfterBudget,
            finalWrong,
            finalSameHalf,
            finalInverted,
            maxNoGrip,
            maxUpperError,
            maxLowerError,
            maxKneeStepRatio,
            minPoleDot,
            progress,
            poleSidesCorrect,
            finite);
    }

    private static bool TryTurnLane(
        SpiderLocomotionController spider,
        SpiderLeg leg,
        Vector3 support,
        out float footLane,
        out float targetLane)
    {
        footLane = float.NegativeInfinity;
        targetLane = float.NegativeInfinity;
        Vector3 target = leg.HasFrozenSwingTarget
            ? leg.SwingLandingGoal
            : leg.HuntPos;
        return TryTurnPointLane(
                spider, leg, support, leg.Pos, out footLane)
            && TryTurnPointLane(
                spider, leg, support, target, out targetLane);
    }

    private static bool TryTurnPointLane(
        SpiderLocomotionController spider,
        SpiderLeg leg,
        Vector3 support,
        Vector3 point,
        out float lane)
    {
        lane = float.NegativeInfinity;
        if (!TryTurnFrame(
            spider, leg.Anchor, support, spider.Forward, out _, out Vector3 right))
        {
            return false;
        }
        float reach = Mathf.Max(leg.MaxReach, 1e-5f);
        lane = (point - leg.RootPos).Dot(right * leg.Side) / reach;
        return float.IsFinite(lane);
    }

    private static bool TryMeasureTurnBalance(
        SpiderLocomotionController spider,
        int tick,
        int windowSize,
        Queue<float>[] footLaneWindows,
        float[] latestTargetLanes,
        bool[] targetLaneSeen,
        bool[] alignedTargetLaneSeen,
        out TurnBalanceSample sample)
    {
        sample = default;
        int legCount = spider.Legs.Count;
        if (footLaneWindows.Length != legCount
            || latestTargetLanes.Length != legCount
            || targetLaneSeen.Length != legCount
            || alignedTargetLaneSeen.Length != legCount)
        {
            return false;
        }

        var footMeans = new float[legCount];
        float leftFoot = 0f;
        float rightFoot = 0f;
        float leftTarget = 0f;
        float rightTarget = 0f;
        int leftCount = 0;
        int rightCount = 0;
        float minFootNominalRatio = float.PositiveInfinity;
        float minTargetNominalRatio = float.PositiveInfinity;
        for (int i = 0; i < legCount; i++)
        {
            Queue<float> window = footLaneWindows[i];
            if (window.Count < windowSize
                || !targetLaneSeen[i]
                || !alignedTargetLaneSeen[i])
            {
                return false;
            }
            float sum = 0f;
            foreach (float value in window)
            {
                sum += value;
            }
            footMeans[i] = sum / window.Count;
            float nominal = Mathf.Max(
                spider.Legs[i].NominalLateralReachRatio, 1e-5f);
            minFootNominalRatio = Mathf.Min(
                minFootNominalRatio, footMeans[i] / nominal);
            minTargetNominalRatio = Mathf.Min(
                minTargetNominalRatio, latestTargetLanes[i] / nominal);
            if (spider.Legs[i].Side < 0)
            {
                leftFoot += footMeans[i];
                leftTarget += latestTargetLanes[i];
                leftCount++;
            }
            else
            {
                rightFoot += footMeans[i];
                rightTarget += latestTargetLanes[i];
                rightCount++;
            }
        }
        if (leftCount == 0 || rightCount == 0)
        {
            return false;
        }

        float pairGapSum = 0f;
        float pairMaxGap = 0f;
        float targetPairMaxGap = 0f;
        float minPairRatio = 1f;
        int pairCount = 0;
        for (int i = 0; i < legCount; i++)
        {
            SpiderLeg leg = spider.Legs[i];
            if (leg.Side >= 0 || leg.Pair is not { } pair)
            {
                continue;
            }
            int pairIndex = spider.Legs.IndexOf(pair);
            if (pairIndex < 0)
            {
                return false;
            }
            float a = footMeans[i];
            float b = footMeans[pairIndex];
            float gap = Mathf.Abs(a - b);
            pairGapSum += gap;
            pairMaxGap = Mathf.Max(pairMaxGap, gap);
            targetPairMaxGap = Mathf.Max(
                targetPairMaxGap,
                Mathf.Abs(
                    latestTargetLanes[i] - latestTargetLanes[pairIndex]));
            float maximum = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            float ratio = maximum > 1e-5f
                ? Mathf.Min(Mathf.Abs(a), Mathf.Abs(b)) / maximum
                : 1f;
            minPairRatio = Mathf.Min(minPairRatio, ratio);
            pairCount++;
        }
        if (pairCount == 0)
        {
            return false;
        }

        sample = new TurnBalanceSample(
            tick,
            Mathf.Abs(leftFoot / leftCount - rightFoot / rightCount),
            pairGapSum / pairCount,
            pairMaxGap,
            Mathf.Abs(leftTarget / leftCount - rightTarget / rightCount),
            targetPairMaxGap,
            minPairRatio,
            minFootNominalRatio,
            minTargetNominalRatio);
        return float.IsFinite(sample.SideGap)
            && float.IsFinite(sample.PairMeanGap)
            && float.IsFinite(sample.PairMaxGap)
            && float.IsFinite(sample.TargetSideGap)
            && float.IsFinite(sample.TargetPairMaxGap)
            && float.IsFinite(sample.MinPairRatio)
            && float.IsFinite(sample.MinFootNominalRatio)
            && float.IsFinite(sample.MinTargetNominalRatio);
    }

    private static float PercentileOr(
        List<float> values, float percentile, float fallback)
    {
        if (values.Count == 0)
        {
            return fallback;
        }
        float[] sorted = values.ToArray();
        Array.Sort(sorted);
        int index = Mathf.Clamp(
            Mathf.CeilToInt(percentile * sorted.Length) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private static bool TryTurnFrame(
        SpiderLocomotionController spider,
        BodyChunk anchor,
        Vector3 support,
        Vector3 fallback,
        out Vector3 forward,
        out Vector3 right)
    {
        forward = Vector3.Zero;
        right = Vector3.Zero;
        int index = SegmentIndex(spider, anchor);
        if (index < 0 || spider.Segments.Count < 2)
        {
            return false;
        }
        Vector3 up = support.LengthSquared() > 1e-10f
            ? support.Normalized()
            : Vector3.Up;
        forward = index < spider.Segments.Count - 1
            ? spider.Segments[index].Pos - spider.Segments[index + 1].Pos
            : spider.Segments[^2].Pos - spider.Segments[^1].Pos;
        forward -= up * forward.Dot(up);
        if (forward.LengthSquared() < 1e-10f)
        {
            forward = fallback - up * fallback.Dot(up);
        }
        if (forward.LengthSquared() < 1e-10f)
        {
            return false;
        }
        forward = forward.Normalized();
        right = forward.Cross(up);
        if (right.LengthSquared() < 1e-10f)
        {
            return false;
        }
        right = right.Normalized();
        return true;
    }

    private static void CountTurnLayout(
        SpiderLocomotionController spider,
        Vector3 support,
        float laneMargin,
        out int wrongLegs,
        out int sameHalfPairs,
        out int invertedPairs,
        out float minPairSeparation)
    {
        wrongLegs = 0;
        sameHalfPairs = 0;
        invertedPairs = 0;
        minPairSeparation = float.PositiveInfinity;
        foreach (SpiderLeg leg in spider.Legs)
        {
            if (!TryTurnLane(spider, leg, support, out float footLane, out _)
                || footLane < laneMargin)
            {
                wrongLegs++;
            }
        }

        for (int i = 0; i < spider.Legs.Count; i++)
        {
            SpiderLeg a = spider.Legs[i];
            for (int j = i + 1; j < spider.Legs.Count; j++)
            {
                SpiderLeg b = spider.Legs[j];
                if (a.PairIndex != b.PairIndex || a.Side == b.Side)
                {
                    continue;
                }
                SpiderLeg left = a.Side < 0 ? a : b;
                SpiderLeg rightLeg = a.Side > 0 ? a : b;
                if (!TryTurnFrame(
                    spider, left.Anchor, support, spider.Forward,
                    out _, out Vector3 right))
                {
                    sameHalfPairs++;
                    invertedPairs++;
                    minPairSeparation = float.NegativeInfinity;
                    continue;
                }
                float leftPosition = (left.Pos - left.Anchor.Pos).Dot(right);
                float rightPosition =
                    (rightLeg.Pos - rightLeg.Anchor.Pos).Dot(right);
                if (leftPosition * rightPosition >= 0f)
                {
                    sameHalfPairs++;
                }
                float reach = Mathf.Max(
                    (left.MaxReach + rightLeg.MaxReach) * 0.5f, 1e-5f);
                float separation =
                    (rightLeg.Pos - left.Pos).Dot(right) / reach;
                minPairSeparation = Mathf.Min(minPairSeparation, separation);
                if (separation <= 0f)
                {
                    invertedPairs++;
                }
            }
        }
        if (float.IsPositiveInfinity(minPairSeparation))
        {
            minPairSeparation = float.NegativeInfinity;
        }
    }

    private static RunResult Run(SpiderBreedParams breed)
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), breed);
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);
        var hasher = new DeterminismHasher();
        Vector3 start = spider.Primary.Pos;
        float maxConstraint = 0f;
        float maxUpper = 0f;
        float maxLower = 0f;
        int maxNoGrip = 0;
        float minBendHeight = float.PositiveInfinity;
        float maxKneeStepRatio = 0f;
        float minPoleDot = 1f;
        float maxReachExcess = 0f;
        float maxFootPenetration = 0f;
        bool finite = true;
        bool poleStable = true;
        var sideRecoveryRuns = new int[spider.Legs.Count];
        int maxSideRecovery = 0;
        var previousPoles = new Vector3[spider.Legs.Count];

        for (long tick = 1; tick <= Ticks; tick++)
        {
            spider.MoveDir = tick < 450
                ? Vector3.Right
                : new Vector3(1f, 0f, -0.35f).Normalized();
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, tick));

            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderBodyState(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.FoldSpiderControllerState(spider);
            maxConstraint = Mathf.Max(maxConstraint, spider.Body.CurrentMaxDeviation());
            maxNoGrip = Mathf.Max(maxNoGrip, spider.NoGripCounter);
            for (int i = 0; i < spider.Legs.Count; i++)
            {
                SpiderLeg leg = spider.Legs[i];
                float upperError = Mathf.Abs((leg.KneePos - leg.RootPos).Length() - leg.UpperLength);
                float lowerError = Mathf.Abs((leg.Pos - leg.KneePos).Length() - leg.LowerLength);
                maxUpper = Mathf.Max(maxUpper, upperError);
                maxLower = Mathf.Max(maxLower, lowerError);
                Vector3 axis = leg.Pos - leg.RootPos;
                float axisLengthSq = axis.LengthSquared();
                if (axisLengthSq > 1e-8f)
                {
                    Vector3 projected = leg.RootPos
                        + axis * ((leg.KneePos - leg.RootPos).Dot(axis) / axisLengthSq);
                    minBendHeight = Mathf.Min(minBendHeight, (leg.KneePos - projected).Length());
                }
                maxKneeStepRatio = Mathf.Max(maxKneeStepRatio,
                    (leg.KneePos - leg.LastKneePos).Length()
                    / (leg.UpperLength + leg.LowerLength));
                maxReachExcess = Mathf.Max(maxReachExcess,
                    (leg.Pos - leg.RootPos).Length() - leg.MaxReach);
                if (terrain.SpherePenetration(leg.Pos, leg.Radius, out _, out float footDepth))
                {
                    maxFootPenetration = Mathf.Max(maxFootPenetration, footDepth);
                }
                if (leg.Pair is { } pair)
                {
                    Vector3 outward = leg.RootPos - pair.RootPos;
                    if (outward.LengthSquared() > 1e-8f)
                    {
                        float sideDot = leg.BendPole.Dot(outward.Normalized());
                        sideRecoveryRuns[i] = sideDot > 0.02f
                            ? 0
                            : sideRecoveryRuns[i] + 1;
                        maxSideRecovery = Math.Max(maxSideRecovery, sideRecoveryRuns[i]);
                    }
                }
                finite &= leg.Pos.IsFinite() && leg.Vel.IsFinite()
                    && leg.KneePos.IsFinite() && leg.BendPole.IsFinite();
                if (previousPoles[i].LengthSquared() > 1e-8f && leg.BendPole.LengthSquared() > 1e-8f)
                {
                    float dot = previousPoles[i].Normalized().Dot(leg.BendPole.Normalized());
                    minPoleDot = Mathf.Min(minPoleDot, dot);
                    poleStable &= dot >= -0.01f;
                }
                previousPoles[i] = leg.BendPole;
            }
            foreach (BodyChunk chunk in spider.Body.Chunks)
            {
                finite &= chunk.Pos.IsFinite() && chunk.Vel.IsFinite();
            }
        }

        bool sidesCorrect = maxSideRecovery <= 30;
        foreach (SpiderLeg leg in spider.Legs)
        {
            if (leg.Gripping && leg.Pair is { } pair)
            {
                Vector3 outward = leg.RootPos - pair.RootPos;
                sidesCorrect &= outward.LengthSquared() > 1e-8f
                    && leg.BendPole.Dot(outward.Normalized()) > 0.02f;
            }
        }

        return new RunResult(
            hasher.Value,
            (spider.Primary.Pos - start).Length(),
            maxConstraint,
            maxUpper,
            maxLower,
            maxNoGrip,
            minBendHeight,
            maxKneeStepRatio,
            minPoleDot,
            maxReachExcess,
            maxFootPenetration,
            maxSideRecovery,
            finite,
            poleStable,
            sidesCorrect);
    }

    private static bool CheckTopology(out string message)
    {
        SpiderLocomotionController small = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 1f, 0f), SpiderFactory.SmallSpider());
        SpiderLocomotionController large = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 1f, 0f), SpiderFactory.LargeSpider());
        SpiderLocomotionController synthetic = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 1f, 0f), SpiderFactory.SyntheticMultiAnchor());

        bool Formal(SpiderLocomotionController spider)
        {
            if (spider.Segments.Count != 2 || spider.Legs.Count != 8)
            {
                return false;
            }
            foreach (SpiderLeg leg in spider.Legs)
            {
                if (leg.Anchor != spider.Segments[0]
                    || !leg.UseExplicitTouchdownLead)
                {
                    return false;
                }
            }
            for (int i = 0; i < spider.Legs.Count; i += 2)
            {
                SpiderLeg left = spider.Legs[i];
                SpiderLeg right = spider.Legs[i + 1];
                if (left.Pair != right || right.Pair != left
                    || left.PhaseGroup == right.PhaseGroup
                    || left.PhaseGroup != (left.PairIndex & 1)
                    || right.PhaseGroup != ((right.PairIndex & 1) ^ 1))
                {
                    return false;
                }
            }
            return BirthPoseReady(spider);
        }

        var anchorCounts = new int[synthetic.Segments.Count];
        foreach (SpiderLeg leg in synthetic.Legs)
        {
            for (int i = 0; i < synthetic.Segments.Count; i++)
            {
                if (leg.Anchor == synthetic.Segments[i])
                {
                    anchorCounts[i]++;
                }
            }
        }
        bool syntheticOk = synthetic.Segments.Count == 3
            && synthetic.Legs.Count == 6
            && anchorCounts[0] == 2 && anchorCounts[1] == 2 && anchorCounts[2] == 2
            && synthetic.Body.Connections.Count == 2
            && Connects(synthetic.Body.Connections[0],
                synthetic.Segments[0], synthetic.Segments[1])
            && Connects(synthetic.Body.Connections[1],
                synthetic.Segments[1], synthetic.Segments[2])
            && BirthPoseReady(synthetic);

        SyntheticResult syntheticA = RunSynthetic();
        SyntheticResult syntheticB = RunSynthetic();
        bool syntheticRuntime = syntheticA.Hash == syntheticB.Hash
            && syntheticA.Finite && syntheticB.Finite
            && syntheticA.LocalRootsCorrect && syntheticB.LocalRootsCorrect
            && syntheticA.MaxConstraintError <= 0.08f
            && syntheticB.MaxConstraintError <= 0.08f;

        bool ok = Formal(small) && Formal(large) && syntheticOk && syntheticRuntime;
        message = $"small/large=2节+8腿全前锚={Formal(small) && Formal(large)}；" +
            $"synthetic=3节每节2腿+相邻链={syntheticOk}；" +
            $"runtime={syntheticRuntime} hash={syntheticA.Hash:X16}";
        return ok;
    }

    private static SyntheticResult RunSynthetic()
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SyntheticMultiAnchor());
        BodyChunk rear = spider.Segments[2];
        rear.Pos += new Vector3(0f, 0f, 0.12f);
        rear.LastPos = rear.Pos;
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);
        var hasher = new DeterminismHasher();
        bool finite = true;
        bool localRoots = true;
        float maxConstraint = 0f;

        for (long tick = 1; tick <= 360; tick++)
        {
            Vector3 supportUsed = spider.SupportNormal;
            spider.MoveDir = tick < 180
                ? Vector3.Right
                : new Vector3(0.65f, 0f, -0.75f).Normalized();
            spider.RunSpeed = 0.8f;
            spider.Tick(new TickContext(gravity, terrain, tick));

            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderBodyState(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.FoldSpiderControllerState(spider);
            maxConstraint = Mathf.Max(maxConstraint, spider.Body.CurrentMaxDeviation());

            foreach (SpiderLeg leg in spider.Legs)
            {
                int index = SegmentIndex(spider, leg.Anchor);
                Vector3 localForward = index < spider.Segments.Count - 1
                    ? spider.Segments[index].Pos - spider.Segments[index + 1].Pos
                    : spider.Segments[^2].Pos - spider.Segments[^1].Pos;
                Vector3 up = supportUsed.Normalized();
                localForward -= up * localForward.Dot(up);
                if (localForward.LengthSquared() > 1e-8f)
                {
                    localForward = localForward.Normalized();
                    Vector3 right = localForward.Cross(up).Normalized();
                    Vector3 expected = leg.Anchor.Pos
                        + localForward * leg.RootOffset.X
                        + up * leg.RootOffset.Y
                        + right * (leg.RootOffset.Z + leg.Side * leg.RootLateralOffset);
                    localRoots &= (leg.RootPos - expected).Length() <= 1e-4f;
                }
                finite &= leg.Pos.IsFinite() && leg.RootPos.IsFinite()
                    && leg.KneePos.IsFinite() && leg.BendPole.IsFinite();
            }
            foreach (BodyChunk chunk in spider.Body.Chunks)
            {
                finite &= chunk.Pos.IsFinite() && chunk.Vel.IsFinite();
            }
        }

        return new SyntheticResult(hasher.Value, finite, localRoots, maxConstraint);
    }

    private static bool BirthPoseReady(SpiderLocomotionController spider)
    {
        foreach (SpiderLeg leg in spider.Legs)
        {
            Vector3 outward = leg.Pair is { } pair
                ? leg.RootPos - pair.RootPos
                : Vector3.Zero;
            if (!Near(leg.RootPos, leg.LastRootPos)
                || !Near(leg.KneePos, leg.LastKneePos)
                || !Near(leg.Pos, leg.LastPos)
                || Mathf.Abs((leg.KneePos - leg.RootPos).Length() - leg.UpperLength) > 1e-4f
                || Mathf.Abs((leg.Pos - leg.KneePos).Length() - leg.LowerLength) > 1e-4f
                || outward.LengthSquared() <= 1e-8f
                || leg.BendPole.Dot(outward.Normalized()) <= 0.02f)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Connects(ChunkConnection connection, BodyChunk a, BodyChunk b) =>
        (connection.A == a && connection.B == b)
        || (connection.A == b && connection.B == a);

    private static int SegmentIndex(SpiderLocomotionController spider, BodyChunk chunk)
    {
        for (int i = 0; i < spider.Segments.Count; i++)
        {
            if (spider.Segments[i] == chunk)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool CheckLifecycle(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);
        for (long tick = 1; tick <= 120; tick++)
        {
            spider.MoveDir = Vector3.Right;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, tick));
        }
        long lifecycleTick = 120;
        while (lifecycleTick < 360
            && (spider.LegsGripping < spider.MinGroundedLegs || spider.ApplyGravity))
        {
            lifecycleTick++;
            spider.MoveDir = Vector3.Right;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, lifecycleTick));
        }
        bool settledBeforeLifecycle = spider.LegsGripping >= spider.MinGroundedLegs
            && !spider.ApplyGravity;

        Vector3 bodyBefore = spider.Primary.Pos;
        Vector3 lastMoveTargetBefore = spider.LastMoveTarget;
        var beforeShift = new LegState[spider.Legs.Count];
        for (int i = 0; i < spider.Legs.Count; i++)
        {
            beforeShift[i] = Capture(spider.Legs[i]);
        }
        Vector3 target = spider.Primary.Pos + Vector3.Right * 2f;
        spider.MoveTarget = target;
        var shift = new Vector3(512f, 0f, -256f);
        spider.Shift(shift);
        bool shiftOk = Near(spider.Primary.Pos, bodyBefore + shift)
            && Near(spider.LastMoveTarget, lastMoveTargetBefore + shift)
            && spider.MoveTarget is { } shiftedTarget
            && Near(shiftedTarget, target + shift);
        for (int i = 0; i < spider.Legs.Count; i++)
        {
            SpiderLeg leg = spider.Legs[i];
            LegState before = beforeShift[i];
            shiftOk &= Near(leg.Pos, before.Pos + shift)
                && Near(leg.LastPos, before.LastPos + shift)
                && Near(leg.HuntPos, before.HuntPos + shift)
                && Near(leg.RootPos, before.RootPos + shift)
                && Near(leg.LastRootPos, before.LastRootPos + shift)
                && Near(leg.KneePos, before.KneePos + shift)
                && Near(leg.LastKneePos, before.LastKneePos + shift)
                && Near(leg.Vel, before.Vel)
                && Near(leg.BendPole, before.BendPole)
                && Near(leg.GripNormal, before.GripNormal)
                && Near(leg.LastGripNormal, before.LastGripNormal)
                && Near(leg.LastIkAxis, before.LastIkAxis)
                && Near(leg.SwingLandingGoal, before.SwingLandingGoal + shift)
                && Near(leg.SwingHuntTarget, before.SwingHuntTarget + shift)
                && leg.HasGrip == before.HasGrip
                && leg.ReachingForTerrain == before.ReachingForTerrain
                && leg.TerrainContact == before.TerrainContact
                && leg.TargetSurfaceContact == before.TargetSurfaceContact
                && leg.HasFrozenSwingTarget == before.HasFrozenSwingTarget
                && leg.GripCounter == before.GripCounter
                && leg.AcquisitionTicks == before.AcquisitionTicks
                && leg.SwingTicks == before.SwingTicks
                && leg.StepSerial == before.StepSerial
                && leg.EmergencyStepCount == before.EmergencyStepCount
                && leg.LandingSerial == before.LandingSerial
                && leg.LastStepReason == before.LastStepReason;
        }

        var beforeTeleport = new LegState[spider.Legs.Count];
        bool teleportHadGrip = false;
        for (int i = 0; i < spider.Legs.Count; i++)
        {
            beforeTeleport[i] = Capture(spider.Legs[i]);
            teleportHadGrip |= spider.Legs[i].Gripping;
        }
        var teleportDelta = new Vector3(4f, 1f, 0f);
        spider.Teleport(teleportDelta);
        bool teleportOk = teleportHadGrip
            && spider.MoveTarget is null && spider.ApplyGravity;
        for (int i = 0; i < spider.Legs.Count; i++)
        {
            SpiderLeg leg = spider.Legs[i];
            LegState before = beforeTeleport[i];
            teleportOk &= Near(leg.Pos, before.Pos + teleportDelta)
                && Near(leg.LastPos, before.LastPos + teleportDelta)
                && Near(leg.RootPos, before.RootPos + teleportDelta)
                && Near(leg.LastRootPos, before.LastRootPos + teleportDelta)
                && Near(leg.KneePos, before.KneePos + teleportDelta)
                && Near(leg.LastKneePos, before.LastKneePos + teleportDelta)
                && Near(leg.HuntPos, leg.Pos)
                && Near(leg.BendPole, before.BendPole)
                && Near(leg.LastGripNormal, before.LastGripNormal)
                && Near(leg.LastIkAxis, before.LastIkAxis)
                && Near(leg.SwingLandingGoal, leg.Pos)
                && Near(leg.SwingHuntTarget, leg.Pos)
                && !leg.HasGrip
                && !leg.ReachingForTerrain
                && !leg.TerrainContact
                && !leg.TargetSurfaceContact
                && !leg.HasFrozenSwingTarget
                && leg.GripCounter == 0
                && leg.AcquisitionTicks == 0
                && leg.SwingTicks == 0
                && leg.StepSerial == before.StepSerial + 1
                && leg.EmergencyStepCount == before.EmergencyStepCount
                && leg.LandingSerial == before.LandingSerial
                && leg.LastStepReason == SpiderStepReason.Forced;
        }

        bool recoveredAfterTeleport = false;
        long teleportDeadline = lifecycleTick + 600;
        for (; lifecycleTick < teleportDeadline;)
        {
            lifecycleTick++;
            spider.MoveDir = Vector3.Right;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, lifecycleTick));
            recoveredAfterTeleport = spider.LegsGripping >= spider.MinGroundedLegs
                && !spider.ApplyGravity;
            if (recoveredAfterTeleport)
            {
                break;
            }
        }

        var beforeLaunch = new LegState[spider.Legs.Count];
        var bodyVelocities = new Vector3[spider.Body.Chunks.Count];
        bool launchHadGrip = false;
        for (int i = 0; i < spider.Legs.Count; i++)
        {
            beforeLaunch[i] = Capture(spider.Legs[i]);
            launchHadGrip |= spider.Legs[i].Gripping;
        }
        for (int i = 0; i < spider.Body.Chunks.Count; i++)
        {
            bodyVelocities[i] = spider.Body.Chunks[i].Vel;
        }
        var impulse = new Vector3(0.05f, 0.35f, 0f);
        spider.Launch(impulse);
        bool launchCleared = launchHadGrip && spider.ApplyGravity;
        for (int i = 0; i < spider.Body.Chunks.Count; i++)
        {
            launchCleared &= Near(spider.Body.Chunks[i].Vel, bodyVelocities[i] + impulse);
        }
        for (int i = 0; i < spider.Legs.Count; i++)
        {
            SpiderLeg leg = spider.Legs[i];
            LegState before = beforeLaunch[i];
            launchCleared &= Near(leg.Pos, before.Pos)
                && Near(leg.LastPos, before.LastPos)
                && Near(leg.RootPos, before.RootPos)
                && Near(leg.KneePos, before.KneePos)
                && Near(leg.HuntPos, leg.Pos)
                && Near(leg.BendPole, before.BendPole)
                && Near(leg.LastGripNormal, before.LastGripNormal)
                && Near(leg.LastIkAxis, before.LastIkAxis)
                && Near(leg.SwingLandingGoal, leg.Pos)
                && Near(leg.SwingHuntTarget, leg.Pos)
                && !leg.HasGrip
                && !leg.ReachingForTerrain
                && !leg.TerrainContact
                && !leg.TargetSurfaceContact
                && !leg.HasFrozenSwingTarget
                && leg.GripCounter == 0
                && leg.AcquisitionTicks == 0
                && leg.SwingTicks == 0
                && leg.StepSerial == before.StepSerial + 1
                && leg.EmergencyStepCount == before.EmergencyStepCount
                && leg.LandingSerial == before.LandingSerial
                && leg.LastStepReason == SpiderStepReason.Forced;
        }

        bool recoveredAfterLaunch = false;
        long launchDeadline = lifecycleTick + 600;
        for (; lifecycleTick < launchDeadline;)
        {
            lifecycleTick++;
            spider.MoveDir = Vector3.Right;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, lifecycleTick));
            recoveredAfterLaunch = spider.LegsGripping >= spider.MinGroundedLegs
                && !spider.ApplyGravity;
            if (recoveredAfterLaunch)
            {
                break;
            }
        }

        bool ok = settledBeforeLifecycle && shiftOk && teleportOk
            && recoveredAfterTeleport && launchCleared && recoveredAfterLaunch;
        message = $"前置抓稳={settledBeforeLifecycle}，Shift={shiftOk}，" +
            $"Teleport清抓点/再抓={teleportOk}/{recoveredAfterTeleport}，" +
            $"Launch从抓稳态清抓点/再抓={launchCleared}/{recoveredAfterLaunch}";
        return ok;
    }

    private static bool CheckMoveTarget(out string message)
    {
        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.65f, 0f), SpiderFactory.SmallSpider());
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);
        for (long tick = 1; tick <= 100; tick++)
        {
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(gravity, terrain, tick));
        }

        Vector3 firstTarget = new(spider.Primary.Pos.X + 2.5f, 0f, spider.Primary.Pos.Z);
        spider.MoveTarget = firstTarget;
        bool externalOnly = true;
        bool arrived = false;
        long currentTick = 101;
        for (; currentTick <= 500; currentTick++)
        {
            spider.MoveDir = Vector3.Left; // 直喂必须覆盖这个故意相反的宿主方向。
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, currentTick));
            if (spider.AtMoveTarget)
            {
                arrived = true;
                break;
            }
            externalOnly &= spider.LastMoveTargetKind == MoveTargetKind.External;
        }
        bool arrivedStops = arrived && spider.AtMoveTarget
            && spider.LastMoveTargetKind == MoveTargetKind.None
            && !spider.HasMoveIntent;

        Vector3 replanStart = spider.Primary.Pos;
        spider.MoveTarget = new Vector3(replanStart.X + 2f, 0f, replanStart.Z);
        bool replanExternal = true;
        for (int step = 0; step < 20; step++)
        {
            currentTick++;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, currentTick));
            replanExternal &= spider.LastMoveTargetKind == MoveTargetKind.External;
        }
        Vector3 secondTarget = new(spider.Primary.Pos.X, 0f, spider.Primary.Pos.Z - 2f);
        spider.MoveTarget = secondTarget;
        Vector3 beforeTurn = spider.Primary.Pos;
        for (int step = 0; step < 80; step++)
        {
            currentTick++;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(gravity, terrain, currentTick));
            if (!spider.AtMoveTarget)
            {
                replanExternal &= spider.LastMoveTargetKind == MoveTargetKind.External;
            }
        }
        bool replanResponded = spider.Primary.Pos.Z < beforeTurn.Z - 0.15f;

        spider.MoveTarget = null;
        spider.MoveDir = Vector3.Zero;
        spider.RunSpeed = 1f;
        currentTick++;
        spider.Tick(new TickContext(gravity, terrain, currentTick));
        bool cancelled = !spider.HasMoveIntent
            && !spider.AtMoveTarget
            && spider.LastMoveTargetKind == MoveTargetKind.None;

        bool ok = externalOnly && arrivedStops
            && replanExternal && replanResponded && cancelled;
        message = $"External恒定={externalOnly}，到达停下={arrivedStops}，" +
            $"运行中换点/响应={replanExternal}/{replanResponded}，取消={cancelled}";
        return ok;
    }

    /// <summary>
    /// 极不等长两段腿的可达域不是球，而是
    /// [abs(Upper-Lower), Upper+Lower) 的球壳。专门钉住出生装配、足端约束、真实抓点
    /// 和地形解算都不能穿进内球；普通大小腿接近等长时内半径接近零，主 smoke 看不见此类回归。
    /// </summary>
    private static bool CheckUnequalLegGeometry(out string message)
    {
        const float upperLength = 0.12f;
        const float lowerLength = 0.50f;
        const float reachTolerance = 1e-3f;
        const float penetrationTolerance = 1e-4f;
        const int runtimeTicks = 320;

        var breed = new SpiderBreedParams
        {
            Name = "spider-annulus-smoke",
            BaseSpeed = 0.04f,
            MaxMoveSpeed = 0.065f,
            NoGripSpeed = 0.08f,
            MinGroundedLegs = 1,
            RegainFootingTicks = 2,
            LoseGripTicks = 6,
            SupportBlend = 0.22f,
            GaitPhaseTicks = 8,
            MinPlantedLegs = 1,
            TrailingGain = 0.16f,
            LookAhead = 0.42f,
            RideHeight = 0.15f,
            StallReleaseTicks = 18,
            ConstraintIterations = 4,
        };
        breed.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.13f,
            Mass = 0.32f,
        });
        breed.BodySegments.Add(new BodySegmentSpec
        {
            Radius = 0.16f,
            Mass = 0.48f,
            ConnectionLength = 0.24f,
            ConnectionWeight = 0.45f,
            ConnectionElasticity = 0.2f,
        });
        breed.LegPairs.Add(new LegPairSpec
        {
            AnchorSegmentIndex = 0,
            RootOffset = Vector3.Zero,
            RootLateralOffset = 0.04f,
            FanAngleDegrees = 0f,
            PhaseGroup = 0,
            UpperLength = upperLength,
            LowerLength = lowerLength,
            FootRadius = 0.03f,
            HuntSpeed = 0.15f,
            Quickness = 0.66f,
            GripDelay = 2,
            StepLength = 0.62f,
            LiftFeet = 0.22f,
            FeetDown = 0.55f,
            PairLateral = 0.24f,
            ProbeReach = 0.08f,
        });

        var terrain = new PlaneTerrainQuery(0f);
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 0.45f, 0f), breed);
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);

        bool birthDistanceOk = spider.Legs.Count == 2;
        bool birthIkOk = spider.Legs.Count == 2;
        bool finite = true;
        float birthMinDistance = float.PositiveInfinity;
        float birthMaxDistance = 0f;
        float minimumReach = float.PositiveInfinity;
        float maximumReach = 0f;

        foreach (SpiderLeg leg in spider.Legs)
        {
            float distance = (leg.Pos - leg.RootPos).Length();
            birthMinDistance = Mathf.Min(birthMinDistance, distance);
            birthMaxDistance = Mathf.Max(birthMaxDistance, distance);
            minimumReach = Mathf.Min(minimumReach, leg.MinimumReach);
            maximumReach = Mathf.Max(maximumReach, leg.MaxReach);
            birthDistanceOk &= distance >= leg.MinimumReach - reachTolerance
                && distance <= leg.MaxReach + reachTolerance;
            birthIkOk &= Mathf.Abs((leg.KneePos - leg.RootPos).Length() - upperLength)
                    <= reachTolerance
                && Mathf.Abs((leg.Pos - leg.KneePos).Length() - lowerLength)
                    <= reachTolerance;
            finite &= leg.Pos.IsFinite() && leg.RootPos.IsFinite()
                && leg.KneePos.IsFinite() && leg.BendPole.IsFinite()
                && float.IsFinite(leg.MinimumReach) && float.IsFinite(leg.MaxReach);
        }

        bool runtimeDistanceOk = true;
        bool runtimeIkOk = true;
        bool noPenetration = true;
        bool gripTargetsInAnnulus = true;
        bool sawGripEvidence = false;
        int gripEvidenceTicks = 0;
        int invalidGripEvidenceTicks = 0;
        float minimumGripReach = float.PositiveInfinity;
        float maximumGripReach = 0f;
        float runtimeMinDistance = float.PositiveInfinity;
        float runtimeMaxDistance = 0f;
        float maxIkError = 0f;
        float maxPenetration = 0f;

        for (long tick = 1; tick <= runtimeTicks; tick++)
        {
            spider.MoveDir = tick <= runtimeTicks / 2
                ? Vector3.Right
                : new Vector3(0.85f, 0f, -0.35f).Normalized();
            spider.RunSpeed = 0.75f;
            spider.Tick(new TickContext(gravity, terrain, tick));

            foreach (SpiderLeg leg in spider.Legs)
            {
                float distance = (leg.Pos - leg.RootPos).Length();
                runtimeMinDistance = Mathf.Min(runtimeMinDistance, distance);
                runtimeMaxDistance = Mathf.Max(runtimeMaxDistance, distance);
                runtimeDistanceOk &= distance >= leg.MinimumReach - reachTolerance
                    && distance <= leg.MaxReach + reachTolerance;

                float upperError = Mathf.Abs(
                    (leg.KneePos - leg.RootPos).Length() - upperLength);
                float lowerError = Mathf.Abs(
                    (leg.Pos - leg.KneePos).Length() - lowerLength);
                maxIkError = Mathf.Max(maxIkError, Mathf.Max(upperError, lowerError));
                runtimeIkOk &= upperError <= reachTolerance && lowerError <= reachTolerance;

                if (terrain.SpherePenetration(
                        leg.Pos, leg.Radius, out _, out float penetration))
                {
                    maxPenetration = Mathf.Max(maxPenetration, penetration);
                    noPenetration &= penetration <= penetrationTolerance;
                }

                if (leg.HasGrip || leg.GripCounter > 0)
                {
                    sawGripEvidence = true;
                    gripEvidenceTicks++;
                    Vector3 normal = leg.GripNormal.LengthSquared() > 1e-10f
                        ? leg.GripNormal.Normalized()
                        : Vector3.Zero;
                    Vector3 contactCenter = leg.HuntPos + normal * leg.Radius;
                    float gripReach = (contactCenter - leg.RootPos).Length();
                    minimumGripReach = Mathf.Min(minimumGripReach, gripReach);
                    maximumGripReach = Mathf.Max(maximumGripReach, gripReach);
                    bool gripInAnnulus = normal != Vector3.Zero
                        && gripReach >= leg.MinimumReach - reachTolerance
                        && gripReach <= leg.MaxReach + reachTolerance;
                    gripTargetsInAnnulus &= gripInAnnulus;
                    if (!gripInAnnulus)
                    {
                        invalidGripEvidenceTicks++;
                    }
                }

                finite &= leg.Pos.IsFinite() && leg.Vel.IsFinite()
                    && leg.RootPos.IsFinite() && leg.KneePos.IsFinite()
                    && leg.BendPole.IsFinite() && leg.HuntPos.IsFinite()
                    && leg.GripNormal.IsFinite();
            }
            foreach (BodyChunk chunk in spider.Body.Chunks)
            {
                finite &= chunk.Pos.IsFinite() && chunk.Vel.IsFinite();
            }
        }

        bool boundaryOk = CheckReachBoundaryCases(out string boundaryMessage);
        bool ok = birthDistanceOk && birthIkOk
            && runtimeDistanceOk && runtimeIkOk
            && noPenetration && gripTargetsInAnnulus
            && sawGripEvidence && finite && boundaryOk;
        message =
            $"birth={birthDistanceOk}/{birthIkOk} d=[{birthMinDistance:F4},{birthMaxDistance:F4}]；" +
            $"ring=[{minimumReach:F4},{maximumReach:F4}] runtime=[{runtimeMinDistance:F4},{runtimeMaxDistance:F4}]；" +
            $"ik={maxIkError:E2} penetration={maxPenetration:E2}；" +
            $"gripEvidence={sawGripEvidence}/{gripEvidenceTicks} " +
            $"gripReach=[{minimumGripReach:F4},{maximumGripReach:F4}] invalid={invalidGripEvidenceTicks} " +
            $"targetsInRing={gripTargetsInAnnulus} finite={finite}；boundary={boundaryMessage}";
        return ok;
    }

    private static bool CheckReachBoundaryCases(out string message)
    {
        (float Upper, float Lower)[] cases =
        {
            (1e-4f, 1e-4f),
            (1e-4f, 1f),
        };
        bool ok = true;
        float worstShortRelativeError = 0f;
        float worstLongRelativeError = 0f;
        foreach ((float upper, float lower) in cases)
        {
            var breed = new SpiderBreedParams
            {
                Name = $"spider-reach-boundary-{upper:R}-{lower:R}",
                MinGroundedLegs = 1,
                MinPlantedLegs = 1,
            };
            breed.BodySegments.Add(new BodySegmentSpec
            {
                Radius = 0.02f,
                Mass = 0.1f,
            });
            breed.BodySegments.Add(new BodySegmentSpec
            {
                Radius = 0.025f,
                Mass = 0.12f,
                ConnectionLength = 0.05f,
            });
            breed.LegPairs.Add(new LegPairSpec
            {
                AnchorSegmentIndex = 0,
                UpperLength = upper,
                LowerLength = lower,
                FootRadius = 0.005f,
                HuntSpeed = 0.01f,
                Quickness = 0.5f,
                GripDelay = 1,
                StepLength = 0.5f,
                LiftFeet = 0.2f,
                FeetDown = 0.4f,
                PairLateral = 0.1f,
                ProbeReach = 0.01f,
            });

            SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
                new Vector3(0f, 2f, 0f), breed);
            foreach (SpiderLeg leg in spider.Legs)
            {
                float sum = upper + lower;
                bool bounds = float.IsFinite(leg.MinimumReach)
                    && float.IsFinite(leg.MaxReach)
                    && float.IsFinite(leg.TheoreticalMaximumReach)
                    && leg.MinimumReach < leg.MaxReach
                    && leg.MaxReach < leg.TheoreticalMaximumReach
                    && leg.TheoreticalMaximumReach < sum;
                float measuredUpper = (leg.KneePos - leg.RootPos).Length();
                float measuredLower = (leg.Pos - leg.KneePos).Length();
                float upperRelativeError = Mathf.Abs(measuredUpper - upper) / upper;
                float lowerRelativeError = Mathf.Abs(measuredLower - lower) / lower;
                worstShortRelativeError = Mathf.Max(
                    worstShortRelativeError,
                    upper <= lower ? upperRelativeError : lowerRelativeError);
                worstLongRelativeError = Mathf.Max(
                    worstLongRelativeError,
                    upper > lower ? upperRelativeError : lowerRelativeError);
                ok &= bounds
                    && leg.Pos.IsFinite()
                    && leg.KneePos.IsFinite()
                    && upperRelativeError <= 0.02f
                    && lowerRelativeError <= 0.001f;
            }
        }
        message =
            $"strict/finite={ok} shortRel={worstShortRelativeError:E2} longRel={worstLongRelativeError:E2}";
        return ok;
    }

    private static bool CheckConfigValidation(out string message)
    {
        SpiderBreedParams ValidBreed(int segmentCount = 2)
        {
            var breed = new SpiderBreedParams
            {
                Name = "spider-config-smoke",
                MinGroundedLegs = 1,
                MinPlantedLegs = 1,
            };
            for (int i = 0; i < segmentCount; i++)
            {
                breed.BodySegments.Add(new BodySegmentSpec
                {
                    Radius = 0.1f,
                    Mass = 0.2f,
                    ConnectionLength = 0.2f,
                });
            }
            breed.LegPairs.Add(new LegPairSpec
            {
                AnchorSegmentIndex = 0,
                UpperLength = 0.2f,
                LowerLength = 0.25f,
                FootRadius = 0.02f,
                HuntSpeed = 0.05f,
                Quickness = 0.5f,
                GripDelay = 2,
                AcquisitionTimeoutTicks = 12,
            });
            return breed;
        }

        bool Throws(SpiderBreedParams breed, Vector3? origin = null)
        {
            try
            {
                _ = SpiderFactory.CreateSpiderController(
                    origin ?? Vector3.Zero, breed);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        SpiderBreedParams overflowLength = ValidBreed();
        overflowLength.LegPairs[0].UpperLength = float.MaxValue;

        SpiderBreedParams cumulativeChain = ValidBreed(segmentCount: 3);
        cumulativeChain.BodySegments[1].ConnectionLength = 600_000f;
        cumulativeChain.BodySegments[2].ConnectionLength = 600_000f;

        SpiderBreedParams iterations = ValidBreed();
        iterations.ConstraintIterations = int.MaxValue;

        SpiderBreedParams gripDelay = ValidBreed();
        gripDelay.LegPairs[0].GripDelay = int.MaxValue;

        SpiderBreedParams acquisitionDelay = ValidBreed();
        acquisitionDelay.LegPairs[0].AcquisitionTimeoutTicks = int.MaxValue;

        SpiderBreedParams trailRelease = ValidBreed();
        trailRelease.LegPairs[0].TrailReleaseRatio = 1.1f;

        SpiderBreedParams forwardBias = ValidBreed();
        forwardBias.LegPairs[0].ForwardStepBias = -0.1f;

        SpiderBreedParams touchdownLead = ValidBreed();
        touchdownLead.LegPairs[0].UseExplicitTouchdownLead = true;
        touchdownLead.LegPairs[0].TouchdownLeadRatio = 1.1f;

        SpiderBreedParams signedTouchdownLead = ValidBreed();
        signedTouchdownLead.LegPairs[0].UseExplicitTouchdownLead = true;
        signedTouchdownLead.LegPairs[0].TouchdownLeadRatio = -0.25f;

        bool lengthOk = Throws(overflowLength);
        bool chainOk = Throws(cumulativeChain);
        bool iterationsOk = Throws(iterations);
        bool gripDelayOk = Throws(gripDelay);
        bool acquisitionOk = Throws(acquisitionDelay);
        bool gaitOk = Throws(trailRelease)
            && Throws(forwardBias)
            && Throws(touchdownLead)
            && !Throws(signedTouchdownLead);
        bool originOk = Throws(ValidBreed(), new Vector3(float.MaxValue, 0f, 0f));
        bool ok = lengthOk && chainOk && iterationsOk
            && gripDelayOk && acquisitionOk && gaitOk && originOk;
        message =
            $"length={lengthOk} chain={chainOk} iterations={iterationsOk} " +
            $"grip/acquisition={gripDelayOk}/{acquisitionOk} gait={gaitOk} origin={originOk}";
        return ok;
    }

    private static bool CheckAcquisitionTimeout(out string message)
    {
        AcquisitionResult first = RunAcquisitionTimeout();
        AcquisitionResult second = RunAcquisitionTimeout();
        bool deterministic = first.Hash == second.Hash;
        bool ok = deterministic
            && first.SawBlockedCandidate
            && first.RejectedWithoutFalseGrip
            && first.RecoveredOnAlternateSurface
            && first.MaxAcquisitionTicks < 30
            && first.Finite
            && second.Finite;
        message =
            $"candidate={first.SawBlockedCandidate} reject/noFake={first.RejectedWithoutFalseGrip} " +
            $"recover={first.RecoveredOnAlternateSurface} maxTicks={first.MaxAcquisitionTicks} " +
            $"finite={first.Finite && second.Finite} " +
            $"hash={first.Hash:X16}/{second.Hash:X16}";
        return ok;
    }

    private static AcquisitionResult RunAcquisitionTimeout()
    {
        var terrain = new BlockedCandidateTerrain();
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            Vector3.Zero, SpiderFactory.SmallSpider());
        var hasher = new DeterminismHasher();
        var previousAcquisition = new int[spider.Legs.Count];
        bool sawCandidate = false;
        bool sawWrongSurfaceContact = false;
        bool noFalseGrip = true;
        bool sawExpiry = false;
        int maxAcquisition = 0;
        bool finite = true;

        for (long tick = 1; tick <= 45; tick++)
        {
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Vector3.Zero, terrain, tick));
            FoldSpiderState(hasher, spider);

            for (int i = 0; i < spider.Legs.Count; i++)
            {
                SpiderLeg leg = spider.Legs[i];
                sawCandidate |= leg.HasGrip;
                sawWrongSurfaceContact |= leg.HasGrip
                    && leg.TerrainContact && !leg.TargetSurfaceContact;
                noFalseGrip &= leg.GripCounter == 0
                    && !leg.Gripping && !leg.TargetSurfaceContact;
                sawExpiry |= previousAcquisition[i] >= leg.AcquisitionTimeoutTicks - 1
                    && leg.AcquisitionTicks == 0;
                previousAcquisition[i] = leg.AcquisitionTicks;
                maxAcquisition = Math.Max(maxAcquisition, leg.AcquisitionTicks);
            }
            finite &= AllSpiderStateFinite(spider);
        }

        terrain.UseRealFloor = true;
        bool recovered = false;
        for (long tick = 46; tick <= 180; tick++)
        {
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Vector3.Zero, terrain, tick));
            FoldSpiderState(hasher, spider);
            recovered |= spider.LegsGripping >= spider.MinGroundedLegs
                && !spider.ApplyGravity
                && AnyTargetSurfaceContact(spider);
            finite &= AllSpiderStateFinite(spider);
        }

        return new AcquisitionResult(
            hasher.Value,
            sawCandidate && sawWrongSurfaceContact,
            sawExpiry && noFalseGrip,
            recovered,
            maxAcquisition,
            finite);
    }

    private readonly record struct DirectionResult(
        ulong Hash,
        bool InitiallyOpposite,
        bool SawCandidate,
        bool SawTargetContact,
        bool Recovered,
        int RecoveryTick,
        float FinalDot,
        bool Finite);

    private static bool CheckAntipodalDirections(out string message)
    {
        DirectionResult forwardA = RunAntipodalForward();
        DirectionResult forwardB = RunAntipodalForward();
        DirectionResult ceilingA = RunDirectCeilingReacquire();
        DirectionResult ceilingB = RunDirectCeilingReacquire();
        bool forwardOk = forwardA.Hash == forwardB.Hash
            && forwardA.InitiallyOpposite
            && forwardA.Recovered && forwardA.RecoveryTick <= 40
            && forwardA.FinalDot > 0.9f
            && forwardA.Finite && forwardB.Finite;
        bool ceilingOk = ceilingA.Hash == ceilingB.Hash
            && ceilingA.InitiallyOpposite
            && ceilingA.SawCandidate
            && ceilingA.SawTargetContact
            && ceilingA.Recovered && ceilingA.RecoveryTick <= 240
            && ceilingA.FinalDot > 0.8f
            && ceilingA.Finite && ceilingB.Finite;
        message =
            $"forward={forwardOk} recover={forwardA.RecoveryTick} dot={forwardA.FinalDot:F3} " +
            $"hash={forwardA.Hash:X16}/{forwardB.Hash:X16}；" +
            $"ceiling={ceilingOk} candidate/contact={ceilingA.SawCandidate}/" +
            $"{ceilingA.SawTargetContact} recover={ceilingA.RecoveryTick} " +
            $"dot={ceilingA.FinalDot:F3} hash={ceilingA.Hash:X16}/{ceilingB.Hash:X16}";
        return forwardOk && ceilingOk;
    }

    private static DirectionResult RunAntipodalForward()
    {
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 4f, 0f), SpiderFactory.SmallSpider());
        var terrain = new EmptyTerrain();
        var hasher = new DeterminismHasher();
        bool initiallyOpposite = spider.Forward.Dot(Vector3.Right) < -0.999f;
        bool recovered = false;
        int recoveryTick = -1;
        bool finite = true;
        for (long tick = 1; tick <= 60; tick++)
        {
            spider.MoveDir = Vector3.Right;
            spider.RunSpeed = 1f;
            spider.Tick(new TickContext(Vector3.Zero, terrain, tick));
            FoldSpiderState(hasher, spider);
            if (!recovered && spider.Forward.Dot(Vector3.Right) > 0.9f)
            {
                recovered = true;
                recoveryTick = (int)tick;
            }
            finite &= AllSpiderStateFinite(spider);
        }
        return new DirectionResult(
            hasher.Value,
            initiallyOpposite,
            false,
            false,
            recovered,
            recoveryTick,
            spider.Forward.Dot(Vector3.Right),
            finite);
    }

    private static DirectionResult RunDirectCeilingReacquire()
    {
        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            Vector3.Zero, SpiderFactory.SmallSpider());
        var terrain = new CeilingTerrain(spider.Primary.Pos.Y + 0.42f);
        var hasher = new DeterminismHasher();
        spider.Teleport(Vector3.Zero);
        bool initiallyOpposite = spider.SupportNormal.Dot(Vector3.Down) < -0.999f;
        bool sawCandidate = false;
        bool sawTargetContact = false;
        bool recovered = false;
        int recoveryTick = -1;
        bool finite = true;

        for (long tick = 1; tick <= 240; tick++)
        {
            spider.MoveDir = Vector3.Zero;
            spider.RunSpeed = 0f;
            spider.Tick(new TickContext(Vector3.Zero, terrain, tick));
            FoldSpiderState(hasher, spider);
            foreach (SpiderLeg leg in spider.Legs)
            {
                bool downTarget = leg.GripNormal.Dot(Vector3.Down) > 0.9f;
                sawCandidate |= leg.HasGrip && downTarget;
                sawTargetContact |= leg.TargetSurfaceContact && downTarget;
            }
            float dot = spider.SupportNormal.Dot(Vector3.Down);
            if (!recovered && dot > 0.8f
                && spider.LegsGripping >= spider.MinGroundedLegs)
            {
                recovered = true;
                recoveryTick = (int)tick;
            }
            finite &= AllSpiderStateFinite(spider);
        }

        return new DirectionResult(
            hasher.Value,
            initiallyOpposite,
            sawCandidate,
            sawTargetContact,
            recovered,
            recoveryTick,
            spider.SupportNormal.Dot(Vector3.Down),
            finite);
    }

    /// <summary>
    /// 多节 trailing 的两个奇点：后节精确位于 Forward 一侧时，投影到连接球面的切向修正
    /// 为零；近反折时修正虽非零但小到会长期停在不稳定平衡。两种出生姿态都保持每条连接
    /// 的 RestLength，只改变角度，分别验证固定手性逃逸和沿原扰动方向放大的路径。
    /// </summary>
    private static bool CheckTrailingRecovery(out string message)
    {
        TrailingResult exactA = RunTrailingRecovery(perturbed: false);
        TrailingResult exactB = RunTrailingRecovery(perturbed: false);
        TrailingResult nearA = RunTrailingRecovery(perturbed: true);
        TrailingResult nearB = RunTrailingRecovery(perturbed: true);

        bool exactDeterministic = exactA.Hash == exactB.Hash;
        bool nearDeterministic = nearA.Hash == nearB.Hash;

        static bool Healthy(in TrailingResult result) =>
            result.InitiallyReversed
            && result.Recovered
            && result.RecoveryTick is > 0 and <= 360
            && result.FinalAlignment > 0.5f
            && result.MaxConstraintError <= 0.08f
            && result.Finite;

        bool exactOk = exactDeterministic && Healthy(exactA) && Healthy(exactB);
        bool nearOk = nearDeterministic && Healthy(nearA) && Healthy(nearB);
        bool ok = exactOk && nearOk;
        message =
            $"exact={exactOk} init/final={exactA.InitialAlignment:F4}/{exactA.FinalAlignment:F4} " +
            $"recover={exactA.RecoveryTick} dev={exactA.MaxConstraintError:F4} " +
            $"hash={exactA.Hash:X16}/{exactB.Hash:X16}；" +
            $"near={nearOk} init/final={nearA.InitialAlignment:F4}/{nearA.FinalAlignment:F4} " +
            $"recover={nearA.RecoveryTick} dev={nearA.MaxConstraintError:F4} " +
            $"hash={nearA.Hash:X16}/{nearB.Hash:X16} finite={exactA.Finite && nearA.Finite}";
        return ok;
    }

    private static TrailingResult RunTrailingRecovery(bool perturbed)
    {
        const int ticks = 360;
        const float tangentPerturbation = 1e-3f;

        SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
            new Vector3(0f, 1f, 0f), SpiderFactory.SyntheticMultiAnchor());
        var terrain = new PlaneTerrainQuery(-50f);
        var gravity = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);

        Vector3 fixedForward = spider.Forward.Normalized();
        Vector3 tangent = spider.SupportNormal.Cross(fixedForward);
        tangent = tangent.LengthSquared() > 1e-10f
            ? tangent.Normalized()
            : Vector3.Forward;
        Vector3 wrongDirection = perturbed
            ? (fixedForward + tangent * tangentPerturbation).Normalized()
            : fixedForward;

        var oldPositions = new Vector3[spider.Segments.Count];
        for (int i = 0; i < spider.Segments.Count; i++)
        {
            oldPositions[i] = spider.Segments[i].Pos;
        }

        for (int i = 1; i < spider.Segments.Count; i++)
        {
            float linkLength = FindLinkLength(spider, i - 1, i);
            BodyChunk previous = spider.Segments[i - 1];
            BodyChunk current = spider.Segments[i];
            current.Pos = previous.Pos + wrongDirection * linkLength;
            current.LastPos = current.Pos;
            current.Vel = Vector3.Zero;
        }
        spider.Segments[0].LastPos = spider.Segments[0].Pos;
        spider.Segments[0].Vel = Vector3.Zero;

        // 足端、根部、膝点和抓点随各自锚点同步搬到反折后的身体节附近，避免测试把
        // “陈旧世界坐标腿”混进 trailing 姿态恢复。
        foreach (SpiderLeg leg in spider.Legs)
        {
            int anchorIndex = SegmentIndex(spider, leg.Anchor);
            leg.Shift(spider.Segments[anchorIndex].Pos - oldPositions[anchorIndex]);
            leg.ForceRelease();
        }

        float initialAlignment = MinimumTrailingAlignment(spider);
        bool initiallyReversed = initialAlignment < -0.95f;
        bool recovered = false;
        int recoveryTick = -1;
        float maxConstraintError = spider.Body.CurrentMaxDeviation();
        bool finite = AllSpiderStateFinite(spider);
        var hasher = new DeterminismHasher();

        for (long tick = 1; tick <= ticks; tick++)
        {
            spider.MoveDir = fixedForward;
            spider.RunSpeed = 0.75f;
            spider.Tick(new TickContext(gravity, terrain, tick));

            hasher.FoldBody(spider.Body);
            hasher.FoldSpiderBodyState(spider.Body);
            hasher.FoldSpiderLegs(spider.Legs);
            hasher.FoldSpiderControllerState(spider);
            maxConstraintError = Mathf.Max(
                maxConstraintError, spider.Body.CurrentMaxDeviation());
            finite &= AllSpiderStateFinite(spider);

            float alignment = MinimumTrailingAlignment(spider);
            if (!recovered && alignment > 0.5f)
            {
                recovered = true;
                recoveryTick = (int)tick;
            }
        }

        return new TrailingResult(
            hasher.Value,
            initiallyReversed,
            recovered,
            recoveryTick,
            initialAlignment,
            MinimumTrailingAlignment(spider),
            maxConstraintError,
            finite);
    }

    private static float FindLinkLength(
        SpiderLocomotionController spider, int previousIndex, int currentIndex)
    {
        BodyChunk previous = spider.Segments[previousIndex];
        BodyChunk current = spider.Segments[currentIndex];
        foreach (ChunkConnection connection in spider.Body.Connections)
        {
            if (Connects(connection, previous, current))
            {
                return connection.RestLength;
            }
        }
        return (current.Pos - previous.Pos).Length();
    }

    private static float MinimumTrailingAlignment(SpiderLocomotionController spider)
    {
        Vector3 desired = -spider.Forward.Normalized();
        float minimum = 1f;
        for (int i = 1; i < spider.Segments.Count; i++)
        {
            Vector3 radial = spider.Segments[i].Pos - spider.Segments[i - 1].Pos;
            if (radial.LengthSquared() < 1e-10f)
            {
                return -1f;
            }
            minimum = Mathf.Min(minimum, radial.Normalized().Dot(desired));
        }
        return minimum;
    }

    private static bool AllSpiderStateFinite(SpiderLocomotionController spider)
    {
        bool finite = spider.Forward.IsFinite()
            && spider.SupportNormal.IsFinite()
            && spider.LastMoveTarget.IsFinite();
        foreach (BodyChunk chunk in spider.Body.Chunks)
        {
            finite &= chunk.Pos.IsFinite() && chunk.LastPos.IsFinite()
                && chunk.Vel.IsFinite();
        }
        foreach (SpiderLeg leg in spider.Legs)
        {
            finite &= leg.Pos.IsFinite() && leg.LastPos.IsFinite()
                && leg.Vel.IsFinite() && leg.HuntPos.IsFinite()
                && leg.RootPos.IsFinite() && leg.KneePos.IsFinite()
                && leg.BendPole.IsFinite() && leg.GripNormal.IsFinite();
        }
        return finite;
    }

    private static void FoldSpiderState(
        DeterminismHasher hasher, SpiderLocomotionController spider)
    {
        hasher.FoldBody(spider.Body);
        hasher.FoldSpiderBodyState(spider.Body);
        hasher.FoldSpiderLegs(spider.Legs);
        hasher.FoldSpiderControllerState(spider);
    }

    private static bool AnyTargetSurfaceContact(SpiderLocomotionController spider)
    {
        foreach (SpiderLeg leg in spider.Legs)
        {
            if (leg.TargetSurfaceContact)
            {
                return true;
            }
        }
        return false;
    }

    private sealed class EmptyTerrain : ITerrainQuery
    {
        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            _ = from;
            _ = to;
            hit = default;
            return false;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            _ = center;
            _ = radius;
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }

    private sealed class CeilingTerrain : ITerrainQuery
    {
        private readonly float _ceilingY;

        public CeilingTerrain(float ceilingY)
        {
            _ceilingY = ceilingY;
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            hit = default;
            if (from.Y > _ceilingY)
            {
                hit = new TerrainHit(from, Vector3.Zero, 2UL);
                return true;
            }
            if (to.Y <= _ceilingY)
            {
                return false;
            }
            float t = (_ceilingY - from.Y) / (to.Y - from.Y);
            hit = new TerrainHit(from.Lerp(to, t), Vector3.Down, 2UL);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            pushDir = Vector3.Down;
            depth = center.Y + radius - _ceilingY;
            return depth > 0f;
        }
    }

    private sealed class BlockedCandidateTerrain : ITerrainQuery
    {
        private readonly PlaneTerrainQuery _floor = new(0f);
        public bool UseRealFloor;

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            if (UseRealFloor)
            {
                return _floor.Raycast(from, to, out hit);
            }
            if ((to - from).Length() <= 0.25f)
            {
                hit = default;
                return false;
            }

            // 长射线“看见”可达墙点；短碰撞射线不返回它。足端 MTD 来自正交
            // 地面法线，验证任意 TerrainContact 不能冒充目标墙面的真实接触。
            hit = new TerrainHit(from.Lerp(to, 0.5f), Vector3.Right, 3UL);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center, float radius, out Vector3 pushDir, out float depth)
        {
            if (UseRealFloor)
            {
                return _floor.SpherePenetration(center, radius, out pushDir, out depth);
            }
            if (radius < 0.08f)
            {
                pushDir = Vector3.Up;
                depth = 0.001f;
                return true;
            }
            pushDir = Vector3.Zero;
            depth = 0f;
            return false;
        }
    }

    private static LegState Capture(SpiderLeg leg) => new(
        leg.Pos,
        leg.LastPos,
        leg.Vel,
        leg.HuntPos,
        leg.RootPos,
        leg.LastRootPos,
        leg.KneePos,
        leg.LastKneePos,
        leg.BendPole,
        leg.GripNormal,
        leg.LastGripNormal,
        leg.LastIkAxis,
        leg.SwingLandingGoal,
        leg.SwingHuntTarget,
        leg.HasGrip,
        leg.ReachingForTerrain,
        leg.TerrainContact,
        leg.TargetSurfaceContact,
        leg.HasFrozenSwingTarget,
        leg.GripCounter,
        leg.AcquisitionTicks,
        leg.SwingTicks,
        leg.StepSerial,
        leg.EmergencyStepCount,
        leg.LandingSerial,
        leg.LastStepReason);

    private static bool Near(Vector3 a, Vector3 b) => (a - b).LengthSquared() <= 1e-8f;
}
