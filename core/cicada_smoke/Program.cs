using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace ProcAnim.Core.CicadaSmoke;

/// <summary>
/// Cicada 专项无引擎回归。这里的地形是解析半空间组成的封闭房间，不依赖
/// Godot 物理服务器；因此飞行、停驻和撞击状态机可以在普通 dotnet 进程里验证。
/// </summary>
internal static class Program
{
    private const float TickDt = 0.025f;
    private const float GravityMps2 = 36f;
    private static readonly Vector3 GravityPerTick =
        new(0f, -GravityMps2 * TickDt * TickDt, 0f);
    private static float _maxResidualPenetration;

    // 在完整行为门人工核对后钉定；只有有意改变 Cicada 内核轨迹时才更新。
    private const ulong ExpectedHash = 0xC56791A6588031FCUL;

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        var failures = new List<string>();

        DeterminismResult a = RunDeterminism(0f);
        DeterminismResult b = RunDeterminism(0f);
        bool detOk = a.Hash == b.Hash && a.Hash == ExpectedHash && a.Finite &&
                     a.MaxConstraintDeviation < 0.02f;
        Report(
            "DET",
            detOk,
            $"run1={a.Hash:X16} run2={b.Hash:X16} expected={ExpectedHash:X16} " +
            $"finite={a.Finite} maxDev={a.MaxConstraintDeviation:F5}m",
            failures);

        Check("FACTORY", CheckFactoryAndTopology, failures);
        Check("FLIGHT", CheckFlightDirectionsAndTargets, failures);
        Check("HOVER", CheckHoverAndOrientation, failures);
        Check("PERCH", CheckPerchSurfacesAndInvalidation, failures);
        Check("TAKEOFF", CheckTakeOff, failures);
        Check("CHARGE", CheckChargeTimelineAndImpacts, failures);
        Check("LIFECYCLE", CheckLifecycle, failures);
        Report(
            "PENETRATION",
            _maxResidualPenetration < 1e-4f,
            $"maxResidual={_maxResidualPenetration:E3}m",
            failures);

