# PoE2 Overlay

Standalone transparent always-on-top HUD for Path of Exile 2.

Reads `Client.txt` to detect your current zone and displays relevant quest guide
steps directly over the game — no ExileCore2, no payment required.

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime (https://dotnet.microsoft.com/download/dotnet/8.0)
- DirectX 11

## Build and run

```powershell
cd tools\PoE2Overlay
dotnet run
```

Or publish a single .exe:

```powershell
dotnet publish -r win-x64 -p:PublishSingleFile=true -c Release
# Output: bin\Release\net8.0-windows\win-x64\publish\PoE2Overlay.exe
```

## Usage

1. Start Path of Exile 2 and log in.
2. Run `PoE2Overlay.exe` (or `dotnet run`).
3. The overlay appears in the top-left corner of your screen.
4. Enter any zone — the guide panel updates automatically.
5. Click `=` to open Settings (opacity, optional steps toggle, pin).
6. Drag the window to reposition it.

## Client.txt path

The tool searches common PoE2 install locations automatically.
If your install is non-standard, edit `LogWatcher.cs` and add your path to `candidates`.

## What it shows

| Icon    | Meaning                                      |
|---------|----------------------------------------------|
| [KILL]  | Boss or required kill                        |
| [TALK]  | NPC interaction / quest turn-in              |
| [ WP ]  | Waypoint to activate                         |
| [USE ]  | Object to interact with                      |
| [GET ]  | Item to pick up (quest item / skill points)  |
| [ >> ]  | Portal / zone transition                     |
| [ ->]   | Movement / navigation                        |
| [ i  ]  | Note or tip (greyed out = optional)          |

## Next steps (memory reading phase)

Once you've discovered offsets with `tools/OffsetVerifier`, you can add live data
to this overlay by wiring `OffsetVerifier`'s `ReadInt32`/`ReadFloat` helpers into
a background service here — HP bar, position, death counter, etc.

The architecture is identical to what ExileCore2 plugins do internally.
