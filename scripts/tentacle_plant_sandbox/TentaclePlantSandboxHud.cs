using System;
using Godot;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// TentaclePlant 白盒的轻量 HUD。它只转发宿主选择并显示核心状态，不拥有行为状态。
/// </summary>
public sealed class TentaclePlantSandboxHud
{
    private OptionButton _presetPicker = null!;
    private OptionButton _mountPicker = null!;
    private OptionButton _routePicker = null!;
    private Label _status = null!;

    public void Build(
        Node parent,
        string[] presetNames,
        string[] mountNames,
        string[] routeNames,
        Action<int> onPresetSelected,
        Action<int> onMountSelected,
        Action<int> onRouteSelected)
    {
        var layer = new CanvasLayer();
        parent.AddChild(layer);

        var panel = new PanelContainer
        {
            Position = new Vector2(14f, 14f),
            CustomMinimumSize = new Vector2(390f, 0f),
        };
        layer.AddChild(panel);

        var column = new VBoxContainer();
        panel.AddChild(column);
        column.AddChild(new Label
        {
            Text = "TENTACLE PLANT AMBUSH LAB",
            ThemeTypeVariation = "HeaderSmall",
        });

        _presetPicker = AddPicker(column, "Preset:", presetNames, onPresetSelected);
        _mountPicker = AddPicker(column, "Mount:", mountNames, onMountSelected);
        _routePicker = AddPicker(column, "Scenario:", routeNames, onRouteSelected);

        _status = new Label();
        column.AddChild(_status);

        column.AddChild(new HSeparator());
        column.AddChild(new Label
        {
            Text =
                "WASD  move prey across mount plane    E / Q  move out / in\n" +
                "Space  release held prey and reset it\n" +
                "1 / 2 / 3  original / short / hunter\n" +
                "F / G / H  floor / wall / ceiling\n" +
                "4 / 5 / 6 / 7  idle / hit / miss / occluded\n" +
                "Hold RMB  free camera (WASD + E/Q, mouse look)",
        });
    }

    public void Sync(int preset, int mount, int route)
    {
        Select(_presetPicker, preset);
        Select(_mountPicker, mount);
        Select(_routePicker, route);
    }

    public void UpdateStatus(
        string preset,
        string mount,
        string route,
        string phase,
        float charge,
        float canGrab,
        float extension,
        ulong? heldTarget,
        bool targetVisible,
        bool hostCaptured,
        bool targetConsumed,
        bool captureStarted,
        bool effectHeld,
        bool released,
        bool consumeRequested,
        int guidePoints,
        int backtrackFrom,
        int tickQueries,
        int peakQueries,
        long attackSerial)
    {
        _status.Text =
            $"type  {preset}\n" +
            $"mount  {mount,-7} scenario  {route}\n" +
            $"phase  {phase,-11} attack  {charge,5:P0}  can grab  {canGrab,5:P0}\n" +
            $"extension  {extension,5:P0}  guide bends  {guidePoints}  " +
            $"backtrack  {(backtrackFrom < 0 ? "none" : backtrackFrom.ToString())}\n" +
            $"held  {(heldTarget is null ? "none" : heldTarget.Value.ToString()),-8} " +
            $"attack serial  {attackSerial}\n" +
            $"mock prey  visible:{targetVisible} captured:{hostCaptured} consumed:{targetConsumed}\n" +
            $"effect  capture:{captureStarted} held:{effectHeld} " +
            $"release:{released} consume:{consumeRequested}\n" +
            $"terrain queries  {tickQueries}/tick  peak {peakQueries}";
    }

    private static OptionButton AddPicker(
        VBoxContainer parent,
        string label,
        string[] items,
        Action<int> callback)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        row.AddChild(new Label { Text = label });
        var picker = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        foreach (string item in items)
        {
            picker.AddItem(item);
        }
        picker.ItemSelected += index => callback((int)index);
        row.AddChild(picker);
        return picker;
    }

    private static void Select(OptionButton picker, int index)
    {
        if (index >= 0 && index < picker.ItemCount)
        {
            picker.Select(index);
        }
    }
}
