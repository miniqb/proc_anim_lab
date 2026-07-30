using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

internal enum SandboxAppendagePhase
{
    Swing,
    Reach,
    Grip,
}

/// <summary>渲染层对脚/附肢末端的只读视图，避免把两种核心腿类型揉成共同基类。</summary>
internal interface ISandboxAppendageView
{
    Vector3 Pos { get; }
    Vector3 LastPos { get; }
    Vector3 AnchorPos { get; }
    Vector3 AnchorLastPos { get; }
    float Radius { get; }
    SandboxAppendagePhase Phase { get; }
}

/// <summary>
/// 沙盒宿主真正需要的最小运动契约。它只存在于 Godot 白盒层：
/// 核心中的蜥蜴和蜈蚣仍是并列的物种专属控制器。
/// </summary>
internal interface ISandboxCreatureAdapter
{
    string StableId { get; }
    string DisplayName { get; }
    Body Body { get; }
    BodyChunk LeadChunk { get; }
    BodyChunk RespawnAnchor { get; }

    Vector3 MoveDir { get; set; }
    float RunSpeed { get; set; }
    Vector3? MoveTarget { get; set; }
    bool AtMoveTarget { get; }
    int AppendageCount { get; }
    int GrippingAppendageCount { get; }
    bool AppendageStateIsFinite { get; }

    void Tick(in TickContext ctx);
    void Shift(Vector3 offset);
    void Teleport(Vector3 delta);
    void Launch(Vector3 impulse);
    void BuildRenderer(BodyRenderer renderer, Node3D parent);
    void DrawDebug(RayDebugDraw debugDraw, Camera3D camera);
    void FoldDeterministicState(DeterminismHasher hasher);
}

/// <summary>既有蜥蜴控制器的零算法适配；所有写入仍直接落到原控制器字段。</summary>
internal sealed class LizardSandboxCreatureAdapter : ISandboxCreatureAdapter
{
    public LizardLocomotionController Controller { get; }
    private readonly string _name;

    public string StableId => $"lizard/{_name}";
    public string DisplayName => _name;
    public Body Body => Controller.Body;
    public BodyChunk LeadChunk => Controller.Head;
    public BodyChunk RespawnAnchor => Controller.Hips;

    public Vector3 MoveDir
    {
        get => Controller.MoveDir;
        set => Controller.MoveDir = value;
    }

    public float RunSpeed
    {
        get => Controller.RunSpeed;
        set => Controller.RunSpeed = value;
    }

    public Vector3? MoveTarget
    {
        get => Controller.MoveTarget;
        set => Controller.MoveTarget = value;
    }

    public bool AtMoveTarget => Controller.AtMoveTarget;
    public int AppendageCount => Controller.Limbs.Count;
    public int GrippingAppendageCount => Controller.LegsGripping;
    public bool AppendageStateIsFinite
    {
        get
        {
            foreach (Limb limb in Controller.Limbs)
            {
                if (!limb.Pos.IsFinite() || !limb.Vel.IsFinite())
                {
                    return false;
                }
            }
            return true;
        }
    }

    public LizardSandboxCreatureAdapter(LizardLocomotionController controller, string name)
    {
        Controller = controller;
        _name = name;
    }

    public void Tick(in TickContext ctx) => Controller.Tick(ctx);
    public void Shift(Vector3 offset) => Controller.Shift(offset);
    public void Teleport(Vector3 delta) => Controller.Teleport(delta);
    public void Launch(Vector3 impulse) => Controller.Launch(impulse);

    public void BuildRenderer(BodyRenderer renderer, Node3D parent) =>
        renderer.Build(parent, new[] { Controller.Body }, Controller.Limbs, Controller);

    public void DrawDebug(RayDebugDraw debugDraw, Camera3D camera) =>
        debugDraw.Draw(camera, Controller);

    public void FoldDeterministicState(DeterminismHasher hasher) =>
        hasher.FoldLimbs(Controller.Limbs);
}

/// <summary>蜈蚣核心腿的沙盒只读渲染视图。</summary>
internal sealed class CentipedeLegView : ISandboxAppendageView
{
    private readonly CentipedeLeg _leg;

    public Vector3 Pos => _leg.Pos;
    public Vector3 LastPos => _leg.LastPos;
    public Vector3 AnchorPos => _leg.Anchor.Chunk.Pos;
    public Vector3 AnchorLastPos => _leg.Anchor.Chunk.LastPos;
    public float Radius => _leg.Radius;
    public SandboxAppendagePhase Phase => _leg.Gripping
        ? SandboxAppendagePhase.Grip
        : _leg.IsSwinging ? SandboxAppendagePhase.Swing : SandboxAppendagePhase.Reach;

    public CentipedeLegView(CentipedeLeg leg)
    {
        _leg = leg;
    }
}

/// <summary>全新蜈蚣控制器的宿主适配；不改变其表面轨迹、行波或双端领航算法。</summary>
internal sealed class CentipedeSandboxCreatureAdapter : ISandboxCreatureAdapter
{
    private readonly List<ISandboxAppendageView> _legViews = new();

    public CentipedeLocomotionController Controller { get; }
    public string StableId { get; }
    public string DisplayName => StableId;
    public Body Body => Controller.Body;
    public BodyChunk LeadChunk => Controller.LeadChunk;
    public BodyChunk RespawnAnchor => Controller.LeadChunk;

    public Vector3 MoveDir
    {
        get => Controller.MoveDir;
        set => Controller.MoveDir = value;
    }

    public float RunSpeed
    {
        get => Controller.RunSpeed;
        set => Controller.RunSpeed = value;
    }

    public Vector3? MoveTarget
    {
        get => Controller.MoveTarget;
        set => Controller.MoveTarget = value;
    }

    public bool AtMoveTarget => Controller.AtMoveTarget;
    public int AppendageCount => Controller.Legs.Count;
    public int GrippingAppendageCount
    {
        get
        {
            int gripping = 0;
            foreach (CentipedeLeg leg in Controller.Legs)
            {
                if (leg.Gripping)
                {
                    gripping++;
                }
            }
            return gripping;
        }
    }

    public bool AppendageStateIsFinite => Controller.DeterministicStateIsFinite;

    public CentipedeSandboxCreatureAdapter(CentipedeLocomotionController controller, string stableId)
    {
        Controller = controller;
        StableId = stableId;
        foreach (CentipedeLeg leg in controller.Legs)
        {
            _legViews.Add(new CentipedeLegView(leg));
        }
    }

    public void Tick(in TickContext ctx) => Controller.Tick(ctx);
    public void Shift(Vector3 offset) => Controller.Shift(offset);
    public void Teleport(Vector3 delta) => Controller.Teleport(delta);
    public void Launch(Vector3 impulse) => Controller.Launch(impulse);

    public void BuildRenderer(BodyRenderer renderer, Node3D parent) =>
        renderer.Build(parent, new[] { Controller.Body }, _legViews,
            () => Controller.SupportedSegmentCount > 0);

    public void DrawDebug(RayDebugDraw debugDraw, Camera3D camera) =>
        debugDraw.Draw(camera, Controller);

    public void FoldDeterministicState(DeterminismHasher hasher) =>
        Controller.FoldDeterministicState(hasher);
}
