using System;
using Godot;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 沙盒生物选择器。列表位置与数字行键位同口径：前四项保留既有蜥蜴，
/// 中四项是蜈蚣实例，后四项是秃鹫。只负责 UI 与回调，不持有任何运动控制器。
/// </summary>
public sealed class CreatureSelectorUI
{
    private OptionButton _dropdown = null!;
    private Label _leadLabel = null!;

    public void Build(Node parent, string[] creatureNames, Action<int> onSelected)
    {
        var layer = new CanvasLayer();
        parent.AddChild(layer);

        var panel = new PanelContainer { Position = new Vector2(12f, 12f) };
        layer.AddChild(panel);

        var column = new VBoxContainer();
        panel.AddChild(column);

        var row = new HBoxContainer();
        column.AddChild(row);
        row.AddChild(new Label { Text = "Creature:" });

        _dropdown = new OptionButton();
        foreach (string name in creatureNames)
        {
            _dropdown.AddItem(name);
        }
        _dropdown.ItemSelected += index => onSelected((int)index);
        row.AddChild(_dropdown);

        _leadLabel = new Label();
        column.AddChild(_leadLabel);
        column.AddChild(new Label
        {
            Text = "1–9,0,-,=: switch creature (humanoids via dropdown)  |  " +
                   "R: swap centipede lead  |  P/C/T: humanoid point/carry/throw  |  F3: debug",
        });
    }

    /// <summary>键盘切换后同步下拉高亮；Select 不会再次发出 ItemSelected。</summary>
    public void SyncSelection(int index)
    {
        if (index >= 0)
        {
            _dropdown.Select(index);
        }
    }

    /// <summary>显示宿主当前请求的蜈蚣领航端；蜥蜴没有这项输入。</summary>
    public void SyncLeadEnd(string? leadEnd)
    {
        _leadLabel.Text = leadEnd is null
            ? "Centipede lead: n/a"
            : $"Centipede lead: {leadEnd} (R to swap)";
    }
}
