// ============================================================================
//  TextEngine.cs — подмена строк игры текстами из папок Text паков Stitchwork.
//  Файлы: Text\<лист>\<ЯЗЫК>.yml (например Text\UI\EN.yml), строки вида  КЛЮЧ: "значение".
//  Игра спрашивает строку по листу и ключу — если в паке есть своя, отдаём её.
//  Уже показанный на экране текст меняется, когда игра спросит его снова
//  (например, при повторном открытии инвентаря).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using TeamCherry.Localization;
using UnityEngine;

namespace Exuvia
{
    internal static class TextEngine
    {
        const float CondEvery = 0.5f;

        /// Выключатель из настроек.
        internal static bool Enabled = true;

        static List<string> dirs = new List<string>();
        static string state = "";
        static readonly Dictionary<string, Dictionary<string, string>> sheets =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static bool patched;
        static float nextCond;
        static bool langKnown;
        static LanguageCode langCode;
        static string langName = "";

        internal static int DirCount { get { return dirs.Count; } }

        internal static void Request()
        {
            nextCond = 0f;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < nextCond) return;
            nextCond = now + CondEvery;
            try
            {
                CondCtx ctx = SpriteEngine.MakeCtx();
                string st = Enabled ? StitchStore.State(ctx) : "off";
                if (st == state) return;
                state = st;
                dirs = Enabled ? StitchStore.TextDirs(ctx) : new List<string>();
                sheets.Clear();
                if (dirs.Count > 0 && !patched) Patch();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Exuvia: ошибка при подготовке текстов: " + e);
            }
        }

        static void Patch()
        {
            patched = true;
            try
            {
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".texts");
                MethodInfo get = typeof(Language).GetMethod("Get",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(string) }, null);
                if (get == null)
                {
                    Plugin.Log.LogWarning("Exuvia: тексты: в игре не найден метод Language.Get, тексты из паков не "
                        + "применяются.");
                    return;
                }
                harmony.Patch(get, null, new HarmonyLib.HarmonyMethod(typeof(TextEngine).GetMethod("AfterGet",
                    BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: тексты: перехват не поставился (" + e.Message
                    + "), тексты из паков не применяются.");
            }
        }

        static void AfterGet(string key, string sheetTitle, ref string __result)
        {
            if (dirs.Count == 0 || key == null || sheetTitle == null) return;
            try
            {
                LanguageCode code = Language.CurrentLanguage();
                if (!langKnown || code != langCode)
                {
                    langKnown = true;
                    langCode = code;
                    langName = code.ToString();
                }
                string lang = langName;
                string id = lang + "|" + sheetTitle;
                Dictionary<string, string> map;
                if (!sheets.TryGetValue(id, out map))
                {
                    map = new Dictionary<string, string>();
                    foreach (string dir in dirs) // поздние папки важнее
                    {
                        string file = System.IO.Path.Combine(System.IO.Path.Combine(dir, sheetTitle), lang + ".yml");
                        foreach (KeyValuePair<string, string> pair in StitchText.ParseSheet(file))
                            map[pair.Key] = pair.Value;
                    }
                    sheets[id] = map;
                    if (map.Count > 0) Plugin.Log.LogInfo("Exuvia: тексты: лист " + sheetTitle + " (" + lang
                        + "), своих строк " + map.Count);
                }
                string value;
                if (map.Count > 0 && map.TryGetValue(key, out value)) __result = value;
            }
            catch (Exception) { }
        }
    }
}
