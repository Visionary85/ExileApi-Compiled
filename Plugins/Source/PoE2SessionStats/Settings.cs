using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace PoE2SessionStats;

public class Settings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Display", 1)]
    public ToggleNode ShowPlayerLevel { get; set; } = new ToggleNode(true);
    public ToggleNode ShowAreaName { get; set; } = new ToggleNode(true);
    public ToggleNode ShowAreaTime { get; set; } = new ToggleNode(true);
    public ToggleNode ShowXpRate { get; set; } = new ToggleNode(true);
    public ToggleNode ShowTimeToLevel { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGold { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGoldRate { get; set; } = new ToggleNode(true);
    public ToggleNode ShowPing { get; set; } = new ToggleNode(true);
    public ToggleNode ShowDeathCount { get; set; } = new ToggleNode(true);

    [Menu("Position", 2)]
    public RangeNode<int> BarXOffset { get; set; } = new RangeNode<int>(0, -400, 400);
    public RangeNode<int> BarYOffset { get; set; } = new RangeNode<int>(0, -100, 100);
    public RangeNode<float> TextScale { get; set; } = new RangeNode<float>(1.0f, 0.5f, 2.0f);

    [Menu("Colors", 3)]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(Color.FromArgb(180, 20, 20, 20));
    public ColorNode TextColor { get; set; } = new ColorNode(Color.FromArgb(255, 220, 220, 220));
    public ColorNode HighlightColor { get; set; } = new ColorNode(Color.FromArgb(255, 255, 215, 0));
    public ColorNode XpColor { get; set; } = new ColorNode(Color.FromArgb(255, 130, 210, 255));
    public ColorNode GoldColor { get; set; } = new ColorNode(Color.FromArgb(255, 255, 200, 50));
    public ColorNode PingGoodColor { get; set; } = new ColorNode(Color.FromArgb(255, 0, 200, 0));
    public ColorNode PingWarnColor { get; set; } = new ColorNode(Color.FromArgb(255, 220, 180, 0));
    public ColorNode PingBadColor { get; set; } = new ColorNode(Color.FromArgb(255, 220, 50, 50));

    [Menu("Ping Thresholds", 4)]
    public RangeNode<int> PingWarnThreshold { get; set; } = new RangeNode<int>(80, 20, 300);
    public RangeNode<int> PingBadThreshold { get; set; } = new RangeNode<int>(150, 50, 500);
}
