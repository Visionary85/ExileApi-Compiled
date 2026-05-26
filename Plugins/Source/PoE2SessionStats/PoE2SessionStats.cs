using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace PoE2SessionStats;

public class PoE2SessionStats : BaseSettingsPlugin<Settings>
{
    // Session tracking
    private long _sessionStartXp;
    private long _areaStartXp;
    private DateTime _sessionStart;
    private DateTime _areaEnterTime;
    private string _lastAreaId = string.Empty;

    // Gold tracking
    private int _lastInventoryGold;
    private int _sessionGoldGained;

    // Death tracking
    private int _sessionDeaths;
    private int _lastLifeHP;

    // Ping history for mini-graph
    private readonly Queue<int> _pingHistory = new();
    private const int PingHistoryMax = 30;

    // XP lookup table for effective level calculation (levels 71+)
    // PoE2 applies diminishing returns above level 70
    private static readonly Dictionary<int, int> EffectiveLevels = new()
    {
        {71,70},{72,70},{73,71},{74,71},{75,72},{76,72},{77,73},{78,73},
        {79,74},{80,74},{81,75},{82,75},{83,76},{84,76},{85,77},{86,77},
        {87,78},{88,78},{89,79},{90,79},{91,80},{92,80},{93,81},{94,81},
        {95,82},{96,82},{97,83},{98,83},{99,84},{100,84},
    };

    public override bool Initialise()
    {
        _sessionStart = DateTime.UtcNow;
        _sessionStartXp = GetCurrentXp();
        _areaEnterTime = DateTime.UtcNow;
        _lastInventoryGold = GetInventoryGold();
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _areaEnterTime = DateTime.UtcNow;
        _areaStartXp = GetCurrentXp();
        _lastAreaId = area?.Area?.Id ?? string.Empty;
        _lastInventoryGold = GetInventoryGold();
    }

    public override void Tick()
    {
        TrackGold();
        TrackDeaths();
        TrackPing();
    }

    private void TrackGold()
    {
        var currentGold = GetInventoryGold();
        var diff = currentGold - _lastInventoryGold;
        if (diff > 0) _sessionGoldGained += diff;
        _lastInventoryGold = currentGold;
    }

    private void TrackDeaths()
    {
        var player = GameController.Player;
        if (player == null) return;
        var life = player.GetComponent<Life>();
        if (life == null) return;

        // Death detected when HP drops to 0 then recovers
        if (_lastLifeHP > 0 && life.CurHP == 0)
            _sessionDeaths++;
        _lastLifeHP = life.CurHP;
    }

    private void TrackPing()
    {
        var ping = GameController.IngameState.Data.ServerData.Latency;
        _pingHistory.Enqueue(ping);
        if (_pingHistory.Count > PingHistoryMax)
            _pingHistory.Dequeue();
    }

    public override void Render()
    {
        if (!Settings.Enable) return;

        var player = GameController.Player;
        if (player == null) return;

        var playerComp = player.GetComponent<Player>();
        var life = player.GetComponent<Life>();
        if (playerComp == null || life == null) return;

        var windowRect = GameController.Window.GetWindowRectangleTimeCache;
        var centerX = windowRect.Width / 2f + Settings.BarXOffset.Value;
        var topY = windowRect.Y + 4f + Settings.BarYOffset.Value;

        var segments = BuildSegments(playerComp, life);

        // Measure total width
        float padding = 10f;
        float segPadding = 16f;
        float segW = 0;
        foreach (var seg in segments)
            segW += Graphics.MeasureText(seg.Text, Settings.TextScale.Value).X + segPadding;

        float barH = 22f * Settings.TextScale.Value;
        float startX = centerX - segW / 2f;

        // Background
        var bgRect = new RectangleF(startX - padding, topY, segW + padding * 2, barH);
        Graphics.DrawBox(bgRect, Settings.BackgroundColor.Value);

        // Draw each segment
        float cursor = startX;
        foreach (var seg in segments)
        {
            var textSize = Graphics.MeasureText(seg.Text, Settings.TextScale.Value);
            Graphics.DrawText(seg.Text,
                new Vector2(cursor, topY + (barH - textSize.Y) / 2f),
                seg.Color, Settings.TextScale.Value);
            cursor += textSize.X + segPadding;
        }
    }

