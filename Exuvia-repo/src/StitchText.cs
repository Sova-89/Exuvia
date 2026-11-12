// ============================================================================
//  StitchText.cs — чтение файлов с текстами (Text\\<лист>\\<ЯЗЫК>.yml).
//  Раньше было частью Stitch.cs (общее описание — там).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Exuvia
{
    /// Файлы текстов: Text\<лист>\<ЯЗЫК>.yml, строки вида  КЛЮЧ: "значение".
    internal static class StitchText
    {
        internal static Dictionary<string, string> ParseSheet(string path)
        {
            var map = new Dictionary<string, string>();
            if (!File.Exists(path)) return map;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim().TrimStart('\uFEFF');
                if (key.Length == 0) continue;
                map[key] = Unquote(line.Substring(colon + 1));
            }
            return map;
        }

        /// Снимает кавычки; внутри кавычек \" значит кавычку, \\ значит обратную черту (как у Stitchwork).
        internal static string Unquote(string raw)
        {
            string s = raw.Trim();
            int last = s.LastIndexOf('"');
            if (s.Length == 0 || s[0] != '"' || last <= 0) return s.Trim('"').Replace("\\\"", "\"");
            var builder = new StringBuilder(last);
            for (int i = 1; i < last; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < last && (s[i + 1] == '\\' || s[i + 1] == '"')) c = s[++i];
                builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
