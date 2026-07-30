using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 人形物种的沙盒驱动：装配/输入/脚本化路线/回归指标/终态判定全部收在这里——
/// SandboxWorld 只保留物种分流的早退分支，蜥蜴代码路径一字不动（哈希基线不受牵连）。
/// 脚本化场景：
/// · --route=hwalk：坡→平地→跨台阶的三点巡逻（MoveDir 方向驱动，撑地/摆臂/髋伺服全进哈希）；
/// · --route=hact：站桩动作脚本（指向→持物→行进中蓄力急停→出手），手臂优先级链的四段覆盖；
/// · --stun=T,D：T tick 昏迷+轻推（瘫倒涌现），T+D 苏醒（站立力偶限时爬起）；
/// · --yank=T：沿用全局开关，Launch 后限时回正续走。
/// 交互模式额外键位：P=指向鼠标点（再按取消）、C=持物开关、T=按住蓄力松开投掷
/// （飞行道具纯视觉：无地形查询、不进哈希）。
/// </summary>
public sealed class HumanoidSandboxDriver
{
	public HumanoidLocomotionController Controller = null!;
	public HumanoidParams Breed = BodyFactory.Scavenger();
	public readonly HumanoidRenderer Renderer = new();

	// —— 脚本化配置（ParseDeterminismArgs 填写）——
	public Vector3[] Waypoints = Array.Empty<Vector3>();
	public bool ActScript;
	public long StunTick = -1;
	public int StunDuration;

	/// <summary>--route=hwalk 的巡逻路点：上缓坡（坡 x∈[1.15,6.85]）→ 平地 → 跨 0.3m 台阶
	/// （台阶在 x=-3）到墙前安全距离（墙面 x=-5.8），来回循环——坡/平地/台阶三种地形进哈希。</summary>
	public static readonly Vector3[] WalkRoute =
	{
		new(4.2f, 0f, 0f),
		new(0.5f, 0f, 1.8f),
		new(-4.4f, 0f, 0f),
	};

	private const float WaypointArriveRadius = 0.6f;

	// —— hact 动作脚本的固定时刻表与世界常量（900 tick 预算）——
	private const long ActPointFrom = 200;
	private const long ActPointTo = 400;
	private const long ActCarryTo = 600;
	private const long ActChargeTo = 660;
	private static readonly Vector3 ActPointTarget = new(3f, 1.5f, 0f);
	private static readonly Vector3 ActThrowDir = new Vector3(1f, 0.2f, 0f).Normalized();

	// —— 指标 ——
	private int _waypointIndex;
	private int _waypointsReached;
	private float _walkDistance;
	private Vector3 _lastChest;
	private double _uprightSum;
	private long _ticks;
	private bool _nonFinite;
	private float _maxEndDev;

	// 恢复窗口（yank 击飞 / stun 苏醒共用）：事件起点 → 首次「回正且站稳」的耗时
	private long _recoveryStart = -1;
	private bool _recoveryPending;
	private int _lastRecoveryTicks = -1;
	private int _maxRecoveryTicks;
	private float _eventMinUpright = 1f;

	// stun 场景采样
	private bool _stunHappened;
	private float _stunMinUpright = 1f;

	// act 场景采样
	private bool _actChargeStarted;
	private float _actBestPointAngleDeg = 999f;
	private float _actBestPointReach;
	private bool _actCarryModeOk;
	private float _actCarryDist = 999f;
	private float _actChargeSpeed = 999f;
	private bool _actHandCharged;
	private bool _actThrowOk;

	// 交互投掷道具（纯视觉弹道：只做积分，无地形查询、不进确定性哈希）
	private Vector3 _propPos;
	private Vector3 _propVel;
	private int _propTicks;

	public Vector3? ThrownProp => _propTicks > 0 ? _propPos : null;

	public void Spawn(Node3D parent, HumanoidParams breed, Vector3 origin, int constraintIterations,
		List<Body> bodies)
	{
		Breed = breed;
		Controller = BodyFactory.CreateHumanoidController(origin, breed);
		Controller.Body.ConstraintIterations = constraintIterations;
		bodies.Clear();
		bodies.Add(Controller.Body);
		Renderer.Clear();
		Renderer.Build(parent, Controller);
		_lastChest = Controller.Chest.Pos;
	}

