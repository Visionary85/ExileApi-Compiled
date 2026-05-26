using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace PoE2Radar;

public class Settings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Monsters", 1)]
    public ToggleNode ShowMonsters { get; set; } = new ToggleNode(true);
    public ToggleNode ShowNormalMonsters { get; set; } = new ToggleNode(false);
    public ToggleNode ShowMagicMonsters { get; set; } = new ToggleNode(true);
    public ToggleNode ShowRareMonsters { get; set; } = new ToggleNode(true);
    public ToggleNode ShowUniqueMonsters { get; set; } = new ToggleNode(true);
    public ColorNode NormalMonsterColor { get; set; } = new ColorNode(Color.FromArgb(180, 200, 50, 50));
    public ColorNode MagicMonsterColor { get; set; } = new ColorNode(Color.FromArgb(200, 100, 100, 255));
    public ColorNode RareMonsterColor { get; set; } = new ColorNode(Color.FromArgb(220, 255, 200, 0));
    public ColorNode UniqueMonsterColor { get; set; } = new ColorNode(Color.FromArgb(255, 175, 96, 37));
    public RangeNode<int> MonsterDotSize { get; set; } = new RangeNode<int>(6, 2, 20);

    [Menu("Health Bars", 2)]
    public ToggleNode ShowHealthBars { get; set; } = new ToggleNode(true);
    public ToggleNode HealthBarsOnlyRarePlus { get; set; } = new ToggleNode(true);
    public RangeNode<float> HealthBarWidth { get; set; } = new RangeNode<float>(60f, 20f, 200f);
    public RangeNode<float> HealthBarYOffset { get; set; } = new RangeNode<float>(30f, 10f, 80f);

    [Menu("Chests", 3)]
    public ToggleNode ShowChests { get; set; } = new ToggleNode(true);
    public ToggleNode ShowOnlyUnopenedChests { get; set; } = new ToggleNode(true);
    public ColorNode ChestColor { get; set; } = new ColorNode(Color.FromArgb(200, 0, 210, 100));
    public RangeNode<int> ChestDotSize { get; set; } = new RangeNode<int>(5, 2, 15);

    [Menu("Area Transitions", 4)]
    public ToggleNode ShowTransitions { get; set; } = new ToggleNode(true);
    public ColorNode TransitionColor { get; set; } = new ColorNode(Color.FromArgb(220, 0, 160, 255));
    public RangeNode<int> TransitionDotSize { get; set; } = new RangeNode<int>(8, 3, 20);
    public ToggleNode ShowTransitionName { get; set; } = new ToggleNode(true);

    [Menu("NPCs", 5)]
    public ToggleNode ShowNPCs { get; set; } = new ToggleNode(true);
    public ColorNode NPCColor { get; set; } = new ColorNode(Color.FromArgb(200, 255, 255, 255));
    public RangeNode<int> NPCDotSize { get; set; } = new RangeNode<int>(5, 2, 15);

    [Menu("Encounters", 6)]
    public ToggleNode ShowBreachObjects { get; set; } = new ToggleNode(true);
    public ColorNode BreachColor { get; set; } = new ColorNode(Color.FromArgb(220, 130, 0, 200));
    public ToggleNode ShowRitualCircles { get; set; } = new ToggleNode(true);
    public ColorNode RitualColor { get; set; } = new ColorNode(Color.FromArgb(220, 180, 0, 50));

    [Menu("Map Drawing", 7)]
    public ToggleNode DrawOnMinimap { get; set; } = new ToggleNode(true);
    public ToggleNode DrawOnLargeMap { get; set; } = new ToggleNode(true);
    public ToggleNode ShowLabelsOnLargeMap { get; set; } = new ToggleNode(true);
    public RangeNode<float> LargeMapTextScale { get; set; } = new RangeNode<float>(0.8f, 0.4f, 2.0f);

    [Menu("Range Filter", 8)]
    public ToggleNode UseRangeFilter { get; set; } = new ToggleNode(false);
    public RangeNode<int> MaxRange { get; set; } = new RangeNode<int>(120, 20, 400);
}
