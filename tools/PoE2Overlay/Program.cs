using PoE2Overlay;

var watcher = new LogWatcher();
using var overlay = new HudOverlay(watcher);

watcher.Start();
await overlay.Run();
