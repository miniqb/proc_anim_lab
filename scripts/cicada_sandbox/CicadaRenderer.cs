using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.Cicada;

namespace ProcAnimLab.CicadaSandbox;

/// <summary>
/// Cicada 的 Godot 表现层：两个椭球身体、四片半透明翼面、四条单粒子触须。
/// 核心只输出向量和附肢状态；完整 Basis、材质和网格全部留在宿主层。
/// </summary>
public sealed class CicadaRenderer
{
    private readonly List<Node3D> _nodes = new();
    private readonly MeshInstance3D[] _tentacleSegments = new MeshInstance3D[4];
    private readonly MeshInstance3D[] _tentacleTips = new MeshInstance3D[4];

    private CicadaLocomotionController? _controller;
    private float _wingLength;
    private float _tentacleLength;
    private MeshInstance3D? _frontBody;
    private MeshInstance3D? _rearBody;
    private MeshInstance3D? _leftEye;
    private MeshInstance3D? _rightEye;
    private MeshInstance3D? _wingNode;
    private ImmediateMesh? _wingMesh;

    private StandardMaterial3D _bodyMaterial = null!;
    private StandardMaterial3D _accentMaterial = null!;
    private StandardMaterial3D _eyeMaterial = null!;
    private StandardMaterial3D _wingMaterial = null!;
    private StandardMaterial3D _tentacleMaterial = null!;
    private StandardMaterial3D _windupMaterial = null!;
    private StandardMaterial3D _dashMaterial = null!;
    private StandardMaterial3D _stunnedMaterial = null!;
    private Color _wingColor;

    public void Build(Node3D parent, CicadaLocomotionController controller, CicadaParams parameters)
    {
        Clear();
        _controller = controller;
        _wingLength = parameters.WingLength;
        _tentacleLength = parameters.TentacleLength;

        Vector3 bodyRgb = parameters.BodyColor;
        Vector3 accentRgb = parameters.AccentColor;
        Vector3 wingRgb = parameters.WingColor;

        _bodyMaterial = OpaqueMaterial(ToColor(bodyRgb), 0.72f);
        _accentMaterial = OpaqueMaterial(ToColor(accentRgb), 0.55f);
        _eyeMaterial = OpaqueMaterial(new Color(0.015f, 0.02f, 0.025f), 0.25f);
        _tentacleMaterial = OpaqueMaterial(ToColor(accentRgb).Darkened(0.28f), 0.65f);
        _windupMaterial = OpaqueMaterial(new Color(0.95f, 0.48f, 0.12f), 0.38f);
        _dashMaterial = OpaqueMaterial(new Color(1f, 0.88f, 0.3f), 0.22f);
        _stunnedMaterial = OpaqueMaterial(new Color(0.58f, 0.32f, 0.72f), 0.7f);
        _wingColor = new Color(wingRgb.X, wingRgb.Y, wingRgb.Z, 0.45f);
        _wingMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
        };

        _frontBody = AddSphere(parent, _bodyMaterial);
        _rearBody = AddSphere(parent, _accentMaterial);
        _leftEye = AddSphere(parent, _eyeMaterial);
        _rightEye = AddSphere(parent, _eyeMaterial);

