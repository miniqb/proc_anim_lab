using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnim.Core.Species.Centipede;

/// <summary>
/// 蜈蚣预设与出生装配器。稳定 ID 是宿主存档/CLI 的对接面；未知 ID 快速失败，不做
/// 静默回落。所有方法都返回新的参数/身体对象，预设之间不共享可变出生配置。
/// </summary>
public static class CentipedeFactory
{
	public const string ShortId = "centipede/short";
	public const string LongId = "centipede/long";
	public const string ArmoredId = "centipede/armored";
	public const string RibbonId = "centipede/ribbon";

	public static CentipedeParams Short() => new()
	{
		StableId = ShortId,
		SegmentCount = 5,
		EndRadius = 0.11f,
		MiddleRadius = 0.15f,
		ProfileExponent = 0.75f,
		BaseSegment = new CentipedeSegmentParams
		{
			Radius = 0.14f,
			Mass = 0.28f,
			LinkLengthToNext = 0.21f,
			BendStiffness = 0.58f,
			DriveWeight = 1f,
			AdhesionWeight = 1f,
			LegPairs = 1,
			LegLength = 0.32f,
			FootRadius = 0.045f,
			LegHuntSpeed = 0.17f,
			LegQuickness = 0.72f,
			LegGripDelay = 2,
			LegStride = 0.28f,
			LegLateral = 0.21f,
		},
		BaseSpeed = 0.058f,
		MaxMoveSpeed = 0.085f,
		// SurfaceClearance 是球半径之外的额外间隙，不是身体离表面的总高度。
		SurfaceClearance = 0.012f,
		SurfaceProbeDistance = 0.62f,
		SurfaceServo = 0.22f,
		SurfaceDamping = 0.55f,
		SupportBlend = 0.3f,
		TrailSampleSpacing = 0.08f,
		CornerProbeSteps = 9,
		GaitFrequency = 0.18f,
		GaitWavelength = 2.1f,
		StanceFraction = 0.66f,
		SelfAvoidanceStrength = 0.12f,
		SelfAvoidanceCellSize = 0.34f,
		ArriveRadius = 0.22f,
	};

	public static CentipedeParams Long() => new()
	{
		StableId = LongId,
		SegmentCount = 18,
		EndRadius = 0.095f,
		MiddleRadius = 0.16f,
		ProfileExponent = 0.62f,
		BaseSegment = new CentipedeSegmentParams
		{
			Radius = 0.14f,
			Mass = 0.3f,
			LinkLengthToNext = 0.23f,
			BendStiffness = 0.43f,
			DriveWeight = 0.85f,
			AdhesionWeight = 1.05f,
			LegPairs = 1,
			LegLength = 0.36f,
			FootRadius = 0.043f,
			LegHuntSpeed = 0.135f,
			LegQuickness = 0.58f,
			LegGripDelay = 3,
			LegStride = 0.34f,
			LegLateral = 0.23f,
		},
		BaseSpeed = 0.044f,
		MaxMoveSpeed = 0.068f,
		SurfaceClearance = 0.014f,
		SurfaceProbeDistance = 0.72f,
		SurfaceServo = 0.19f,
		SurfaceDamping = 0.6f,
		SupportBlend = 0.22f,
		TrailSampleSpacing = 0.075f,
		CornerProbeSteps = 12,
		GaitFrequency = 0.105f,
		GaitWavelength = 4.2f,
		StanceFraction = 0.72f,
		SelfAvoidanceStrength = 0.15f,
		SelfAvoidanceCellSize = 0.39f,
		ArriveRadius = 0.3f,
	};

