using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace PoE2Radar;

public enum EntityCategory
{
    Ignored,
    Monster,
    Chest,
    AreaTransition,
    NPC,
    Breach,
    Ritual,
}

public class TrackedEntity
{
    public Entity Entity { get; }
    public EntityCategory Category { get; }

    public TrackedEntity(Entity entity, EntityCategory category)
    {
        Entity = entity;
        Category = category;
    }
}

public class PoE2Radar : BaseSettingsPlugin<Settings>
{
    private readonly ConcurrentDictionary<uint, TrackedEntity> _tracked = new();

    // Metadata prefixes for encounter detection
    private const string BreachPrefix = "Metadata/MiscellaneousObjects/Breach/BreachObject";
    private const string RitualPrefix = "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable";

    public override bool Initialise()
    {
        GameController.EntityListWrapper.EntityAdded += OnEntityAdded;
        GameController.EntityListWrapper.EntityRemoved += OnEntityRemoved;
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _tracked.Clear();
    }

    public override void Dispose()
    {
        GameController.EntityListWrapper.EntityAdded -= OnEntityAdded;
        GameController.EntityListWrapper.EntityRemoved -= OnEntityRemoved;
    }

    private void OnEntityAdded(Entity entity)
    {
        var category = Categorize(entity);
        if (category == EntityCategory.Ignored) return;
        _tracked[entity.Id] = new TrackedEntity(entity, category);
    }

    private void OnEntityRemoved(Entity entity)
    {
        _tracked.TryRemove(entity.Id, out _);
    }

    public override void Render()
    {
        if (!Settings.Enable) return;

        var smallMap = GameController.IngameState.IngameUi.Map.SmallMinimap;
        var largeMap = GameController.IngameState.IngameUi.Map.LargeMap;
        bool minimapVisible = smallMap?.IsVisible ?? false;
        bool largeMapVisible = largeMap?.IsVisible ?? false;

        if (!minimapVisible && !largeMapVisible && !Settings.ShowHealthBars) return;

        var playerGridPos = GameController.Player?.GridPos ?? Vector2.Zero;

        foreach (var kvp in _tracked)
        {
            var tracked = kvp.Value;
            if (tracked.Entity == null || !tracked.Entity.IsValid) continue;

            // Skip dead monsters
            if (tracked.Category == EntityCategory.Monster && !tracked.Entity.IsAlive) continue;

            // Skip opened chests
            if (tracked.Category == EntityCategory.Chest && Settings.ShowOnlyUnopenedChests)
            {
                var blockage = tracked.Entity.GetComponent<TriggerableBlockage>();
                if (blockage?.IsOpened == true) continue;
            }

            // Range filter
            if (Settings.UseRangeFilter)
            {
                var dist = Vector2.Distance(playerGridPos, tracked.Entity.GridPos);
                if (dist > Settings.MaxRange.Value) continue;
            }

            if (minimapVisible && Settings.DrawOnMinimap)
                DrawOnMap(smallMap, playerGridPos, tracked, false);

            if (largeMapVisible && Settings.DrawOnLargeMap)
                DrawOnMap(largeMap, playerGridPos, tracked, true);

            if (tracked.Category == EntityCategory.Monster && Settings.ShowHealthBars)
                DrawHealthBar(tracked);
        }
    }

    private void DrawOnMap(Element mapElement, Vector2 playerGridPos, TrackedEntity tracked, bool isLargeMap)
    {
        if (mapElement == null) return;

        var mapRect = mapElement.GetClientRectCache;
        var mapCenter = new Vector2(mapRect.Center.X, mapRect.Center.Y);
        var zoom = mapElement.Zoom;

        var diff = tracked.Entity.GridPos - playerGridPos;
        var drawPos = mapCenter + diff * zoom;

        // Clip to minimap circle for small map
        if (!isLargeMap)
        {
            var radius = Math.Min(mapRect.Width, mapRect.Height) / 2f;
            if (Vector2.Distance(drawPos, mapCenter) > radius) return;
        }

        var (color, dotSize) = GetRenderSettings(tracked);

        Graphics.DrawFilledCircle(drawPos, dotSize, color);

        // Labels: transitions always, rare+ monsters on large map only
        if (!isLargeMap) return;
        if (!Settings.ShowLabelsOnLargeMap) return;

        bool showLabel = tracked.Category == EntityCategory.AreaTransition && Settings.ShowTransitionName;
        showLabel |= tracked.Category == EntityCategory.Monster &&
                     tracked.Entity.Rarity >= MonsterRarity.Rare;

        if (showLabel)
        {
            var label = tracked.Entity.RenderName
                ?? tracked.Entity.Path?.Split('/').LastOrDefault()
                ?? string.Empty;
            if (!string.IsNullOrEmpty(label))
                Graphics.DrawText(label, drawPos + new Vector2(dotSize + 2, -5), color,
                    Settings.LargeMapTextScale.Value);
        }
    }

