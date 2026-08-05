using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DaddyLongLegs;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// DaddyLongLegs 白盒表现层：可变数量的重叠身体球、每条触手的完整段链、
/// 逐段地形接触、职责分色、落点与汇总支撑法线。所有节点都是宿主资源，
/// 只读核心纯值状态，不向控制器回写任何物理量。
/// </summary>
public sealed class DaddyLongLegsRenderer
{
    private sealed class TentacleVisual
    {
        public required DaddyTentacle Tentacle;
        public readonly List<MeshInstance3D> SegmentNodes = new();
        public required MeshInstance3D LandingMarker;
        public required MeshInstance3D IdealLandingMarker;
        public required MeshInstance3D ExternalTargetMarker;
    }

    private readonly List<Node3D> _nodes = new();
    private readonly List<(BodyChunk Chunk, MeshInstance3D Node)> _bodyNodes = new();
    private readonly List<ChunkConnection> _bodyConnections = new();
    private readonly List<TentacleVisual> _tentacleVisuals = new();

    private DaddyLongLegsLocomotionController? _controller;
    private ImmediateMesh? _lineMesh;

    private StandardMaterial3D _bodyMaterial = null!;
    private StandardMaterial3D _passiveContactMaterial = null!;
    private StandardMaterial3D _idleMaterial = null!;
    private StandardMaterial3D _idleContactMaterial = null!;
    private StandardMaterial3D _locomotionMaterial = null!;
    private StandardMaterial3D _locomotionContactMaterial = null!;
    private StandardMaterial3D _externalMaterial = null!;
    private StandardMaterial3D _externalContactMaterial = null!;
    private StandardMaterial3D _stunnedMaterial = null!;
    private StandardMaterial3D _stunnedContactMaterial = null!;
    private StandardMaterial3D _landingMaterial = null!;
    private StandardMaterial3D _landingArrivedMaterial = null!;
    private StandardMaterial3D _idealLandingMaterial = null!;

    /// <summary>建立一套与当前出生形态一一对应的宿主可视化节点。</summary>
    public void Build(Node3D parent, DaddyLongLegsLocomotionController controller)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(controller);
        Clear();
        _controller = controller;

        _bodyMaterial = Material(new Color(0.24f, 0.10f, 0.31f));
        _passiveContactMaterial = Material(new Color(0.88f, 0.53f, 0.16f));
        _idleMaterial = Material(new Color(0.36f, 0.44f, 0.55f));
        _idleContactMaterial = ContactMaterial(new Color(0.58f, 0.72f, 0.88f));
        _locomotionMaterial = Material(new Color(0.12f, 0.70f, 0.42f));
        _locomotionContactMaterial = ContactMaterial(new Color(0.35f, 1.00f, 0.58f));
        _externalMaterial = Material(new Color(0.76f, 0.24f, 0.88f));
        _externalContactMaterial = ContactMaterial(new Color(0.96f, 0.48f, 1.00f));
        _stunnedMaterial = Material(new Color(0.72f, 0.17f, 0.10f));
        _stunnedContactMaterial = ContactMaterial(new Color(1.00f, 0.43f, 0.18f));
        _landingMaterial = MarkerMaterial(new Color(1.00f, 0.58f, 0.10f));
        _landingArrivedMaterial = MarkerMaterial(new Color(0.24f, 1.00f, 0.42f));
        _idealLandingMaterial = MarkerMaterial(new Color(0.18f, 0.86f, 1.00f));

        foreach (BodyChunk chunk in controller.Body.Chunks)
        {
            MeshInstance3D bodyNode = AddSphere(
                parent,
                chunk.Radius,
                _bodyMaterial,
                radialSegments: 18,
                rings: 10);
            _bodyNodes.Add((chunk, bodyNode));
        }
        foreach (ChunkConnection connection in controller.Body.Connections)
        {
            if (!connection.SoftOnly)
            {
                _bodyConnections.Add(connection);
            }
        }