	public static CentipedeParams Armored() => new()
	{
		StableId = ArmoredId,
		SegmentCount = 10,
		EndRadius = 0.17f,
		MiddleRadius = 0.23f,
		ProfileExponent = 0.7f,
		BaseSegment = new CentipedeSegmentParams
		{
			Radius = 0.2f,
			Mass = 0.72f,
			LinkLengthToNext = 0.28f,
			BendStiffness = 0.76f,
			DriveWeight = 0.72f,
			AdhesionWeight = 1.35f,
			LegPairs = 1,
			LegLength = 0.43f,
			FootRadius = 0.065f,
			LegHuntSpeed = 0.115f,
			LegQuickness = 0.48f,
			LegGripDelay = 4,
			LegStride = 0.32f,
			LegLateral = 0.29f,
		},
		Overrides =
		[
			new CentipedeSegmentOverride
			{
				SegmentIndex = 4,
				Mass = 1.05f,
				BendStiffness = 0.9f,
				LegPairs = 2,
				AdhesionWeight = 1.55f,
			},
			new CentipedeSegmentOverride
			{
				SegmentIndex = 5,
				Mass = 1.05f,
				BendStiffness = 0.9f,
				LegPairs = 2,
				AdhesionWeight = 1.55f,
			},
		],
		BaseSpeed = 0.036f,
		MaxMoveSpeed = 0.058f,
		ConstraintIterations = 6,
		SurfaceClearance = 0.018f,
		SurfaceProbeDistance = 0.82f,
		SurfaceServo = 0.24f,
		SurfaceDamping = 0.64f,
		SupportBlend = 0.18f,
		TrailSampleSpacing = 0.1f,
		CornerProbeSteps = 13,
		GaitFrequency = 0.085f,
		GaitWavelength = 3.6f,
		StanceFraction = 0.76f,
		SelfAvoidanceStrength = 0.2f,
		SelfAvoidanceCellSize = 0.54f,
		ArriveRadius = 0.36f,
	};

	public static CentipedeParams Ribbon()
	{
		var alternating = new CentipedeSegmentOverride?[12];
		for (int i = 0; i < alternating.Length; i++)
		{
			alternating[i] = new CentipedeSegmentOverride
			{
				SegmentIndex = i,
				LinkLengthToNext = i % 2 == 0 ? 0.23f : 0.3f,
				LegLength = i % 2 == 0 ? 0.39f : 0.47f,
				LegLateral = i % 2 == 0 ? 0.2f : 0.27f,
			};
		}
		return new CentipedeParams
		{
			StableId = RibbonId,
			SegmentCount = 12,
			EndRadius = 0.065f,
			MiddleRadius = 0.09f,
			ProfileExponent = 0.5f,
			BaseSegment = new CentipedeSegmentParams
			{
				Radius = 0.08f,
				Mass = 0.12f,
				LinkLengthToNext = 0.26f,
				BendStiffness = 0.2f,
				DriveWeight = 1.2f,
				AdhesionWeight = 0.85f,
				LegPairs = 1,
				LegLength = 0.42f,
				FootRadius = 0.033f,
				LegHuntSpeed = 0.2f,
				LegQuickness = 0.78f,
				LegGripDelay = 2,
				LegStride = 0.38f,
				LegLateral = 0.23f,
			},
			Overrides = alternating,
			BaseSpeed = 0.068f,
			MaxMoveSpeed = 0.105f,
			ConstraintIterations = 6,
			SurfaceClearance = 0.01f,
			SurfaceProbeDistance = 0.75f,
			SurfaceServo = 0.17f,
			SurfaceDamping = 0.5f,
			SupportBlend = 0.32f,
			TrailSampleSpacing = 0.06f,
			CornerProbeSteps = 11,
			GaitFrequency = 0.19f,
			GaitWavelength = 2.8f,
			StanceFraction = 0.61f,
			SelfAvoidanceStrength = 0.09f,
			SelfAvoidanceCellSize = 0.3f,
			ArriveRadius = 0.24f,
		};
	}

	/// <summary>沙盒数字键 5~8 与 --creature= 共用此固定顺序。</summary>
	public static CentipedeParams[] AllPresets() => [Short(), Long(), Armored(), Ribbon()];

	public static bool TryByStableId(string? stableId, out CentipedeParams parameters)
	{
		parameters = stableId switch
		{
			ShortId => Short(),
			LongId => Long(),
			ArmoredId => Armored(),
			RibbonId => Ribbon(),
			_ => null!,
		};
		return parameters is not null;
	}

	/// <summary>按稳定 ID 新建预设；未知值是宿主配置错误，必须快速失败。</summary>
	public static CentipedeParams ByStableId(string stableId)
	{
		if (TryByStableId(stableId, out CentipedeParams parameters))
		{
			return parameters;
		}
		throw new ArgumentException($"Unknown centipede stable ID '{stableId}'.", nameof(stableId));
	}

	public static CentipedeLocomotionController CreateController(Vector3 origin) =>
		CreateController(origin, Short());

	public static CentipedeLocomotionController CreateController(Vector3 origin, string stableId) =>
		CreateController(origin, ByStableId(stableId));

