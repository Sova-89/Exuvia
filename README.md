# Exuvia

*An exuvia is the shed skin an insect leaves behind when it molts.*

A skin switcher for Hollow Knight: Silksong. Skins are turned on and off inside the game, from the pause menu or the main menu, with no restart.

It installs on its own: only BepInEx 5 is required. Customizer, CustomizerT2D, CustomizerCinematic, Patchwork and Stitchwork are not needed, and skins made for them are read directly.

Русская версия: [README.ru.md](README.ru.md)

> Pre-release. Tested on one PC (Windows) with eight skins. See "What has been tested" for details.

## Features

- **A "Skins" entry in the game menus.** A screen in the game's own style: a list of skins, toggle with one press, works with keyboard, mouse and gamepad.
- **No restart.** A skin is applied and removed on the fly, including menus, inventory and HUD.
- **Two skin formats.**
  - Customizer format: sprite atlases (`Knight\atlas0.png` and so on), the `Texture2D` folder (menus, UI), the `Video` / `VideoClip` folders (video cutscenes).
  - Patchwork / Stitchwork format: individual sprites (`Sprites\...`), `Sprites\T2D` images, texts (`Text`), sounds (`Sounds`), scene recolor (`Recolor`), crest and scene conditions (`conditions.json`).
- **One folder for skins.** Drop an archive (zip, rar, 7z) or a skin folder into `<game folder>\Skins`; the mod unpacks it when the game starts and when the Skins screen opens. Skins stay in `Skins`, one folder per skin; to remove a skin, delete its folder.
- **Previews.** A picture of the selected skin is shown next to the list: the icon from the archive, or Hornet in that skin if there is none.
- **No third-party code.** If a skin archive ships its author's DLL, the mod does not install it and says so in the skin's caption. If you need it, put it into `BepInEx\plugins` yourself.
- **No freezes.** Images are decoded on background threads. Recently disabled skins stay in video memory as a reserve, so turning one back on is almost instant.

## Installation

1. Install [BepInEx 5](https://thunderstore.io/c/hollow-knight-silksong/p/BepInEx/BepInExPack_Silksong/) for Silksong.
2. Put `Exuvia.dll` into `Hollow Knight Silksong\BepInEx\plugins\Exuvia\`.
3. Start the game. A "Skins" entry appears in the main menu and in the pause menu.

Put new skins into the `Skins` folder (it appears in the game folder after the first launch). The unpacked archive is moved to `Skins\_installed` — it is not needed any more and can be deleted.

Skins already installed in `Hollow Knight Silksong_Data\Mods\Customizer\`, `Packs\<name>\Stitchwork` or `BepInEx\plugins\...\Stitchwork` are picked up as well — there is no need to move them.

### Other skin mods

- **Customizer** — disable it (`Customizer.dll`): it replaces the same atlases.
- **CustomizerT2D, CustomizerCinematic** — not needed. If they are installed, Exuvia steps aside: menus, UI and video cutscenes are left to them.
- **Patchwork, Stitchwork** — do not install together with Exuvia: both would replace the same sprites.

## Memory and low-end PCs

Skin images sit in video memory uncompressed. This comes from the skin formats themselves; it was the same with Customizer. A large skin with atlases and UI takes about 1.1 GB of video memory, a Stitchwork-format skin about 0.4 GB.

The reserve for recently disabled skins is chosen automatically:

| Video memory | Reserve |
|---|---|
| under 3 GB | none |
| 4 GB | about 0.8 GB |
| 6 GB | about 1.2 GB |
| 8 GB and more | 1.5 GB |

With no reserve, a skin is loaded again in the background each time it is enabled — a second or two, without freezes. The amount can be set by hand with `KeepRecentMB`.

## Settings

File `BepInEx\config\sova.exuvia.cfg` (or F1 with ConfigurationManager).

| Setting | Default | What it does |
|---|---|---|
| `Keys / Menu` | `\` | Opens the fallback window with the skin list |
| `General / Language` | `auto` | Language: `auto`, `ru`, `en` |
| `General / MenuButtons` | `true` | The "Skins" entry in the game menus |
| `General / ExclusiveOnConflict` | `true` | Enabling a skin disables skins that replace the same textures |
| `General / KeepRecentMB` | `-1` | Video memory reserve for recent skins, MB (`-1` = auto, `0` = none) |
| `General / MaxCacheMB` | `1500` | Memory limit for textures of enabled skins |
| `General / BackgroundDecode` | `true` | Decode images on background threads |
| `General / LogFrameLoad` | `false` | Log the mod's per-frame cost every 30 seconds |
| `Stitchwork / Images, Texts, Sounds, Recolor` | `true` | Parts of Stitchwork-format skins, individually |

## What has been tested

Tested in game: turning skins of both formats on and off from the menus, skin menus and UI (the `Texture2D` folder) without CustomizerT2D, installing from the `Skins` folder (zip), background loading, the reserve of recent skins, video cutscenes, previews.

Lightly tested or untested: rar and 7z archives, texts, sounds and recolor (one skin only), archives with several skins inside, Linux and macOS.

## Not supported

From Stitchwork's feature set, these are missing: the `area`, `act`, `boss`, `ability` conditions (treated as not met), whole-texture replacement for meshes and particles, fonts, cursor, global recolor rules. There are no tools for skin authors (sprite dumping, editor) — use Patchwork or Stitchwork for that.

## Building

Requires .NET SDK 8 and the game with BepInEx installed.

```
dotnet build src/Exuvia.csproj -c Release -p:GameDir="<game folder>"
```

Output: `src\bin\Release\netstandard2.1\Exuvia.dll`.

## Credits

Exuvia contains no code from these mods, but it reads the skin formats their authors designed and follows their replacement rules:

- [Patchwork](https://github.com/Ashiepaws/silksong-patchwork/) by Ashiepaws — the per-sprite skin format, texts, sounds.
- [Stitchwork](https://thunderstore.io/c/hollow-knight-silksong/p/SilksongSkinCreators/Stitchwork/) by FabulatorK and the skin creators — conditions, sets, recolor.
- Silksong Customizer by RatherChaoticDev — the atlas skin format.
- CustomizerT2D by Su4enka — the matching rule for menu and UI textures.
- Custom Knight for Hollow Knight — the idea of switching skins from the game menu.

The icon was made by Lev. It shows Hornet in community skins: VoidHornet by faarisaahmed, MaidHornet by Snek44, WispHornet by Me_and_me_also_me, and Flame by SH (uploaded to Nexus Mods by asdasdasdv). These skins belong to their authors.

All game assets belong to Team Cherry.

## License

MIT © 2026 Sova
