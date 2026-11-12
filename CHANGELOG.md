# Changelog

## 0.10.1

- New icon; the skins shown on it and their authors are credited in the README.
- No changes to the mod itself.

## 0.10.0

First public release (pre-release).

- "Skins" entry in the pause menu and the main menu; skins are switched without restarting the game.
- Reads Customizer-format skins (atlases, `Texture2D`, `Video` / `VideoClip`) and Patchwork / Stitchwork-format skins (sprites, T2D images, texts, sounds, scene recolor, crest and scene conditions).
- Skins are installed by dropping a zip, rar, 7z or a folder into the `Skins` folder in the game folder.
- Skin previews next to the list.
- Images are decoded on background threads; recently disabled skins are kept in video memory within a budget that depends on the video card.
- Interface in English and Russian.
