using ProcAnimLab.Sandbox;

namespace ProcAnimLab.Render;

/// <summary>正式渲染器分派：按物种适配器创建对应渲染器；未覆盖的物种返回 null（沙盒回落
/// debug 白盒渲染）。技术验证顺序：Lizard → Centipede → Vulture（渲染研究 §4/§5）。</summary>
internal static class FormalRendererFactory
{
    public static IFormalRenderer? TryCreate(ISandboxCreatureAdapter creature) => creature switch
    {
        LizardSandboxCreatureAdapter lizard =>
            new LizardFormalRenderer(lizard.Controller, lizard.DisplayName),
        CentipedeSandboxCreatureAdapter centipede =>
            new CentipedeFormalRenderer(centipede.Controller, centipede.StableId),
        VultureSandboxCreatureAdapter vulture =>
            new VultureFormalRenderer(vulture.Controller, vulture.Breed.Name,
                vulture.Breed.FeathersPerWing),
        _ => null,
    };
}
