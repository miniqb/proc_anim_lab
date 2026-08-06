using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Spider;

namespace ProcAnimLab.SpiderSandbox;

/// <summary>
/// 蜘蛛白盒渲染器。身体仍画成 chunk 球与连接线；每条腿正式绘制为
/// Root → Knee → Foot 两段，膝点来自 SpiderLeg 的稳定两段 IK 输出。
/// </summary>
public sealed class SpiderBodyRenderer
{
    private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _chunks = new();
    private readonly List<(SpiderLeg Leg, MeshInstance3D Node)> _feet = new();
    private readonly List<ChunkConnection> _connections = new();
    private readonly List<SpiderLeg> _legs = new();

    private SpiderLocomotionController? _controller;
    private ImmediateMesh? _lineMesh;
    private MeshInstance3D? _lineNode;

    private StandardMaterial3D _bodyAirborne = null!;
    private StandardMaterial3D _bodyFooted = null!;
    private StandardMaterial3D _footGrip = null!;
    private StandardMaterial3D _footReach = null!;
    private StandardMaterial3D _footSwing = null!;

    public void Build(Node3D parent, SpiderLocomotionController controller)
    {
        _controller = controller;
        _bodyAirborne = Material(new Color(0.72f, 0.24f, 0.25f));
        _bodyFooted = Material(new Color(0.24f, 0.22f, 0.3f));
        _footGrip = Material(new Color(0.25f, 0.88f, 0.42f));
        _footReach = Material(new Color(1f, 0.58f, 0.18f));
        _footSwing = Material(new Color(0.48f, 0.58f, 0.75f));

        foreach (BodyChunk chunk in controller.Body.Chunks)
        {
            var node = new MeshInstance3D
            {
                Mesh = new SphereMesh
                {
                    Radius = chunk.Radius,
                    Height = chunk.Radius * 2f,
                    RadialSegments = 16,
                    Rings = 8,
                },
                MaterialOverride = _bodyAirborne,
                TopLevel = true,
            };
            parent.AddChild(node);
            _chunks.Add((chunk, node));
        }

        foreach (ChunkConnection connection in controller.Body.Connections)
        {
            if (!connection.SoftOnly)
            {
                _connections.Add(connection);
            }
        }

        foreach (SpiderLeg leg in controller.Legs)
        {
            var node = new MeshInstance3D
            {
                Mesh = new SphereMesh
                {
                    Radius = leg.Radius,
                    Height = leg.Radius * 2f,
                    RadialSegments = 12,
                    Rings = 6,
                },
                MaterialOverride = _footSwing,
                TopLevel = true,
            };
            parent.AddChild(node);
            _feet.Add((leg, node));
            _legs.Add(leg);
        }

        _lineMesh = new ImmediateMesh();
        _lineNode = new MeshInstance3D
        {
            Mesh = _lineMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
            TopLevel = true,
        };
        parent.AddChild(_lineNode);
    }

    public void Clear()
    {
        foreach ((_, MeshInstance3D node) in _chunks)
        {
            node.QueueFree();
        }
        foreach ((_, MeshInstance3D node) in _feet)
        {
            node.QueueFree();
        }
        _lineNode?.QueueFree();

        _chunks.Clear();
        _feet.Clear();
        _connections.Clear();
        _legs.Clear();
        _controller = null;
        _lineMesh = null;
        _lineNode = null;
    }

    /// <summary>整体显隐（正式渲染 V 键切换用）：只切可见性，Draw 照常推进——切回白盒时
    /// 位置/配色立即是新鲜的。</summary>
    public void SetVisible(bool visible)
    {
        foreach ((_, MeshInstance3D node) in _chunks)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.Visible = visible;
            }
        }
        foreach ((_, MeshInstance3D node) in _feet)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.Visible = visible;
            }
        }
        if (_lineNode is not null && GodotObject.IsInstanceValid(_lineNode))
        {
            _lineNode.Visible = visible;
        }
    }

    public void Draw(float interpolation)
    {
        if (_controller is null)
        {
            return;
        }

        StandardMaterial3D bodyMaterial =
            _controller.ApplyGravity ? _bodyAirborne : _bodyFooted;
        foreach ((BodyChunk chunk, MeshInstance3D node) in _chunks)
        {
            node.Position = chunk.LerpPos(interpolation);
            node.MaterialOverride = bodyMaterial;
        }

        foreach ((SpiderLeg leg, MeshInstance3D node) in _feet)
        {
            node.Position = leg.LerpPos(interpolation);
            node.MaterialOverride = leg.Gripping ? _footGrip
                : leg.ReachingForTerrain ? _footReach
                : _footSwing;
        }

        if (_lineMesh is null)
        {
            return;
        }
        _lineMesh.ClearSurfaces();
        if (_connections.Count == 0 && _legs.Count == 0)
        {
            return;
        }

        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        _lineMesh.SurfaceSetColor(new Color(0.95f, 0.86f, 0.4f));
        foreach (ChunkConnection connection in _connections)
        {
            _lineMesh.SurfaceAddVertex(connection.A.LerpPos(interpolation));
            _lineMesh.SurfaceAddVertex(connection.B.LerpPos(interpolation));
        }

        foreach (SpiderLeg leg in _legs)
        {
            Vector3 root = leg.LerpRoot(interpolation);
            Vector3 knee = leg.LerpKnee(interpolation);
            Vector3 foot = leg.LerpPos(interpolation);
            Color color = leg.Gripping
                ? new Color(0.45f, 0.95f, 0.5f)
                : leg.ReachingForTerrain
                    ? new Color(1f, 0.65f, 0.2f)
                    : new Color(0.7f, 0.75f, 0.9f);
            _lineMesh.SurfaceSetColor(color);
            _lineMesh.SurfaceAddVertex(root);
            _lineMesh.SurfaceAddVertex(knee);
            _lineMesh.SurfaceAddVertex(knee);
            _lineMesh.SurfaceAddVertex(foot);
        }
        _lineMesh.SurfaceEnd();
    }

    private static StandardMaterial3D Material(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 0.78f,
    };
}