        foreach (DaddyTentacle tentacle in controller.Tentacles)
        {
            var visual = new TentacleVisual
            {
                Tentacle = tentacle,
                LandingMarker = AddSphere(
                    parent, 0.085f, _landingMaterial, radialSegments: 10, rings: 5),
                IdealLandingMarker = AddSphere(
                    parent, 0.060f, _idealLandingMaterial, radialSegments: 10, rings: 5),
                ExternalTargetMarker = AddSphere(
                    parent, 0.10f, _externalMaterial, radialSegments: 12, rings: 6),
            };
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
            {
                visual.SegmentNodes.Add(AddSphere(
                    parent,
                    MathF.Max(0.035f, segment.Radius),
                    _idleMaterial,
                    radialSegments: 8,
                    rings: 4));
            }
            _tentacleVisuals.Add(visual);
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

    /// <summary>与 Build 同义，便于宿主在装配语义上明确表达绑定。</summary>
    public void Bind(Node3D parent, DaddyLongLegsLocomotionController controller) =>
        Build(parent, controller);

    /// <summary>整套白盒节点显隐（正式/白盒渲染切换用；隐藏期间宿主不再调用 Render，
    /// 逐帧的 marker Visible 翻转不会与此打架）。</summary>
    public void SetVisible(bool visible)
    {
        foreach (Node3D node in _nodes)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.Visible = visible;
            }
        }
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
        _bodyConnections.Clear();
        _tentacleVisuals.Clear();
        _controller = null;
        _lineMesh = null;
    }

    /// <summary>用引擎插值分数绘制当前核心状态。</summary>
    public void Render(float interpolation)
    {
        if (_controller is null || _lineMesh is null)
        {
            return;
        }

        interpolation = Mathf.Clamp(interpolation, 0f, 1f);
        // 身体越接近连续浮空支撑，越从暗紫转向冷青；只是调试反馈。
        float supported = Mathf.Clamp(_controller.GravityCancellation, 0f, 1f);
        _bodyMaterial.AlbedoColor = ColorLerp(
            new Color(0.24f, 0.10f, 0.31f),
            new Color(0.13f, 0.48f, 0.55f),
            supported);
        foreach ((BodyChunk chunk, MeshInstance3D node) in _bodyNodes)
        {
            node.GlobalPosition = chunk.LerpPos(interpolation);
        }

        foreach (TentacleVisual visual in _tentacleVisuals)
        {
            DaddyTentacle tentacle = visual.Tentacle;
            for (int i = 0; i < visual.SegmentNodes.Count; i++)
            {
                DaddyTentacleSegmentState segment = tentacle.Segments[i];
                MeshInstance3D node = visual.SegmentNodes[i];
                node.GlobalPosition = segment.LerpPos(interpolation);
                node.MaterialOverride = SegmentMaterial(tentacle.Role, segment);
            }

            visual.LandingMarker.Visible = tentacle.HasLandingTarget;
            if (tentacle.HasLandingTarget)
            {
                visual.LandingMarker.GlobalPosition = tentacle.LandingPoint;
                visual.LandingMarker.MaterialOverride = tentacle.AtGrabDestination
                    ? _landingArrivedMaterial
                    : _landingMaterial;
            }

            visual.IdealLandingMarker.Visible =
                tentacle.Task == DaddyTentacleTask.Locomotion
                && tentacle.StunTicks <= 0;
            if (visual.IdealLandingMarker.Visible)
            {
                visual.IdealLandingMarker.GlobalPosition = tentacle.IdealLandingPoint;
            }

            visual.ExternalTargetMarker.Visible = tentacle.ExternalTarget is not null;
            if (tentacle.ExternalTarget is { } target)
            {
                visual.ExternalTargetMarker.GlobalPosition = target.Position;
                float diameterScale = MathF.Max(0.8f, target.Radius / 0.10f);
                visual.ExternalTargetMarker.Scale = Vector3.One * diameterScale;
            }
        }

        DrawLines(interpolation);
    }

    /// <summary>与 Render 同义，与旧沙盒 renderer 的调用风格兼容。</summary>
    public void Draw(float interpolation) => Render(interpolation);

    private void DrawLines(float interpolation)
    {
        DaddyLongLegsLocomotionController controller = _controller!;
        ImmediateMesh mesh = _lineMesh!;
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        SetLineColor(new Color(0.72f, 0.50f, 0.82f, 0.62f));
        foreach (ChunkConnection connection in _bodyConnections)
        {
            AddLine(
                connection.A.LerpPos(interpolation),
                connection.B.LerpPos(interpolation));
        }

        foreach (TentacleVisual visual in _tentacleVisuals)
        {
            DaddyTentacle tentacle = visual.Tentacle;
            Color chainColor = TentacleLineColor(tentacle);
            SetLineColor(chainColor);
            Vector3 previous = tentacle.Anchor.LerpPos(interpolation);
            for (int i = 0; i < tentacle.Segments.Count; i++)
            {
                DaddyTentacleSegmentState segment = tentacle.Segments[i];
                Vector3 current = segment.LerpPos(interpolation);
                // 核心已把真实阻断边设成无张力边；渲染也必须显示为断开，不能继续
                // 画一根穿墙直线，让调试画面看起来仍像被墙后的腿拽住。
                if (i != tentacle.BacktrackFrom)
                    AddLine(previous, current);
                previous = current;

                if (segment.ActiveGrip)
                {
                    Vector3 normal = NormalizeOr(segment.GripNormal, segment.ContactNormal);
                    if (normal.LengthSquared() > 1e-10f)
                    {
                        SetLineColor(new Color(0.78f, 1.00f, 0.20f, 0.96f));
                        AddLine(current, current + normal.Normalized() * 0.20f);
                        SetLineColor(chainColor);
                    }
                }
                else if (segment.TerrainContact
                    && segment.ContactNormal.LengthSquared() > 1e-10f)
                {
                    SetLineColor(new Color(1.00f, 0.57f, 0.16f, 0.78f));
                    AddLine(current, current + segment.ContactNormal.Normalized() * 0.10f);
                    SetLineColor(chainColor);
                }
            }

            if (tentacle.HasLandingTarget && tentacle.BacktrackFrom < 0)
            {
                SetLineColor(tentacle.AtGrabDestination
                    ? new Color(0.24f, 1.00f, 0.42f, 0.82f)
                    : new Color(1.00f, 0.58f, 0.10f, 0.72f));
                AddLine(previous, tentacle.LandingPoint);
            }
            if (tentacle.Task == DaddyTentacleTask.Locomotion
                && tentacle.StunTicks <= 0
                && tentacle.BacktrackFrom < 0)
            {
                SetLineColor(new Color(0.18f, 0.86f, 1.00f, 0.42f));
                AddLine(tentacle.Anchor.LerpPos(interpolation), tentacle.IdealLandingPoint);
            }
            if (tentacle.BacktrackFrom < 0
                && tentacle.ExternalTarget is { } target)
            {
                SetLineColor(new Color(0.96f, 0.48f, 1.00f, 0.84f));
                AddLine(previous, target.Position);
            }
        }

        Vector3 center = InterpolatedBodyCenter(interpolation);
        Vector3 supportNormal = NormalizeOr(controller.SupportNormal, Vector3.Up);
        float supportLength = 0.55f + 1.35f * Mathf.Clamp(controller.EffectiveSupport, 0f, 1.5f);
        SetLineColor(new Color(0.20f, 0.90f, 1.00f, 0.98f));
        AddLine(center, center + supportNormal * supportLength);

        mesh.SurfaceEnd();
    }

    private Vector3 InterpolatedBodyCenter(float interpolation)
    {
        Vector3 weighted = Vector3.Zero;
        float mass = 0f;
        foreach ((BodyChunk chunk, _) in _bodyNodes)
        {
            weighted += chunk.LerpPos(interpolation) * chunk.Mass;
            mass += chunk.Mass;
        }
        return mass > 1e-8f ? weighted / mass : _controller!.BodyCenter;
    }

    private StandardMaterial3D SegmentMaterial(
        DaddyTentacleRole role,
        DaddyTentacleSegmentState segment)
    {
        if (segment.ActiveGrip)
        {
            return ActiveGripMaterial(role);
        }
        return segment.TerrainContact
            ? _passiveContactMaterial
            : RoleMaterial(role);
    }

    private StandardMaterial3D ActiveGripMaterial(DaddyTentacleRole role) => role switch
    {
        DaddyTentacleRole.Locomotion => _locomotionContactMaterial,
        DaddyTentacleRole.ExternalReach => _externalContactMaterial,
        DaddyTentacleRole.Stunned => _stunnedContactMaterial,
        _ => _idleContactMaterial,
    };

    private StandardMaterial3D RoleMaterial(DaddyTentacleRole role) => role switch
    {
        DaddyTentacleRole.Locomotion => _locomotionMaterial,
        DaddyTentacleRole.ExternalReach => _externalMaterial,
        DaddyTentacleRole.Stunned => _stunnedMaterial,
        _ => _idleMaterial,
    };

    private static Color TentacleLineColor(DaddyTentacle tentacle)
    {
        if (tentacle.Role != DaddyTentacleRole.Locomotion)
        {
            return RoleColor(tentacle.Role);
        }
        return tentacle.ReplantPhase switch
        {
            DaddyTentacleReplantPhase.Peeling => new Color(1.00f, 0.52f, 0.12f, 0.94f),
            DaddyTentacleReplantPhase.Reaching => new Color(0.16f, 0.84f, 1.00f, 0.94f),
            _ => RoleColor(DaddyTentacleRole.Locomotion),
        };
    }

    private static Color RoleColor(DaddyTentacleRole role) => role switch
    {
        DaddyTentacleRole.Locomotion => new Color(0.20f, 0.96f, 0.48f, 0.92f),
        DaddyTentacleRole.ExternalReach => new Color(0.94f, 0.38f, 1.00f, 0.92f),
        DaddyTentacleRole.Stunned => new Color(1.00f, 0.30f, 0.14f, 0.88f),
        _ => new Color(0.56f, 0.68f, 0.82f, 0.76f),
    };

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
        Roughness = 0.76f,
    };

    private static StandardMaterial3D ContactMaterial(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 0.48f,
        EmissionEnabled = true,
        Emission = color,
        EmissionEnergyMultiplier = 0.55f,
    };

    private static StandardMaterial3D MarkerMaterial(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 0.42f,
        EmissionEnabled = true,
        Emission = color,
        EmissionEnergyMultiplier = 0.85f,
    };

    private static Color ColorLerp(Color from, Color to, float t) => new(
        Mathf.Lerp(from.R, to.R, t),
        Mathf.Lerp(from.G, to.G, t),
        Mathf.Lerp(from.B, to.B, t),
        Mathf.Lerp(from.A, to.A, t));

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f ? value.Normalized() : fallback;
}
