// ============================================================================
//  SoundEngine.cs — подмена звуков игры файлами из папок Sounds паков Stitchwork.
//
//  Как работает: перед тем как игра проигрывает звук, смотрим его имя. Если во включённом
//  паке есть файл с таким именем (Sounds\...\<имя>.wav / .ogg / .mp3) — играем его.
//  Родной звук запоминается: после выключения пака источник снова играет своё.
//  Наборы по условиям (например, звуки только в одной сцене) учитываются.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace Exuvia
{
    internal static class SoundEngine
    {
        const float CondEvery = 0.5f;

        /// Выключатель из настроек.
        internal static bool Enabled = true;

        static Dictionary<string, string> wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string state = "";
        // файл -> звук
        static readonly Dictionary<string, AudioClip> clips =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, AudioClip> originalOfOurs = new Dictionary<int, AudioClip>();
        static readonly Dictionary<int, string> pathOfOurs = new Dictionary<int, string>();
        static bool patched, busy;
        static float nextCond;

        internal static int WantedCount { get { return wanted.Count; } }

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
                wanted = Enabled ? StitchStore.BuildWantedSounds(ctx) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (wanted.Count > 0)
                {
                    if (!patched) Patch();
                    Plugin.Log.LogInfo("Exuvia: звуков в действующих паках: " + wanted.Count);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Exuvia: ошибка при подготовке звуков: " + e);
            }
        }

        // ---- перехват проигрывания ---------------------------------------------

        static void Patch()
        {
            patched = true;
            try
            {
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".sounds");
                const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                MethodInfo play = typeof(AudioSource).GetMethod("PlayHelper", any, null, new[] { typeof(AudioSource),
                    typeof(ulong) }, null);
                MethodInfo shot = typeof(AudioSource).GetMethod("PlayOneShotHelper", any, null,
                    new[] { typeof(AudioSource), typeof(AudioClip), typeof(float) }, null);
                if (play != null) harmony.Patch(play,
                    new HarmonyLib.HarmonyMethod(typeof(SoundEngine).GetMethod("BeforePlay", any)));
                if (shot != null) harmony.Patch(shot,
                    new HarmonyLib.HarmonyMethod(typeof(SoundEngine).GetMethod("BeforeOneShot", any)));
                if (play == null || shot == null)
                    Plugin.Log.LogWarning("Exuvia: звуки: не найдены методы проигрывания Unity, часть звуков не "
                    + "подменится.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: звуки: перехват не поставился (" + e.Message
                    + "), звуки из паков не применяются.");
            }
        }

        static void BeforePlay(AudioSource source)
        {
            if (busy || source == null) return;
            try
            {
                AudioClip clip = source.clip;
                if (clip == null) return;

                // Сейчас на источнике наш звук: проверяем, нужен ли он ещё.
                AudioClip original;
                if (originalOfOurs.TryGetValue(clip.GetInstanceID(), out original))
                {
                    string path;
                    if (original != null && wanted.TryGetValue(original.name, out path)
                        && path == pathOfOurs[clip.GetInstanceID()]) return;
                    if (original == null) return;
                    Set(source, original);
                    clip = original;
                }
                if (wanted.Count == 0) return;

                AudioClip ours = Find(clip);
                if (ours != null) Set(source, ours);
            }
            catch (Exception) { }
        }

        static void BeforeOneShot(ref AudioClip clip)
        {
            if (busy || clip == null || wanted.Count == 0) return;
            try
            {
                AudioClip ours = Find(clip);
                if (ours != null) clip = ours;
            }
            catch (Exception) { }
        }

        static void Set(AudioSource source, AudioClip clip)
        {
            busy = true;
            try { source.clip = clip; }
            finally { busy = false; }
        }

        static AudioClip Find(AudioClip vanilla)
        {
            string name = vanilla.name;
            string path;
            if (string.IsNullOrEmpty(name) || !wanted.TryGetValue(name, out path)) return null;
            AudioClip ours;
            if (clips.TryGetValue(path, out ours) && ours != null)
            {
                originalOfOurs[ours.GetInstanceID()] = vanilla;
                return ours;
            }
            if (failed.Contains(path)) return null;

            ours = Load(path, name);
            if (ours == null)
            {
                failed.Add(path);
                return null;
            }
            ours.hideFlags = HideFlags.HideAndDontSave;
            clips[path] = ours;
            originalOfOurs[ours.GetInstanceID()] = vanilla;
            pathOfOurs[ours.GetInstanceID()] = path;
            if (ours.frequency != vanilla.frequency || ours.channels != vanilla.channels)
                Plugin.Log.LogInfo("Exuvia: звук " + name + ": в паке " + ours.frequency + " Гц, каналов "
                    + ours.channels
                    + "; в игре " + vanilla.frequency + " Гц, каналов " + vanilla.channels);
            return ours;
        }

        // ---- чтение файла ------------------------------------------------------

        static AudioClip Load(string path, string name)
        {
            try
            {
                if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    float[] samples;
                    int channels, rate;
                    string error = Wav.Parse(File.ReadAllBytes(path), out samples, out channels, out rate);
                    if (error == null && samples.Length >= channels)
                    {
                        AudioClip clip = AudioClip.Create("Exuvia "
                            + name, samples.Length / channels, channels, rate, false);
                        clip.SetData(samples, 0);
                        return clip;
                    }
                    if (error != null) Plugin.Log.LogInfo("Exuvia: звук " + path + ": " + error
                        + " — пробую через Unity.");
                }
                return LoadWithUnity(path, name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: звук " + path + ": " + e.Message);
                return null;
            }
        }

        /// ogg, mp3 и необычные wav читает сам Unity. Файл локальный, поэтому ждём прямо тут, но не дольше 5 секунд.
        static AudioClip LoadWithUnity(string path, string name)
        {
            string url = "file:///" + Uri.EscapeUriString(path.Replace("\\", "/"));
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!operation.isDone && watch.ElapsedMilliseconds < 5000) { }
                if (!operation.isDone || request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogWarning("Exuvia: звук " + path + " не прочитан: "
                        + (operation.isDone ? request.error : "слишком долго"));
                    return null;
                }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip != null) clip.name = "Exuvia " + name;
                return clip;
            }
        }
    }
}
