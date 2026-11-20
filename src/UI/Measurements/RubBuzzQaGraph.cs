namespace SoncaAudioInspector;

/// <summary>The single QA image of the C360 Rè test (owner 2026-11-18: "rub buzz có 1 hình của chirp được rồi"):
/// chirp rattle bump per frequency, same colours as the Rè tab, rendered without a WPF control.</summary>
public static class RubBuzzQaGraph
{
    public static ScottPlot.Plot Chirp(ChirpRubBuzzRun? run, string title, string? route = null)
    {
        var plot = new ScottPlot.Plot();
        Draw(plot, run, title, route: route);
        return plot;
    }

    /// <summary>Draws the same chirp graph into an existing plot (the result view of the main window, v1.5.6), so the operator sees
    /// exactly the picture that is uploaded as the IO008 QA image.</summary>
    public static void Draw(ScottPlot.Plot plot, ChirpRubBuzzRun? run, string title, int activeRepeat = -1, string? route = null)
    {
        plot.Clear();
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101011");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D4D4D8"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#34343B");
        var m = run?.Metrics;
        bool anyRepeat = m is { IsValid: true } && m.RepeatExcessDb.Any(e => e.Any(v => v >= 3));
        bool hasData = m is { HasSensitiveBins: true } || anyRepeat;
        string status = m is { HasSensitiveBins: true } ? run!.Verdict : m?.GraphStatus ?? "KHÔNG CÓ TÍN HIỆU";
        plot.Axes.Title.Label.Text = title + " · " + status;
        plot.Axes.Title.Label.FontSize = 16;
        if (!string.IsNullOrWhiteSpace(route)) plot.Axes.Title.Label.Text += "\n" + route;
        plot.Axes.Left.Label.Text = "Đỉnh rè (dB)";
        plot.Axes.Bottom.Label.Text = "Tần số chirp (Hz) · xanh nét đứt: phần lặp lại · xám: từng lượt · đỏ: giới hạn";
        double[] ticks = [20, 50, 100, 200, 500, 1000, 2000];
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.Select(f => new ScottPlot.Tick(Math.Log10(f), f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}")).ToArray());
        void Line(double[] x, double[] y, ScottPlot.Color color, float width)
        { var line = plot.Add.Scatter(x, y); line.MarkerSize = 2; line.Color = color; line.LineWidth = width; }
        if (hasData && m != null && run != null)
        {
            double[] x = m.FrequenciesHz.Select(Math.Log10).ToArray();
            void Masked(double[] values, double[] excess, ScottPlot.Color color, float width, bool combined = false)
            {
                if (values.Length != x.Length || excess.Length != x.Length) return;
                var visible = values.Select((v, i) => excess[i] >= 3 && double.IsFinite(v) ? v : double.NaN).ToArray();
                if (!visible.Any(double.IsFinite)) return;
                var line = plot.Add.Scatter(x, visible);
                line.Color = color;
                line.LineWidth = width;
                line.MarkerSize = 2;
                // The repeated curve is a minimum, so it can overlap one of the gray takes exactly.
                if (combined) line.LinePattern = ScottPlot.LinePattern.Dashed;
            }
            for (int i = 0; i < m.RepeatBumpDb.Length; i++)
                Masked(m.RepeatBumpDb[i], m.RepeatExcessDb.ElementAtOrDefault(i) ?? m.ExcessDb,
                    i == activeRepeat ? ScottPlot.Colors.Gold : ScottPlot.Colors.SlateGray, i == activeRepeat ? 3 : 1);
            Masked(m.BumpDb, m.ExcessDb, ScottPlot.Colors.DeepSkyBlue, 2, combined: true);
            var limits = run.Limits ?? new ChirpRubBuzzLimits();
            if (run.StandardMetrics is { } standard && standard.BumpDb.Length == x.Length)
            {
                Line(x, standard.BumpDb, ScottPlot.Colors.Gray, 2);
                Line(x, standard.BumpDb.Select(v => v + limits.BumpMarginDb).ToArray(), ScottPlot.Colors.Salmon, 2);
            }
            else plot.Add.HorizontalLine(limits.AbsoluteFailDb, color: ScottPlot.Colors.Salmon);
        }
        if (hasData) plot.Axes.AutoScale();
        else plot.Axes.SetLimitsY(-1, 10);
        plot.Axes.SetLimitsX(Math.Log10(Math.Min(20, run?.Settings.F1Hz ?? 20)), Math.Log10(2000));
    }
}