	/// <summary>WASD → 世界 XZ 移动意图；Shift+右键点地形 → MoveTarget 直喂；
	/// 到点自动清除（与蜥蜴的交互约定逐条对齐）。</summary>
	public void SampleWalkInput(Camera3D camera, RayDebugDraw rayDebug, bool wantCameraFly)
	{
		if (wantCameraFly)
		{
			Controller.MoveDir = Vector3.Zero;
			Controller.RunSpeed = 0f;
			return;
		}

		Vector3 dir = Vector3.Zero;
		if (Input.IsPhysicalKeyPressed(Key.W)) dir.Z -= 1f;
		if (Input.IsPhysicalKeyPressed(Key.S)) dir.Z += 1f;
		if (Input.IsPhysicalKeyPressed(Key.A)) dir.X -= 1f;
		if (Input.IsPhysicalKeyPressed(Key.D)) dir.X += 1f;

		if (dir != Vector3.Zero)
		{
			Controller.MoveTarget = null;
			Controller.MoveDir = dir.Normalized();
			Controller.RunSpeed = 1f;
			return;
		}

		if (Input.IsMouseButtonPressed(MouseButton.Right) && Input.IsPhysicalKeyPressed(Key.Shift))
		{
			Vector2 mouse = camera.GetViewport().GetMousePosition();
			Vector3 origin = camera.ProjectRayOrigin(mouse);
			Vector3 rayDir = camera.ProjectRayNormal(mouse);
			if (rayDebug.Raycast(origin, origin + rayDir * 100f, out TerrainHit hit))
			{
				Controller.MoveTarget = hit.Point;
			}
		}

		if (Controller.MoveTarget is not null)
		{
			if (Controller.AtMoveTarget)
			{
				Controller.MoveTarget = null;
				Controller.RunSpeed = 0f;
				Controller.MoveDir = Vector3.Zero;
				return;
			}
			Controller.RunSpeed = 1f;
			return;
		}

		Controller.MoveDir = Vector3.Zero;
		Controller.RunSpeed = 0f;
	}

	/// <summary>确定性模式的脚本化输入（controller.Tick 之前调用）。</summary>
	public void SteerScripted(long tick)
	{
		if (StunTick >= 0)
		{
			if (tick == StunTick)
			{
				// 昏迷 + 轻推：完全对称的直立杆是确定性的不稳定平衡点，零随机内核里
				// 没有噪声帮它倒——瘫倒涌现需要一次显式扰动（与 smoke STUN 同款）。
				Controller.Conscious = false;
				Controller.Launch(new Vector3(0.1f, 0f, 0.03f));
				_stunHappened = true;
				_stunMinUpright = 1f;
			}
			else if (tick == StunTick + StunDuration)
			{
				Controller.Conscious = true;
				NotifyRecoveryEvent(tick);
			}
			if (!Controller.Conscious)
			{
				_stunMinUpright = Mathf.Min(_stunMinUpright, Controller.Uprightness);
			}
		}

		if (ActScript)
		{
			SteerActScript(tick);
			return;
		}
		SteerWaypoints();
	}

	private void SteerWaypoints()
	{
		if (Waypoints.Length == 0)
		{
			Controller.MoveDir = Vector3.Zero;
			Controller.RunSpeed = 0f;
			return;
		}
		Vector3 target = Waypoints[_waypointIndex];
		Vector3 delta = target - Controller.Chest.Pos;
		delta.Y = 0f;
		if (delta.Length() < WaypointArriveRadius)
		{
			_waypointIndex = (_waypointIndex + 1) % Waypoints.Length;
			_waypointsReached++;
			target = Waypoints[_waypointIndex];
			delta = target - Controller.Chest.Pos;
			delta.Y = 0f;
		}
		Controller.MoveDir = delta.LengthSquared() < 1e-8f ? Vector3.Zero : delta.Normalized();
		Controller.RunSpeed = 1f;
	}

