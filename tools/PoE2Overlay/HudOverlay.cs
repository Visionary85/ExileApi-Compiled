using System;
using System.Collections.Generic;
using System.Linq;
using ClickableTransparentOverlay;
using ImGuiNET;
using System.Numerics;

namespace PoE2Overlay;

/// <summary>
/// Transparent always-on-top overlay that renders quest guide steps
/// for the player's current PoE2 zone.
/// </summary>
sealed class HudOverlay : Overlay
{
    readonly LogWatcher _watcher;

    volatile string _zone    = "Waiting for PoE2...";
    volatile int    _act     = 0;
    volatile int    _level   = 0;

    // Settings (user-adjustable via in-overlay Settings panel)
    Vector2 _windowPos  = new(10, 10);
    float   _bgAlpha    = 0.80f;
    float   _textScale  = 1.0f;
    bool    _showSettings = false;
    bool    _showOptional = true;
    bool    _pinWindow  = false;

    static readonly Vector4[] ActColors =
    {
        new(0.7f, 0.7f, 0.7f, 1f),   // 0  unknown
        new(0.55f, 0.95f, 0.45f, 1f), // 1  green
        new(1.00f, 0.80f, 0.30f, 1f), // 2  amber
        new(0.40f, 0.80f, 1.00f, 1f), // 3  sky blue
        new(0.95f, 0.45f, 0.45f, 1f), // 4  red
    };

    static readonly Dictionary<StepType, (string Label, Vector4 Color)> StepStyle = new()
    {
        [StepType.Kill]     = ("[KILL]",  new Vector4(1.00f, 0.35f, 0.35f, 1f)),
        [StepType.Talk]     = ("[TALK]",  new Vector4(0.50f, 0.90f, 1.00f, 1f)),
        [StepType.Waypoint] = ("[ WP ]",  new Vector4(0.30f, 1.00f, 0.55f, 1f)),
        [StepType.Interact] = ("[USE ]",  new Vector4(1.00f, 0.90f, 0.35f, 1f)),
        [StepType.Pickup]   = ("[GET ]",  new Vector4(1.00f, 0.70f, 0.20f, 1f)),
        [StepType.Portal]   = ("[ >> ]",  new Vector4(0.75f, 0.50f, 1.00f, 1f)),
        [StepType.Note]     = ("[ i  ]",  new Vector4(0.65f, 0.65f, 0.65f, 1f)),
        [StepType.Move]     = ("[ -> ]",  new Vector4(0.85f, 0.85f, 0.85f, 1f)),
    };

    public HudOverlay(LogWatcher watcher) : base("PoE2 Guide")
    {
        _watcher = watcher;
        _watcher.ZoneChanged  += (zone, act) => { _zone = zone; _act = act; };
        _watcher.LevelChanged += level => _level = level;
    }

    protected override void Render()
    {
        RenderGuidePanel();
        if (_showSettings) RenderSettingsPanel();
    }

    void RenderGuidePanel()
    {
        var steps = QuestData.GetSteps(_zone);
        var visible = _showOptional
            ? steps
            : steps.Where(s => !s.IsOptional).ToList();

        var flags = ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoCollapse
                  | ImGuiWindowFlags.AlwaysAutoResize;
        if (_pinWindow) flags |= ImGuiWindowFlags.NoMove;

        ImGui.SetNextWindowPos(_windowPos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(_bgAlpha);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(4, 3));

        bool open = true;
        ImGui.Begin("PoE2 Guide", ref open, flags);
        ImGui.SetWindowFontScale(_textScale);

        // ── Zone header ────────────────────────────────────────────────────
        var actIdx = Math.Clamp(_act, 0, ActColors.Length - 1);
        var actCol = ActColors[actIdx];

        if (_act > 0)
        {
            ImGui.TextColored(actCol, $"Act {_act}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 1, 1, 0.9f), $" |  {_zone}");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), _zone);
        }

        if (_level > 0)
        {
            ImGui.SameLine(0, 16);
            ImGui.TextDisabled($"Lv {_level}");
        }

        // Settings gear icon on same line, right-aligned
        ImGui.SameLine(ImGui.GetWindowWidth() - 30);
        if (ImGui.SmallButton(_showSettings ? " X " : " = "))
            _showSettings = !_showSettings;

        ImGui.Separator();

        // ── Step list ──────────────────────────────────────────────────────
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("  No guide data for this zone.");
        }
        else
        {
            foreach (var step in visible)
            {
                var (label, color) = StepStyle[step.Type];

                ImGui.TextColored(color, label);
                ImGui.SameLine(0, 6);

                if (step.IsOptional)
                    ImGui.TextDisabled(step.Text);
                else
                    ImGui.TextWrapped(step.Text);
            }
        }

        ImGui.SetWindowFontScale(1.0f); // reset before PopStyleVar
        ImGui.PopStyleVar(2);
        ImGui.End();

        if (!open) Environment.Exit(0);
    }

    void RenderSettingsPanel()
    {
        ImGui.SetNextWindowPos(new Vector2(_windowPos.X + 410, _windowPos.Y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(260, 180), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.90f);

        bool settingsOpen = true;
        ImGui.Begin("PoE2 Guide — Settings", ref settingsOpen, ImGuiWindowFlags.NoResize);

        ImGui.SliderFloat("Opacity",    ref _bgAlpha,    0.2f, 1.0f);
        ImGui.SliderFloat("Text scale", ref _textScale,  0.7f, 2.0f);
        ImGui.Checkbox("Show optional steps", ref _showOptional);
        ImGui.Checkbox("Pin window (no drag)", ref _pinWindow);

        ImGui.Separator();
        ImGui.TextDisabled($"Log: {_watcher.LogPath ?? "not found"}");

        ImGui.End();

        if (!settingsOpen) _showSettings = false;
    }

}
