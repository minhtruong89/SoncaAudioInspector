namespace SoncaAudioInspector;

/// <summary>The single QA image of the C360 Rè test (owner 2026-11-18: "rub buzz có 1 hình của chirp được rồi"):
/// chirp rattle bump per frequency, same colours as the Rè tab, rendered without a WPF control.</summary>
public static class RubBuzzQaGraph
{
    public static ScottPlot.Plot Chirp(ChirpRubBuzzRun run, string title)
    {
        var plot = new ScottPlot.Plot();
        Draw(plot, run, title);
        return plot;
    }

    /// <summary>Draws the same chirp graph into an existing plot (the result view of the main window, v1.5.6), so the operator sees
    /// exactly the picture that is uploaded as the IO008 QA image.</summary>
    public static void Draw(ScottPlot.Plot plot, ChirpRubBuzzRun run, string title)
    {
        plot.Clear();
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101011");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D4D4D8"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#34343B");
        plot.Axes.Title.Label.Text = title + " · " + run.Verdict;
        plot.Axes.Left.Label.Text = "Đỉnh rè (dB)";
        plot.Axes.Bottom.Label.Text = "Tần số chirp (Hz) · xanh: trung vị · xám: từng lượt · xám nhạt: chuẩn · đỏ: giới hạn";
        double[] ticks = [20, 50, 100, 200, 500, 1000, 2000];
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.Select(f => new ScottPlot.Tick(Math.Log10(f), f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}")).ToArray());
        void Line(double[] x, double[] y, ScottPlot.Color color, float width)
        { var line = plot.Add.Scatter(x, y); line.MarkerSize = 0; line.Color = color; line.LineWidth = width; }
        var m = run.Metrics;
        if (m.FrequenciesHz.Length > 0)
        {
            double[] x = m.FrequenciesHz.Select(Math.Log10).ToArray();
            foreach (var repeat in m.RepeatBumpDb.Where(r => r.Length == x.Length)) Line(x, repeat, ScottPlot.Colors.SlateGray, 1);
            if (m.BumpDb.Length == x.Length) Line(x, m.BumpDb, ScottPlot.Colors.DeepSkyBlue, 3);
            var limits = run.Limits ?? new ChirpRubBuzzLimits();
            if (run.StandardMetrics is { } standard && standard.BumpDb.Length == x.Length)
            {
                Line(x, standard.BumpDb, ScottPlot.Colors.Gray, 2);
                Line(x, standard.BumpDb.Select(v => v + limits.BumpMarginDb).ToArray(), ScottPlot.Colors.Salmon, 2);
            }
            else plot.Add.HorizontalLine(limits.AbsoluteFailDb, color: ScottPlot.Colors.Salmon);
        }
        plot.Axes.AutoScale();
    }
}
