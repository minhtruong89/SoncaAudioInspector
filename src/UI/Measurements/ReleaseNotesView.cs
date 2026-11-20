using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>The same compact release presentation in the version tab and update confirmation.</summary>
public sealed class ReleaseNotesView : StackPanel
{
    public TextBlock Summary { get; } = new() { Name = "ReleaseNotesSummary", FontSize = 15, Foreground = Brushes.WhiteSmoke,
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 22, MaxHeight = 44, Margin = new Thickness(0,6,0,8) };
    public TextBlock Details { get; } = new() { Name = "ReleaseNotesDetailText", FontSize = 14, Foreground = Brushes.WhiteSmoke,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    public Expander DetailExpander { get; } = new() { Name = "ReleaseNotesDetails", Header = "Chi tiết", IsExpanded = false,
        Foreground = Brushes.White, Margin = new Thickness(0,4,0,8) };
    public ReleaseNotesView()
    {
        Children.Add(Summary); DetailExpander.Content = Details; Children.Add(DetailExpander);
    }
    public void SetNotes(string summary, IEnumerable<string> details)
    {
        Summary.Text = Update.UpdateManifest.SingleLine(summary); Summary.ToolTip = Summary.Text;
        Details.Text = string.Join("\n", details.Select(Update.UpdateManifest.SingleLine).Select(line => "• " + line));
        DetailExpander.IsExpanded = false;
        DetailExpander.Visibility = Details.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
