using System.Globalization;
using System.Runtime.CompilerServices;

using Avalonia;

[assembly: InternalsVisibleTo("Oadm.Client.Tests")]

namespace Oadm.Sdk.Client.Controls;

/// <summary>A value axis: from <see cref="Min"/> to <see cref="Max"/> in steps of <see cref="Step"/>.</summary>
internal readonly record struct ChartAxis(double Min, double Max, double Step, int Decimals)
{
    /// <summary>The values of the grid lines, from the bottom up.</summary>
    public IReadOnlyList<double> Ticks()
    {
        var count = (int)Math.Floor((Max - Min) / Step + 1e-9);
        var ticks = new List<double>(count + 1);
        for (var i = 0; i <= count; i++)
        {
            ticks.Add(Math.Round(Min + i * Step, Decimals));
        }

        return ticks;
    }

    /// <summary>A grid line's label: the decimals the step needs, no trailing zeros.</summary>
    public string Label(double value) => ChartScale.FormatNumber(value, Decimals);
}

/// <summary>The pure math of the charts (axis steps, time labels, point reduction), without drawing.</summary>
internal static class ChartScale
{
    private static readonly int[] TimeSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400];

    /// <summary>
    /// A value axis with round steps (1, 2, 2.5 or 5 times a power of ten) and about <paramref name="ticks"/> intervals that
    /// covers <paramref name="min"/> to <paramref name="max"/>; with <paramref name="fixedMax"/> the top stays at
    /// <paramref name="max"/>.
    /// </summary>
    public static ChartAxis Nice(double min, double max, int ticks, bool fixedMax = false)
    {
        if (!double.IsFinite(min))
        {
            min = 0;
        }

        if (!double.IsFinite(max) || max <= min)
        {
            max = min + (min == 0 ? 1 : Math.Abs(min));
        }

        var step = NiceStep((max - min) / Math.Clamp(ticks, 1, 20));
        var low = Math.Floor(min / step + 1e-9) * step;
        var high = fixedMax ? max : Math.Ceiling(max / step - 1e-9) * step;
        return new ChartAxis(low, high, step, Decimals(step));
    }

    /// <summary>The round step at or above <paramref name="raw"/>.</summary>
    public static double NiceStep(double raw)
    {
        if (!double.IsFinite(raw) || raw <= 0)
        {
            return 1;
        }

        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var mantissa = raw / power;
        var nice = mantissa <= 1 + 1e-9 ? 1 : mantissa <= 2 + 1e-9 ? 2 : mantissa <= 2.5 + 1e-9 ? 2.5 : mantissa <= 5 + 1e-9 ? 5 : 10;
        return nice * power;
    }

    /// <summary>The decimals a step needs to show exactly (0.25 = 2).</summary>
    public static int Decimals(double step)
    {
        for (var d = 0; d < 6; d++)
        {
            var scaled = step * Math.Pow(10, d);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-6 * Math.Max(1, scaled))
            {
                return d;
            }
        }

        return 6;
    }

    /// <summary>A number with thousands separators and at most <paramref name="decimals"/> decimals, no trailing zeros.</summary>
    public static string FormatNumber(double value, int decimals)
    {
        var format = decimals <= 0 ? "#,##0" : "#,##0." + new string('#', decimals);
        var text = value.ToString(format, CultureInfo.CurrentCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>A measured value as shown on hover: 812, 81.2, 8.12 (three digits that matter), with the unit.</summary>
    public static string FormatValue(double value, string? unit)
    {
        if (!double.IsFinite(value))
        {
            return "No value";
        }

        var magnitude = Math.Abs(value);
        var decimals = magnitude >= 100 ? 0 : magnitude >= 10 ? 1 : 2;
        var number = FormatNumber(value, decimals);
        return string.IsNullOrEmpty(unit) ? number : number + " " + unit;
    }

    /// <summary>A time on the time axis: 0:00, 2:30, 1:05:00.</summary>
    public static string FormatTime(TimeSpan time)
    {
        var seconds = (long)Math.Round(time.TotalSeconds);
        var hours = seconds / 3600;
        var minutes = seconds / 60 % 60;
        var rest = seconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>
    /// Every how many values a time label goes: a round time (1 s, 5 s, 30 s, 1 min, ...) with at most
    /// <paramref name="maxLabels"/> labels over <paramref name="count"/> values of <paramref name="step"/> each.
    /// </summary>
    public static int TimeLabelEvery(int count, TimeSpan step, int maxLabels)
    {
        var seconds = step.TotalSeconds > 0 ? step.TotalSeconds : 1;
        maxLabels = Math.Max(1, maxLabels);
        foreach (var candidate in TimeSteps)
        {
            var every = Math.Max(1, (int)Math.Round(candidate / seconds));
            if ((count - 1) / every + 1 <= maxLabels)
            {
                return every;
            }
        }

        return Math.Max(1, (int)Math.Ceiling(count / (double)maxLabels));
    }

    /// <summary>The x of value <paramref name="index"/> of <paramref name="count"/> across <paramref name="plot"/>.</summary>
    public static double X(int index, int count, Rect plot) => count <= 1 ? plot.Left : plot.Left + index * plot.Width / (count - 1);

    /// <summary>The value index nearest to <paramref name="x"/>.</summary>
    public static int IndexAt(double x, int count, Rect plot) =>
        count <= 1 || plot.Width <= 0 ? 0 : Math.Clamp((int)Math.Round((x - plot.Left) / plot.Width * (count - 1)), 0, count - 1);

    /// <summary>
    /// The points of one line, reduced to the pixel columns of <paramref name="plot"/>: when there are more values than
    /// columns, each column keeps its lowest and highest value (in their order), so peaks stay and the point count stays
    /// below about two per column. NaN breaks the line: <paramref name="emit"/> gets true for the first point of each
    /// piece. Returns the number of points emitted.
    /// </summary>
    public static int Reduce(IReadOnlyList<double> values, int count, Rect plot, Func<double, double> y, Action<Point, bool> emit)
    {
        var emitted = 0;
        var newFigure = true;
        if (count <= plot.Width + 1)
        {
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i];
                if (!double.IsFinite(v))
                {
                    newFigure = true;
                    continue;
                }

                emit(new Point(X(i, count, plot), y(v)), newFigure);
                newFigure = false;
                emitted++;
            }

            return emitted;
        }

        var column = -1;
        int minIndex = -1, maxIndex = -1;
        double min = 0, max = 0;

        void Flush()
        {
            if (minIndex < 0)
            {
                return;
            }

            var (first, firstValue, second, secondValue) = minIndex <= maxIndex ? (minIndex, min, maxIndex, max) : (maxIndex, max, minIndex, min);
            emit(new Point(X(first, count, plot), y(firstValue)), newFigure);
            newFigure = false;
            emitted++;
            if (second != first)
            {
                emit(new Point(X(second, count, plot), y(secondValue)), false);
                emitted++;
            }

            minIndex = maxIndex = -1;
        }

        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (!double.IsFinite(v))
            {
                Flush();
                newFigure = true;
                column = -1;
                continue;
            }

            var c = (int)Math.Floor(X(i, count, plot) - plot.Left);
            if (c != column)
            {
                Flush();
                column = c;
            }

            if (minIndex < 0)
            {
                (minIndex, maxIndex, min, max) = (i, i, v, v);
            }
            else if (v < min)
            {
                (minIndex, min) = (i, v);
            }
            else if (v > max)
            {
                (maxIndex, max) = (i, v);
            }
        }

        Flush();
        return emitted;
    }
}
