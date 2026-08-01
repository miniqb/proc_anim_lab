using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Humanoid;
using ProcAnim.Core.Species.Lizard;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 人形白盒渲染（与 <see cref="BodyRenderer"/> 同风格，物种并列不混装）：
/// 身体球按状态换色——青=清醒近地（失重伺服态）、红=坠落（重力在拽）、灰紫=昏迷瘫倒；
/// 脚球沿用蜥蜴三色（绿抓稳/橙迈步/灰蓝摆动）；手球按手臂模式换色——
/// 黄=撑地锁点（HuntAbsolute+GrabPos）、紫=指向/蓄力/出手（HuntAbsolute 无撑点）、
/// 绿=持物/休息位（HuntRelative）、灰蓝=垂摆（Dangle）。
/// 另画：橙色小球=KnucklePos 撑点参考（物理层抽象量的可视化，挂 F3 射线调试开关）、棕色球=被持物
/// （硬钉 MainHandPos，≙ RW grasp 0）与交互投掷后的飞行道具。
/// </summary>
public sealed class HumanoidRenderer
{
	private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _spheres = new();
	private readonly List<(Limb Leg, MeshInstance3D Node)> _feet = new();
	private readonly List<(Arm Arm, MeshInstance3D Node)> _hands = new();
	private HumanoidLocomotionController? _controller;
	private ImmediateMesh? _lineMesh;
	private MeshInstance3D? _lineNode;
	private MeshInstance3D? _knuckleNode;
	private MeshInstance3D? _propNode;

	private StandardMaterial3D _chunkFalling = null!;
	private StandardMaterial3D _chunkFooted = null!;
	private StandardMaterial3D _chunkStunned = null!;
	private StandardMaterial3D _footGrip = null!;
	private StandardMaterial3D _footReach = null!;
	private StandardMaterial3D _footSwing = null!;
	private StandardMaterial3D _handLock = null!;
	private StandardMaterial3D _handAction = null!;
	private StandardMaterial3D _handRelative = null!;
	private StandardMaterial3D _handDangle = null!;