	/// <summary>hact 动作脚本：站定 → 指向 → 持物 → 行进意图下蓄力（强制停驶）→ 出手。
	/// 采样在 PostTick（物理后）做，这里只写输入与阶段切换。</summary>
	private void SteerActScript(long tick)
	{
		Controller.MoveDir = Vector3.Zero;
		Controller.RunSpeed = 0f;

		if (tick == ActPointFrom)
		{
			Controller.PointTarget = ActPointTarget;
		}
		else if (tick == ActPointTo)
		{
			Controller.PointTarget = null;
			Controller.Carrying = true;
		}
		else if (tick == ActCarryTo)
		{
			Controller.Carrying = false;
			_actChargeStarted = Controller.StartThrowCharge(ActThrowDir);
		}
		else if (tick == ActChargeTo)
		{
			Vector3? thrown = Controller.ReleaseThrow();
			Vector3? doubleRelease = Controller.ReleaseThrow();
			_actThrowOk = thrown is { } v
				&& Mathf.Abs(v.Length() - Controller.ThrowSpeed) < 1e-4f
				&& v.Normalized().Dot(ActThrowDir) >= 0.999f
				&& doubleRelease is null;
		}

		// 蓄力窗口内保持行进意图：内核必须强制停驶（≙ RW 瞄准时 moving=false）。
		if (tick > ActCarryTo && tick <= ActChargeTo)
		{
			Controller.MoveDir = new Vector3(1f, 0f, 0f);
			Controller.RunSpeed = 1f;
		}
	}

	/// <summary>--yank 击飞后由 SandboxWorld 调用：打开限时回正窗口。</summary>
	public void NotifyLaunch(long tick) => NotifyRecoveryEvent(tick);

	private void NotifyRecoveryEvent(long tick)
	{
		_recoveryStart = tick;
		_recoveryPending = true;
		_eventMinUpright = 1f;
	}

	/// <summary>controller.Tick 之后调用：指标累计 + 事件恢复判定 + hact 采样 + 交互道具积分。</summary>
	public void PostTick(long tick, Vector3 gravityPerTick)
	{
		_ticks++;
		Vector3 step = Controller.Chest.Pos - _lastChest;
		step.Y = 0f;
		_walkDistance += step.Length();
		_lastChest = Controller.Chest.Pos;
		_uprightSum += Controller.Uprightness;
		_maxEndDev = Mathf.Max(_maxEndDev, Controller.Body.CurrentMaxDeviation());
		foreach (BodyChunk c in Controller.Body.Chunks)
		{
			_nonFinite |= !c.Pos.IsFinite() || !c.Vel.IsFinite();
		}
		foreach (Limb leg in Controller.Legs)
		{
			_nonFinite |= !leg.Pos.IsFinite();
		}
		foreach (Arm arm in Controller.Arms)
		{
			_nonFinite |= !arm.Pos.IsFinite();
		}

		if (_recoveryPending)
		{
			_eventMinUpright = Mathf.Min(_eventMinUpright, Controller.Uprightness);
			if (Controller.Uprightness >= 0.8f && Controller.Grounded && !Controller.ApplyGravity)
			{
				_lastRecoveryTicks = (int)(tick - _recoveryStart);
				_maxRecoveryTicks = Math.Max(_maxRecoveryTicks, _lastRecoveryTicks);
				_recoveryPending = false;
			}
		}

		if (ActScript)
		{
			SampleActMetrics(tick);
		}

		if (_propTicks > 0)
		{
			// 纯视觉弹道（交互演示）：不查询地形，落到地板高度或超时即消失。
			_propVel += gravityPerTick;
			_propPos += _propVel;
			_propTicks--;
			if (_propPos.Y < 0.09f)
			{
				_propTicks = 0;
			}
		}
	}

