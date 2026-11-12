// ============================================================================
//  Png.cs — своя расшифровка PNG в фоновых потоках (без Unity).
//
//  Зачем: Unity расшифровывает PNG только в главном потоке (ImageConversion.LoadImage),
//  и на большом атласе 4096x4096 игра замирает на 0.1–0.3 с. Здесь PNG расшифровывается
//  в фоновом потоке прямо в память будущей текстуры, а главному потоку остаётся только
//  отправить готовое в видеокарту.
//
//  Что умеем: обычные PNG — 8 бит на канал, RGBA или RGB, без чересстрочности. Всё прочее
//  (палитра, 16 бит, серые, чересстрочные, RGB с прозрачным цветом) помечается Supported = false,
//  и такой файл расшифровывает Unity по-старому.
//
//  Строки пишутся снизу вверх — так их хранит Unity (первая строка текстуры — нижняя).
//
//  Png        — разбор файла и расшифровка в указанную память.
//  DecodeJobs — очередь заданий и фоновые потоки.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Exuvia
{
    internal sealed class PngHead
    {
        internal int Width, Height, BitDepth, ColorType, Interlace;
        internal bool HasTrns;
        internal readonly List<int> IdatAt = new List<int>();
        internal readonly List<int> IdatLen = new List<int>();
        /// Не null — файл не PNG или испорчен.
        internal string Problem;

        /// Можем расшифровать сами (иначе пусть читает Unity).
        internal bool Supported
        {
            get { return Problem == null && BitDepth == 8 && Interlace == 0 && (ColorType == 6 || (ColorType == 2
                && !HasTrns)); }
        }
    }

    internal static class Png
    {
        const int MaxSide = 16384;

        /// Начало массива заданной длины (файл может занимать не весь массив).
        struct Span
        {
            readonly byte[] b;
            internal readonly int Length;
            internal Span(byte[] bytes, int len) { b = bytes; Length = bytes == null ? 0 : Math.Min(len,
                bytes.Length); }
            internal byte this[int i] { get { return b[i]; } }
        }

        // Запасы на поток: мелких картинок бывают тысячи, и заводить под каждую новые массивы — значит
        // постоянно дёргать сборщик мусора (он один на все потоки).
        [ThreadStatic] static byte[] fileBuf;
        [ThreadStatic] static byte[] inflateBuf;
        const int KeepFileBuf = 1 << 20; // файлы крупнее читаем в отдельный массив и не держим его

        /// Читает файл целиком. Для небольших файлов возвращает общий массив этого потока (он длиннее файла,
        /// настоящая длина — в len), поэтому результат годится только до следующего вызова в том же потоке.
        internal static byte[] ReadFile(string path, out int len)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                FileOptions.SequentialScan))
            {
                long size = stream.Length;
                if (size > int.MaxValue - 64) throw new IOException("файл слишком большой");
                len = (int)size;
                byte[] buf;
                if (len > KeepFileBuf) buf = new byte[len];
                else
                {
                    buf = fileBuf;
                    if (buf == null || buf.Length < len) fileBuf = buf = new byte[Math.Max(len, 64 * 1024)];
                }
                int got = 0;
                while (got < len)
                {
                    int n = stream.Read(buf, got, len - got);
                    if (n <= 0) throw new IOException("файл обрезан");
                    got += n;
                }
                return buf;
            }
        }

        /// Разбирает файл на части: размеры, вид пикселей, где лежат сжатые данные.
        internal static PngHead Parse(byte[] f)
        {
            return Parse(f, f != null ? f.Length : 0);
        }

        /// То же для массива, в котором файл занимает только первые len байт.
        internal static PngHead Parse(byte[] file, int fileLen)
        {
            var f = new Span(file, fileLen);
            var head = new PngHead();
            if (file == null || f.Length < 8 + 25 || f[0] != 0x89 || f[1] != 0x50 || f[2] != 0x4E || f[3] != 0x47
                || f[4] != 0x0D || f[5] != 0x0A || f[6] != 0x1A || f[7] != 0x0A)
            {
                head.Problem = "это не PNG";
                return head;
            }

            int pos = 8;
            bool gotHeader = false;
            while (pos + 8 <= f.Length)
            {
                uint ulen = ((uint)f[pos] << 24) | ((uint)f[pos + 1] << 16) | ((uint)f[pos + 2] << 8) | f[pos + 3];
                int data = pos + 8;
                if (ulen > (uint)(f.Length - data))
                {
                    head.Problem = "файл обрезан";
                    return head;
                }
                int len = (int)ulen;
                byte a = f[pos + 4], b = f[pos + 5], c = f[pos + 6], d = f[pos + 7];

                if (a == 'I' && b == 'H' && c == 'D' && d == 'R')
                {
                    if (len < 13) { head.Problem = "испорчен заголовок"; return head; }
                    uint w = ((uint)f[data] << 24) | ((uint)f[data + 1] << 16) | ((uint)f[data + 2] << 8) | f[data + 3];
                    uint h = ((uint)f[data + 4] << 24) | ((uint)f[data + 5] << 16) | ((uint)f[data + 6] << 8) | f[data + 7];
                    if (w == 0 || h == 0 || w > MaxSide
                        || h > MaxSide) { head.Problem = "странный размер картинки"; return head; }
                    head.Width = (int)w;
                    head.Height = (int)h;
                    head.BitDepth = f[data + 8];
                    head.ColorType = f[data + 9];
                    if (f[data + 10] != 0 || f[data
                        + 11] != 0) { head.Problem = "неизвестный способ сжатия"; return head; }
                    head.Interlace = f[data + 12];
                    gotHeader = true;
                }
                else if (a == 'I' && b == 'D' && c == 'A' && d == 'T')
                {
                    if (len > 0)
                    {
                        head.IdatAt.Add(data);
                        head.IdatLen.Add(len);
                    }
                }
                else if (a == 't' && b == 'R' && c == 'N' && d == 'S')
                {
                    head.HasTrns = true;
                }
                else if (a == 'I' && b == 'E' && c == 'N' && d == 'D')
                {
                    break;
                }

                long next = (long)data + len + 4; // + контрольная сумма
                if (next > f.Length) break;       // хвост без суммы — не страшно, если данные уже собраны
                pos = (int)next;
            }

            if (!gotHeader) head.Problem = "нет заголовка";
            else if (head.IdatAt.Count == 0) head.Problem = "нет данных картинки";
            // Файл без завершающей части (IEND) всё равно пробуем: расшифровка сама скажет, если данных не хватит.
            return head;
        }

        /// Расшифровывает картинку в память dst (RGBA, по 4 байта, строки снизу вверх).
        /// Возвращает null, если всё получилось, иначе — что не так.
        internal static unsafe string Decode(byte[] f, PngHead head, byte* dst, long dstLen)
        {
            return Decode(f, head, dst, head != null ? (long)head.Width * 4 : 0, dstLen);
        }

        /// То же, но картинка пишется внутрь большей (на лист): dst — её левый нижний пиксель на листе,
        /// dstStride — сколько байт от строки до строки листа, dstLen — сколько байт листа доступно начиная с dst.
        internal static unsafe string Decode(byte[] f, PngHead head, byte* dst, long dstStride, long dstLen)
        {
            if (head == null || !head.Supported) return "такой PNG сами не читаем";
            int w = head.Width, h = head.Height;
            int bpp = head.ColorType == 6 ? 4 : 3;
            int stride = w * bpp;
            long rowBytes = dstStride;
            if (dst == null || dstStride < (long)w * 4 || dstLen < dstStride * (h - 1) + (long)w * 4)
                return "под картинку выделено мало памяти";

            using (var seg = new IdatStream(f, head))
            {
                int cmf = seg.ReadByte(), flg = seg.ReadByte();
                if (cmf < 0 || flg < 0) return "данные обрезаны";
                if ((cmf & 0x0F) != 8 || ((cmf << 8) + flg) % 31 != 0 || (flg & 0x20) != 0)
                    return "неизвестный способ сжатия";

                using (var inflate = new DeflateStream(seg, CompressionMode.Decompress))
                {
                    // Распакованные данные берём большими кусками, а не по строке: каждое обращение к
                    // распаковщику стоит заметно, а у мелких картинок строк тысячи.
                    int rowLen = stride + 1;
                    byte[] buf = inflateBuf;
                    int wantBuf = Math.Max(256 * 1024, rowLen);
                    if (buf == null || buf.Length < wantBuf) inflateBuf = buf = new byte[wantBuf];
                    int have = 0, pos = 0;
                    bool eof = false;
                    byte[] rowA = bpp == 3 ? new byte[stride] : null;
                    byte[] rowB = bpp == 3 ? new byte[stride] : null;

                    fixed (byte* pbuf = buf, pa = rowA, pb = rowB)
                    {
                        byte* cur = pa, prev = pb;
                        for (int y = 0; y < h; y++)
                        {
                            if (have - pos < rowLen)
                            {
                                if (pos > 0)
                                {
                                    if (have > pos) Buffer.BlockCopy(buf, pos, buf, 0, have - pos);
                                    have -= pos;
                                    pos = 0;
                                }
                                while (!eof && have < buf.Length)
                                {
                                    int n = inflate.Read(buf, have, buf.Length - have);
                                    if (n <= 0) eof = true;
                                    else have += n;
                                }
                                if (have < rowLen) return "данные обрезаны";
                            }
                            byte* pl = pbuf + pos;
                            pos += rowLen;

                            int filter = pl[0];
                            byte* src = pl + 1;
                            byte* o = dst + (long)(h - 1 - y) * rowBytes;

                            if (bpp == 4)
                            {
                                // Прямо в текстуру: строка выше (уже готовая) лежит сразу за этой.
                                if (!Unfilter(filter, src, o, y > 0 ? o + rowBytes : null, stride, 4))
                                    return "испорчены данные картинки";
                            }
                            else
                            {
                                if (!Unfilter(filter, src, cur, y > 0 ? prev : null, stride, 3))
                                    return "испорчены данные картинки";
                                byte* sp = cur;
                                for (int x = 0; x < w; x++)
                                {
                                    o[0] = sp[0];
                                    o[1] = sp[1];
                                    o[2] = sp[2];
                                    o[3] = 255;
                                    o += 4;
                                    sp += 3;
                                }
                                byte* t = cur;
                                cur = prev;
                                prev = t;
                            }
                        }
                    }
                }
            }
            return null;
        }

        /// Снимает с одной строки PNG «фильтр» (разностное кодирование). p — готовая строка выше, null у первой.
        static unsafe bool Unfilter(int filter, byte* src, byte* o, byte* p, int n, int bpp)
        {
            int i;
            switch (filter)
            {
                case 0:
                    Buffer.MemoryCopy(src, o, n, n);
                    return true;

                case 1: // слева
                    for (i = 0; i < bpp; i++) o[i] = src[i];
                    for (; i < n; i++) o[i] = (byte)(src[i] + o[i - bpp]);
                    return true;

                case 2: // сверху
                    if (p == null) Buffer.MemoryCopy(src, o, n, n);
                    else for (i = 0; i < n; i++) o[i] = (byte)(src[i] + p[i]);
                    return true;

                case 3: // среднее
                    if (p == null)
                    {
                        for (i = 0; i < bpp; i++) o[i] = src[i];
                        for (; i < n; i++) o[i] = (byte)(src[i] + (o[i - bpp] >> 1));
                    }
                    else
                    {
                        for (i = 0; i < bpp; i++) o[i] = (byte)(src[i] + (p[i] >> 1));
                        for (; i < n; i++) o[i] = (byte)(src[i] + ((o[i - bpp] + p[i]) >> 1));
                    }
                    return true;

                case 4: // Paeth
                    if (p == null)
                    {
                        for (i = 0; i < bpp; i++) o[i] = src[i];
                        for (; i < n; i++) o[i] = (byte)(src[i] + o[i - bpp]);
                    }
                    else
                    {
                        for (i = 0; i < bpp; i++) o[i] = (byte)(src[i] + p[i]);
                        for (; i < n; i++)
                        {
                            int a = o[i - bpp], b = p[i], c = p[i - bpp];
                            int pa = b - c; if (pa < 0) pa = -pa;
                            int pb = a - c; if (pb < 0) pb = -pb;
                            int pc = a + b - c - c; if (pc < 0) pc = -pc;
                            int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                            o[i] = (byte)(src[i] + pred);
                        }
                    }
                    return true;
            }
            return false;
        }

        /// Заполняет память нулями (прозрачный чёрный). Можно звать из любого потока.
        internal static unsafe void Clear(byte* p, long n)
        {
            long i = 0;
            for (; i + 8 <= n; i += 8) *(ulong*)(p + i) = 0UL;
            for (; i < n; i++) p[i] = 0;
        }

        // ---- пустая картинка заданного размера -------------------------------------
        // Нужна, чтобы превратить сжатую текстуру игры в обычную несжатую того же размера: Unity умеет
        // это только загрузкой картинки (LoadImage). Пустая (прозрачная чёрная) картинка сжимается в
        // тысячу раз и расшифровывается почти мгновенно — в отличие от настоящей.

        static readonly object blankGate = new object();
        static readonly Dictionary<long, byte[]> blanks = new Dictionary<long, byte[]>();

        /// Уже есть готовая пустая картинка такого размера (тогда Blank вернёт её сразу)?
        internal static bool HasBlank(int w, int h)
        {
            lock (blankGate) { return blanks.ContainsKey(((long)w << 32) | (uint)h); }
        }

        /// PNG w x h, RGBA 8 бит, все пиксели нулевые. Можно звать из любого потока; готовое запоминается.
        internal static byte[] Blank(int w, int h)
        {
            long key = ((long)w << 32) | (uint)h;
            lock (blankGate)
            {
                byte[] ready;
                if (blanks.TryGetValue(key, out ready)) return ready;
            }

            // Данные картинки: h строк, в каждой байт «без фильтра» и 4*w нулей — то есть одни нули.
            long total = (long)h * (4L * w + 1);
            byte[] deflated;
            using (var memory = new MemoryStream())
            {
                using (var inflate = new DeflateStream(memory, CompressionLevel.Optimal, true))
                {
                    byte[] zeros = new byte[65536];
                    long left = total;
                    while (left > 0)
                    {
                        int n = left > zeros.Length ? zeros.Length : (int)left;
                        inflate.Write(zeros, 0, n);
                        left -= n;
                    }
                }
                deflated = memory.ToArray();
            }

            // Обёртка zlib: два байта заголовка, сжатые данные, контрольная сумма Adler-32 исходных данных.
            // Для одних нулей она считается без прохода по данным: a = 1, b = длина mod 65521.
            uint adler = ((uint)(total % 65521) << 16) | 1u;
            byte[] idat = new byte[2 + deflated.Length + 4];
            idat[0] = 0x78;
            idat[1] = 0x9C;
            Buffer.BlockCopy(deflated, 0, idat, 2, deflated.Length);
            WriteBE(idat, idat.Length - 4, adler);

            byte[] ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)w);
            WriteBE(ihdr, 4, (uint)h);
            ihdr[8] = 8; // бит на канал
            ihdr[9] = 6; // RGBA

            byte[] png;
            using (var memory = new MemoryStream())
            {
                memory.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                WriteChunk(memory, "IHDR", ihdr);
                WriteChunk(memory, "IDAT", idat);
                WriteChunk(memory, "IEND", new byte[0]);
                png = memory.ToArray();
            }
            lock (blankGate) { blanks[key] = png; }
            return png;
        }

        static void WriteBE(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] head = new byte[8];
            WriteBE(head, 0, (uint)data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            stream.Write(head, 0, 8);
            stream.Write(data, 0, data.Length);
            uint crc = Crc(head, 4, 4, 0xFFFFFFFFu);
            crc = Crc(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            byte[] tail = new byte[4];
            WriteBE(tail, 0, crc);
            stream.Write(tail, 0, 4);
        }

        static uint[] crcTable;

        static uint Crc(byte[] b, int at, int count, uint crc)
        {
            uint[] t = crcTable;
            if (t == null)
            {
                t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            for (int i = 0; i < count; i++) crc = t[(crc ^ b[at + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static bool ReadFull(Stream stream, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        /// Сжатые данные PNG лежат в файле кусками (IDAT); этот поток читает их подряд, без копирования.
        sealed class IdatStream : Stream
        {
            readonly byte[] f;
            readonly PngHead head;
            int seg, at;

            internal IdatStream(byte[] file, PngHead pngHead)
            {
                f = file;
                head = pngHead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int done = 0;
                while (count > 0 && seg < head.IdatAt.Count)
                {
                    int left = head.IdatLen[seg] - at;
                    if (left <= 0) { seg++; at = 0; continue; }
                    int n = left < count ? left : count;
                    Buffer.BlockCopy(f, head.IdatAt[seg] + at, buffer, offset, n);
                    at += n;
                    offset += n;
                    count -= n;
                    done += n;
                }
                return done;
            }

            public override int ReadByte()
            {
                while (seg < head.IdatAt.Count)
                {
                    if (at < head.IdatLen[seg]) return f[head.IdatAt[seg] + at++];
                    seg++;
                    at = 0;
                }
                return -1;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }

}
