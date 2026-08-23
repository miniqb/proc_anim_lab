using System;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Lizard;

namespace ProcAnim.Core.Species.RatFiend;

/// <summary>
/// 鼠煞出生参数表与装配器。四个稳定 ID：
/// ratfiend/gaunt（基线，苍白灰皮）、ratfiend/dusk（gaunt 同体格，暗剪影调色变体——
/// 调色板归渲染层，体格参数刻意逐位相同）、ratfiend/broad（重型）、ratfiend/whelp（幼体）。
/// 未知 ID 快速失败（DropBug/TentaclePlant 先例），不静默回落。
/// </summary>
public static class RatFiendFactory
{
	/// <summary>双足出生站距（米，脚沿侧向外撇的半距）。</summary>
	private const float FootSpread = 0.15f;

	/// <summary>双腿出生前后错开（对角相位种子——严格比较 gripCounter 打破平局）。</summary>
	private const float LegStagger = 0.10f;

	/// <summary>基线：修长驼背的苍白鼠怪。</summary>
	public static RatFiendParams Gaunt() => new();

	/// <summary>gaunt 的暗剪影调色变体：体格参数逐位相同，只换 ID——调色板由渲染层按 ID 定档。</summary>
	public static RatFiendParams Dusk() => new()
	{
		Id = "ratfiend/dusk",
	};

	/// <summary>重型：大体格短脖、力偶更猛、腿慢步大。</summary>
	public static RatFiendParams Broad() => new()
	{
		Id = "ratfiend/broad",
		ChestRadius = 0.27f,
		HipsRadius = 0.21f,
		HeadRadius = 0.16f,
		ChestMass = 0.8f,
		HipsMass = 0.5f,
		HeadMass = 0.1f,
		ChestHipsDist = 0.62f,
		NeckLength = 0.45f,
		HunchAngleDegrees = 22f,
		StandCoupleMax = 0.16f,
		StandCoupleMin = 0.009f,
		HeadPush = 0.009f,
		ChestDamping = 0.88f,
		HipsDamping = 0.88f,
		BaseSpeed = 0.05f,
		MaxMoveSpeed = 0.07f,
		LegLength = 0.9f,
		FootRadius = 0.07f,
		LegSpeed = 0.24f,
		LegQuickness = 0.55f,
		LegGripDelay = 4,
		StepLength = 0.65f,
		LiftFeet = 0.25f,
		LegLateral = 0.18f,
		LegSwingSpeedFactor = 0.55f, // 0.24×0.45=0.108 破 §6.5 速度下界；0.55 → 0.132

		HipRideHeight = 0.7f,
		ArmLength = 1.3f,
		HandRadius = 0.075f,
		ArmSpeed = 0.45f,
		ArmQuickness = 0.8f,
		ArmExaggerate = 0.05f,
		CrawlForcePerLimb = 0.009f,
		CrawlMaxSpeed = 0.04f,
	};

	/// <summary>幼体：小体格快节奏、驼得更深。</summary>
	public static RatFiendParams Whelp() => new()
	{
		Id = "ratfiend/whelp",
		ChestRadius = 0.16f,
		HipsRadius = 0.12f,
		HeadRadius = 0.10f,
		ChestMass = 0.3f,
		HipsMass = 0.18f,
		HeadMass = 0.04f,
		ChestHipsDist = 0.42f,
		NeckLength = 0.45f,
		HunchAngleDegrees = 28f,
		StandCoupleMax = 0.11f,
		StandCoupleMin = 0.006f,
		HeadPush = 0.006f,
		ChestDamping = 0.92f,
		HipsDamping = 0.92f,
		BaseSpeed = 0.075f,
		MaxMoveSpeed = 0.11f,
		LegLength = 0.58f,
		FootRadius = 0.05f,
		LegSpeed = 0.35f,
		LegQuickness = 0.8f,
		StepLength = 0.55f,
		LiftFeet = 0.15f,
		LegLateral = 0.12f,
		HipRideHeight = 0.47f,
		ArmLength = 0.85f,
		HandRadius = 0.05f,
		ArmSpeed = 0.7f,
		ArmExaggerate = 0.15f,
		CrawlForcePerLimb = 0.012f,
		CrawlMaxSpeed = 0.05f,
	};

	public static RatFiendParams[] AllPresets() => new[] { Gaunt(), Dusk(), Broad(), Whelp() };

	/// <summary>按稳定 ID 取预设；未知 ID 抛出（快速失败，不静默回落）。</summary>
	public static RatFiendParams ById(string? id)
	{
		foreach (RatFiendParams preset in AllPresets())
		{
			if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
			{
				return preset;
			}
		}
		throw new ArgumentException($"未知 RatFiend 预设 ID：{id}");
	}

