// ============================================================================
//  StitchPack.cs — один скин формата Stitchwork: слои и подобранные картинки.
//  Раньше было частью Stitch.cs (общее описание — там).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Exuvia
{
    /// Слой пака: основной (Sprites\) или набор (Sets\<имя>\). Выше слой — важнее.
    internal sealed class StitchLayer
    {
        internal string Name = "";            // "" — основной слой
        internal CondNode When;               // null — работает всегда
        internal string WhenText = "";        // для окна: что за условие
        internal string Dir;                  // папка, в которой лежит Sprites
        internal readonly Dictionary<string, SpriteFile> Sprites =
            new Dictionary<string, SpriteFile>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, T2DFile> T2D =
            new Dictionary<string, T2DFile>(StringComparer.OrdinalIgnoreCase);
        // имя звука -> файл
        internal readonly Dictionary<string, string> Sounds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal int T2DFiles;
        internal int Index;                   // номер слоя в паке (для подписи состояния)
    }

    internal sealed class StitchPack
    {
        internal string Name;
        internal string Author = "";
        internal string Description = "";
        internal string Dir;                  // папка Stitchwork (или Patchwork)
        internal bool Enabled;
        internal readonly List<StitchLayer> Layers = new List<StitchLayer>(); // сверху вниз; основной слой последний
        internal readonly Dictionary<string, CondNode> Named =
            new Dictionary<string, CondNode>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal int SpriteCount;
        internal int T2DFiles;
        internal int SoundFiles;
        internal bool HasText;
        internal bool HasRecolor;
        internal long Bytes;
        internal List<string> Problems = new List<string>();

        /// Какие слои сейчас действуют (по условиям). Подпись нужна, чтобы замечать перемену.
        internal string ActiveSignature(CondCtx ctx, out List<StitchLayer> active)
        {
            active = new List<StitchLayer>();
            var builder = new StringBuilder();
            foreach (StitchLayer layer in Layers)
            {
                bool on = layer.When == null || layer.When.Eval(ctx, 0);
                if (on) { active.Add(layer); builder.Append(layer.Index).Append(','); }
            }
            return builder.ToString();
        }

        /// Итоговые файлы спрайтов при данных условиях: выше слой — побеждает.
        internal Dictionary<string, SpriteFile> Effective(List<StitchLayer> active)
        {
            var map = new Dictionary<string, SpriteFile>(StringComparer.OrdinalIgnoreCase);
            for (int i = active.Count - 1; i >= 0; i--)
                foreach (KeyValuePair<string, SpriteFile> pair in active[i].Sprites) map[pair.Key] = pair.Value;
            return map;
        }
    }

    /// Что должно быть подменено в одной коллекции.
    internal sealed class Wanted
    {
        internal string Collection;
        // "атлас|спрайт" строчными
        internal readonly Dictionary<string, SpriteFile> Files = new Dictionary<string, SpriteFile>();
        internal string Signature = "";

        /// Подпись набора файлов: меняется, если изменился хоть один файл или их состав.
        internal void Seal()
        {
            var keys = new List<string>(Files.Keys);
            keys.Sort(StringComparer.Ordinal);
            ulong h = 1469598103934665603UL;
            foreach (string k in keys)
            {
                SpriteFile file = Files[k];
                h = Mix(h, k);
                h = Mix(h, file.Path);
                h = (h ^ (ulong)file.Len) * 1099511628211UL;
                h = (h ^ (ulong)file.Ticks) * 1099511628211UL;
            }
            Signature = keys.Count + ":" + h.ToString("x");
        }

        static ulong Mix(ulong h, string s)
        {
            for (int i = 0; i < s.Length; i++) h = (h ^ s[i]) * 1099511628211UL;
            return h;
        }
    }

    // ---- общие подпрограммы --------------------------------------------------
}