	private void SampleActMetrics(long tick)
	{
		if (tick > ActPointFrom && tick <= ActPointTo && Controller.PointTarget is { } pt)
		{
			Vector3 want = pt - Controller.Chest.Pos;
			Vector3 got = Controller.Arms[0].Pos - Controller.Chest.Pos;
			if (want.LengthSquared() > 1e-8f && got.LengthSquared() > 1e-8f)
			{
				float angle = Mathf.RadToDeg(want.Normalized().AngleTo(got.Normalized()));
				if (angle < _actBestPointAngleDeg)
				{
					_actBestPointAngleDeg = angle;
					_actBestPointReach = got.Length();
				}
			}
		}
		else if (tick > ActCarryTo - 60 && tick <= ActCarryTo && Controller.Carrying)
		{
			float dist = (Controller.Arms[0].Pos - Controller.Arms[0].HuntPos).Length();
			if (dist < _actCarryDist)
			{
				_actCarryDist = dist;
				_actCarryModeOk = Controller.Arms[0].Mode == Arm.ArmMode.HuntRelativePosition;
			}
		}
		else if (tick == ActChargeTo - 1)
		{
			// 蓄力末端（出手前一 tick）：停驶已生效、主手拉在身后上方。
			Vector3 vel = Controller.Chest.Vel;
			vel.Y = 0f;
			_actChargeSpeed = vel.Length();
			Vector3 handRel = Controller.Arms[0].Pos - Controller.Chest.Pos;
			_actHandCharged = handRel.Dot(Controller.Facing) < 0f && handRel.Y > 0.2f;
		}
	}

	// —— 交互演示键位 ——
	/// <summary>P：设置/清除指向目标（鼠标地形命中点）。</summary>
	public void TogglePoint(Camera3D camera, RayDebugDraw rayDebug)
	{
		if (Controller.PointTarget is not null)
		{
			Controller.PointTarget = null;
			return;
		}
		Vector2 mouse = camera.GetViewport().GetMousePosition();
		Vector3 origin = camera.ProjectRayOrigin(mouse);
		Vector3 rayDir = camera.ProjectRayNormal(mouse);
		if (rayDebug.Raycast(origin, origin + rayDir * 100f, out TerrainHit hit))
		{
			Controller.PointTarget = hit.Point + Vector3.Up * 0.3f;
		}
	}

	/// <summary>C：持物开关（被持物由渲染层硬钉 MainHandPos）。</summary>
	public void ToggleCarry() => Controller.Carrying = !Controller.Carrying;

	/// <summary>T 按下：沿朝向（微抬）蓄力。</summary>
	public void BeginThrowCharge() =>
		Controller.StartThrowCharge((Controller.Facing + Vector3.Up * 0.25f).Normalized());

	/// <summary>T 松开：出手——内核返回出手速度，道具本体归宿主（这里就是那个"宿主"）。</summary>
	public void ReleaseThrowInteractive()
	{
		if (Controller.ReleaseThrow() is { } vel)
		{
			_propPos = Controller.MainHandPos;
			_propVel = vel;
			_propTicks = 200;
		}
	}