	public void Build(Node3D parent, HumanoidLocomotionController controller)
	{
		_controller = controller;
		_chunkFalling = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.35f, 0.3f) };
		_chunkFooted = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.65f, 0.7f) };
		_chunkStunned = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.5f, 0.62f) };
		_footGrip = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.8f, 0.35f) };
		_footReach = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.6f, 0.2f) };
		_footSwing = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.6f, 0.75f) };
		_handLock = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.85f, 0.25f) };
		_handAction = new StandardMaterial3D { AlbedoColor = new Color(0.75f, 0.4f, 0.95f) };
		_handRelative = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.8f, 0.5f) };
		_handDangle = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.6f, 0.75f) };

		foreach (BodyChunk chunk in controller.Body.Chunks)
		{
			var node = new MeshInstance3D
			{
				Mesh = new SphereMesh { Radius = chunk.Radius, Height = chunk.Radius * 2f },
				MaterialOverride = _chunkFalling,
				TopLevel = true, // 物理坐标是世界系；Position 直写世界
			};
			parent.AddChild(node);
			_spheres.Add((chunk, node));
		}
		foreach (Limb leg in controller.Legs)
		{
			var node = new MeshInstance3D
			{
				Mesh = new SphereMesh { Radius = leg.Radius, Height = leg.Radius * 2f },
				MaterialOverride = _footSwing,
				TopLevel = true,
			};
			parent.AddChild(node);
			_feet.Add((leg, node));
		}
		foreach (Arm arm in controller.Arms)
		{
			var node = new MeshInstance3D
			{
				Mesh = new SphereMesh { Radius = arm.Radius, Height = arm.Radius * 2f },
				MaterialOverride = _handDangle,
				TopLevel = true,
			};
			parent.AddChild(node);
			_hands.Add((arm, node));
		}

		_knuckleNode = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f },
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color(1f, 0.6f, 0.1f),
			},
			TopLevel = true,
			Visible = false,
		};
		parent.AddChild(_knuckleNode);

		_propNode = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.4f, 0.25f) },
			TopLevel = true,
			Visible = false,
		};
		parent.AddChild(_propNode);

		_lineMesh = new ImmediateMesh();
		_lineNode = new MeshInstance3D
		{
			Mesh = _lineMesh,
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color(0.95f, 0.9f, 0.4f),
			},
			TopLevel = true, // 顶点直接写世界坐标
		};
		parent.AddChild(_lineNode);
	}

	/// <summary>拆掉本次 Build 创建的全部节点（物种/品种切换重生用），之后可再次 Build。</summary>
	public void Clear()
	{
		foreach ((_, MeshInstance3D node) in _spheres)
		{
			node.QueueFree();
		}
		foreach ((_, MeshInstance3D node) in _feet)
		{
			node.QueueFree();
		}
		foreach ((_, MeshInstance3D node) in _hands)
		{
			node.QueueFree();
		}
		_knuckleNode?.QueueFree();
		_propNode?.QueueFree();
		_lineNode?.QueueFree();
		_knuckleNode = null;
		_propNode = null;
		_lineNode = null;
		_lineMesh = null;
		_spheres.Clear();
		_feet.Clear();
		_hands.Clear();
		_controller = null;
	}

	/// <summary>thrownProp = 交互投掷后的飞行道具位置（null = 无）；
	/// showKnuckle = KnucklePos 撑点球是否可见（挂 F3 射线调试开关——物理层抽象量属调试观测）。</summary>
	public void Draw(float t, Vector3? thrownProp, bool showKnuckle)
	{
		if (_controller is null)
		{
			return;
		}
		StandardMaterial3D chunkMat = !_controller.Conscious ? _chunkStunned
			: _controller.ApplyGravity ? _chunkFalling
			: _chunkFooted;
		foreach ((BodyChunk chunk, MeshInstance3D node) in _spheres)
		{
			node.Position = chunk.LerpPos(t);
			node.MaterialOverride = chunkMat;
		}
		foreach ((Limb leg, MeshInstance3D node) in _feet)
		{
			node.Position = leg.LerpPos(t);
			node.MaterialOverride = leg.Gripping ? _footGrip
				: leg.ReachingForTerrain && !leg.IdlePose ? _footReach
				: _footSwing;
		}
		foreach ((Arm arm, MeshInstance3D node) in _hands)
		{
			node.Position = arm.LerpPos(t);
			node.MaterialOverride = arm.Mode switch
			{
				Arm.ArmMode.HuntAbsolutePosition when arm.GrabPos is not null => _handLock,
				Arm.ArmMode.HuntAbsolutePosition => _handAction,
				Arm.ArmMode.HuntRelativePosition => _handRelative,
				_ => _handDangle,
			};
		}

		if (_knuckleNode is not null)
		{
			_knuckleNode.Visible = showKnuckle && _controller.KnucklePos is not null;
			if (_controller.KnucklePos is { } kp)
			{
				_knuckleNode.Position = kp;
			}
		}
		if (_propNode is not null)
		{
			bool showProp = thrownProp is not null || _controller.Carrying;
			_propNode.Visible = showProp;
			if (thrownProp is { } prop)
			{
				_propNode.Position = prop;
			}
			else if (_controller.Carrying)
			{
				// 被持物硬钉主手（≙ RW GraphicsModuleUpdated 对 grasp 0 的钉定——宿主侧一行）。
				_propNode.Position = _controller.MainHandPos;
			}
		}

		if (_lineMesh is null)
		{
			return;
		}
		_lineMesh.ClearSurfaces();
		_lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
		foreach (ChunkConnection conn in _controller.Body.Connections)
		{
			_lineMesh.SurfaceAddVertex(conn.A.LerpPos(t));
			_lineMesh.SurfaceAddVertex(conn.B.LerpPos(t));
		}
		foreach (Limb leg in _controller.Legs)
		{
			_lineMesh.SurfaceAddVertex(leg.Anchor.LerpPos(t));
			_lineMesh.SurfaceAddVertex(leg.LerpPos(t));
		}
		foreach (Arm arm in _controller.Arms)
		{
			_lineMesh.SurfaceAddVertex(arm.Shoulder.LerpPos(t));
			_lineMesh.SurfaceAddVertex(arm.LerpPos(t));
		}
		_lineMesh.SurfaceEnd();
	}
}
