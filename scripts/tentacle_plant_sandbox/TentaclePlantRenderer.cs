using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.TentaclePlant;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// TentaclePlant 的白盒调试表现层：锥形段链、末端手、猎物和导引线（植物性修饰的
/// 叶片已随"肉质触手怪"改造移除；正式观感由 TentaclePlantFormalRenderer 负责，
/// 本件保留作 V 键切换的调试视图）。绝不使用引擎随机数，也不回写核心状态。
/// </summary>
public sealed class TentaclePlantRenderer
{
    private readonly List<Node3D> _nodes = new();
    private readonly List<MeshInstance3D> _segmentNodes = new();
    private readonly List<MeshInstance3D> _fingerNodes = new();
    private bool _plantVisible = true;

    private TentaclePlantController? _controller;
    private MeshInstance3D? _rootNode;
    private MeshInstance3D? _handNode;
    private MeshInstance3D? _preyNode;
    private MeshInstance3D? _wanderNode;
    private MeshInstance3D? _backtrackNode;
    private ImmediateMesh? _debugMesh;
    private float _handVisualRadius;

    private StandardMaterial3D _stemMaterial = null!;
    private StandardMaterial3D _rootMaterial = null!;
    private StandardMaterial3D _handMaterial = null!;
    private StandardMaterial3D _windupMaterial = null!;
    private StandardMaterial3D _strikeMaterial = null!;
    private StandardMaterial3D _holdingMaterial = null!;
    private StandardMaterial3D _preyMaterial = null!;
    private StandardMaterial3D _preyHeldMaterial = null!;
    private StandardMaterial3D _debugMaterial = null!;

    public void Build(Node3D parent, TentaclePlantController controller)
    {
        Clear();
        _controller = controller;
        _handVisualRadius = controller.Params.HandVisualRadius;

        Color stem;
        Color hand;
        if (controller.Params.Name.EndsWith("/hunter", StringComparison.Ordinal))
        {
            stem = new Color(0.20f, 0.34f, 0.12f);
            hand = new Color(0.74f, 0.21f, 0.12f);
        }
        else if (controller.Params.Name.EndsWith("/short", StringComparison.Ordinal))
        {
            stem = new Color(0.26f, 0.38f, 0.16f);
            hand = new Color(0.64f, 0.52f, 0.18f);
        }
        else
        {
            stem = new Color(0.18f, 0.34f, 0.12f);
            hand = new Color(0.58f, 0.44f, 0.13f);
        }

        _stemMaterial = OpaqueMaterial(stem, 0.86f);
        _rootMaterial = OpaqueMaterial(stem.Darkened(0.38f), 0.94f);
        _handMaterial = OpaqueMaterial(hand, 0.62f);
        _windupMaterial = OpaqueMaterial(new Color(0.95f, 0.58f, 0.12f), 0.42f);
        _strikeMaterial = OpaqueMaterial(new Color(1f, 0.18f, 0.08f), 0.32f);
        _holdingMaterial = OpaqueMaterial(new Color(0.72f, 0.26f, 0.78f), 0.48f);
        _preyMaterial = OpaqueMaterial(new Color(0.15f, 0.70f, 1f), 0.26f, emission: true);
        _preyHeldMaterial = OpaqueMaterial(new Color(1f, 0.34f, 0.78f), 0.24f, emission: true);
        _debugMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
        };

        _rootNode = AddSphere(parent, _rootMaterial, 18, 10);
        _handNode = AddSphere(parent, _handMaterial, 18, 10);
        _preyNode = AddSphere(parent, _preyMaterial, 18, 10);
        _wanderNode = AddSphere(parent, _debugMaterial, 10, 6);
        _backtrackNode = AddSphere(parent, _strikeMaterial, 10, 6);

        IReadOnlyList<TentacleSegmentState> segments = controller.Segments;
        for (int i = 0; i < segments.Count; i++)
        {
            float bottomRadius = VisualStemRadius(
                controller.Params,
                i / (float)segments.Count);
            float topRadius = VisualStemRadius(
                controller.Params,
                (i + 1f) / segments.Count);
            var cylinder = new CylinderMesh
            {
                BottomRadius = bottomRadius,
                TopRadius = topRadius,
                Height = 1f,
                RadialSegments = 8,
                Rings = 1,
            };
            var node = new MeshInstance3D
            {
                Mesh = cylinder,
                MaterialOverride = _stemMaterial,
                TopLevel = true,
            };
            parent.AddChild(node);
            _nodes.Add(node);
            _segmentNodes.Add(node);
        }

        for (int i = 0; i < 4; i++)
        {
            var fingerMesh = new CylinderMesh
            {
                BottomRadius = 0.022f,
                TopRadius = 0.012f,
                Height = 1f,
                RadialSegments = 6,
                Rings = 1,
            };
            var finger = new MeshInstance3D
            {
                Mesh = fingerMesh,
                MaterialOverride = _handMaterial,
                TopLevel = true,
            };
            parent.AddChild(finger);
            _nodes.Add(finger);
            _fingerNodes.Add(finger);
        }