    private List<(string Text, Color Color)> BuildSegments(Player playerComp, Life life)
    {
        var segments = new List<(string, Color)>();
        var now = DateTime.UtcNow;

        // Level
        if (Settings.ShowPlayerLevel)
            segments.Add(($"Lv {playerComp.Level}", Settings.HighlightColor.Value));

        // Area name
        if (Settings.ShowAreaName)
        {
            var areaName = GameController.Area.CurrentArea?.Area?.Name ?? "Unknown";
            segments.Add((areaName, Settings.TextColor.Value));
        }

        // Time in area
        if (Settings.ShowAreaTime)
        {
            var elapsed = now - _areaEnterTime;
            segments.Add((FormatTime(elapsed), Settings.TextColor.Value));
        }

        // XP rate
        if (Settings.ShowXpRate && playerComp.Level < 100)
        {
            var currentXp = GetCurrentXp();
            var sessionElapsed = (now - _sessionStart).TotalHours;
            if (sessionElapsed > 0.005)
            {
                var xpGained = currentXp - _sessionStartXp;
                var xpPerHour = xpGained / sessionElapsed;
                segments.Add(($"{ShortenNumber(xpPerHour)} xp/h", Settings.XpColor.Value));

                // Time to level
                if (Settings.ShowTimeToLevel && xpPerHour > 0)
                {
                    var xpToLevel = GetXpToNextLevel(playerComp.Level, currentXp);
                    if (xpToLevel > 0)
                    {
                        var hoursLeft = xpToLevel / xpPerHour;
                        segments.Add(($"~{FormatTime(TimeSpan.FromHours(hoursLeft))}", Settings.XpColor.Value));
                    }
                }
            }
        }

        // Gold
        if (Settings.ShowGold)
        {
            var gold = GetInventoryGold();
            segments.Add(($"{gold:N0}g", Settings.GoldColor.Value));

            if (Settings.ShowGoldRate && _sessionGoldGained > 0)
            {
                var sessionHours = (now - _sessionStart).TotalHours;
                if (sessionHours > 0.005)
                {
                    var goldPerHour = _sessionGoldGained / sessionHours;
                    segments.Add(($"{ShortenNumber(goldPerHour)}/h", Settings.GoldColor.Value));
                }
            }
        }

        // Deaths
        if (Settings.ShowDeathCount && _sessionDeaths > 0)
            segments.Add(($"Deaths: {_sessionDeaths}", Color.FromArgb(220, 220, 80, 80)));

        // Ping
        if (Settings.ShowPing)
        {
            var ping = GameController.IngameState.Data.ServerData.Latency;
            var pingColor = ping < Settings.PingWarnThreshold.Value ? Settings.PingGoodColor.Value
                          : ping < Settings.PingBadThreshold.Value ? Settings.PingWarnColor.Value
                          : Settings.PingBadColor.Value;
            segments.Add(($"{ping}ms", pingColor));
        }

        return segments;
    }

    private long GetCurrentXp()
    {
        try
        {
            return GameController.Player?.GetComponent<Player>()?.XP ?? 0;
        }
        catch { return 0; }
    }

    private int GetInventoryGold()
    {
        try
        {
            return GameController.IngameState.Data.ServerData.GoldAmount;
        }
        catch { return 0; }
    }

    private long GetXpToNextLevel(int level, long currentXp)
    {
        // PoE2 XP thresholds are not yet fully public — placeholder uses PoE1 table structure
        // Replace XpTable entries with PoE2 values when available
        return 0;
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h{t.Minutes:D2}m";
        return $"{t.Minutes}m{t.Seconds:D2}s";
    }

    private static string ShortenNumber(double n)
    {
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000:0.#}B";
        if (n >= 1_000_000)     return $"{n / 1_000_000:0.#}M";
        if (n >= 1_000)         return $"{n / 1_000:0.#}K";
        return $"{n:0}";
    }
}
