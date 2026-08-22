using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Species.RatFiend;

namespace ProcAnimLab.RatFiendSandbox;

/// <summary>
/// 鼠煞的白盒表现层（V 键对照实验的「解释前」侧）。只画看得清物理与几何的调试量：
/// 三 chunk（重力/爬行/昏迷换色）、双手（模式换色，断臂暗红）、双足（planted 换色，
/// 断腿残肢暗红）、骨架线、**嘴开度 V 形线**（调 MouthOpen 曲线直读）、抓取目标与直喂目标。
/// </summary>
public sealed class RatFiendRenderer
{
    private readonly List<Node3D> _nodes = new();
    private RatFiendLocomotionController? _controller;
    private bool _visible = true;

    private MeshInstance3D? _chest;
    private MeshInstance3D? _hips;
    private MeshInstance3D? _head;
    private readonly List<MeshInstance3D> _hands = new();
    private readonly List<MeshInstance3D> _feet = new();
    private MeshInstance3D? _grabMarker;
    private MeshInstance3D? _moveMarker;
    private MeshInstance3D? _lineNode;
    private ImmediateMesh? _lines;

    private StandardMaterial3D _bodyWeightless = null!;
    private StandardMaterial3D _bodyFalling = null!;
    private StandardMaterial3D _bodyCrawling = null!;
    private StandardMaterial3D _bodyUnconscious = null!;
    private StandardMaterial3D _headMaterial = null!;
    private StandardMaterial3D _handDangle = null!;
    private StandardMaterial3D _handHunt = null!;
    private StandardMaterial3D _handPlanted = null!;
    private StandardMaterial3D _severedMaterial = null!;
    private StandardMaterial3D _footPlanted = null!;
    private StandardMaterial3D _footSwing = null!;
    private StandardMaterial3D _grabMaterial = null!;
    private StandardMaterial3D _moveMaterial = null!;