	/// <summary>
	/// 装配（Humanoid 装配线的物种私有版）。出生姿态：髋在 origin、胸在髋上、头在胸上（直立——
	/// 首个 tick 起倾斜站立力偶把平衡态雕成驼背），脚垂髋下左右错开（对角相位种子），手垂胸侧。
	/// chunk 折叠序固定 [胸, 髋, 头]（确定性哈希按此序）。
	/// </summary>
	public static RatFiendLocomotionController CreateController(
		Vector3 origin, Vector3 forward, RatFiendParams parameters)
	{
		RatFiendParams p = parameters.Snapshot();
		Vector3 fwd = forward.LengthSquared() > 1e-10f ? forward.Normalized() : Vector3.Right;

		var body = new Body();
		var chest = new BodyChunk(origin + new Vector3(0f, p.ChestHipsDist, 0f), p.ChestRadius, p.ChestMass);
		var hips = new BodyChunk(origin, p.HipsRadius, p.HipsMass);
		var head = new BodyChunk(chest.Pos + new Vector3(0f, p.NeckLength * 0.9f, 0f), p.HeadRadius, p.HeadMass);
		body.Chunks.Add(chest);
		body.Chunks.Add(hips);
		body.Chunks.Add(head);

		// 胸↔髋双向刚性：躯干杆——站立承重构件。WeightA 按质量反比：胸重少动。
		body.Connections.Add(new ChunkConnection(chest, hips, p.ChestHipsDist,
			weightA: p.HipsMass / (p.ChestMass + p.HipsMass))
		{
			ConstraintMode = ChunkConnection.Mode.Rigid,
			Elasticity = 0.25f,
		});
		// 胸↔头只防拉长：脖可压缩不可拉断——「耷拉」姿态的构件基础。
		body.Connections.Add(new ChunkConnection(chest, head, p.NeckLength,
			weightA: p.HeadMass / (p.ChestMass + p.HeadMass))
		{
			ConstraintMode = ChunkConnection.Mode.PullOnly,
			Elasticity = 0.25f,
		});

		// 朝向钉定（显式重申，不吃建链顺序巧合，CLAUDE.md §6.3）：胸→髋 = 躯干上轴
		// （前向是控制器的 Facing，消费端注意语义差）；髋→胸；头→胸。
		chest.RotationChunk = hips;
		hips.RotationChunk = chest;
		head.RotationChunk = chest;

		// 近零风阻——清醒时的真实阻尼是控制器的逐 chunk 各向异性档。
		body.AirFriction = 0.999f;

		var controller = new RatFiendLocomotionController(body, chest, hips, head, p, fwd);

		// 双腿：anchor=髋，出生错位相反 = 对角相位种子；LookaheadTicks>0 硬性
		// （双足必须走前瞻点循环，Humanoid §3.5 结论）。取值 3 是 R11 步态修复：
		// 释放点 = 前瞻圈（髋 + vel×前瞻）沿步向后退水平余量 sqrt(腿长²−站高²)≈0.42m；
		// 前瞻 10 时圈心在髋前 vel×10≈0.65m，释放点被推到髋前 0.4m——脚从落点到释放
		// 全程在髋前（实测 bothAhead=100%、11 步/秒碎步）。缩到 3 后走姿释放点落到
		// 髋后 ≈0.33m，脚过髋后蹬、步频降到 ≈3 步/秒（人样迈步）。
		foreach (int side in new[] { -1, +1 })
		{
			float stagger = LegStagger * side * -1f;
			Vector3 foot = hips.Pos + new Vector3(-0.1f + stagger, -p.HipsRadius, side * FootSpread);
			var leg = new Limb(hips, foot, p.FootRadius, side)
			{
				JointDist = p.LegLength,
				HuntSpeed = p.LegSpeed,
				Quickness = p.LegQuickness,
				GripDelay = p.LegGripDelay,
				StepLength = p.StepLength,
				LiftFeet = p.LiftFeet,
				FeetDown = p.FeetDown,
				PairLateral = p.LegLateral,
				LookaheadTicks = 3,
				// 闲置垂脚深度 = 站高/腿长（R10：站高 0.83×腿长 > 默认 0.6×腿长，
				// 不 opt-in 则站定进 IdlePose 时脚悬空 0.18m）。
				RestDepth = p.HipRideHeight / p.LegLength,
			};
			controller.Legs.Add(leg);
		}
		controller.Legs[0].Pair = controller.Legs[1];
		controller.Legs[1].Pair = controller.Legs[0];

		// 双臂：anchor=胸，[0]=左 / [1]=右（RatFiendLimbId 的序）。
		foreach (int side in new[] { -1, +1 })
		{
			Vector3 hand = chest.Pos + new Vector3(0.1f, -p.ArmLength * 0.5f,
				side * (p.ChestRadius + p.HandRadius));
			var arm = new RatArm(chest, hand, p.HandRadius, side)
			{
				ArmLength = p.ArmLength,
				SeveredLengthFactor = p.SeveredLengthFactor,
				HuntSpeed = p.ArmSpeed,
				Quickness = p.ArmQuickness,
				DefaultHuntSpeed = p.ArmSpeed,
				DefaultQuickness = p.ArmQuickness,
				AdaptVel = p.ArmAdaptVel,
				Exaggerate = p.ArmExaggerate,
				ArmpitGap = p.ChestRadius + p.HandRadius,
			};
			controller.Arms.Add(arm);
		}

		return controller;
	}
}