        _wingMesh = new ImmediateMesh();
        _wingNode = new MeshInstance3D
        {
            Mesh = _wingMesh,
            MaterialOverride = _wingMaterial,
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(_wingNode);
        _nodes.Add(_wingNode);

        for (int i = 0; i < 4; i++)
        {
            var cylinder = new CylinderMesh
            {
                TopRadius = 0.009f,
                BottomRadius = 0.013f,
                Height = 1f,
                RadialSegments = 6,
                Rings = 1,
            };
            _tentacleSegments[i] = new MeshInstance3D
            {
                Mesh = cylinder,
                MaterialOverride = _tentacleMaterial,
                TopLevel = true,
            };
            parent.AddChild(_tentacleSegments[i]);
            _nodes.Add(_tentacleSegments[i]);

            _tentacleTips[i] = AddSphere(parent, _tentacleMaterial);
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
        Array.Clear(_tentacleSegments);
        Array.Clear(_tentacleTips);
        _controller = null;
        _frontBody = null;
        _rearBody = null;
        _leftEye = null;
        _rightEye = null;
        _wingNode = null;
        _wingMesh = null;
    }

    public void Draw(float t)
    {
        if (_controller is null || _frontBody is null || _rearBody is null
            || _leftEye is null || _rightEye is null || _wingMesh is null)
        {
            return;
        }

        Vector3 front = _controller.Front.LerpPos(t);
        Vector3 rear = _controller.Rear.LerpPos(t);
        GetStableFrame(_controller, front - rear, out Vector3 forward, out Vector3 up, out Vector3 right);
        var frame = new Basis(right, up, -forward).Orthonormalized();

        float frontRadius = _controller.Front.Radius;
        float rearRadius = _controller.Rear.Radius;
        SetEllipsoid(_frontBody, front, frame,
            new Vector3(frontRadius * 1.72f, frontRadius * 1.48f, frontRadius * 2.12f));
        SetEllipsoid(_rearBody, rear, frame,
            new Vector3(rearRadius * 1.88f, rearRadius * 1.55f, rearRadius * 2.18f));

        string charge = _controller.ChargePhase.ToString();
        string mode = _controller.Mode.ToString();
        StandardMaterial3D activeBodyMaterial = mode == "Stunned" ? _stunnedMaterial
            : charge == "Windup" ? _windupMaterial
            : charge == "Dash" ? _dashMaterial
            : _bodyMaterial;
        _frontBody.MaterialOverride = activeBodyMaterial;

        float eyeSide = frontRadius * 0.65f;
        float eyeUp = frontRadius * 0.28f;
        float eyeFront = frontRadius * 0.72f;
        SetEllipsoid(_leftEye, front + forward * eyeFront - right * eyeSide + up * eyeUp,
            frame, Vector3.One * frontRadius * 0.28f);
        SetEllipsoid(_rightEye, front + forward * eyeFront + right * eyeSide + up * eyeUp,
            frame, Vector3.One * frontRadius * 0.28f);

        DrawWings(t, front, rear, forward, up, right);
        DrawTentacles(t, front, rear, forward, up, right);
    }

    private void DrawWings(float t, Vector3 front, Vector3 rear, Vector3 forward, Vector3 up, Vector3 right)
    {
        _wingMesh!.ClearSurfaces();
        _wingMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);

        float deployment = Mathf.Clamp(_controller!.WingDeployment, 0f, 1f);
        float flapPhase = _controller.FlapPhase;
        float configuredSpan = _wingLength;
        IReadOnlyList<CicadaWingState> wingStates = _controller.Wings;
        Vector3 center = front.Lerp(rear, 0.38f);

        for (int i = 0; i < 4; i++)
        {
            bool rightSide = (i & 1) != 0;
            bool hindWing = i >= 2;
            float side = rightSide ? 1f : -1f;
            float longitudinal = hindWing ? -0.12f : 0.08f;
            float chord = hindWing ? 0.22f : 0.28f;
            float span = configuredSpan * (hindWing ? 0.8f : 1f);
            Vector3 root = center + forward * longitudinal + right * side * 0.085f + up * 0.035f;

            float phaseOffset = hindWing ? Mathf.Pi * 0.56f : 0f;
            float flap = Mathf.Sin(flapPhase * Mathf.Tau + phaseOffset) * (0.2f + deployment * 0.32f);
            Vector3 generatedTip = root
                + right * side * span * Mathf.Lerp(0.2f, 1f, deployment)
                - forward * (hindWing ? span * 0.34f : span * 0.08f)
                + up * flap * span;

            if (i < wingStates.Count)
            {
                CicadaWingState wing = wingStates[i];
                root = wing.LastPos.Lerp(wing.Pos, t);
                generatedTip = wing.LastTip.Lerp(wing.Tip, t);
            }

            Vector3 innerLeading = root + forward * chord * 0.42f;
            Vector3 innerTrailing = root - forward * chord * 0.48f;
            Vector3 outerLeading = generatedTip + forward * chord * (hindWing ? 0.16f : 0.32f);
            Vector3 outerTrailing = generatedTip - forward * chord * (hindWing ? 0.54f : 0.42f);
            Color color = hindWing ? _wingColor.Darkened(0.08f) : _wingColor;

            AddTriangle(innerLeading, outerLeading, outerTrailing, color);
            AddTriangle(innerLeading, outerTrailing, innerTrailing, color);
        }
        _wingMesh.SurfaceEnd();
    }

