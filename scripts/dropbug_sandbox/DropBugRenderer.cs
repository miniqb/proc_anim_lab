using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.DropBug;

namespace ProcAnimLab.DropBugSandbox;

/// <summary>
/// DropBug 的白盒表现层。本轮不做渲染美化——只画看得清物理与几何的调试量：
/// 三节身体（按状态换色）、三条连接线（颜色随静息长度收放）、四条表现腿与足端、
/// 悬挂锚与法线、扑击目标与当前可及距离。
/// </summary>
public sealed class DropBugRenderer
{
    private readonly List<Node3D> _nodes = new();
    private DropBugLocomotionController? _controller;

    private MeshInstance3D? _head;
    private MeshInstance3D? _mid;
    private MeshInstance3D? _tail;
    private readonly List<MeshInstance3D> _feet = new();
    private MeshInstance3D? _anchorMarker;
    private MeshInstance3D? _targetMarker;
    private MeshInstance3D? _lineNode;
    private ImmediateMesh? _lines;

    private StandardMaterial3D _headMaterial = null!;
    private StandardMaterial3D _midMaterial = null!;
    private StandardMaterial3D _tailMaterial = null!;
    private StandardMaterial3D _chargeMaterial = null!;
    private StandardMaterial3D _diveMaterial = null!;
    private StandardMaterial3D _jumpMaterial = null!;
    private StandardMaterial3D _hangMaterial = null!;
    private StandardMaterial3D _footPlanted = null!;
    private StandardMaterial3D _footDangle = null!;
    private StandardMaterial3D _anchorMaterial = null!;
    private StandardMaterial3D _targetMaterial = null!;

    public void Build(Node3D parent, DropBugLocomotionController controller)
    {
        Clear();
        _controller = controller;

        _headMaterial = Opaque(new Color(0.36f, 0.78f, 0.70f));
        _midMaterial = Opaque(new Color(0.45f, 0.50f, 0.55f));
        _tailMaterial = Opaque(new Color(0.32f, 0.36f, 0.42f));
        _chargeMaterial = Opaque(new Color(0.96f, 0.55f, 0.12f));
        _diveMaterial = Opaque(new Color(0.92f, 0.22f, 0.22f));
        _jumpMaterial = Opaque(new Color(0.95f, 0.85f, 0.25f));
        _hangMaterial = Opaque(new Color(0.62f, 0.40f, 0.85f));
        _footPlanted = Opaque(new Color(0.30f, 0.85f, 0.35f));
        _footDangle = Opaque(new Color(0.55f, 0.58f, 0.62f));
        _anchorMaterial = Opaque(new Color(0.25f, 0.85f, 0.95f));
        _targetMaterial = Opaque(new Color(0.95f, 0.25f, 0.35f));

        _head = AddSphere(parent, _headMaterial, controller.Head.Radius);
        _mid = AddSphere(parent, _midMaterial, controller.Mid.Radius);
        _tail = AddSphere(parent, _tailMaterial, controller.Tail.Radius);
        foreach (DropBugLeg _ in controller.Legs)
        {
            _feet.Add(AddSphere(parent, _footDangle, 0.05f));
        }
        _anchorMarker = AddSphere(parent, _anchorMaterial, 0.06f);
        _targetMarker = AddSphere(parent, _targetMaterial, 0.07f);

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
        _feet.Clear();
        _controller = null;
        _head = null;
        _mid = null;
        _tail = null;
        _anchorMarker = null;
        _targetMarker = null;
        _lineNode = null;
        _lines = null;
    }

    public void Draw(float t)
    {
        if (_controller is null || _head is null || _mid is null || _tail is null ||
            _lines is null)
        {
            return;
        }
        DropBugLocomotionController bug = _controller;
        Vector3 head = bug.Head.LerpPos(t);
        Vector3 mid = bug.Mid.LerpPos(t);
        Vector3 tail = bug.Tail.LerpPos(t);

        StandardMaterial3D headState =
            bug.Diving ? _diveMaterial
            : bug.Jumping ? _jumpMaterial
            : bug.ChargingPounce ? _chargeMaterial
            : bug.HangFactor > 0.01f ? _hangMaterial
            : _headMaterial;
        _head.MaterialOverride = headState;
        _mid.MaterialOverride = bug.HangFactor > 0.01f ? _hangMaterial : _midMaterial;
        _tail.MaterialOverride = bug.HangFactor > 0.01f ? _hangMaterial : _tailMaterial;

        _head.GlobalPosition = head;
        _mid.GlobalPosition = mid;
        _tail.GlobalPosition = tail;

        for (int i = 0; i < _feet.Count && i < bug.Legs.Count; i++)
        {
            DropBugLeg leg = bug.Legs[i];
            _feet[i].GlobalPosition = leg.LastPos.Lerp(leg.Pos, t);
            _feet[i].MaterialOverride = leg.Planted ? _footPlanted : _footDangle;
        }

        if (_anchorMarker is not null)
        {
            _anchorMarker.Visible = bug.HangAnchor is not null;
            if (bug.HangAnchor is { } anchor)
            {
                _anchorMarker.GlobalPosition = anchor.Point;
            }
        }
        if (_targetMarker is not null)
        {
            _targetMarker.Visible = bug.AttackTarget is not null;
            if (bug.AttackTarget is { } target)
            {
                _targetMarker.GlobalPosition = target.Point;
            }
        }

        _lines.ClearSurfaces();
        _lines.SurfaceBegin(Mesh.PrimitiveType.Lines);

        // 连接线：颜色按「当前静息长度 / 出生静息长度」——收拢时偏紫，展开为绿。
        float morph = Mathf.Clamp(bug.HangFactor, 0f, 1f);
        Color linkColor = new Color(0.3f, 0.9f, 0.4f).Lerp(new Color(0.7f, 0.4f, 0.95f),
            morph);
        AddLine(head, mid, linkColor);
        AddLine(mid, tail, linkColor);
        AddLine(head, tail, linkColor.Darkened(0.35f));

        // 表现腿：头 → 足端。
        for (int i = 0; i < bug.Legs.Count; i++)
        {
            DropBugLeg leg = bug.Legs[i];
            Vector3 foot = leg.LastPos.Lerp(leg.Pos, t);
            AddLine(head, foot, leg.Planted
                ? new Color(0.35f, 0.8f, 0.4f)
                : new Color(0.5f, 0.53f, 0.58f));
        }

        // 悬挂锚：法线短线 + 到身体的引导线。
        if (bug.HangAnchor is { } a)
        {
            AddLine(a.Point, a.Point + a.Normal * 0.4f, new Color(0.25f, 0.85f, 0.95f));
            AddLine(a.Point, mid, new Color(0.25f, 0.85f, 0.95f, 0.5f));
        }

        // 扑击目标：头 → 可及点（黄）与头 → 目标（超出可及时红）。
        if (bug.AttackTarget is { } tgt)
        {
            Vector3 dir = tgt.Point - head;
            float distance = dir.Length();
            if (distance > 1e-4f)
            {
                dir /= distance;
                float reach = bug.PounceReach(dir);
                AddLine(head, head + dir * Mathf.Min(reach, distance),
                    new Color(0.95f, 0.85f, 0.2f));
                if (distance > reach)
                {
                    AddLine(head + dir * reach, tgt.Point, new Color(0.9f, 0.25f, 0.25f));
                }
            }
        }

        // travelDir 箭头。
        if (bug.TravelDir.LengthSquared() > 1e-6f)
        {
            AddLine(mid, mid + bug.TravelDir.Normalized() * 0.5f,
                new Color(0.9f, 0.9f, 0.9f));
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
