using System;
using Godot;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 品种切换下拉面板（数字键 1~9 换品种的 UI 版本——品种表以后长过 9 个也不受键位上限约束）。
/// 纯 UI 外壳：只负责显示品种表 + 把选中项回调出去；装配/重生逻辑仍在 SandboxWorld.SelectBreed。
/// </summary>
public sealed class BreedSelectorUI
{
    private OptionButton _dropdown = null!;

    public void Build(Node parent, string[] breedNames, Action<int> onSelected)
    {
        var layer = new CanvasLayer();
        parent.AddChild(layer);

        var panel = new PanelContainer { Position = new Vector2(12f, 12f) };
        layer.AddChild(panel);

        var row = new HBoxContainer();
        panel.AddChild(row);

        row.AddChild(new Label { Text = "Breed:" });

        _dropdown = new OptionButton();
        foreach (string name in breedNames)
        {
            _dropdown.AddItem(name);
        }
        // 信号传的是列表位置（position），与 SandboxWorld 品种数组下标同口径。
        _dropdown.ItemSelected += index => onSelected((int)index);
        row.AddChild(_dropdown);
    }

    /// <summary>外部（键盘切换）改品种后同步下拉高亮。Select() 不触发 ItemSelected，不会递归回调。</summary>
    public void SyncSelection(int index)
    {
        if (index >= 0)
        {
            _dropdown.Select(index);
        }
    }
}
