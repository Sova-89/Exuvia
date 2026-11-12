// ============================================================================
//  Video.cs — видео-заставки из скинов (папки Video и VideoClip внутри пака).
//
//  Раньше это делал отдельный мод CustomizerCinematic: он проигрывал своё видео поверх
//  заставки игры и подгонял к ней звук, пропуск и затемнения. Здесь проще: игра сама умеет
//  брать заставку из файла. Её проигрыватель (TeamCherry.Cinematics) спрашивает путь к файлу
//  заставки (GetAbsolutePath) и, если файл есть, играет его вместо встроенного ролика. Мы
//  подставляем в этот ответ файл из включённого скина — пропуск, звук и затемнения остаются
//  игровыми.
//
//  На случай заставок, которые игра проигрывает мимо этого проигрывателя (обычный VideoPlayer
//  на сцене): перед подготовкой и запуском такого проигрывателя и на загрузке сцены заменяем
//  ему встроенный ролик на файл из скина.
//
//  Имя файла = имя ролика в игре (можно с хвостом _30, _60, _30_fps, _60_fps), расширение
//  .webm или .mp4. Правило поиска — PackStore.VideoFor.
//  Если в игре стоит CustomizerCinematic, заставками занимается он, мы не вмешиваемся.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;

namespace Exuvia
{
    internal static class VideoHook
    {
        const string TheirType = "CustomizerCinematicOverlayPlayer.Plugin";
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        static bool enabled;
        static MethodInfo getConfig, getReference, getClip;
        static readonly HashSet<string> told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// Включаем свою подмену заставок. Звать из Start, когда все моды уже загружены.
        internal static void Enable()
        {
            if (enabled) return;
            if (FindType(TheirType) != null)
            {
                Plugin.Log.LogInfo("Exuvia: в игре стоит CustomizerCinematic — видео-заставками скинов занимается "
                    + "он.");
                return;
            }
            enabled = true;
            const BindingFlags Mine = BindingFlags.Static | BindingFlags.NonPublic;
            var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".video");
            int ok = 0;

            // Проигрыватель заставок игры: подставляем путь к файлу из скина.
            try
            {
                Type desktop = FindType("TeamCherry.Cinematics.DesktopCinematicVideoPlayer");
                Type basePlayer = FindType("TeamCherry.Cinematics.CinematicVideoPlayer");
                Type config = FindType("TeamCherry.Cinematics.CinematicVideoPlayerConfig");
                Type reference = FindType("TeamCherry.Cinematics.CinematicVideoReference");
                if (desktop == null || basePlayer == null || config == null || reference == null)
                    throw new Exception("нет проигрывателя заставок TeamCherry.Cinematics");
                MethodInfo path = desktop.GetMethod("GetAbsolutePath", Any);
                getConfig = basePlayer.GetMethod("get_Config", Any);
                getReference = config.GetMethod("get_VideoReference", Any);
                getClip = reference.GetMethod("get_EmbeddedVideoClip", Any);
                if (path == null || getConfig == null || getReference == null)
                    throw new Exception("нет ожидаемых методов (другая версия игры?)");
                harmony.Patch(path, null, new HarmonyLib.HarmonyMethod(typeof(VideoHook).GetMethod("PathPostfix",
                    Mine)));
                ok++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: перехват проигрывателя заставок не поставился (" + e.Message + ").");
            }

            // Обычные проигрыватели видео на сцене.
            try
            {
                var prefix = new HarmonyLib.HarmonyMethod(typeof(VideoHook).GetMethod("PlayerPrefix", Mine));
                harmony.Patch(typeof(VideoPlayer).GetMethod("Prepare", Type.EmptyTypes), prefix);
                harmony.Patch(typeof(VideoPlayer).GetMethod("Play", Type.EmptyTypes), prefix);
                ok++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: перехват проигрывателей видео не поставился (" + e.Message + ").");
            }

            if (ok > 0)
                Plugin.Log.LogInfo("Exuvia: видео-заставки из скинов (папки Video, VideoClip) подставляю сам; во "
                    + "включённых скинах их сейчас: " + PackStore.VideoCount + ".");
        }

        /// Игра спросила, где лежит файл заставки. Если во включённом скине есть своя — отвечаем её путём.
        static void PathPostfix(object __instance, ref string __result)
        {
            try
            {
                if (PackStore.VideoCount == 0) return;
                object config = getConfig.Invoke(__instance, null);
                UnityEngine.Object reference = config != null ? getReference.Invoke(config,
                    null) as UnityEngine.Object : null;
                if (reference == null) return;
                UnityEngine.Object clip = getClip != null ? getClip.Invoke(reference,
                    null) as UnityEngine.Object : null;

                // Сначала по имени встроенного ролика (так искал CustomizerCinematic), потом по имени самой заставки.
                string name = clip != null ? clip.name : null;
                string path = PackStore.VideoFor(name);
                if (path == null)
                {
                    name = reference.name;
                    path = PackStore.VideoFor(name);
                }
                if (path == null || !System.IO.File.Exists(path)) return;
                __result = path;
                Tell(name, path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: заставку из скина подставить не вышло: " + e.Message);
            }
        }

        /// Обычный проигрыватель видео готовится или запускается: если для его ролика есть файл в скине — играем файл.
        static void PlayerPrefix(VideoPlayer __instance)
        {
            Swap(__instance);
        }

        static void Swap(VideoPlayer player)
        {
            try
            {
                if (player == null || PackStore.VideoCount == 0) return;
                if (player.source != VideoSource.VideoClip) return;
                VideoClip clip = player.clip;
                if (clip == null) return;
                string path = PackStore.VideoFor(clip.name);
                if (path == null || !System.IO.File.Exists(path)) return;
                player.url = new Uri(path).AbsoluteUri;
                player.source = VideoSource.Url;
                Tell(clip.name, path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: видео из скина подставить не вышло: " + e.Message);
            }
        }

        /// Сцена загрузилась: проигрыватели, которые запускаются сами, без команды из кода.
        internal static void OnSceneLoaded()
        {
            if (!enabled || PackStore.VideoCount == 0) return;
            try
            {
                VideoPlayer[] players = UnityEngine.Object.FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int i = 0; i < players.Length; i++) Swap(players[i]);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Exuvia: поиск проигрывателей видео на сцене не удался: " + e.Message);
            }
        }

        static void Tell(string name, string path)
        {
            if (!told.Add(name + "|" + path)) return;
            Plugin.Log.LogInfo("Exuvia: заставка «" + name + "» берётся из скина: "
                + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(path)))
                    + "\\" + System.IO.Path.GetFileName(path));
        }

        static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    Type type = asm.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}