	/// <summary>
	/// 头节出生在 origin，其余节沿 -X 排列。相邻节用质量加权刚性连接；每个关节再以
	/// 隔节 PushOnly 软支柱限制深折叠。最后显式钉定 RotationChunk，避免后建支柱覆盖朝向。
	/// </summary>
	public static CentipedeLocomotionController CreateController(Vector3 origin, CentipedeParams parameters)
	{
		ArgumentNullException.ThrowIfNull(parameters);
		CentipedeSegmentParams[] specs = parameters.ResolveSegments();
		var body = new Body();
		var chunks = new BodyChunk[specs.Length];

		float distance = 0f;
		for (int i = 0; i < chunks.Length; i++)
		{
			if (i > 0)
			{
				distance += specs[i - 1].LinkLengthToNext;
			}
			chunks[i] = new BodyChunk(origin + Vector3.Left * distance, specs[i].Radius, specs[i].Mass);
			body.Chunks.Add(chunks[i]);
		}

		for (int i = 0; i + 1 < chunks.Length; i++)
		{
			body.Connections.Add(new ChunkConnection(chunks[i], chunks[i + 1],
				specs[i].LinkLengthToNext, MassWeightedCorrectionForA(chunks[i], chunks[i + 1]))
			{
				ConstraintMode = ChunkConnection.Mode.Rigid,
				// 长链贴着墙角/台阶换面时，地形碰撞发生在本 tick 的硬约束之后。
				// 允许 Body 只恢复“碰撞新增”的距离违反，避免刚拉开的相邻节又被 MTD
				// 压回一团；它不会重复修正碰撞前已经存在的软体形变。
				TerrainCoupled = true,
			});
		}

		for (int i = 0; i + 2 < chunks.Length; i++)
		{
			float firstLink = specs[i].LinkLengthToNext;
			float secondLink = specs[i + 1].LinkLengthToNext;
			float stiffness = Mathf.Clamp(specs[i + 1].BendStiffness, 0f, 1f);
			float minimumSpan = Mathf.Max(firstLink, secondLink);
			float straightSpan = firstLink + secondLink;
			body.Connections.Add(new ChunkConnection(chunks[i], chunks[i + 2],
				Mathf.Lerp(minimumSpan, straightSpan, stiffness),
				MassWeightedCorrectionForA(chunks[i], chunks[i + 2]))
			{
				ConstraintMode = ChunkConnection.Mode.PushOnly,
				Elasticity = Mathf.Lerp(0.1f, 0.5f, stiffness),
				SoftOnly = true,
				TerrainCoupled = true,
			});
		}

		for (int i = 0; i + 1 < chunks.Length; i++)
		{
			chunks[i].RotationChunk = chunks[i + 1];
		}
		chunks[^1].RotationChunk = chunks[^2];

		var controller = new CentipedeLocomotionController(body, specs)
		{
			BaseSpeed = parameters.BaseSpeed,
			MaxMoveSpeed = parameters.MaxMoveSpeed,
			MoveIntentDeadzone = parameters.MoveIntentDeadzone,
			SurfaceClearance = parameters.SurfaceClearance,
			SurfaceProbeDistance = parameters.SurfaceProbeDistance,
			SurfaceServo = parameters.SurfaceServo,
			SurfaceDamping = parameters.SurfaceDamping,
			StanceDamping = parameters.StanceDamping,
			SupportBlend = parameters.SupportBlend,
			TrailSampleSpacing = parameters.TrailSampleSpacing,
			CornerProbeSteps = parameters.CornerProbeSteps,
			GaitFrequency = parameters.GaitFrequency,
			GaitWavelength = parameters.GaitWavelength,
			StanceFraction = parameters.StanceFraction,
			SelfAvoidanceStrength = parameters.SelfAvoidanceStrength,
			SelfAvoidanceCellSize = parameters.SelfAvoidanceCellSize,
			ArriveRadius = parameters.ArriveRadius,
		};
		// 蜥蜴只在局部卡角时开启该门；蜈蚣的每节都可能同时跨不同表面，因此始终让
		// TerrainCoupled 连接走接触可行锥恢复。未标记的连接仍完全不受影响。
		controller.Body.EnablePostCollisionStructureRecovery = true;
		controller.Body.ConstraintIterations = parameters.ConstraintIterations;
		return controller;
	}

	private static float MassWeightedCorrectionForA(BodyChunk a, BodyChunk b) =>
		b.Mass / (a.Mass + b.Mass);
}
