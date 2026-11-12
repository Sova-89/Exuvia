// ============================================================================
//  T2D.cs — мост к CustomizerT2D (он применяет папки Texture2D внутри паков:
//  меню, интерфейс, часть текстур Хорнет).
//
//  CustomizerT2D сам по себе умеет только включать: он записывает картинку пака
//  прямо в текстуру игры и оригинал не хранит, а если ни один пак не включён —
//  применяет папки Texture2D ВСЕХ паков подряд. Поэтому здесь три вещи:
//    1. Reload() — просим его перечитать паки сразу после переключения (то же
//       делает его клавиша F4), а не при следующем запуске игры.
//    2. Список паков ему отдаём сами (только включённые; пусто — значит пусто).
//    3. Перед тем как он запишет картинку в текстуру игры, снимаем с этой текстуры
//       копию (в видеопамяти, в родном сжатом виде). Когда пак выключают —
//       возвращаем текстуре родное содержимое из копии. Перезапуск игры не нужен.
//    4. Список картинок паков составляем за него. Сам он при запуске игры и при
//       каждом переключении читал и полностью расшифровывал каждую картинку только
//       ради её размера (45 картинок — около 3 секунд) и держал все файлы в памяти.
//       Мы берём размер из заголовка файла (и помним его, см. Files.cs), а сам файл
//       читаем в тот момент, когда игре действительно понадобилась эта текстура.
//    5. Саму подмену тоже делаем за него (FastSwap). Он расшифровывал картинку прямо в главном
//       потоке (ImageConversion.LoadImage, 20–130 мс на текстуру — около секунды замирания на скин)
//       и делал это заново при каждом включении. Мы расшифровываем в фоновом потоке (Png.cs) в свою
//       текстуру-копию, а в текстуру игры переносим готовое копированием внутри видеокарты. Сжатую
//       текстуру игры перед первым переносом один раз превращаем в обычную несжатую: загружаем в неё
//       пустую картинку того же размера (Png.Blank — расшифровывается почти мгновенно). Текстуры
//       вида DXT так не превратить (Unity при загрузке картинки снова сжимает их в DXT), поэтому их
//       первый раз подменяем по-старому и тут же снимаем с результата копию в том же сжатом виде —
//       дальше картинка возвращается копированием (DxtSwap). Копии
//       недавних скинов остаются в памяти (общий предел — Spare.cs), поэтому повторное включение мгновенное.
//       Не получилось по-новому — подменяем по-старому, как делал он.
//  Если что-то из этого не поставилось (другая версия CustomizerT2D) — работает
//  по-старому: выключение меню/интерфейса только после перезапуска игры.
//
//  СВОЙ РЕЖИМ (без CustomizerT2D). Если CustomizerT2D в игре нет, Exuvia делает его работу сам
//  (EnableOwn): тот же список картинок (ключ — PackStore.TextureKey), та же проверка размера, та же
//  подмена (FastSwap / OldSwap) и возврат родного вида. Текстуры игры находим так же, как он:
//    • перехваты: материалу назначили текстуру, SpriteRenderer или картинке интерфейса назначили спрайт;
//    • обход всех текстур: при перечитывании паков, на загрузке сцены и понемногу в фоне
//      (раз в ScanTick секунд не дольше ScanBudgetMs, новый список текстур — не чаще SnapshotEvery).
//  Exuvia при этом ставится один, без Customizer.
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Exuvia
{
    internal static partial class T2D
    {
        const string TypeName = "CustomizerT2DOnly.CustomizerT2DOnly";
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// Текстура игры и её родное содержимое.
        sealed class Saved
        {
            internal Texture2D Tex;
            internal Texture2D Copy;
            internal string Key;
            internal bool WasReadable;
            internal bool Applied; // сейчас в текстуре картинка пака (а не родная)
            // Картинка какого файла сейчас в текстуре (чтобы не переносить то же самое повторно).
            internal string ShownPath;
            internal long ShownLen, ShownTicks;
        }

        /// Расшифрованная картинка пака в видеопамяти: из неё содержимое переносится в текстуру игры.
        sealed class SkinCopy
        {
            internal string Path;
            internal long Len, Ticks, Bytes;
            internal Texture2D Tex;
            internal bool Ready, Mips;
            internal bool Native; // копия в том же сжатом виде, что и текстура игры (для DXT)
            internal long Used; // отметка Spare.Next()
            internal readonly List<Saved> Waiting = new List<Saved>();
        }

        static readonly Dictionary<int, Saved> saved = new Dictionary<int, Saved>();
        static bool hookTried, hooked, backupBroken;
        static int failedCopies;
        static FieldInfo keysField, mapField;
        static MethodInfo normalize;
        static HashSet<int> replacedIds, notOurs;
        static object owner;

        // Свой список картинок паков: ключ текстуры -> файл. null — список составил сам CustomizerT2D.
        static Dictionary<string, string> lazyPaths;
        static bool lazyBroken;
        static readonly byte[] NoBytes = new byte[0];

        // Быстрая подмена (см. шапку, пункт 5).
        // после первой подмены за кадр делаем следующую, только если уложились в это время
        const double TransferBudgetMs = 4.0;
        static Dictionary<string, FileRec> lazyRecs;
        static readonly Dictionary<string, SkinCopy> copies =
            new Dictionary<string, SkinCopy>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<KeyValuePair<SkinCopy, Saved>> transfers = new Queue<KeyValuePair<SkinCopy, Saved>>();
        static readonly object jobOwner = new object();
        static bool fastBroken;
        static int copiesLoading, transferWarnings;
        static System.Diagnostics.Stopwatch batchClock;
        static int batchDecoded, batchMoved, batchOld;
        static double batchDecodeMs, batchUploadMs, batchMoveMs, batchWorstMs;

        /// Расшифровывать картинки меню и интерфейса в фоне (та же настройка, что у атласов: General/BackgroundDecode).
        internal static bool Fast = true;

        const int BlankWaitFrames = 600;     // сколько кадров перенос готов ждать пустую картинку от фоновых потоков

        // Свой режим (см. шапку).
        const float ScanTick = 0.5f;         // как часто продолжать фоновый обход текстур, секунд
        const double ScanBudgetMs = 0.5;     // сколько времени кадра можно на него тратить
        const float SnapshotEvery = 3f;      // новый список всех текстур игры берём не чаще, секунд
        // столько запомненных текстур — и память о них сбрасывается (как у CustomizerT2D)
        const int ForgetAfter = 16384;
        static bool own, inOwn;
        static HashSet<string> ownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, int[]> ownSizes = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        static Texture2D[] scan;
        static int scanAt;
        static float nextScanTick, snapshotAt;

        /// Работаем сами, без CustomizerT2D.
        internal static bool Own { get { return own; } }
        static int blankWaits, batchPlain;
        static double batchPlainMs;

        // Замер подмен: сколько текстур и за сколько миллисекунд (читает Plugin для журнала).
        internal static int SwapCount;
        internal static double SwapMs;

        /// Есть ли кому подменять меню и интерфейс: CustomizerT2D или мы сами.
        internal static bool Available()
        {
            return own || FindType() != null;
        }

        /// Можем ли вернуть меню и интерфейсу обычный вид без перезапуска игры.
        internal static bool CanRestore { get { return hooked && !backupBroken; } }
    }
}
