using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Centipede;
using ProcAnim.Core.Species.Humanoid;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Species.Vulture;

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
        debugDraw.Draw(camera, Controller.Head.Pos,
            Controller.LastMoveTargetKind, Controller.LastMoveTarget);

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

/// <summary>秃鹫飞行控制器的宿主适配。渲染走翅链专用重载（翅模式配色 + 羽毛线扇），
/// 不经 <see cref="ISandboxAppendageView"/>——翅链是段链不是单点足端，且身体配色需要
/// 飞行/栖息/翻滚三态（bool isSupported 表达不了）。</summary>
internal sealed class VultureSandboxCreatureAdapter : ISandboxCreatureAdapter
{
    public VultureFlightController Controller { get; }
    private readonly VultureBreedParams _breed;

    public string StableId => $"vulture/{_breed.Name}";
    public string DisplayName => _breed.Name;
    public Body Body => Controller.Body;
    public BodyChunk LeadChunk => Controller.FrontSpine;
    public BodyChunk RespawnAnchor => Controller.RearSpine;

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
    public int AppendageCount => Controller.Wings.Count;
    public int GrippingAppendageCount
    {
        get
        {
            int attached = 0;
            foreach (VultureWing wing in Controller.Wings)
            {
                if (wing.Attached)
                {
                    attached++;
                }
            }
            return attached;
        }
    }

    public bool AppendageStateIsFinite
    {
        get
        {
            foreach (VultureWing wing in Controller.Wings)
            {
                foreach (VultureWing.WingSegment seg in wing.Segments)
                {
                    if (!seg.Pos.IsFinite() || !seg.Vel.IsFinite())
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    public VultureSandboxCreatureAdapter(VultureFlightController controller, VultureBreedParams breed)
    {
        Controller = controller;
        _breed = breed;
    }

    public void Tick(in TickContext ctx) => Controller.Tick(ctx);
    public void Shift(Vector3 offset) => Controller.Shift(offset);
    public void Teleport(Vector3 delta) => Controller.Teleport(delta);
    public void Launch(Vector3 impulse) => Controller.Launch(impulse);

    public void BuildRenderer(BodyRenderer renderer, Node3D parent) =>
        renderer.Build(parent, new[] { Controller.Body }, null, null,
            Controller.Wings, Controller, _breed.FeathersPerWing);

    public void DrawDebug(RayDebugDraw debugDraw, Camera3D camera) =>
        debugDraw.Draw(camera, Controller);

    public void FoldDeterministicState(DeterminismHasher hasher) =>
        hasher.FoldWings(Controller.Wings);
}

/// <summary>
/// 人形驱动器的适配：SandboxWorld 的统一表面（微扰 chunk、重生锚、StableId、击飞）走这里；
/// 输入采样、脚本化路线、指标与终态判定仍由 <see cref="HumanoidSandboxDriver"/> 在
/// 物种专属早退分支里独立承担（探针折叠序 chunks → legs → arms 也由该分支给定——
/// 本适配器的 FoldDeterministicState 保持同序，供统一路径复用）。
/// </summary>
internal sealed class HumanoidSandboxCreatureAdapter : ISandboxCreatureAdapter
{
    private readonly HumanoidSandboxDriver _driver;

    public HumanoidLocomotionController Controller => _driver.Controller;

    public string StableId => $"humanoid/{_driver.Breed.Name}";
    public string DisplayName => _driver.Breed.Name;
    public Body Body => Controller.Body;
    public BodyChunk LeadChunk => Controller.Chest;
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
    public int AppendageCount => Controller.Legs.Count + Controller.Arms.Count;
    public int GrippingAppendageCount => Controller.LegsGripping;

    public bool AppendageStateIsFinite
    {
        get
        {
            foreach (Limb leg in Controller.Legs)
            {
                if (!leg.Pos.IsFinite() || !leg.Vel.IsFinite())
                {
                    return false;
                }
            }
            foreach (Arm arm in Controller.Arms)
            {
                if (!arm.Pos.IsFinite() || !arm.Vel.IsFinite())
                {
                    return false;
                }
            }
            return true;
        }
    }

    public HumanoidSandboxCreatureAdapter(HumanoidSandboxDriver driver)
    {
        _driver = driver;
    }

    public void Tick(in TickContext ctx) => Controller.Tick(ctx);
    public void Shift(Vector3 offset) => Controller.Shift(offset);
    public void Teleport(Vector3 delta) => Controller.Teleport(delta);
    public void Launch(Vector3 impulse) => Controller.Launch(impulse);

    /// <summary>人形渲染由专用 <see cref="HumanoidRenderer"/> 承担（臂模式配色、撑点可视化），
    /// BodyRenderer 参数仅为满足统一接口——这里整体重建专用渲染而不注册任何 BodyRenderer 几何。</summary>
    public void BuildRenderer(BodyRenderer renderer, Node3D parent)
    {
        _driver.Renderer.Clear();
        _driver.Renderer.Build(parent, Controller);
    }

    public void DrawDebug(RayDebugDraw debugDraw, Camera3D camera) =>
        debugDraw.Draw(camera);

    public void FoldDeterministicState(DeterminismHasher hasher)
    {
        hasher.FoldLimbs(Controller.Legs);
        hasher.FoldArms(Controller.Arms);
    }
}