	/// <summary>探针跑完后输出终态与判定（与蜥蜴 DumpFinalState 同角色），返回进程退出码。</summary>
	public int DumpFinalState(DeterminismProbe probe, ulong? expectHash, long ticks)
	{
		float meanUpright = _ticks > 0 ? (float)(_uprightSum / _ticks) : 0f;
		GD.Print($"[METRIC] walkDistance={_walkDistance:F2}m waypointsReached={_waypointsReached} " +
				 $"meanUpright={meanUpright:F2} knucklePlants={Controller.KnucklePlants} " +
				 $"maxEndDev={_maxEndDev:F4}m snagReleases={Controller.Body.SnagReleases} " +
				 $"lastRecovery={_lastRecoveryTicks} maxRecovery={_maxRecoveryTicks} " +
				 $"recoveryPending={_recoveryPending} eventMinUpright={_eventMinUpright:F2} " +
				 $"stunMinUpright={_stunMinUpright:F2}");
		if (ActScript)
		{
			GD.Print($"[ACT] chargeStarted={_actChargeStarted} pointAngle={_actBestPointAngleDeg:F1}deg " +
					 $"pointReach={_actBestPointReach:F2}m carryMode={_actCarryModeOk} " +
					 $"carryDist={_actCarryDist:F2}m chargeSpeed={_actChargeSpeed:F4} " +
					 $"handCharged={_actHandCharged} throwOk={_actThrowOk}");
		}
		GD.Print($"[FINAL] controller conscious={Controller.Conscious} applyGravity={Controller.ApplyGravity} " +
				 $"grounded={Controller.Grounded} upright={Controller.Uprightness:F2} " +
				 $"facing=({Controller.Facing.X:F2},{Controller.Facing.Z:F2}) legsGrip={Controller.LegsGripping}");
		for (int i = 0; i < Controller.Body.Chunks.Count; i++)
		{
			BodyChunk c = Controller.Body.Chunks[i];
			GD.Print($"[FINAL] body=0 chunk={i} pos=({c.Pos.X:F4},{c.Pos.Y:F4},{c.Pos.Z:F4}) " +
					 $"vel={c.Vel.Length():F5} contact={c.TerrainContact} r={c.Radius:F2}");
		}
		for (int i = 0; i < Controller.Legs.Count; i++)
		{
			Limb l = Controller.Legs[i];
			GD.Print($"[FINAL] leg={i} pos=({l.Pos.X:F4},{l.Pos.Y:F4},{l.Pos.Z:F4}) " +
					 $"grip={l.GripCounter} reaching={l.ReachingForTerrain} idle={l.IdlePose}");
		}
		for (int i = 0; i < Controller.Arms.Count; i++)
		{
			Arm a = Controller.Arms[i];
			GD.Print($"[FINAL] arm={i} pos=({a.Pos.X:F4},{a.Pos.Y:F4},{a.Pos.Z:F4}) " +
					 $"mode={a.Mode} grab={(a.GrabPos is not null)} snap={a.ReachedSnapPosition}");
		}

		var reasons = new List<string>();
		if (_nonFinite)
		{
			reasons.Add("状态出现 NaN/Inf");
		}
		if (expectHash is ulong expect && probe.Hash != expect)
		{
			reasons.Add($"哈希 {probe.Hash:X16} ≠ 基线 {expect:X16}（有意改内核请同步两处真相源：矩阵脚本 + smoke）");
		}
		if (Controller.Body.SnagReleases > 5)
		{
			reasons.Add($"卡链释放 {Controller.Body.SnagReleases} 次（>5）——三 chunk 身体不该出现慢性断裂");
		}
		if (Waypoints.Length > 0 && !ActScript)
		{
			if (meanUpright < 0.5f)
			{
				reasons.Add($"巡逻平均直立度 {meanUpright:F2}（<0.5）——knuckle 步态塌成爬行");
			}
			if (Controller.KnucklePlants < 60)
			{
				reasons.Add($"撑地锁点仅 {Controller.KnucklePlants} 次（<60）——指关节行走没在跑");
			}
			if (_walkDistance < 20f)
			{
				reasons.Add($"巡逻里程 {_walkDistance:F2}m（<20m）");
			}
		}
		if (_recoveryStart >= 0 && (_recoveryPending || _maxRecoveryTicks > 250))
		{
			reasons.Add($"击飞/苏醒后未在预算内回正站稳（max={_maxRecoveryTicks}, pending={_recoveryPending}）");
		}
		if (StunTick >= 0 && !_stunHappened)
		{
			reasons.Add("stun 场景没有触发昏迷（StunTick 越界/场景覆盖失效）");
		}
		if (_stunHappened)
		{
			if (_stunMinUpright >= 0.5f)
			{
				reasons.Add($"昏迷窗口直立度未跌穿 0.5（min={_stunMinUpright:F2}）——瘫倒没有发生");
			}
		}
		if (ActScript)
		{
			if (!_actChargeStarted)
			{
				reasons.Add("蓄力未能启动");
			}
			if (_actBestPointAngleDeg >= 10f || _actBestPointReach < Controller.Arms[0].ArmLength * 0.6f)
			{
				reasons.Add($"指向未收敛（angle={_actBestPointAngleDeg:F1}°, reach={_actBestPointReach:F2}m）");
			}
			if (!_actCarryModeOk || _actCarryDist >= 0.2f)
			{
				reasons.Add($"持物未到位（mode={_actCarryModeOk}, dist={_actCarryDist:F2}m）");
			}
			if (_actChargeSpeed >= 0.005f)
			{
				reasons.Add($"蓄力未停驶（|v|={_actChargeSpeed:F4} ≥0.005）");
			}
			if (!_actHandCharged)
			{
				reasons.Add("蓄力位不对（主手不在身后上方）");
			}
			if (!_actThrowOk)
			{
				reasons.Add("出手向量契约失败（大小/方向/二次释放）");
			}
		}
		bool pass = reasons.Count == 0;
		GD.Print(pass ? "[RESULT] PASS" : $"[RESULT] FAIL: {string.Join("; ", reasons)}");
		return pass ? 0 : 1;
	}
}