        _debugMesh = new ImmediateMesh();
        var debugNode = new MeshInstance3D
        {
            Mesh = _debugMesh,
            MaterialOverride = _debugMaterial,
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(debugNode);
        _nodes.Add(debugNode);
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
        _segmentNodes.Clear();
        _fingerNodes.Clear();
        _controller = null;
        _rootNode = null;
        _handNode = null;
        _preyNode = null;
        _wanderNode = null;
        _backtrackNode = null;
        _debugMesh = null;
    }

    /// <summary>正式视图下隐藏植物白盒（mock 猎物球与其材质仍由本件负责——它是场景
    /// 道具不是植物）。</summary>
    public void SetPlantVisible(bool visible)
    {
        _plantVisible = visible;
    }

    public void Draw(float interpolation, bool targetVisible, Vector3 targetPosition, float targetRadius)
    {
        if (_controller is null || _rootNode is null || _handNode is null ||
            _preyNode is null || _wanderNode is null || _backtrackNode is null ||
            _debugMesh is null)
        {
            return;
        }

        if (!_plantVisible)
        {
            foreach (MeshInstance3D node in _segmentNodes)
            {
                node.Visible = false;
            }
            foreach (MeshInstance3D node in _fingerNodes)
            {
                node.Visible = false;
            }
            _rootNode.Visible = false;
            _handNode.Visible = false;
            _wanderNode.Visible = false;
            _backtrackNode.Visible = false;
            _debugMesh.ClearSurfaces();
            DrawPrey(targetVisible, targetPosition, targetRadius);
            return;
        }

        TentaclePlantController controller = _controller;
        IReadOnlyList<TentacleSegmentState> segments = controller.Segments;
        Vector3 root = controller.Root.LerpPos(interpolation);
        Vector3 hand = controller.Hand.LerpPos(interpolation);

        SetEllipsoid(_rootNode, root - controller.Outward * 0.025f, Basis.Identity,
            new Vector3(0.34f, 0.20f, 0.34f));

        Vector3 previous = root;
        int count = Math.Min(segments.Count, _segmentNodes.Count);
        for (int i = 0; i < count; i++)
        {
            Vector3 next = segments[i].LerpPos(interpolation);
            SetCylinder(_segmentNodes[i], previous, next);
            previous = next;
        }

        StandardMaterial3D activeHandMaterial = controller.Phase.ToString() switch
        {
            "Windup" => _windupMaterial,
            "Striking" => _strikeMaterial,
            "Recovering" => _windupMaterial,
            "Holding" => _holdingMaterial,
            _ => _handMaterial,
        };
        _handNode.MaterialOverride = activeHandMaterial;
        SetEllipsoid(
            _handNode,
            hand,
            StablePalmBasis(controller),
            new Vector3(
                _handVisualRadius * 1.10f,
                _handVisualRadius * 0.60f,
                _handVisualRadius * 0.90f));
        DrawFingers(controller, hand, activeHandMaterial);
        DrawPrey(targetVisible, targetPosition, targetRadius);

        _wanderNode.Visible = controller.Phase.ToString() is "Wandering" or "Tracking";
        if (_wanderNode.Visible)
        {
            SetEllipsoid(_wanderNode, controller.WanderGoal, Basis.Identity, Vector3.One * 0.055f);
        }

        int backtrack = controller.BacktrackFrom;
        _backtrackNode.Visible = backtrack >= 0 && backtrack < segments.Count;
        if (_backtrackNode.Visible)
        {
            SetEllipsoid(_backtrackNode, segments[backtrack].LerpPos(interpolation),
                Basis.Identity, Vector3.One * 0.075f);
        }

        DrawDebugLines(controller, root, targetVisible, targetPosition);
    }

    private void DrawPrey(bool targetVisible, Vector3 targetPosition, float targetRadius)
    {
        _preyNode!.Visible = targetVisible;
        if (targetVisible)
        {
            _preyNode.MaterialOverride = _controller!.HeldTargetId is null
                ? _preyMaterial
                : _preyHeldMaterial;
            SetEllipsoid(_preyNode, targetPosition, Basis.Identity,
                Vector3.One * Mathf.Max(0.08f, targetRadius));
        }
    }

