# Jeapfrog Emulator

Jeapfrog is a Windows front end for the [Tadpole / Glasspole LeapPad2 emulator](https://github.com/BubbletopTag/tadpole). It adds one-click setup, a searchable Explorer game library, and direct launch for installed cartridges.

## Build and run

On Windows, open PowerShell in this folder and run the build script.

~~~powershell
.\build.ps1
~~~

The build uses the Windows .NET Framework C# compiler and creates **Jeapfrog.exe**.

1. Open **Jeapfrog.exe** and click **Setup**. Jeapfrog reuses a compatible local Glasspole setup when it finds one. A fresh setup downloads the engine and LeapPad2 firmware.
2. Click **Open…** to import a local cartridge archive or image. **Game Library** shows the Explorer catalog and its download links.
3. Double-click an installed game to play. **Tools → Open Data Folder** opens the folder containing your games and saves.

## Controls

| Control | Input |
|---|---|
| Stylus | Mouse |
| D-pad | Arrow keys |
| A / B | X / Z |
| L / R | Q / W |
| Menu / Back | Home / Esc |
| Volume | - / = |

## Credits

Thanks to **BubbletopTag and the Tadpole contributors** for Tadpole, Glasspole, and the tools that make the LeapPad2 games run. See [CREDITS.md](CREDITS.md) for upstream sources and the pinned engine release.