        bool pass = failures.Count == 0;
        Console.WriteLine(pass
            ? "[CICADA-CORE-SMOKE] PASS：固定哈希、拓扑、3D 飞行、停驻、起飞、Charge 与生命周期均通过"
            : $"[CICADA-CORE-SMOKE] FAIL：{string.Join("；", failures)}");
        return pass ? 0 : 1;
    }

    private static void Check(
        string name,
        Func<(bool Ok, string Message)> test,
        List<string> failures)
    {
        try
        {
            (bool ok, string message) = test();
            Report(name, ok, message, failures);
        }
        catch (Exception ex)
        {
            Report(name, false, $"{ex.GetType().Name}: {ex.Message}", failures);
        }
    }

    private static void Report(
        string name,
        bool ok,
        string message,
        List<string> failures)
    {
        Console.WriteLine($"[CICADA-CORE-{name}] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok)
        {
            failures.Add(name);
        }
    }

    private readonly record struct DeterminismResult(
        ulong Hash,
        bool Finite,
        float MaxConstraintDeviation);

    private static DeterminismResult RunDeterminism(float perturb)
    {
        CicadaLocomotionController cicada = CicadaFactory.CreateController(
            new Vector3(0f, 3f, 0f),
            Vector3.Right,
            CicadaFactory.Light());
        if (perturb != 0f)
        {
            cicada.Front.Pos.X += perturb;
            cicada.Front.LastPos = cicada.Front.Pos;
        }

        var terrain = new RoomTerrain();
        var hasher = new DeterminismHasher();
        long tick = 0;
        float maxDeviation = 0f;
        for (int i = 1; i <= 800; i++)
        {
            cicada.MoveTarget = null;
            cicada.RunSpeed = 1f;
            cicada.MoveDir = i switch
            {
                <= 100 => Vector3.Right,
                <= 200 => Vector3.Left,
                <= 300 => Vector3.Back,
                <= 400 => Vector3.Forward,
                <= 520 => new Vector3(1f, 1f, 0f).Normalized(),
                <= 640 => new Vector3(0f, -1f, 1f).Normalized(),
                _ => new Vector3(-1f, 0.35f, -1f).Normalized(),
            };
            Tick(cicada, terrain, ref tick);
            hasher.FoldBody(cicada.Body);
            hasher.Fold(cicada.Forward);
            hasher.Fold(cicada.Up);
            hasher.Fold(cicada.Right);
            hasher.Fold(new Vector3(cicada.Bank, cicada.WingDeployment, cicada.FlapPhase));
            maxDeviation = Math.Max(maxDeviation, cicada.Body.CurrentMaxDeviation());
        }

        return new DeterminismResult(
            hasher.Value,
            IsFinite(cicada),
            maxDeviation);
    }

    private static (bool, string) CheckFactoryAndTopology()
    {
        CicadaParams light = CicadaFactory.Light();
        CicadaParams dark = CicadaFactory.Dark();
        CicadaParams[] all = CicadaFactory.AllPresets();
        CicadaLocomotionController cicada = CicadaFactory.CreateController(
            new Vector3(1f, 2f, 3f),
            Vector3.Forward,
            light);

        bool topology = cicada.Body.Chunks.Count == 2 &&
                        cicada.Body.Connections.Count == 1 &&
                        cicada.Wings.Count == 4 &&
                        cicada.Tentacles.Count == 4 &&
                        ReferenceEquals(cicada.Body.Chunks[0], cicada.Front) &&
                        ReferenceEquals(cicada.Body.Chunks[1], cicada.Rear);
        bool dimensions = Math.Abs(cicada.Front.Radius - 0.1875f) < 0.03f &&
                          Math.Abs(cicada.Rear.Radius - 0.175f) < 0.03f &&
                          Math.Abs(cicada.Body.Connections[0].RestLength - 0.35f) < 0.03f;
        bool presets = all.Length >= 2 &&
                       CicadaFactory.ByName(light.Name).Name == light.Name &&
                       CicadaFactory.ByName("DARK").Name == dark.Name &&
                       light.Name != dark.Name;

        // 出生配置必须冻结：调用侧随后改同一张表，已出生实例的轨迹不能变化。
        CicadaParams mutable = CicadaFactory.Light();
        CicadaLocomotionController frozen = CicadaFactory.CreateController(
            new Vector3(0f, 3f, 0f), Vector3.Right, mutable);
        mutable.BodyLength = 9f;
        mutable.BodyMass = 9f;
        mutable.FrontLift = 9f;
        mutable.WingLength = 9f;
        CicadaLocomotionController reference = NewCicada(new Vector3(0f, 3f, 0f));
        var empty = new RoomTerrain();
        long frozenTick = 0;
        long referenceTick = 0;
        for (int i = 0; i < 30; i++)
        {
            frozen.MoveDir = reference.MoveDir = Vector3.Right;
            frozen.RunSpeed = reference.RunSpeed = 1f;
            Tick(frozen, empty, ref frozenTick);
            Tick(reference, empty, ref referenceTick);
        }
        bool birthSnapshot = frozen.Front.Pos == reference.Front.Pos &&
                             frozen.Rear.Pos == reference.Rear.Pos &&
                             frozen.Wings[0].Tip == reference.Wings[0].Tip;

        // Body 共享积分器不读取 Mass；Cicada 将预设体重转为自身力响应，重体应更迟缓。
        CicadaParams heavyConfig = CicadaFactory.Light();
        heavyConfig.BodyMass *= 2f;
        CicadaLocomotionController normalMass = NewCicada(new Vector3(0f, 3f, 0f));
        CicadaLocomotionController heavyMass = CicadaFactory.CreateController(
            new Vector3(0f, 3f, 0f), Vector3.Right, heavyConfig);
        long normalTick = 0;
        long heavyTick = 0;
        for (int i = 0; i < 40; i++)
        {
            normalMass.MoveDir = heavyMass.MoveDir = Vector3.Right;
            normalMass.RunSpeed = heavyMass.RunSpeed = 1f;
            Tick(normalMass, empty, ref normalTick);
            Tick(heavyMass, empty, ref heavyTick);
        }
        float normalTravel = Center(normalMass).X;
        float heavyTravel = Center(heavyMass).X;
        bool massResponse = normalTravel > heavyTravel + 0.25f;

        bool ok = topology && dimensions && presets && birthSnapshot && massResponse;
        return (
            ok,
            $"chunks={cicada.Body.Chunks.Count} connections={cicada.Body.Connections.Count} " +
            $"wings={cicada.Wings.Count} tentacles={cicada.Tentacles.Count} presets={all.Length} " +
            $"radii={cicada.Front.Radius:F4}/{cicada.Rear.Radius:F4} " +
            $"length={cicada.Body.Connections[0].RestLength:F4} snapshot={birthSnapshot} " +
            $"massTravel={normalTravel:F2}/{heavyTravel:F2}");
    }

    private static (bool, string) CheckFlightDirectionsAndTargets()
    {
        Vector3[] directions =
        {
            Vector3.Right,
            Vector3.Left,
            Vector3.Back,
            Vector3.Forward,
            new Vector3(1f, 1f, 1f).Normalized(),
            new Vector3(-1f, -1f, -1f).Normalized(),
        };

        float minProjection = float.MaxValue;
        bool directionOk = true;
        foreach (Vector3 direction in directions)
        {
            CicadaLocomotionController cicada = NewCicada(new Vector3(0f, 3f, 0f));
            var terrain = new RoomTerrain();
            long tick = 0;
            Vector3 start = Center(cicada);
            for (int i = 0; i < 120; i++)
            {
                cicada.MoveDir = direction;
                cicada.RunSpeed = 1f;
                Tick(cicada, terrain, ref tick);
            }
            float projection = (Center(cicada) - start).Dot(direction);
            minProjection = Math.Min(minProjection, projection);
            directionOk &= projection > 0.5f && IsFinite(cicada);
        }

        CicadaLocomotionController targetCicada = NewCicada(new Vector3(0f, 3f, 0f));
        var targetTerrain = new RoomTerrain();
        long targetTick = 0;
        Vector3 target = Center(targetCicada) + new Vector3(1.5f, 0.75f, -0.5f);
        targetCicada.MoveTarget = target;
        targetCicada.RunSpeed = 1f;
        bool reached = false;
        for (int i = 0; i < 240; i++)
        {
            Tick(targetCicada, targetTerrain, ref targetTick);
            reached |= targetCicada.AtMoveTarget;
        }
        float atDistance = Center(targetCicada).DistanceTo(target);
        Vector3 holdStart = Center(targetCicada);
        for (int i = 0; i < 120; i++)
        {
            Tick(targetCicada, targetTerrain, ref targetTick);
        }
        float holdDrift = Center(targetCicada).DistanceTo(holdStart);
        bool targetOk = reached && atDistance < 0.75f && holdDrift < 0.5f;
        return (
            directionOk && targetOk,
            $"minDirectionProjection={minProjection:F3}m reached={reached} " +
            $"targetDistance={atDistance:F3}m postArrivalDrift={holdDrift:F3}m");
    }

    private static (bool, string) CheckHoverAndOrientation()
    {
        CicadaLocomotionController cicada = NewCicada(new Vector3(0f, 4f, 0f));
        var terrain = new RoomTerrain();
        long tick = 0;
        Vector3 start = Center(cicada);
        float maxAxisError = 0f;
        float minConsecutiveUpDot = 1f;
        Vector3 previousUp = cicada.Up;
        for (int i = 0; i < 300; i++)
        {
            cicada.MoveDir = i switch
            {
                < 120 => Vector3.Zero,
                < 210 => new Vector3(0.02f, 1f, 0.01f).Normalized(),
                _ => new Vector3(-0.01f, -1f, 0.02f).Normalized(),
            };
            cicada.RunSpeed = i < 120 ? 0f : 1f;
            Tick(cicada, terrain, ref tick);
            maxAxisError = Math.Max(maxAxisError, AxisError(cicada));
            minConsecutiveUpDot = Math.Min(minConsecutiveUpDot, previousUp.Dot(cicada.Up));
            previousUp = cicada.Up;
        }

        float displacement = Center(cicada).DistanceTo(start);
        bool axes = maxAxisError < 1e-4f && minConsecutiveUpDot > 0f;

        CicadaLocomotionController hover = NewCicada(new Vector3(0f, 4f, 0f));
        var hoverTerrain = new RoomTerrain();
        long hoverTick = 0;
        Vector3 hoverStart = Center(hover);
        for (int i = 0; i < 240; i++)
        {
            hover.MoveDir = Vector3.Zero;
            hover.RunSpeed = 0f;
            Tick(hover, hoverTerrain, ref hoverTick);
        }
        float hoverDrift = Center(hover).DistanceTo(hoverStart);
        bool hoverOk = hoverDrift < 1.0f && CenterVelocity(hover).Length() < 0.12f;
        return (
            axes && hoverOk && IsFinite(cicada) && IsFinite(hover),
            $"axisError={maxAxisError:E2} minUpDot={minConsecutiveUpDot:F4} " +
            $"verticalRoute={displacement:F2}m hoverDrift={hoverDrift:F3}m " +
            $"hoverSpeed={CenterVelocity(hover).Length():F4}");
    }

    private static (bool, string) CheckPerchSurfacesAndInvalidation()
    {
        PerchCase[] cases =
        {
            new(
                "floor",
                new Vector3(0f, 1f, 0f),
                new TerrainHit(new Vector3(0f, 0f, 0f), Vector3.Up, 11UL),
                terrain => terrain.AddPlane(Vector3.Zero, Vector3.Up, 11UL)),
            new(
                "wall",
                new Vector3(5f, 3f, 0f),
                new TerrainHit(new Vector3(6f, 3f, 0f), Vector3.Left, 12UL),
                terrain => terrain.AddPlane(new Vector3(6f, 0f, 0f), Vector3.Left, 12UL)),
            new(
                "ceiling",
                new Vector3(0f, 5f, 0f),
                new TerrainHit(new Vector3(0f, 6f, 0f), Vector3.Down, 13UL),
                terrain => terrain.AddPlane(new Vector3(0f, 6f, 0f), Vector3.Down, 13UL)),
        };

        int worstLandingTicks = 0;
        float worstStableDrift = 0f;
        float worstUpAlignment = 1f;
        bool allOk = true;
        foreach (PerchCase perchCase in cases)
        {
            var terrain = new RoomTerrain();
            perchCase.Configure(terrain);
            CicadaLocomotionController cicada = NewCicada(perchCase.Origin);
            bool accepted = cicada.RequestPerch(perchCase.Hit);
            long tick = 0;
            int landingTicks = 0;
            while (landingTicks < 160 && cicada.Mode != CicadaMode.Perched)
            {
                landingTicks++;
                Tick(cicada, terrain, ref tick);
            }
            worstLandingTicks = Math.Max(worstLandingTicks, landingTicks);

            Vector3 stableStart = Center(cicada);
            float stableDrift = 0f;
            for (int i = 0; i < 120; i++)
            {
                Tick(cicada, terrain, ref tick);
                stableDrift = Math.Max(stableDrift, Center(cicada).DistanceTo(stableStart));
            }
            worstStableDrift = Math.Max(worstStableDrift, stableDrift);
            float upAlignment = cicada.Up.Dot(perchCase.Hit.Normal);
            worstUpAlignment = Math.Min(worstUpAlignment, upAlignment);
            bool appendagesOnSurface = true;
            foreach (CicadaWingState wing in cicada.Wings)
            {
                appendagesOnSurface &=
                    (wing.Pos - perchCase.Hit.Point).Dot(perchCase.Hit.Normal) >= -1e-4f &&
                    (wing.Tip - perchCase.Hit.Point).Dot(perchCase.Hit.Normal) >= -1e-4f;
            }
            foreach (CicadaTentacleState tentacle in cicada.Tentacles)
            {
                float distance = (tentacle.Pos - perchCase.Hit.Point)
                    .Dot(perchCase.Hit.Normal);
                appendagesOnSurface &= tentacle.Attached && distance >= 0f && distance < 0.08f;
            }
            bool stable = cicada.Mode == CicadaMode.Perched &&
                          cicada.ActivePerchTarget is not null &&
                          stableDrift < 0.2f &&
                          upAlignment > 0.995f &&
                          appendagesOnSurface;

            terrain.SetEnabled(perchCase.Hit.ColliderId, false);
            for (int i = 0; i < 12; i++)
            {
                Tick(cicada, terrain, ref tick);
            }
            bool invalidated = cicada.Mode == CicadaMode.Flying &&
                               cicada.ActivePerchTarget is null;
            allOk &= accepted && landingTicks <= 160 && stable && invalidated && IsFinite(cicada);
        }

        CicadaLocomotionController reject = NewCicada(Vector3.Zero);
        bool rejectedZeroNormal = !reject.RequestPerch(
            new TerrainHit(Vector3.Zero, Vector3.Zero, 99UL));
        allOk &= rejectedZeroNormal;
        return (
            allOk,
            $"surfaces={cases.Length} worstLanding={worstLandingTicks}ticks " +
            $"worstStableDrift={worstStableDrift:F4}m minUpDot={worstUpAlignment:F5} " +
            $"zeroNormalRejected={rejectedZeroNormal}");
    }

    private static (bool, string) CheckTakeOff()
    {
        var terrain = new RoomTerrain().AddPlane(Vector3.Zero, Vector3.Up, 21UL);
        var hit = new TerrainHit(Vector3.Zero, Vector3.Up, 21UL);
        CicadaLocomotionController cicada = NewCicada(new Vector3(0f, 1f, 0f));
        bool accepted = cicada.RequestPerch(hit);
        long tick = 0;
        for (int i = 0; i < 160 && cicada.Mode != CicadaMode.Perched; i++)
        {
            Tick(cicada, terrain, ref tick);
        }
        bool perched = cicada.Mode == CicadaMode.Perched;
        cicada.TakeOff(Vector3.Up);
        for (int i = 0; i < 30; i++)
        {
            Tick(cicada, terrain, ref tick);
        }
        float clearance = (Center(cicada) - hit.Point).Dot(hit.Normal);
        bool manualOk = accepted && perched && cicada.Mode == CicadaMode.Flying &&
                  cicada.ActivePerchTarget is null && clearance >= 0.5f &&
                  cicada.FlightPower >= 0.5f && IsFinite(cicada);

        CicadaLocomotionController early = NewCicada(new Vector3(0f, 1f, 0f));
        long earlyTick = 0;
        for (int i = 0; i < 8; i++)
        {
            Tick(early, terrain, ref earlyTick);
        }
        float preEarlyTakeoffPower = early.FlightPower;
        early.RequestPerch(hit);
        early.TakeOff(Vector3.Up);
        bool exactTakeoffPower = preEarlyTakeoffPower > 0.5f &&
                                 early.FlightPower == 0.5f;

        CicadaLocomotionController automatic = NewCicada(new Vector3(0f, 1f, 0f));
        automatic.RequestPerch(hit);
        long automaticTick = 0;
        for (int i = 0; i < 160 && automatic.Mode != CicadaMode.Perched; i++)
        {
            Tick(automatic, terrain, ref automaticTick);
        }
        bool autoWasPerched = automatic.Mode == CicadaMode.Perched;
        for (int i = 0; i < 31; i++)
        {
            automatic.MoveDir = Vector3.Right;
            automatic.RunSpeed = 1f;
            Tick(automatic, terrain, ref automaticTick);
        }
        bool autoOk = autoWasPerched && automatic.Mode == CicadaMode.Flying &&
                      automatic.ActivePerchTarget is null;

        // 从 Landing 开始就一直按住方向，计数也必须等 Perched 后才起算。
        CicadaLocomotionController held = NewCicada(new Vector3(0f, 1f, 0f));
        held.RequestPerch(hit);
        held.MoveDir = Vector3.Right;
        held.RunSpeed = 1f;
        long heldTick = 0;
        int firstHeldPerched = -1;
        int heldTakeoff = -1;
        for (int i = 1; i <= 100; i++)
        {
            Tick(held, terrain, ref heldTick);
            if (held.Mode == CicadaMode.Perched && firstHeldPerched < 0)
            {
                firstHeldPerched = i;
            }
            if (firstHeldPerched >= 0 && held.Mode == CicadaMode.Flying)
            {
                heldTakeoff = i;
                break;
            }
        }
        bool noLandingPrecount = firstHeldPerched > 0 &&
                                 heldTakeoff - firstHeldPerched >= 29;
        return (
            manualOk && exactTakeoffPower && autoOk && noLandingPrecount,
            $"perched={perched} clearance30={clearance:F3}m mode={cicada.Mode} " +
            $"flightPower={cicada.FlightPower:F2} exactPower={exactTakeoffPower} " +
            $"autoTakeoff={autoOk} heldPerch/takeoff={firstHeldPerched}/{heldTakeoff}");
    }

    private static (bool, string) CheckChargeTimelineAndImpacts()
    {
        CicadaLocomotionController timeline = NewCicada(new Vector3(0f, 3f, 0f));
        var empty = new RoomTerrain();
        bool started = timeline.TryStartCharge(Vector3.Right);
        long tick = 0;
        bool timelineOk = started && timeline.ChargePhase == CicadaChargePhase.Windup &&
                          timeline.ChargeCounter == 1;
        for (int expectedCounter = 2; expectedCounter <= 39; expectedCounter++)
        {
            Tick(timeline, empty, ref tick);
            CicadaChargePhase expectedPhase = expectedCounter switch
            {
                <= 20 => CicadaChargePhase.Windup,
                <= 38 => CicadaChargePhase.Dash,
                _ => CicadaChargePhase.None,
            };
            timelineOk &= timeline.ChargeCounter == expectedCounter ||
                          (expectedCounter == 39 && timeline.ChargeCounter == 0);
            timelineOk &= timeline.ChargePhase == expectedPhase;
        }

        // 低速墙放在短弹道可达的位置，隔离验证普通撞击的反射分支。
        var lowWall = new RoomTerrain()
            .AddPlane(new Vector3(0.45f, 0f, 0f), Vector3.Left, 31UL);
        CicadaLocomotionController lowSpeed = NewCicada(new Vector3(0f, 3f, 0f));
        lowSpeed.Launch(new Vector3(0.035f, 0f, 0f));
        long lowTick = 0;
        bool lowContact = false;
        float lowAwaySpeed = float.NegativeInfinity;
        for (int i = 0; i < 80; i++)
        {
            Tick(lowSpeed, lowWall, ref lowTick);
            if (lowSpeed.Front.TerrainContact || lowSpeed.Rear.TerrainContact)
            {
                lowContact = true;
                lowAwaySpeed = CenterVelocity(lowSpeed).Dot(Vector3.Left);
            }
        }
        bool lowOk = lowContact && lowSpeed.Mode != CicadaMode.Stunned && lowAwaySpeed >= -1e-4f;

        // 高速墙必须给 Dash 留出加速到 hard-impact 门槛的距离；太近只会按设计走
        // 低速反射分支，无法证明高速中断。
        var highWall = new RoomTerrain()
            .AddPlane(new Vector3(3.5f, 0f, 0f), Vector3.Left, 32UL);
        CicadaLocomotionController highSpeed = NewCicada(new Vector3(0f, 3f, 0f));
        bool highStarted = highSpeed.TryStartCharge(Vector3.Right);
        long highTick = 0;
        bool stunned = false;
        int stunnedRun = 0;
        int maxStunnedRun = 0;
        for (int i = 0; i < 80; i++)
        {
            Tick(highSpeed, highWall, ref highTick);
            stunned |= highSpeed.Mode == CicadaMode.Stunned;
            stunnedRun = highSpeed.Mode == CicadaMode.Stunned ? stunnedRun + 1 : 0;
            maxStunnedRun = Math.Max(maxStunnedRun, stunnedRun);
        }
        bool highOk = highStarted && stunned &&
                      highSpeed.ChargePhase == CicadaChargePhase.None &&
                      maxStunnedRun == CicadaFactory.Light().StunTicks;
        return (
            timelineOk && lowOk && highOk && IsFinite(timeline) &&
            IsFinite(lowSpeed) && IsFinite(highSpeed),
            $"timeline={timelineOk} lowContact={lowContact} lowAway={lowAwaySpeed:F4} " +
            $"lowMode={lowSpeed.Mode} highStunned={stunned} stunRun={maxStunnedRun}");
    }

    private static (bool, string) CheckLifecycle()
    {
        var terrain = new RoomTerrain()
            .AddPlane(Vector3.Zero, Vector3.Up, 41UL);
        CicadaLocomotionController shifted = NewCicada(new Vector3(0f, 2f, 0f));
        shifted.MoveTarget = new Vector3(3f, 3f, 4f);
        shifted.RequestPerch(new TerrainHit(Vector3.Zero, Vector3.Up, 41UL));
        Vector3[] oldPos = new Vector3[shifted.Body.Chunks.Count];
        Vector3[] oldLastPos = new Vector3[shifted.Body.Chunks.Count];
        for (int i = 0; i < shifted.Body.Chunks.Count; i++)
        {
            oldPos[i] = shifted.Body.Chunks[i].Pos;
            oldLastPos[i] = shifted.Body.Chunks[i].LastPos;
        }
        var oldWings =
            new (Vector3 Pos, Vector3 LastPos, Vector3 Tip, Vector3 LastTip)[shifted.Wings.Count];
        for (int i = 0; i < shifted.Wings.Count; i++)
        {
            CicadaWingState wing = shifted.Wings[i];
            oldWings[i] = (wing.Pos, wing.LastPos, wing.Tip, wing.LastTip);
        }
        var oldTentacles =
            new (Vector3 Anchor, Vector3 Pos, Vector3 LastPos)[shifted.Tentacles.Count];
        for (int i = 0; i < shifted.Tentacles.Count; i++)
        {
            CicadaTentacleState tentacle = shifted.Tentacles[i];
            oldTentacles[i] = (tentacle.Anchor, tentacle.Pos, tentacle.LastPos);
        }
        Vector3 oldTarget = shifted.MoveTarget!.Value;
        Vector3 oldPerchPoint = shifted.ActivePerchTarget!.Value.Point;
        Vector3 delta = new(512f, 64f, -256f);
        shifted.Shift(delta);
        bool shiftExact = shifted.MoveTarget == oldTarget + delta &&
                          shifted.ActivePerchTarget!.Value.Point == oldPerchPoint + delta;
        for (int i = 0; i < shifted.Body.Chunks.Count; i++)
        {
            shiftExact &= shifted.Body.Chunks[i].Pos == oldPos[i] + delta &&
                          shifted.Body.Chunks[i].LastPos == oldLastPos[i] + delta;
        }
        for (int i = 0; i < shifted.Wings.Count; i++)
        {
            CicadaWingState wing = shifted.Wings[i];
            shiftExact &= wing.Pos == oldWings[i].Pos + delta &&
                          wing.LastPos == oldWings[i].LastPos + delta &&
                          wing.Tip == oldWings[i].Tip + delta &&
                          wing.LastTip == oldWings[i].LastTip + delta;
        }
        for (int i = 0; i < shifted.Tentacles.Count; i++)
        {
            CicadaTentacleState tentacle = shifted.Tentacles[i];
            shiftExact &= tentacle.Anchor == oldTentacles[i].Anchor + delta &&
                          tentacle.Pos == oldTentacles[i].Pos + delta &&
                          tentacle.LastPos == oldTentacles[i].LastPos + delta;
        }

        CicadaLocomotionController teleported = NewCicada(new Vector3(0f, 2f, 0f));
        teleported.MoveTarget = new Vector3(2f, 3f, 4f);
        teleported.RequestPerch(new TerrainHit(Vector3.Zero, Vector3.Up, 41UL));
        Vector3 teleportBefore = Center(teleported);
        Vector3 teleportDelta = new(20f, 8f, -7f);
        teleported.Teleport(teleportDelta);
        bool teleportOk = Center(teleported) == teleportBefore + teleportDelta &&
                          teleported.MoveTarget is null &&
                          teleported.ActivePerchTarget is null &&
                          teleported.ChargePhase == CicadaChargePhase.None &&
                          teleported.Mode == CicadaMode.Flying;

        CicadaLocomotionController chargeTeleport = NewCicada(new Vector3(0f, 2f, 0f));
        chargeTeleport.TryStartCharge(Vector3.Right);
        chargeTeleport.Teleport(Vector3.Up);
        teleportOk &= chargeTeleport.ChargePhase == CicadaChargePhase.None;

        CicadaLocomotionController launched = NewCicada(new Vector3(0f, 2f, 0f));
        launched.MoveTarget = new Vector3(2f, 3f, 4f);
        launched.RequestPerch(new TerrainHit(Vector3.Zero, Vector3.Up, 41UL));
        Vector3 frontVelocity = launched.Front.Vel;
        Vector3 rearVelocity = launched.Rear.Vel;
        Vector3 impulse = new(0.1f, 0.2f, -0.05f);
        launched.Launch(impulse);
        bool launchOk = launched.Front.Vel == frontVelocity + impulse &&
                        launched.Rear.Vel == rearVelocity + impulse &&
                        launched.MoveTarget is null &&
                        launched.ActivePerchTarget is null &&
                        launched.ChargePhase == CicadaChargePhase.None &&
                        launched.Mode == CicadaMode.Flying;

        long tick = 0;
        for (int i = 0; i < 80; i++)
        {
            Tick(launched, terrain, ref tick);
        }
        bool constraints = launched.Body.CurrentMaxDeviation() < 0.02f;
        return (
            shiftExact && teleportOk && launchOk && constraints &&
            IsFinite(shifted) && IsFinite(teleported) && IsFinite(launched),
            $"shiftExact={shiftExact} teleport={teleportOk} launch={launchOk} " +
            $"endDev={launched.Body.CurrentMaxDeviation():F5}m");
    }

    private static CicadaLocomotionController NewCicada(Vector3 origin) =>
        CicadaFactory.CreateController(origin, Vector3.Right, CicadaFactory.Light());

    private static void Tick(
        CicadaLocomotionController cicada,
        ITerrainQuery terrain,
        ref long tick)
    {
        tick++;
        cicada.Tick(new TickContext(GravityPerTick, terrain, tick));
        if (terrain is RoomTerrain room)
        {
            _maxResidualPenetration = Math.Max(
                _maxResidualPenetration,
                room.MeasureResidualPenetration(cicada.Body));
        }
    }

    private static Vector3 Center(CicadaLocomotionController cicada) =>
        (cicada.Front.Pos + cicada.Rear.Pos) * 0.5f;

    private static Vector3 CenterVelocity(CicadaLocomotionController cicada) =>
        (cicada.Front.Vel + cicada.Rear.Vel) * 0.5f;

    private static float AxisError(CicadaLocomotionController cicada)
    {
        float error = 0f;
        error = Math.Max(error, Math.Abs(cicada.Forward.Length() - 1f));
        error = Math.Max(error, Math.Abs(cicada.Up.Length() - 1f));
        error = Math.Max(error, Math.Abs(cicada.Right.Length() - 1f));
        error = Math.Max(error, Math.Abs(cicada.Forward.Dot(cicada.Up)));
        error = Math.Max(error, Math.Abs(cicada.Forward.Dot(cicada.Right)));
        error = Math.Max(error, Math.Abs(cicada.Up.Dot(cicada.Right)));
        error = Math.Max(
            error,
            Math.Abs(Math.Abs(cicada.Forward.Cross(cicada.Up).Dot(cicada.Right)) - 1f));
        return error;
    }

    private static bool IsFinite(CicadaLocomotionController cicada)
    {
        if (!cicada.Forward.IsFinite() || !cicada.Up.IsFinite() || !cicada.Right.IsFinite() ||
            !float.IsFinite(cicada.Bank) || !float.IsFinite(cicada.WingDeployment) ||
            !float.IsFinite(cicada.FlapPhase))
        {
            return false;
        }
        foreach (BodyChunk chunk in cicada.Body.Chunks)
        {
            if (!chunk.Pos.IsFinite() || !chunk.LastPos.IsFinite() || !chunk.Vel.IsFinite())
            {
                return false;
            }
        }
        foreach (CicadaWingState wing in cicada.Wings)
        {
            if (!wing.Pos.IsFinite() || !wing.LastPos.IsFinite() ||
                !wing.Tip.IsFinite() || !wing.LastTip.IsFinite())
            {
                return false;
            }
        }
        foreach (CicadaTentacleState tentacle in cicada.Tentacles)
        {
            if (!tentacle.Pos.IsFinite() || !tentacle.LastPos.IsFinite() ||
                !tentacle.Vel.IsFinite() || !tentacle.Anchor.IsFinite())
            {
                return false;
            }
        }
        return true;
    }

    private readonly record struct PerchCase(
        string Name,
        Vector3 Origin,
        TerrainHit Hit,
        Action<RoomTerrain> Configure);

    private sealed class RoomTerrain : ITerrainQuery
    {
        private sealed class Plane
        {
            public readonly Vector3 Point;
            public readonly Vector3 Normal;
            public readonly ulong ColliderId;
            public bool Enabled;

            public Plane(Vector3 point, Vector3 normal, ulong colliderId)
            {
                Point = point;
                Normal = normal.Normalized();
                ColliderId = colliderId;
                Enabled = true;
            }
        }

        private readonly List<Plane> _planes = new();

        public long RayCount { get; private set; }
        public long ShapeQueryCount { get; private set; }

        /// <summary>
        /// normal 指向可通行半空间；其背面是实体。固定添加顺序就是命中 tie-break。
        /// </summary>
        public RoomTerrain AddPlane(Vector3 point, Vector3 normal, ulong colliderId)
        {
            _planes.Add(new Plane(point, normal, colliderId));
            return this;
        }

        public void SetEnabled(ulong colliderId, bool enabled)
        {
            foreach (Plane plane in _planes)
            {
                if (plane.ColliderId == colliderId)
                {
                    plane.Enabled = enabled;
                }
            }
        }

        public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
        {
            RayCount++;
            hit = default;

            // 与 Godot HitFromInside 对齐：起点在任一实体半空间内时给零法线。
            foreach (Plane plane in _planes)
            {
                if (plane.Enabled && SignedDistance(from, plane) < 0f)
                {
                    hit = new TerrainHit(from, Vector3.Zero, plane.ColliderId);
                    return true;
                }
            }

            bool found = false;
            float bestT = float.PositiveInfinity;
            Plane? bestPlane = null;
            foreach (Plane plane in _planes)
            {
                if (!plane.Enabled)
                {
                    continue;
                }
                float fromDistance = SignedDistance(from, plane);
                float toDistance = SignedDistance(to, plane);
                if (fromDistance < 0f || toDistance >= 0f)
                {
                    continue;
                }
                float denominator = fromDistance - toDistance;
                if (denominator <= 1e-12f)
                {
                    continue;
                }
                float t = fromDistance / denominator;
                if (t < bestT)
                {
                    found = true;
                    bestT = t;
                    bestPlane = plane;
                }
            }

            if (!found || bestPlane is null)
            {
                return false;
            }
            hit = new TerrainHit(from.Lerp(to, bestT), bestPlane.Normal, bestPlane.ColliderId);
            return true;
        }

        public bool SpherePenetration(
            Vector3 center,
            float radius,
            out Vector3 pushDir,
            out float depth)
        {
            ShapeQueryCount++;
            pushDir = Vector3.Up;
            depth = 0f;
            foreach (Plane plane in _planes)
            {
                if (!plane.Enabled)
                {
                    continue;
                }
                float candidate = radius - SignedDistance(center, plane);
                if (candidate > depth)
                {
                    depth = candidate;
                    pushDir = plane.Normal;
                }
            }
            return depth > 0f;
        }

        public float MeasureResidualPenetration(Body body)
        {
            float max = 0f;
            foreach (BodyChunk chunk in body.Chunks)
            {
                foreach (Plane plane in _planes)
                {
                    if (!plane.Enabled)
                    {
                        continue;
                    }
                    max = Math.Max(
                        max,
                        chunk.TerrainRadius - SignedDistance(chunk.Pos, plane));
                }
            }
            return max;
        }

        private static float SignedDistance(Vector3 point, Plane plane) =>
            (point - plane.Point).Dot(plane.Normal);
    }
}