    private void DrawHealthBar(TrackedEntity tracked)
    {
        var entity = tracked.Entity;
        if (!entity.IsAlive || !entity.IsHostile) return;
        if (Settings.HealthBarsOnlyRarePlus && entity.Rarity < MonsterRarity.Rare) return;

        var life = entity.GetComponent<Life>();
        if (life == null || life.MaxHP <= 0) return;

        var screenPos = GameController.IngameState.Camera.WorldToScreen(entity.Pos);
        if (screenPos == Vector2.Zero) return;

        float barW = Settings.HealthBarWidth.Value;
        float barH = 6f;
        float yOff = Settings.HealthBarYOffset.Value;

        float hpFrac = Math.Clamp((float)life.CurHP / life.MaxHP, 0f, 1f);

        var bg = new RectangleF(screenPos.X - barW / 2, screenPos.Y - yOff, barW, barH);
        var hp = new RectangleF(bg.X, bg.Y, barW * hpFrac, barH);

        Graphics.DrawBox(bg, Color.FromArgb(160, 0, 0, 0));

        var hpColor = hpFrac > 0.5f ? Color.FromArgb(200, 0, 210, 0)
                    : hpFrac > 0.25f ? Color.FromArgb(200, 210, 160, 0)
                    : Color.FromArgb(210, 210, 0, 0);
        Graphics.DrawBox(hp, hpColor);

        // Energy shield bar above HP
        if (life.MaxES > 0)
        {
            float esFrac = Math.Clamp((float)life.CurES / life.MaxES, 0f, 1f);
            var es = new RectangleF(bg.X, bg.Y - barH - 1, barW * esFrac, barH);
            Graphics.DrawBox(es, Color.FromArgb(200, 0, 140, 220));
        }
    }

    private (Color color, float dotSize) GetRenderSettings(TrackedEntity tracked)
    {
        return tracked.Category switch
        {
            EntityCategory.Monster => tracked.Entity.Rarity switch
            {
                MonsterRarity.Unique => (Settings.UniqueMonsterColor.Value, Settings.MonsterDotSize.Value + 3f),
                MonsterRarity.Rare   => (Settings.RareMonsterColor.Value,   Settings.MonsterDotSize.Value + 1f),
                MonsterRarity.Magic  => (Settings.MagicMonsterColor.Value,  Settings.MonsterDotSize.Value + 0f),
                _                    => (Settings.NormalMonsterColor.Value,  Settings.MonsterDotSize.Value - 1f),
            },
            EntityCategory.Chest         => (Settings.ChestColor.Value,      Settings.ChestDotSize.Value),
            EntityCategory.AreaTransition => (Settings.TransitionColor.Value, Settings.TransitionDotSize.Value),
            EntityCategory.NPC           => (Settings.NPCColor.Value,         Settings.NPCDotSize.Value),
            EntityCategory.Breach        => (Settings.BreachColor.Value,      8f),
            EntityCategory.Ritual        => (Settings.RitualColor.Value,      7f),
            _                            => (Color.White, 4f),
        };
    }

    private EntityCategory Categorize(Entity entity)
    {
        if (!entity.IsValid) return EntityCategory.Ignored;
        if (entity.Type == EntityType.Effect) return EntityCategory.Ignored;

        switch (entity.Type)
        {
            case EntityType.Monster:
                if (!Settings.ShowMonsters) return EntityCategory.Ignored;
                if (!entity.IsHostile) return EntityCategory.Ignored;
                if (!entity.HasComponent<Monster>()) return EntityCategory.Ignored;
                if (entity.Rarity == MonsterRarity.White && !Settings.ShowNormalMonsters)
                    return EntityCategory.Ignored;
                if (entity.Rarity == MonsterRarity.Magic && !Settings.ShowMagicMonsters)
                    return EntityCategory.Ignored;
                if (entity.Rarity == MonsterRarity.Rare && !Settings.ShowRareMonsters)
                    return EntityCategory.Ignored;
                if (entity.Rarity == MonsterRarity.Unique && !Settings.ShowUniqueMonsters)
                    return EntityCategory.Ignored;
                return EntityCategory.Monster;

            case EntityType.Chest:
                if (!Settings.ShowChests) return EntityCategory.Ignored;
                return EntityCategory.Chest;

            case EntityType.AreaTransition:
                if (!Settings.ShowTransitions) return EntityCategory.Ignored;
                return EntityCategory.AreaTransition;

            case EntityType.Npc:
                if (!Settings.ShowNPCs) return EntityCategory.Ignored;
                return EntityCategory.NPC;

            default:
                // Encounter objects identified by path prefix
                if (Settings.ShowBreachObjects && entity.Path?.StartsWith(BreachPrefix) == true)
                    return EntityCategory.Breach;
                if (Settings.ShowRitualCircles && entity.Path == RitualPrefix)
                    return EntityCategory.Ritual;
                return EntityCategory.Ignored;
        }
    }
}
