// ============================================================================
//  Menu.Screen.cs — часть класса Menu: логика списка для экрана в стиле игры (SkinScreen.cs).
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
        // ---- то же самое для экрана в стиле игры (SkinScreen.cs) ----------------
        // Экран сам ничего не решает: спрашивает здесь список и просит выполнить действие.

        internal int ItemCount { get { return rows.Count; } }

        internal string ItemName(int i)
        {
            Row row = rows[i];
            return row.P != null ? row.P.Name : (row.S != null ? row.S.Name : row.A.FileName);
        }

        internal bool ItemEnabled(int i) { return rows[i].Enabled; }

        /// Это не скин, а то, что не удалось установить из папки Skins.
        internal bool ItemFailed(int i) { return rows[i].A != null; }

        /// Картинка-превью скина (null — нет или ещё делается).
        internal Texture ItemPreview(int i)
        {
            if (i < 0 || i >= rows.Count) return null;
            Row row = rows[i];
            if (row.P != null) return Preview.Get(row.P.Dir, false);
            if (row.S != null) return Preview.Get(row.S.Dir, true);
            return null;
        }

        /// «Мод автора X.dll не поставлен» — если в архиве скина был свой код (мы его не ставим).
        string DllNote(string home)
        {
            if (string.IsNullOrEmpty(home)) return "";
            string note;
            if (dllNotes.TryGetValue(home, out note)) return note;
            note = "";
            try
            {
                string file = System.IO.Path.Combine(home, Installer.ExtraFile);
                if (System.IO.File.Exists(file))
                {
                    string names = string.Join(", ", System.IO.File.ReadAllLines(file)).Trim();
                    if (names.Length > 0) note = SkippedText(names);
                }
            }
            catch (Exception) { }
            dllNotes[home] = note;
            return note;
        }

        static string SkippedText(string names)
        {
            return L.T("Мод автора " + names + " не поставлен (он в архиве).",
                "The author's " + names + " was not installed (it is in the archive).");
        }

        /// Одна строка о скине для подписи под списком.
        internal string ItemInfo(int i)
        {
            Row row = rows[i];
            if (row.A != null)
            {
                return row.A.Error != null
                    ? L.T("Не установилось: ", "Not installed: ") + row.A.Error
                        + L.T(" Нажми, чтобы повторить.", " Press to retry.")
                    : L.T("Ещё не установлено. Нажми, чтобы установить.", "Not installed yet. Press to install.");
            }
            string state = row.Enabled ? L.T("Включён", "On") : L.T("Выключен", "Off");
            string extra = DllNote(row.P != null ? row.P.Dir
                : System.IO.Path.GetDirectoryName(row.S.Dir.TrimEnd('\\', '/')));
            if (extra.Length > 0) state += ". " + extra.TrimEnd('.');
            if (row.S != null)
            {
                string about = row.S.Description.Length > 0 ? row.S.Description
                    : (row.S.Author.Length > 0 ? L.T("автор ", "by ") + row.S.Author : L.T("спрайтов: ", "sprites: ")
                        + row.S.SpriteCount);
                if (row.S.Description.Length > 0 && row.S.Author.Length > 0)
                    about += L.T(" — автор ", " — by ") + row.S.Author;
                return state + ". " + about;
            }
            return state + L.T(". Меняет: ", ". Changes: ") + Plainly(row.P.CollectionNames(), row.P.Texture2DCount > 0)
                + ".";
        }

        /// Что меняет скин, человеческими словами вместо списка внутренних имён игры.
        static string Plainly(List<string> names, bool hasTexture2D)
        {
            bool hornet = false;
            int other = 0;
            foreach (string n in names)
            {
                if (n.StartsWith("Knight", StringComparison.OrdinalIgnoreCase)
                    || n.IndexOf("Hornet", StringComparison.OrdinalIgnoreCase) >= 0) hornet = true;
                else other++;
            }
            var parts = new List<string>();
            if (hornet) parts.Add(L.T("Хорнет", "Hornet"));
            if (hasTexture2D) parts.Add(L.T("меню и интерфейс", "menus and UI"));
            if (other > 0) parts.Add(L.T("прочее (предметы, эффекты, враги): ", "other (items, effects, enemies): ")
                + other);
            if (parts.Count == 0) return L.T("ничего (нет подходящих файлов)", "nothing (no matching files)");
            return string.Join(", ", parts.ToArray());
        }

        /// Нажатие на строку: включить/выключить скин или повторить установку.
        internal void ItemSubmit(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            Row row = rows[i];
            if (row.P != null) Toggle(row.P);
            else if (row.S != null) Toggle(row.S);
            else RetryInstall();
        }

        internal void SubmitDisableAll() { DisableAll(); }

        internal bool AnyEnabled
        {
            get
            {
                foreach (Row row in rows) if (row.Enabled) return true;
                return false;
            }
        }

        /// Последнее сообщение о сделанном (пусто, если нечего сказать).
        internal string Status { get { return status; } }

        /// Перед показом экрана: перечитать список с диска и поставить новое из папки Skins.
        internal void Prepare()
        {
            status = "";
            Reload();
            if (Installer.HasNew) AutoInstall();
        }

        /// Что сейчас грузится (null — ничего).
        internal static string Progress()
        {
            if (SpriteEngine.Busy)
                return L.T("Загружаю спрайты ", "Loading sprites ") + SpriteEngine.DoneSprites + "/"
                    + SpriteEngine.TotalSprites + "…";
            if (Engine.Pending) return L.T("Загружаю текстуры…", "Loading textures…");
            return null;
        }
    }
}
