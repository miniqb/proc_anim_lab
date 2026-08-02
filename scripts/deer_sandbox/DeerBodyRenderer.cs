using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Deer;

namespace ProcAnimLab.DeerSandbox;

/// <summary>
/// Deer 白盒表现层：身体 chunk、每条腿的每一节、连接线与落脚调试标记。
/// 所有网格均为宿主资源；核心只提供纯值状态。
/// </summary>
public sealed class DeerBodyRenderer
{
    private sealed class LegVisual
    {
        public required DeerLeg Leg;
        public readonly List<MeshInstance3D> SegmentNodes = new();
        public required MeshInstance3D DesiredMarker;
        public required MeshInstance3D CandidateMarker;
        public required MeshInstance3D GripMarker;
    }

    private readonly List<Node3D> _nodes = new();
    private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _bodyNodes = new();
    private readonly List<ChunkConnection> _connections = new();
    private readonly List<LegVisual> _legVisuals = new();

    private DeerLocomotionController? _controller;
    private ImmediateMesh? _lineMesh;

    private StandardMaterial3D _headMaterial = null!;
    private StandardMaterial3D _trunkMaterial = null!;
    private StandardMaterial3D _antlerMaterial = null!;
    private StandardMaterial3D _legGripMaterial = null!;
    private StandardMaterial3D _legCandidateMaterial = null!;
    private StandardMaterial3D _legSwingMaterial = null!;
    private StandardMaterial3D _desiredMaterial = null!;

    public void Build(Node3D parent, DeerLocomotionController controller)
    {
        Clear();
        _controller = controller;

        _headMaterial = Material(new Color(0.74f, 0.57f, 0.30f));
        _trunkMaterial = Material(new Color(0.34f, 0.23f, 0.16f));
        _antlerMaterial = Material(new Color(0.82f, 0.76f, 0.60f));
        _legGripMaterial = Material(new Color(0.20f, 0.92f, 0.38f));
        _legCandidateMaterial = Material(new Color(1.00f, 0.61f, 0.15f));
        _legSwingMaterial = Material(new Color(0.48f, 0.62f, 0.82f));
        _desiredMaterial = Material(new Color(0.20f, 0.86f, 1.00f));

        foreach (BodyChunk chunk in controller.Body.Chunks)
        {
            StandardMaterial3D material = ReferenceEquals(chunk, controller.Head)
                ? _headMaterial
                : ReferenceEquals(chunk, controller.Antler)
                    ? _antlerMaterial
                    : _trunkMaterial;
            MeshInstance3D node = AddSphere(parent, chunk.Radius, material, 16, 8);
            _bodyNodes.Add((chunk, node));
        }
        foreach (ChunkConnection connection in controller.Body.Connections)
        {
            if (!connection.SoftOnly)
            {
                _connections.Add(connection);
            }
        }

        foreach (DeerLeg leg in controller.Legs)
        {
            var visual = new LegVisual
            {
                Leg = leg,
                DesiredMarker = AddSphere(parent, MathF.Max(0.045f, leg.FootRadius * 0.42f),
                    _desiredMaterial, 10, 5),
                CandidateMarker = AddSphere(parent, MathF.Max(0.05f, leg.FootRadius * 0.52f),
                    _legCandidateMaterial, 10, 5),
                GripMarker = AddSphere(parent, MathF.Max(0.055f, leg.FootRadius * 0.62f),
                    _legGripMaterial, 10, 5),
            };
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                visual.SegmentNodes.Add(AddSphere(parent, segment.Radius, _legSwingMaterial, 10, 5));
            }
            _legVisuals.Add(visual);
        }

