// ============================================================================
//  Menu.Draw.cs — часть класса Menu: рисование старого окна и мелочи.
//  Общее описание — в начале Menu.cs.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Exuvia
{
    internal sealed partial class Menu
    {
        // ---- рисование ---------------------------------------------------------

        internal void Draw()
        {
            EnsureStyles();
            KeepCursor();

            float scale = Screen.height / RefHeight;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float screenW = Screen.width / scale;

            GUI.DrawTexture(new Rect(0f, 0f, screenW, RefHeight), shadeTex);
            Rect win = new Rect((screenW - WindowW) / 2f, (RefHeight - WindowH) / 2f, WindowW, WindowH);
            GUI.Box(win, GUIContent.none, windowStyle);

            GUILayout.BeginArea(new Rect(win.x + 22f, win.y + 16f, win.width - 44f, win.height - 32f));
            GUILayout.Label(L.T("Exuvia — скины", "Exuvia — skins"), titleStyle);

            if (Plugin.CustomizerLoaded())
            {
                GUILayout.Label(L.T("Customizer.dll всё ещё загружен. Exuvia его заменяет: переименуй Customizer.dll "
                    + "в .off, иначе работа дублируется.",
                    "Customizer.dll is still loaded. Exuvia replaces it: rename Customizer.dll to .off or the work "
                        + "is done twice."), warnStyle);
            }
            GUILayout.Space(6f);

            Row selected = null;
            foreach (Row row in rows) if (row.Key == selectedKey) selected = row;

            GUILayout.BeginHorizontal();
            DrawList();
            GUILayout.Space(18f);
            GUILayout.BeginVertical();
            GUILayout.Label(L.T("О скине", "Skin info"), headStyle);
            if (selected == null) GUILayout.Label(L.T("Выбери скин слева.", "Pick a skin on the left."), dimStyle);
            else if (selected.P != null) DrawDetails(selected.P);
            else if (selected.S != null) DrawDetails(selected.S);
            else DrawDetails(selected.A);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            DrawFooter();
            GUILayout.EndArea();

            GUI.matrix = oldMatrix;
        }

        void DrawList()
        {
            GUILayout.BeginVertical(GUILayout.Width(ListW));
            GUILayout.Label(L.T("Скины", "Skins"), headStyle);

            scrollList = GUILayout.BeginScrollView(scrollList, GUILayout.Height(470f));
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                GUILayout.BeginHorizontal();

                string title;
                if (row.P != null) title = (row.Enabled ? "[x]  " : "[  ]  ") + row.P.Name;
                else if (row.S != null) title = (row.Enabled ? "[x]  " : "[  ]  ") + row.S.Name + "   · Stitchwork";
                else title = L.T("не установлено:  ", "not installed:  ") + row.A.FileName;

                GUI.backgroundColor = row.Key == selectedKey ? ColSelected : Color.white;
                if (GUILayout.Button(title, rowStyle)) selectedKey = row.Key;

                Row target = row;
                if (row.A != null)
                {
                    GUI.backgroundColor = ColInstall;
                    if (GUILayout.Button(L.T("Повторить", "Retry"), buttonStyle, GUILayout.Width(170f)))
                    {
                        selectedKey = row.Key;
                        queued = RetryInstall;
                    }
                }
                else
                {
                    GUI.backgroundColor = row.Enabled ? ColOn : Color.white;
                    string caption = row.Enabled ? L.T("Выключить", "Disable") : L.T("Включить", "Enable");
                    if (GUILayout.Button(caption, buttonStyle, GUILayout.Width(170f)))
                    {
                        if (target.P != null) queued = delegate { Toggle(target.P); };
                        else queued = delegate { Toggle(target.S); };
                    }
                }
                GUI.backgroundColor = Color.white;
                GUILayout.EndHorizontal();
            }
            if (rows.Count == 0)
            {
                GUILayout.Label(L.T("Скинов нет. Кинь скин (zip, rar, 7z или папку) в папку Skins в папке игры и нажми "
                    + "«Обновить список».",
                    "No skins. Drop a skin (zip, rar, 7z or a folder) into the Skins folder in the game folder and "
                        + "press Refresh list."), textStyle);
            }
            GUILayout.EndScrollView();
            GUILayout.Label(L.T("Новый скин: кинь zip, rar, 7z или папку в Skins (в папке игры) — установится сам.",
                "New skin: drop a zip, rar, 7z or a folder into Skins (in the game folder) — it installs itself."), dimStyle);
            GUILayout.EndVertical();
        }

        void DrawDetails(PackInfo pack)
        {
            GUILayout.Label(pack.Name, labelStyle);
            GUILayout.Label(L.T("Формат Customizer. Состояние: ", "Customizer format. State: ")
                + (pack.Enabled ? L.T("включён", "enabled") : L.T("выключен", "disabled")), dimStyle);
            GUILayout.Label(L.T("Коллекций: ", "Collections: ") + pack.CollectionCount
                + L.T(",  файлов: ", ",  files: ") + pack.FileCount
                + ",  " + (pack.Bytes / (1024.0 * 1024.0)).ToString("0.0") + L.T(" МБ", " MB"), dimStyle);

            GUILayout.Space(4f);
            GUILayout.Label(L.T("Заменяет: ", "Replaces: ")
                + Summary(pack.CollectionNames(), pack.Texture2DCount > 0), textStyle);

            if (pack.Texture2DCount > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label(L.T("Есть папка Texture2D (" + pack.Texture2DCount
                    + " файлов: меню, интерфейс, часть Хорнет).",
                    "Has a Texture2D folder (" + pack.Texture2DCount
                        + " files: menus, UI, part of Hornet)."), textStyle);
            }

            if (!pack.Enabled && Plugin.ExclusiveOnConflict.Value)
            {
                List<PackInfo> clash = PackStore.WouldDisable(pack);
                if (clash.Count > 0)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label(L.T("Включение выключит: ", "Enabling will disable: ") + Names(clash), warnStyle);
                }
            }
        }

        void DrawDetails(StitchPack pack)
        {
            GUILayout.Label(pack.Name + (pack.Author.Length > 0 ? L.T("  — автор ", "  — by ")
                + pack.Author : ""), labelStyle);
            GUILayout.Label(L.T("Формат Stitchwork. Состояние: ", "Stitchwork format. State: ")
                + (pack.Enabled ? L.T("включён", "enabled") : L.T("выключен", "disabled")), dimStyle);
            if (pack.Description.Length > 0) GUILayout.Label(pack.Description, textStyle);
            GUILayout.Label(L.T("Спрайтов: ", "Sprites: ") + pack.SpriteCount
                + L.T(",  коллекций: ", ",  collections: ") + pack.Collections.Count
                + ",  " + (pack.Bytes / (1024.0 * 1024.0)).ToString("0.0") + L.T(" МБ", " MB"), dimStyle);

            GUILayout.Space(4f);
            var names = new List<string>(pack.Collections);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            GUILayout.Label(L.T("Заменяет: ", "Replaces: ") + Summary(names, false), textStyle);

            var extras = new StringBuilder();
            if (pack.T2DFiles > 0) extras.Append(L.T("картинки меню, инвентаря, фонов и эффектов (",
                "menu, inventory, background and effect images (") + pack.T2DFiles + ")");
            if (pack.SoundFiles > 0) extras.Append((extras.Length > 0 ? ", " : "") + L.T("звуки (", "sounds (")
                + pack.SoundFiles + ")");
            if (pack.HasText) extras.Append((extras.Length > 0 ? ", " : "") + L.T("тексты", "texts"));
            if (pack.HasRecolor) extras.Append((extras.Length > 0 ? ", " : "")
                + L.T("перекраска сцен", "scene recolor"));
            if (extras.Length > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label(L.T("Также применяются: ", "Also applied: ") + extras + ".", textStyle);
            }
            GUILayout.Label(L.T("Свою DLL автора (если она есть в архиве) мод не ставит.",
                "The author's own DLL (if the archive has one) is not installed."), dimStyle);
            if (pack.Problems.Count > 0)
                GUILayout.Label(L.T("Проблемы в паке: ", "Pack problems: ") + pack.Problems.Count
                    + L.T(" (подробности в логе)", " (see the log)"), warnStyle);
        }

        void DrawDetails(ArchiveInfo archive)
        {
            GUILayout.Label(archive.FileName, labelStyle);
            GUILayout.Label(archive.IsFolder ? L.T("Папка в Skins", "Folder in Skins")
                : L.T("Архив в папке Skins,  ", "Archive in the Skins folder,  ")
                    + (archive.Len / (1024.0 * 1024.0)).ToString("0.0") + L.T(" МБ", " MB"), dimStyle);
            if (archive.Error != null)
            {
                GUILayout.Label(L.T("Не установилось: ", "Not installed: ") + archive.Error, warnStyle);
                GUILayout.Label(L.T("Исправь и нажми «Повторить», либо убери это из папки Skins.",
                    "Fix it and press Retry, or remove it from the Skins folder."), dimStyle);
                return;
            }
            GUILayout.Label(L.T("Ещё не установлено. Нажми «Обновить список».",
                "Not installed yet. Press Refresh list."), textStyle);
        }

        void DrawFooter()
        {
            if (status.Length > 0) GUILayout.Label(status, textStyle);
            GUILayout.Label(EngineLine(), dimStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("Всё выключить", "Disable all"), buttonStyle, GUILayout.Width(220f)))
                queued = DisableAll;
            if (GUILayout.Button(L.T("Обновить список", "Refresh list"), buttonStyle, GUILayout.Width(220f)))
                queued = Rescan;
            if (GUILayout.Button(L.T("Закрыть", "Close") + "  [" + Plugin.MenuKeyName
                + "]", buttonStyle, GUILayout.Width(220f))) queued = Close;
            GUILayout.EndHorizontal();
        }

        static string EngineLine()
        {
            string text = L.T("Атласов подменено: ", "Atlases replaced: ") + Engine.AppliedCount
                + " (~" + Engine.CachedMegabytes.ToString("0") + L.T(" МБ)", " MB)")
                + L.T(",  спрайтов: ", ",  sprites: ") + SpriteEngine.AppliedSprites
                + " (~" + SpriteEngine.Megabytes.ToString("0") + L.T(" МБ)", " MB)")
                + L.T(",  картинок T2D: ", ",  T2D images: ") + T2DEngine.SpriteCount
                + " (~" + T2DEngine.Megabytes.ToString("0") + L.T(" МБ)", " MB)");
            if (SpriteEngine.Busy)
                text = L.T("Загружаю спрайты ", "Loading sprites ") + SpriteEngine.DoneSprites + "/"
                    + SpriteEngine.TotalSprites + "…  " + text;
            else if (Engine.Pending)
                text = L.T("Загружаю текстуры…  ", "Loading textures…  ") + text;
            return text;
        }

        // ---- мелочи ------------------------------------------------------------

        static string Names(List<PackInfo> list)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(list[i].Name);
            }
            return builder.ToString();
        }

        static string Summary(List<string> names, bool hasTexture2D)
        {
            if (names.Count == 0)
            {
                return hasTexture2D
                    ? L.T("только папка Texture2D (см. ниже)", "only the Texture2D folder (see below)")
                    : L.T("ничего (нет подходящих файлов)", "nothing (no matching files)");
            }
            const int Show = 6;
            var builder = new StringBuilder();
            for (int i = 0; i < names.Count && i < Show; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(names[i]);
            }
            if (names.Count > Show) builder.Append(L.T(" и ещё ", " and ") + (names.Count - Show) + L.T("", " more"));
            return builder.ToString();
        }

        static Texture2D Solid(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        void EnsureStyles()
        {
            if (windowStyle != null && windowTex != null) return;

            windowTex = Solid(new Color(0.06f, 0.07f, 0.10f, 0.96f));
            shadeTex = Solid(new Color(0f, 0f, 0f, 0.45f));

            windowStyle = new GUIStyle(GUI.skin.box);
            windowStyle.normal.background = windowTex;

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 20;
            labelStyle.alignment = TextAnchor.MiddleLeft;
            labelStyle.wordWrap = false;
            labelStyle.clipping = TextClipping.Clip;
            labelStyle.fixedHeight = RowH;
            labelStyle.normal.textColor = Color.white;

            dimStyle = new GUIStyle(labelStyle);
            dimStyle.normal.textColor = new Color(0.70f, 0.74f, 0.80f);

            headStyle = new GUIStyle(labelStyle);
            headStyle.fontSize = 22;
            headStyle.fontStyle = FontStyle.Bold;

            titleStyle = new GUIStyle(headStyle);
            titleStyle.fontSize = 28;
            titleStyle.fixedHeight = 42f;

            // Текст с переносом строк: высоту считает сам GUILayout.
            textStyle = new GUIStyle(GUI.skin.label);
            textStyle.fontSize = 19;
            textStyle.wordWrap = true;
            textStyle.alignment = TextAnchor.UpperLeft;
            textStyle.normal.textColor = Color.white;

            warnStyle = new GUIStyle(textStyle);
            warnStyle.normal.textColor = ColWarn;

            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 19;
            buttonStyle.fixedHeight = RowH;
            buttonStyle.wordWrap = false;
            buttonStyle.clipping = TextClipping.Clip;

            rowStyle = new GUIStyle(buttonStyle);
            rowStyle.alignment = TextAnchor.MiddleLeft;
        }
    }
}