    private void DrawFingers(
        TentaclePlantController controller,
        Vector3 hand,
        Material material)
    {
        Vector3 tangent = SafeDirection(controller.Tangent, Vector3.Right);
        Vector3 bitangent = SafeDirection(controller.Bitangent, Vector3.Forward);
        Vector3 forward = SafeDirection(controller.Outward, Vector3.Up);
        bool curled = controller.Phase.ToString() == "Holding";
        for (int i = 0; i < _fingerNodes.Count; i++)
        {
            float a = Mathf.Tau * i / _fingerNodes.Count + Mathf.Pi * 0.25f;
            Vector3 radial = tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a);
            Vector3 tip = hand
                + radial * _handVisualRadius * (curled ? 0.45f : 0.90f)
                + forward * _handVisualRadius * (curled ? -0.175f : 0.35f);
            MeshInstance3D finger = _fingerNodes[i];
            finger.MaterialOverride = material;
            SetCylinder(finger, hand + radial * (_handVisualRadius * 0.175f), tip);
        }
    }

    private static float VisualStemRadius(TentaclePlantParams parameters, float t)
    {
        float tapered = Mathf.Lerp(parameters.RootRadius, parameters.TipRadius, t);
        float handPhase = Mathf.InverseLerp(0.70f, 1f, t);
        float handBulge = Mathf.Sin(Mathf.Pi * handPhase);
        float bulb = Mathf.Lerp(parameters.TipRadius, parameters.HandVisualRadius, handBulge);
        return Mathf.Max(0.012f, Mathf.Max(tapered, bulb));
    }

    private void DrawDebugLines(
        TentaclePlantController controller,
        Vector3 root,
        bool targetVisible,
        Vector3 targetPosition)
    {
        _debugMesh!.ClearSurfaces();
        bool showWander =
            controller.Phase is TentaclePlantPhase.Wandering or TentaclePlantPhase.Tracking;
        IReadOnlyList<Vector3> guide = controller.GuidePoints;
        if (!showWander && guide.Count == 0 && !targetVisible)
        {
            return;
        }
        _debugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        if (showWander)
        {
            AddLine(root, controller.WanderGoal,
                new Color(0.35f, 0.85f, 0.30f, 0.70f));
        }
        if (guide.Count > 0)
        {
            Vector3 previous = root;
            Color color = controller.BacktrackFrom >= 0
                ? new Color(1f, 0.25f, 0.12f, 0.92f)
                : new Color(0.98f, 0.78f, 0.18f, 0.82f);
            for (int i = 0; i < guide.Count; i++)
            {
                AddLine(previous, guide[i], color);
                previous = guide[i];
            }
            Vector3 destination = targetVisible ? targetPosition : controller.WanderGoal;
            AddLine(previous, destination, color);
        }
        if (targetVisible)
        {
            AddLine(controller.Hand.Pos, targetPosition,
                controller.HeldTargetId is null
                    ? new Color(0.20f, 0.72f, 1f, 0.65f)
                    : new Color(1f, 0.30f, 0.78f, 0.95f));
        }
        _debugMesh.SurfaceEnd();
    }

    private void AddLine(Vector3 a, Vector3 b, Color color)
    {
        _debugMesh!.SurfaceSetColor(color);
        _debugMesh.SurfaceAddVertex(a);
        _debugMesh.SurfaceSetColor(color);
        _debugMesh.SurfaceAddVertex(b);
    }

    private MeshInstance3D AddSphere(
        Node3D parent,
        Material material,
        int radialSegments,
        int rings)
    {
        var node = new MeshInstance3D
        {
            Mesh = new SphereMesh
            {
                Radius = 0.5f,
                Height = 1f,
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

    private static StandardMaterial3D OpaqueMaterial(
        Color color,
        float roughness,
        bool emission = false)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = roughness,
        };
        if (emission)
        {
            material.EmissionEnabled = true;
            material.Emission = color;
            material.EmissionEnergyMultiplier = 1.4f;
        }
        return material;
    }

    private static void SetCylinder(MeshInstance3D node, Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float length = delta.Length();
        if (length <= 1e-6f)
        {
            node.Visible = false;
            return;
        }
        node.Visible = true;
        ((CylinderMesh)node.Mesh).Height = length;
        var rotation = new Basis(new Quaternion(Vector3.Up, delta / length));
        node.GlobalTransform = new Transform3D(rotation, from.Lerp(to, 0.5f));
    }

    private static void SetEllipsoid(
        MeshInstance3D node,
        Vector3 position,
        Basis basis,
        Vector3 dimensions)
    {
        node.Visible = true;
        node.GlobalTransform = new Transform3D(basis.ScaledLocal(dimensions), position);
    }

    private static Basis StablePalmBasis(TentaclePlantController controller)
    {
        Vector3 y = SafeDirection(controller.Outward, Vector3.Up);
        Vector3 x = controller.Tangent - y * controller.Tangent.Dot(y);
        x = SafeDirection(x, Mathf.Abs(y.Dot(Vector3.Right)) < 0.9f
            ? Vector3.Right
            : Vector3.Forward);
        Vector3 z = SafeDirection(x.Cross(y), controller.Bitangent);
        x = SafeDirection(y.Cross(z), x);
        return new Basis(x, y, z);
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Up;
    }
}
