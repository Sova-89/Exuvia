// ============================================================================
//  Menu.cs — окно выбора скина (рисуется поверх игры, управляется мышью).
//  Слева список: скины формата Customizer, скины формата Stitchwork и то из папки
//  Skins, что не удалось установить. Новое из Skins ставится само при открытии окна.
//  Справа сведения о выбранном.
//  Включение и выключение применяются сразу, без перезапуска игры.
//  Что менять: размеры и цвета — константы и EnsureStyles ниже.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Exuvia
{
    internal sealed partial class Menu
    {
        // Все размеры заданы для экрана высотой 1080 и растягиваются под реальный.
        internal const float RefHeight = 1080f;
        const float WindowW = 1240f;
        const float WindowH = 820f;
        const float ListW = 740f;
        const float RowH = 36f;

        static readonly Color ColOn = new Color(0.55f, 1.00f, 0.60f);
        static readonly Color ColSelected = new Color(0.55f, 0.85f, 1.00f);
        static readonly Color ColWarn = new Color(1.00f, 0.55f, 0.45f);
        static readonly Color ColInstall = new Color(1.00f, 0.90f, 0.55f);

        /// Строка списка: скин Customizer (P), скин Stitchwork (S) или архив (A).
        sealed class Row
        {
            internal string Key;
            internal PackInfo P;
            internal StitchPack S;
            internal ArchiveInfo A;
            internal bool Enabled { get { return P != null ? P.Enabled : (S != null && S.Enabled); } }
        }

        internal bool Open;

        Vector2 scrollList;
        string selectedKey = "";
        string status = "";
        Action queued;
        readonly List<Row> rows = new List<Row>();
        readonly Dictionary<string, string> dllNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Долгие действия делаем через пару кадров, чтобы сначала показать надпись.
        int laterAtFrame = -1;
        Action later;

        UnityEngine.UI.Selectable returnTo;
        bool cursorWasVisible;
        CursorLockMode cursorWasLocked;
        HeroController blockedHero;
        // Пока окно открыто, Esc должен только закрывать окно, а не ставить/снимать паузу игры.
        InputHandler pauseHeld;
        int pauseBackAtFrame = -1;
        readonly ClickBlocker blocker = new ClickBlocker();

        GUIStyle windowStyle, titleStyle, headStyle, labelStyle, dimStyle, textStyle, warnStyle, buttonStyle, rowStyle;
        Texture2D windowTex, shadeTex;

        // ---- открыть / закрыть -------------------------------------------------

        internal void Toggle()
        {
            if (Open) Close(); else Show();
        }

        /// Открыть окно с кнопки меню игры. После закрытия выбор вернётся на эту кнопку.
        internal void OpenFrom(UnityEngine.UI.Selectable button)
        {
            if (Open) return;
            Show();
            returnTo = button;
        }

        void Show()
        {
            Open = true;
            status = "";
            Reload();
            if (Installer.HasNew) AutoInstall();
            cursorWasVisible = Cursor.visible;
            cursorWasLocked = Cursor.lockState;

            // Пока окно открыто, Хорнет не реагирует на клавиши, а клики не доходят до кнопок игры.
            blockedHero = HeroController.SilentInstance;
            if (blockedHero != null) blockedHero.AddInputBlocker(this);
            blocker.Show();
            HoldPause();
        }

        void HoldPause()
        {
            try
            {
                pauseBackAtFrame = -1;
                if (pauseHeld != null) return; // ещё держим с прошлого раза
                GameManager game = GameManager.SilentInstance;
                InputHandler input = game != null ? game.inputHandler : null;
                if (input != null && input.PauseAllowed)
                {
                    input.PreventPause();
                    pauseHeld = input;
                }
            }
            catch (Exception) { }
        }

        /// Возвращаем игре клавишу паузы — не сразу, а через пару кадров, чтобы тот же Esc не сработал ещё и в игре.
        void ReleasePause(bool now)
        {
            if (pauseHeld == null) { pauseBackAtFrame = -1; return; }
            if (!now) { pauseBackAtFrame = Time.frameCount + 3; return; }
            try { pauseHeld.AllowPause(); } catch (Exception) { }
            pauseHeld = null;
            pauseBackAtFrame = -1;
        }

        internal void Shutdown()
        {
            ReleasePause(true);
        }

        internal void Close()
        {
            if (!Open) return;
            Open = false;
            if (blockedHero != null) blockedHero.RemoveInputBlocker(this);
            blockedHero = null;
            blocker.Hide();
            ReleasePause(false);
            Cursor.visible = cursorWasVisible;
            Cursor.lockState = cursorWasLocked;

            // Окно открывали из меню игры: возвращаем выбор на нашу кнопку, чтобы меню снова слушалось клавиш.
            UnityEngine.UI.Selectable back = returnTo;
            returnTo = null;
            try
            {
                if (back != null && back.gameObject.activeInHierarchy && back.interactable) back.Select();
            }
            catch (Exception) { }
        }

        /// Перечитывает с диска паки и архивы и просит движки применить изменения.
        void Reload()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            PackStore.Scan();
            StitchStore.Scan();
            Installer.Scan();
            // Перечитывание идёт при каждом открытии списка — должно быть незаметным. Если нет, это видно в журнале.
            if (clock.ElapsedMilliseconds > 100)
                Plugin.Log.LogInfo("Exuvia: перечитывание списка скинов заняло " + clock.ElapsedMilliseconds
                    + " мс.");
            BuildRows();
            Engine.Request();
            SpriteEngine.Request();
            RequestExtras();
        }

        /// Картинки T2D, тексты, звуки и перекраска Stitchwork-скинов: пересчитать, что действует.
        static void RequestExtras()
        {
            T2DEngine.Request();
            TextEngine.Request();
            SoundEngine.Request();
            RecolorEngine.Request();
        }

        void BuildRows()
        {
            rows.Clear();
            dllNotes.Clear();
            foreach (PackInfo pack in PackStore.Packs) rows.Add(new Row { Key = "c:" + pack.Name, P = pack });
            foreach (StitchPack stitchPack in StitchStore.Packs)
                rows.Add(new Row { Key = "s:" + stitchPack.Name, S = stitchPack });
            foreach (ArchiveInfo archive in Installer.Archives)
                rows.Add(new Row { Key = "z:" + archive.FileName, A = archive });
        }

        /// Каждый кадр, вне OnGUI: выполняем отложенные действия кнопок.
        internal void Update()
        {
            if (Open && blockedHero == null && HeroController.SilentInstance != null)
            {
                blockedHero = HeroController.SilentInstance;
                blockedHero.AddInputBlocker(this);
            }

            if (!Open && pauseBackAtFrame >= 0 && Time.frameCount >= pauseBackAtFrame) ReleasePause(true);

            // Esc закрывает только окно: пауза игры на это время придержана (см. HoldPause).
            if (Open && Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }

            if (queued != null)
            {
                Action action = queued;
                queued = null;
                action();
            }

            if (laterAtFrame >= 0 && Time.frameCount >= laterAtFrame)
            {
                laterAtFrame = -1;
                Action action = later;
                later = null;
                if (action != null) action();
            }
        }

        void Later(Action action)
        {
            later = action;
            laterAtFrame = Time.frameCount + 2;
        }

        /// Игра прячет курсор каждый кадр — возвращаем его, пока окно открыто.
        internal void KeepCursor()
        {
            if (!Open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ---- действия кнопок ---------------------------------------------------

        void Toggle(PackInfo pack)
        {
            bool turnOn = !pack.Enabled;
            bool exclusive = Plugin.ExclusiveOnConflict.Value;

            // Запоминаем заранее: после включения список конфликтов уже будет пустым.
            bool lostTexture2D = false;
            if (turnOn && exclusive)
                foreach (PackInfo other in PackStore.WouldDisable(pack))
                    if (other.Texture2DCount > 0) lostTexture2D = true;
            if (!turnOn && pack.Texture2DCount > 0) lostTexture2D = true;

            var disabled = new List<string>();
            string error = PackStore.SetEnabled(pack, turnOn, exclusive, disabled);
            if (error != null)
            {
                status = L.T("Не вышло: ", "Failed: ") + error;
                return;
            }
            // Скин меняется целиком: Stitchwork-скины на те же коллекции тоже выключаем.
            if (turnOn && exclusive)
                foreach (StitchPack stitchPack in StitchStore.Packs)
                    if (stitchPack.Enabled && StitchStore.Shares(stitchPack, pack.CollectionNames()))
                    {
                        StitchStore.SetEnabled(stitchPack, false);
                        disabled.Add(stitchPack.Name);
                    }

            selectedKey = "c:" + pack.Name;
            Engine.Request();
            SpriteEngine.Request();
            RequestExtras();

            string text;
            if (turnOn)
            {
                text = L.T("Включён: ", "Enabled: ") + pack.Name;
                if (disabled.Count > 0) text += L.T(". Выключен: ", ". Disabled: ")
                    + string.Join(", ", disabled.ToArray());
                text += ".";
            }
            else
            {
                text = L.T("Выключен: ", "Disabled: ") + pack.Name + ".";
            }

            bool gained = turnOn && pack.Texture2DCount > 0;
            if (gained || lostTexture2D) text += MenuTextures(text, gained, lostTexture2D);
            status = text;
        }

        void Toggle(StitchPack pack)
        {
            bool turnOn = !pack.Enabled;
            var disabled = new List<string>();
            bool lostTexture2D = false;

            if (turnOn && Plugin.ExclusiveOnConflict.Value)
            {
                foreach (StitchPack other in StitchStore.Packs)
                    if (other != pack && other.Enabled && StitchStore.Shares(other, pack.Collections))
                    {
                        StitchStore.SetEnabled(other, false);
                        disabled.Add(other.Name);
                    }
                foreach (PackInfo other in new List<PackInfo>(PackStore.Packs))
                    if (other.Enabled && StitchStore.Shares(pack, other.CollectionNames()))
                    {
                        if (other.Texture2DCount > 0) lostTexture2D = true;
                        PackStore.SetEnabled(other, false, false, new List<string>());
                        disabled.Add(other.Name);
                    }
            }

            string error = StitchStore.SetEnabled(pack, turnOn);
            selectedKey = "s:" + pack.Name;
            Engine.Request();
            SpriteEngine.Request();
            RequestExtras();

            string text = (turnOn ? L.T("Включён: ", "Enabled: ") : L.T("Выключен: ", "Disabled: ")) + pack.Name + ".";
            if (disabled.Count > 0) text += L.T(" Выключен: ", " Disabled: ") + string.Join(", ", disabled.ToArray())
                + ".";
            if (turnOn) text += L.T(" Спрайты загружаются.", " Sprites are loading.");
            if (lostTexture2D) text += MenuTextures(text, false, true);
            if (error != null) text += L.T(" Выбор не сохранился: ", " The choice was not saved: ") + error;
            status = text;
        }

        /// Ставит всё новое из папки Skins (архивы и папки) и показывает, что получилось.
        void AutoInstall()
        {
            status = L.T("В папке Skins есть новое, устанавливаю — игра может замереть на несколько секунд…",
                "Found new items in the Skins folder, installing — the game may freeze for a few seconds…");
            Later(delegate
            {
                var installed = new List<string>();
                var errors = new List<string>();
                Installer.ProcessAll(installed, errors);
                Preview.Forget();
                Reload();

                string text = "";
                if (installed.Count > 0)
                {
                    text = L.T("Установлено: ", "Installed: ") + string.Join(", ", installed.ToArray())
                        + L.T(". Скин выключен — нажми «Включить».", ". The skin is off — press Enable.");
                    string first = installed[0];
                    foreach (Row row in rows)
                    {
                        bool match = (row.P != null && row.P.Name == first)
                            || (row.S != null && (row.S.Name == first
                                || row.S.Dir.IndexOf(System.IO.Path.DirectorySeparatorChar + first + System.IO.Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase) >= 0));
                        if (match) { selectedKey = row.Key; break; }
                    }
                }
                if (Installer.SkippedDlls.Count > 0)
                    text += (text.Length > 0 ? " " : "") + SkippedText(string.Join(", ", Installer.SkippedDlls.ToArray()));
                if (errors.Count > 0)
                {
                    foreach (string e in errors) Plugin.Log.LogWarning("Exuvia: не установлено: " + e);
                    text += (text.Length > 0 ? " " : "") + L.T("Не установлено: ", "Not installed: ") + errors[0]
                        + (errors.Count > 1 ? L.T(" (и ещё ", " (and ") + (errors.Count - 1) + ")" : "");
                }
                status = text;
            });
        }

        void RetryInstall()
        {
            Installer.ForgetFailures();
            AutoInstall();
        }

        void DisableAll()
        {
            bool hadTexture2D = false;
            foreach (PackInfo pack in PackStore.Packs) if (pack.Enabled && pack.Texture2DCount > 0) hadTexture2D = true;

            string error = PackStore.DisableAll();
            string error2 = StitchStore.DisableAll();
            Engine.Request();
            SpriteEngine.Request();
            RequestExtras();
            if (error != null || error2 != null)
            {
                status = L.T("Не вышло: ", "Failed: ") + (error ?? error2);
                return;
            }
            status = L.T("Все скины выключены (обычные текстуры).", "All skins off (vanilla textures).");
            if (hadTexture2D) status += MenuTextures(status, false, true);
        }

        /// Меню и интерфейс скинов (папки Texture2D) применяет CustomizerT2D. После любого переключения просим его
        /// перечитать включённые скины; текстуры выключенных при этом получают родной вид обратно (см. T2D.cs).
        /// Это может занять пару секунд, поэтому сперва показываем надпись, а делаем через два кадра.
        /// gained — включили скин с такой папкой, lost — выключили. Возвращает добавку к сообщению.
        string MenuTextures(string note, bool gained, bool lost)
        {
            string restartNote = L.T(" Меню и интерфейс станут обычными после перезапуска игры.",
                " Menus and UI go back to normal after a game restart.");
            if (!gained)
            {
                if (!T2D.Available()) return "";           // без CustomizerT2D их никто и не менял
                if (!T2D.CanRestore) return restartNote;   // по-старому: вернуть нечем
            }
            Later(delegate
            {
                string error = T2D.Reload();
                string done;
                if (error != null) done = L.T(" Меню и интерфейс не применились: ", " Menus and UI failed: ") + error;
                else if (gained) done = L.T(" Меню и интерфейс применены.", " Menus and UI applied.");
                else done = L.T(" Меню и интерфейс снова обычные.", " Menus and UI are back to normal.");
                if (error == null && lost && !T2D.CanRestore) done += restartNote;
                status = note + done;
            });
            return gained
                ? L.T(" Применяю меню и интерфейс, это может занять несколько секунд…",
                    " Applying menus and UI, this can take a few seconds…")
                : L.T(" Возвращаю обычные меню и интерфейс…", " Restoring the normal menus and UI…");
        }

        void Rescan()
        {
            Reload();
            status = L.T("Список обновлён", "List refreshed");
            if (Installer.HasNew) AutoInstall();
        }
    }

    /// Невидимая «стена» из интерфейса Unity поверх всех окон игры, пока открыто наше окно.
    /// Без неё клик по нашей кнопке мог бы попасть и в кнопку игры под ней (например, «Выйти в меню»).
    internal sealed class ClickBlocker
    {
        GameObject root;

        internal void Show()
        {
            try
            {
                if (root == null) Build();
                root.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: не удалось поставить защиту от кликов: " + e.Message);
            }
        }

        internal void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        void Build()
        {
            root = new GameObject("Exuvia.ClickBlocker");
            UnityEngine.Object.DontDestroyOnLoad(root);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            root.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var wall = new GameObject("Wall", typeof(RectTransform));
            wall.transform.SetParent(root.transform, false);
            var rect = (RectTransform)wall.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var img = wall.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(0f, 0f, 0f, 0.004f); // почти прозрачный, но ловит клики
            img.raycastTarget = true;
        }
    }
}