        _lineMesh = new ImmediateMesh();
        var lineNode = new MeshInstance3D
        {
            Mesh = _lineMesh,
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
        };
        parent.AddChild(lineNode);
        _nodes.Add(lineNode);
    }

    public void Clear()
    {
        foreach (Node3D node in _nodes)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
        _nodes.Clear();
        _bodyNodes.Clear();
        _connections.Clear();
        _legVisuals.Clear();
        _controller = null;
        _lineMesh = null;
    }

    public void Draw(
        float interpolation,
        Vector3 supportNormal,
        float totalSupport,
        float actualHeight,
        float desiredHeight)
    {
        if (_controller is null)
        {
            return;
        }

        foreach ((BodyChunk chunk, MeshInstance3D node) in _bodyNodes)
        {
            node.GlobalPosition = chunk.LerpPos(interpolation);
        }

        foreach (LegVisual visual in _legVisuals)
        {
            DeerLeg leg = visual.Leg;
            // AttachedAtTip 是足端已经物理落地；Gripping 还要等待 2..4 tick 的支撑确认。
            // 几何颜色按真实接触显示，避免“绿抓点 + 蓝摆腿”同时出现的矛盾图例。
            StandardMaterial3D segmentMaterial = leg.AttachedAtTip
                ? _legGripMaterial
                : leg.HasCandidate ? _legCandidateMaterial : _legSwingMaterial;
            for (int i = 0; i < leg.Segments.Length; i++)
            {
                visual.SegmentNodes[i].GlobalPosition = leg.Segments[i].LerpPos(interpolation);
                visual.SegmentNodes[i].MaterialOverride = segmentMaterial;
            }

            visual.DesiredMarker.GlobalPosition = leg.DesiredGripPoint;
            visual.DesiredMarker.Visible = true;
            visual.CandidateMarker.GlobalPosition = leg.CandidatePoint;
            visual.CandidateMarker.Visible = leg.HasCandidate;
            visual.GripMarker.GlobalPosition = leg.GripPoint;
            visual.GripMarker.Visible = leg.AttachedAtTip;
        }

        DrawLines(interpolation, supportNormal, totalSupport, actualHeight, desiredHeight);
    }

    private void DrawLines(
        float interpolation,
        Vector3 supportNormal,
        float totalSupport,
        float actualHeight,
        float desiredHeight)
    {
        if (_controller is null || _lineMesh is null)
        {
            return;
        }
        _lineMesh.ClearSurfaces();
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        SetLineColor(new Color(0.95f, 0.85f, 0.35f));
        foreach (ChunkConnection connection in _connections)
        {
            AddLine(connection.A.LerpPos(interpolation), connection.B.LerpPos(interpolation));
        }

        foreach (LegVisual visual in _legVisuals)
        {
            DeerLeg leg = visual.Leg;
            Color color = leg.AttachedAtTip
                ? new Color(0.20f, 1.00f, 0.35f)
                : leg.HasCandidate
                    ? new Color(1.00f, 0.62f, 0.14f)
                    : new Color(0.48f, 0.65f, 0.92f);
            SetLineColor(color);
            Vector3 previous = leg.Anchor.LerpPos(interpolation);
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                Vector3 current = segment.LerpPos(interpolation);
                AddLine(previous, current);
                previous = current;
            }

            if (leg.AttachedAtTip)
            {
                SetLineColor(new Color(0.25f, 1f, 0.55f, 0.75f));
                AddLine(leg.GripPoint, leg.GripPoint + leg.GripNormal * (0.18f + leg.SupportContribution));
            }
        }

        Vector3 center = BodyCenter(interpolation);
        // ActualBodyHeight 的核心契约是沿重力反方向的垂直高度，不是沿斜坡法线的距离。
        // Deer 沙盒重力固定向下，因此高度标尺必须使用世界 Up；支撑法线只画支撑向量。
        Vector3 heightUp = Vector3.Up;
        Vector3 supportUp = NormalizeOr(supportNormal, heightUp);
        Vector3 ground = center - heightUp * MathF.Max(0f, actualHeight);
        SetLineColor(new Color(0.25f, 0.95f, 0.45f));
        AddLine(ground, center);
        SetLineColor(new Color(1f, 0.55f, 0.12f));
        AddLine(ground, ground + heightUp * MathF.Max(0f, desiredHeight));
        SetLineColor(new Color(0.25f, 0.90f, 1f));
        AddLine(center, center + supportUp * MathF.Max(0.12f, totalSupport * 0.8f));

        _lineMesh.SurfaceEnd();
    }

    private Vector3 BodyCenter(float interpolation)
    {
        Vector3 sum = Vector3.Zero;
        float mass = 0f;
        foreach (BodyChunk chunk in _controller!.Body.Chunks)
        {
            sum += chunk.LerpPos(interpolation) * chunk.Mass;
            mass += chunk.Mass;
        }
        return mass > 1e-8f ? sum / mass : Vector3.Zero;
    }

    private void SetLineColor(Color color) => _lineMesh!.SurfaceSetColor(color);

    private void AddLine(Vector3 from, Vector3 to)
    {
        _lineMesh!.SurfaceAddVertex(from);
        _lineMesh.SurfaceAddVertex(to);
    }

    private MeshInstance3D AddSphere(
        Node3D parent,
        float radius,
        StandardMaterial3D material,
        int radialSegments,
        int rings)
    {
        var node = new MeshInstance3D
        {
            Mesh = new SphereMesh
            {
                Radius = radius,
                Height = radius * 2f,
                RadialSegments = radialSegments,
                Rings = rings,
            },
            MaterialOverride = material,
            TopLevel = true,
        };
        parent.AddChild(node);
        _nodes.Add(node);
        return node;
    }

    private static StandardMaterial3D Material(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 0.78f,
    };

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f ? value.Normalized() : fallback;
}
