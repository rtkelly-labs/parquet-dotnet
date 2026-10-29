using System;
using System.Numerics;
using Parquet.Data;
using Parquet.File.Values.Primitives;

namespace Parquet.Encodings;

static class StatsCompute {
    public static void Compute<T>(ReadOnlySpan<T> data, DataColumnStatistics? stats) where T : struct, INumber<T> {
        if(stats == null)
            return;

        if(typeof(T) == typeof(float) || typeof(T) == typeof(double)) {
            ComputeFloating(data, stats);
            return;
        }

        MinMax<T>(data, out T min, out T max);
        stats.MinValue = min;
        stats.MaxValue = max;

    }

    /// <summary>
    /// Floating point min/max as the format requires: NaN never a bound (no bounds when every value is NaN) but counted,
    /// a zero min written as -0.0 and a zero max as +0.0.
    /// </summary>
    private static void ComputeFloating<T>(ReadOnlySpan<T> data, DataColumnStatistics stats) where T : struct, INumber<T> {
        bool any = false;
        long nans = 0;
        T min = default, max = default;
        foreach(T v in data) {
            if(T.IsNaN(v)) {
                nans++;
                continue;
            }
            if(!any) {
                min = max = v;
                any = true;
                continue;
            }
            if(v < min)
                min = v;
            if(v > max)
                max = v;
        }
        // with TYPE_ORDER, nan_count is required for floating point columns, even when zero
        stats.NanCount = nans;
        if(!any) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }
        if(T.IsZero(min))
            min = -T.Zero;
        if(T.IsZero(max))
            max = T.Zero;
        stats.MinValue = min;
        stats.MaxValue = max;
    }

    /// <summary>Min/max of booleans (false before true).</summary>
    public static void Compute(ReadOnlySpan<bool> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;
        if(data.IsEmpty) {
            stats.MinValue = stats.MaxValue = null;
            return;
        }
        bool min = data.IndexOf(false) < 0;     // all true: min is true
        bool max = data.IndexOf(true) >= 0;
        stats.MinValue = min;
        stats.MaxValue = max;
    }

    /// <summary>Min/max of byte arrays in unsigned byte-wise order (strings, binary, fixed, UUIDs).</summary>
    public static void Compute(ReadOnlySpan<ReadOnlyMemory<byte>> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;
        if(data.IsEmpty) {
            stats.MinValue = stats.MaxValue = null;
            return;
        }
        ReadOnlyMemory<byte> min = data[0], max = min;
        foreach(ReadOnlyMemory<byte> v in data) {
            if(v.Span.SequenceCompareTo(min.Span) < 0)
                min = v;
            if(v.Span.SequenceCompareTo(max.Span) > 0)
                max = v;
        }
        // like parquet-cpp's default, no bounds rather than footers holding very long values
        if(min.Length > MaxBoundBytes || max.Length > MaxBoundBytes) {
            stats.MinValue = stats.MaxValue = null;
            return;
        }
        stats.MinValue = min.ToArray();
        stats.MaxValue = max.ToArray();
    }

    internal const int MaxBoundBytes = 4096;

    /// <summary>
    /// UTF-16 strings in Unicode code point order, which is UTF-8 byte order (the format's order for strings): code
    /// unit order differs only where a surrogate meets a code unit from U+E000 to U+FFFF.
    /// </summary>
    internal static int CompareCodePoints(ReadOnlySpan<char> a, ReadOnlySpan<char> b) {
        int n = Math.Min(a.Length, b.Length);
        for(int i = 0; i < n; i++) {
            char x = a[i], y = b[i];
            if(x == y)
                continue;
            if(x >= 0xD800 && y >= 0xD800)
                return Fixup(x).CompareTo(Fixup(y));
            return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);

        // surrogates (D800-DFFF) sort after E000-FFFF
        static int Fixup(char c) => c >= 0xE000 ? c - 0x800 : c + 0x2000;
    }

    public static void Compute(ReadOnlySpan<string> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        // Pairwise min/max: ~1.5 comparisons per element instead of ~2.
        // Compare pairs against each other first, then compare the smaller
        // against min and the larger against max.
        string min = data[0];
        string max = min;
        int i = 1;
        int len = data.Length;

        for(; i + 1 < len; i += 2) {
            string a = data[i];
            string b = data[i + 1];

            // 1 comparison to order the pair
            if(CompareCodePoints(a, b) <= 0) {
                // a <= b: a is candidate for min, b for max
                if(CompareCodePoints(a, min) < 0)
                    min = a;
                if(CompareCodePoints(b, max) > 0)
                    max = b;
            } else {
                // b < a: b is candidate for min, a for max
                if(CompareCodePoints(b, min) < 0)
                    min = b;
                if(CompareCodePoints(a, max) > 0)
                    max = a;
            }
        }

        // handle the odd trailing element
        if(i < len) {
            string last = data[i];
            if(CompareCodePoints(last, min) < 0)
                min = last;
            else if(CompareCodePoints(last, max) > 0)
                max = last;
        }

        stats.MinValue = min;
        stats.MaxValue = max;
    }

    public static void Compute(ReadOnlySpan<ReadOnlyMemory<char>> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        // Pairwise min/max: ~1.5 comparisons per element instead of ~2.
        ReadOnlyMemory<char> min = data[0];
        ReadOnlyMemory<char> max = min;
        int i = 1;
        int len = data.Length;

        for(; i + 1 < len; i += 2) {
            ReadOnlyMemory<char> a = data[i];
            ReadOnlyMemory<char> b = data[i + 1];

            if(CompareCodePoints(a.Span, b.Span) <= 0) {
                if(CompareCodePoints(a.Span, min.Span) < 0)
                    min = a;
                if(CompareCodePoints(b.Span, max.Span) > 0)
                    max = b;
            } else {
                if(CompareCodePoints(b.Span, min.Span) < 0)
                    min = b;
                if(CompareCodePoints(a.Span, max.Span) > 0)
                    max = a;
            }
        }

        if(i < len) {
            ReadOnlyMemory<char> last = data[i];
            if(CompareCodePoints(last.Span, min.Span) < 0)
                min = last;
            else if(CompareCodePoints(last.Span, max.Span) > 0)
                max = last;
        }

        stats.MinValue = min.ToString();
        stats.MaxValue = max.ToString();
    }

    public static void Compute(ReadOnlySpan<BigDecimal> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        BigInteger min = data[0].UnscaledValue;
        BigInteger max = min;
        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            BigInteger v = data[i].UnscaledValue;
            if(v < min) {
                min = v;
                minIdx = i;
            }
            if(v > max) {
                max = v;
                maxIdx = i;
            }
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    public static void Compute(ReadOnlySpan<DateTime> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        long min = data[0].Ticks;
        long max = min;
        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            long v = data[i].Ticks;
            if(v < min) { min = v; minIdx = i; }
            if(v > max) { max = v; maxIdx = i; }
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    public static void Compute(ReadOnlySpan<DateOnly> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        int min = data[0].DayNumber;
        int max = min;
        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            int v = data[i].DayNumber;
            if(v < min) { min = v; minIdx = i; }
            if(v > max) { max = v; maxIdx = i; }
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    public static void Compute(ReadOnlySpan<TimeOnly> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        long min = data[0].Ticks;
        long max = min;
        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            long v = data[i].Ticks;
            if(v < min) { min = v; minIdx = i; }
            if(v > max) { max = v; maxIdx = i; }
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    public static void Compute(ReadOnlySpan<TimeSpan> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        long min = data[0].Ticks;
        long max = min;
        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            long v = data[i].Ticks;
            if(v < min) { min = v; minIdx = i; }
            if(v > max) { max = v; maxIdx = i; }
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    public static void Compute(ReadOnlySpan<Interval> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            ref readonly Interval cur = ref data[i];
            ref readonly Interval mn = ref data[minIdx];
            ref readonly Interval mx = ref data[maxIdx];

            if(cur.Months < mn.Months ||
               (cur.Months == mn.Months && cur.Days < mn.Days) ||
               (cur.Months == mn.Months && cur.Days == mn.Days && cur.Millis < mn.Millis))
                minIdx = i;

            if(cur.Months > mx.Months ||
               (cur.Months == mx.Months && cur.Days > mx.Days) ||
               (cur.Months == mx.Months && cur.Days == mx.Days && cur.Millis > mx.Millis))
                maxIdx = i;
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    public static void Compute(ReadOnlySpan<Guid> data, DataColumnStatistics? stats) {
        if(stats == null)
            return;

        if(data.IsEmpty) {
            stats.MinValue = null;
            stats.MaxValue = null;
            return;
        }

        int minIdx = 0;
        int maxIdx = 0;

        for(int i = 1; i < data.Length; i++) {
            if(data[i].CompareTo(data[minIdx]) < 0)
                minIdx = i;
            if(data[i].CompareTo(data[maxIdx]) > 0)
                maxIdx = i;
        }

        stats.MinValue = data[minIdx];
        stats.MaxValue = data[maxIdx];
    }

    private static void MinMax<T>(ReadOnlySpan<T> span, out T min, out T max) where T : struct, INumber<T> {
        min = span.IsEmpty ? default(T) : span[0];
        max = min;
        foreach(T i in span) {
            if(i < min)
                min = i;
            if(i > max)
                max = i;
        }
    }

}