    public void Build(Node3D parent, RatFiendLocomotionController controller)
    {
        Clear();
        _controller = controller;

        _bodyWeightless = Opaque(new Color(0.42f, 0.72f, 0.66f));
        _bodyFalling = Opaque(new Color(0.52f, 0.54f, 0.58f));
        _bodyCrawling = Opaque(new Color(0.92f, 0.60f, 0.20f));
        _bodyUnconscious = Opaque(new Color(0.30f, 0.30f, 0.34f));
        _headMaterial = Opaque(new Color(0.72f, 0.68f, 0.62f));
        _handDangle = Opaque(new Color(0.55f, 0.58f, 0.62f));
        _handHunt = Opaque(new Color(0.35f, 0.72f, 0.92f));
        _handPlanted = Opaque(new Color(0.30f, 0.85f, 0.35f));
        _severedMaterial = Opaque(new Color(0.55f, 0.14f, 0.12f));
        _footPlanted = Opaque(new Color(0.30f, 0.85f, 0.35f));
        _footSwing = Opaque(new Color(0.55f, 0.58f, 0.62f));
        _grabMaterial = Opaque(new Color(0.95f, 0.25f, 0.35f));
        _moveMaterial = Opaque(new Color(0.9f, 0.85f, 0.3f));

        _chest = AddSphere(parent, _bodyWeightless, controller.Chest.Radius);
        _hips = AddSphere(parent, _bodyWeightless, controller.Hips.Radius);
        _head = AddSphere(parent, _headMaterial, controller.Head.Radius);
        foreach (RatArm arm in controller.Arms)
        {
            _hands.Add(AddSphere(parent, _handDangle, arm.Radius));
        }
        foreach (Limb leg in controller.Legs)
        {
            _feet.Add(AddSphere(parent, _footSwing, leg.Radius));
        }
        _grabMarker = AddSphere(parent, _grabMaterial, 0.07f);
        _moveMarker = AddSphere(parent, _moveMaterial, 0.06f);

        _lines = new ImmediateMesh();
        _lineNode = new MeshInstance3D
        {
            Mesh = _lines,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(_lineNode);
        _nodes.Add(_lineNode);
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
        _hands.Clear();
        _feet.Clear();
        _controller = null;
        _chest = null;
        _hips = null;
        _head = null;
        _grabMarker = null;
        _moveMarker = null;
        _lineNode = null;
        _lines = null;
    }

    public void SetVisible(bool visible)
    {
        // 记住可见态：Draw 每帧会按业务条件写标记球的 Visible，必须与整体可见态相与，
        // 否则正式视图下标记球漏显（首轮截图实测：攻击目标粉球浮在正式渲染上）。
        _visible = visible;
        foreach (Node3D node in _nodes)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.Visible = visible;
            }
        }
    }

    public void Draw(float t)
    {
        if (_controller is null || _chest is null || _hips is null || _head is null ||
            _lines is null)
        {
            return;
        }
        RatFiendLocomotionController rat = _controller;
        Vector3 chest = rat.Chest.LerpPos(t);
        Vector3 hips = rat.Hips.LerpPos(t);
        Vector3 head = rat.Head.LerpPos(t);

        StandardMaterial3D bodyState =
            !rat.Conscious ? _bodyUnconscious
            : rat.Crawling ? _bodyCrawling
            : rat.ApplyGravity ? _bodyFalling
            : _bodyWeightless;
        _chest.MaterialOverride = bodyState;
        _hips.MaterialOverride = bodyState;
        _chest.GlobalPosition = chest;
        _hips.GlobalPosition = hips;
        _head.GlobalPosition = head;

        _lines.ClearSurfaces();
        _lines.SurfaceBegin(Mesh.PrimitiveType.Lines);

        var boneColor = new Color(0.75f, 0.78f, 0.82f);
        AddLine(chest, hips, boneColor);
        AddLine(chest, head, boneColor);

        for (int i = 0; i < _hands.Count && i < rat.Arms.Count; i++)
        {
            RatArm arm = rat.Arms[i];
            Vector3 hand = arm.LerpPos(t);
            _hands[i].GlobalPosition = hand;
            _hands[i].MaterialOverride =
                arm.Severed ? _severedMaterial
                : arm.Mode == RatArm.ArmMode.Dangle ? _handDangle
                : arm.GrabPos is not null ? _handPlanted
                : _handHunt;
            AddLine(chest, hand, arm.Severed
                ? new Color(0.6f, 0.2f, 0.2f)
                : new Color(0.5f, 0.55f, 0.6f));
        }
        for (int i = 0; i < _feet.Count && i < rat.Legs.Count; i++)
        {
            Limb leg = rat.Legs[i];
            Vector3 foot = leg.LerpPos(t);
            _feet[i].GlobalPosition = foot;
            bool severed = rat.IsSevered(
                i == 0 ? RatFiendLimbId.LegLeft : RatFiendLimbId.LegRight);
            _feet[i].MaterialOverride =
                severed ? _severedMaterial
                : leg.Gripping ? _footPlanted
                : _footSwing;
            AddLine(hips, foot, severed
                ? new Color(0.6f, 0.2f, 0.2f)
                : new Color(0.5f, 0.55f, 0.6f));
        }

        // 嘴开度 V 形线：从头心沿 Facing 张开 MouthOpen×50°（上下颌各半）——
        // 调内核 MouthOpen 曲线时白盒直读，不必等正式渲染件。
        float mouth = Mathf.Lerp(rat.LastMouthOpen, rat.MouthOpen, t);
        float halfAngle = Mathf.DegToRad(mouth * 50f * 0.5f);
        Vector3 up = Vector3.Up;
        Vector3 facing = rat.Facing;
        Vector3 side = facing.Cross(up);
        if (side.LengthSquared() > 1e-8f)
        {
            side = side.Normalized();
            Vector3 upperJaw = facing.Rotated(side, halfAngle) * 0.45f;
            Vector3 lowerJaw = facing.Rotated(side, -halfAngle) * 0.4f;
            var jawColor = new Color(0.95f, 0.5f, 0.5f);
            AddLine(head, head + upperJaw, jawColor);
            AddLine(head, head + lowerJaw, jawColor);
        }

        // Facing 箭头。
        AddLine(chest, chest + facing * 0.5f, new Color(0.9f, 0.9f, 0.9f));

        if (_grabMarker is not null)
        {
            _grabMarker.Visible = _visible && rat.GrabTarget is not null;
            if (rat.GrabTarget is { } grab)
            {
                _grabMarker.GlobalPosition = grab;
            }
        }
        if (_moveMarker is not null)
        {
            _moveMarker.Visible = _visible && rat.MoveTarget is not null;
            if (rat.MoveTarget is { } move)
            {
                _moveMarker.GlobalPosition = move;
            }
        }

        _lines.SurfaceEnd();
    }

    private void AddLine(Vector3 a, Vector3 b, Color color)
    {
        _lines!.SurfaceSetColor(color);
        _lines.SurfaceAddVertex(a);
        _lines.SurfaceSetColor(color);
        _lines.SurfaceAddVertex(b);
    }

    private MeshInstance3D AddSphere(Node3D parent, Material material, float radius)
    {
        var node = new MeshInstance3D
        {
            Mesh = new SphereMesh
            {
                Radius = radius,
                Height = radius * 2f,
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

    private static StandardMaterial3D Opaque(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 0.7f,
    };
}