    private void DrawTentacles(float t, Vector3 front, Vector3 rear,
        Vector3 forward, Vector3 up, Vector3 right)
    {
        IReadOnlyList<CicadaTentacleState> states = _controller!.Tentacles;
        float configuredLength = _tentacleLength;

        for (int i = 0; i < 4; i++)
        {
            bool frontPair = i < 2;
            float side = (i & 1) == 0 ? -1f : 1f;
            Vector3 bodyPoint = frontPair ? front : rear;
            Vector3 root = bodyPoint
                + right * side * (frontPair ? 0.085f : 0.07f)
                - up * 0.055f
                + forward * (frontPair ? 0.02f : -0.04f);
            Vector3 tip = root
                - up * configuredLength * (frontPair ? 0.52f : 0.38f)
                - forward * configuredLength * (frontPair ? 0.48f : 0.72f)
                + right * side * configuredLength * 0.22f;

            bool attached = false;
            if (i < states.Count)
            {
                CicadaTentacleState state = states[i];
                root = state.Anchor;
                tip = state.LastPos.Lerp(state.Pos, t);
                attached = state.Attached;
            }

            MeshInstance3D segment = _tentacleSegments[i];
            MeshInstance3D tipNode = _tentacleTips[i];
            segment.MaterialOverride = attached ? _dashMaterial : _tentacleMaterial;
            tipNode.MaterialOverride = attached ? _dashMaterial : _tentacleMaterial;

            Vector3 delta = tip - root;
            float length = delta.Length();
            if (length < 1e-5f)
            {
                segment.Visible = false;
            }
            else
            {
                segment.Visible = true;
                ((CylinderMesh)segment.Mesh).Height = length;
                var rotation = new Basis(new Quaternion(Vector3.Up, delta / length));
                segment.GlobalTransform = new Transform3D(rotation, root.Lerp(tip, 0.5f));
            }
            SetEllipsoid(tipNode, tip, Basis.Identity, Vector3.One * 0.032f);
        }
    }

    private void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        _wingMesh!.SurfaceSetColor(color);
        _wingMesh.SurfaceAddVertex(a);
        _wingMesh.SurfaceSetColor(color);
        _wingMesh.SurfaceAddVertex(b);
        _wingMesh.SurfaceSetColor(color);
        _wingMesh.SurfaceAddVertex(c);
    }

    private MeshInstance3D AddSphere(Node3D parent, Material material)
    {
        var node = new MeshInstance3D
        {
            Mesh = new SphereMesh
            {
                Radius = 0.5f,
                Height = 1f,
                RadialSegments = 16,
                Rings = 8,
            },
            MaterialOverride = material,
            TopLevel = true,
        };
        parent.AddChild(node);
        _nodes.Add(node);
        return node;
    }

    private static StandardMaterial3D OpaqueMaterial(Color color, float roughness) =>
        new()
        {
            AlbedoColor = color,
            Roughness = roughness,
        };

    private static void SetEllipsoid(MeshInstance3D node, Vector3 position, Basis basis, Vector3 dimensions)
    {
        // 椭球尺寸属于身体局部轴；ScaledLocal 才会按 Basis 的列（Right/Up/-Forward）缩放。
        node.GlobalTransform = new Transform3D(basis.ScaledLocal(dimensions), position);
    }

    private static void GetStableFrame(CicadaLocomotionController controller, Vector3 bodyForward,
        out Vector3 forward, out Vector3 up, out Vector3 right)
    {
        forward = bodyForward.LengthSquared() > 1e-10f ? bodyForward.Normalized() : controller.Forward;
        if (forward.LengthSquared() < 1e-10f)
        {
            forward = Vector3.Forward;
        }
        up = controller.Up;
        up -= forward * up.Dot(forward);
        if (up.LengthSquared() < 1e-10f)
        {
            up = Mathf.Abs(forward.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
            up -= forward * up.Dot(forward);
        }
        up = up.Normalized();
        right = forward.Cross(up);
        if (right.LengthSquared() < 1e-10f)
        {
            right = controller.Right;
        }
        right = right.Normalized();
        up = right.Cross(forward).Normalized();
    }

    private static Color ToColor(Vector3 rgb) =>
        new(Mathf.Clamp(rgb.X, 0f, 1f), Mathf.Clamp(rgb.Y, 0f, 1f), Mathf.Clamp(rgb.Z, 0f, 1f));
}
