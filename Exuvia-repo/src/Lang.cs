// ============================================================================
//  Lang.cs — язык интерфейса (ru / en).
//  Все надписи мода двуязычные: L.T("по-русски", "in English").
//  Выбор языка лежит в конфиге (Language = auto / ru / en), см. Plugin.cs.
// ============================================================================
using UnityEngine;

namespace Exuvia
{
    internal static class L
    {
        /// "auto" (по языку системы), "ru" или "en". Выставляется из конфига.
        internal static string Mode = "auto";

        internal static bool IsRu
        {
            get
            {
                if (Mode == "ru") return true;
                if (Mode == "en") return false;
                SystemLanguage lang = Application.systemLanguage;
                return lang == SystemLanguage.Russian || lang == SystemLanguage.Ukrainian
                    || lang == SystemLanguage.Belarusian;
            }
        }

        internal static string T(string ru, string en)
        {
            return IsRu ? ru : en;
        }
    }
}
