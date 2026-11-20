using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;

namespace SoncaAudioInspector;

public sealed class PreviousQaWindow : Window
{
    public string Verdict { get; private set; }
    public string? OperatorNote => ProductMeasurementContext.NormalizeOperatorNote(_noteBox.Text);
    public string ServerQaStatusText => _serverStatus.Text;
    public TextBox NoteTextBox => _noteBox;
    public Button ConfirmButton => _confirmButton;
    public bool? ConfirmedResult { get; private set; }
    private readonly Dictionary<string, Button> _choices = new();
    private readonly TextBlock _serverStatus;
    private readonly TextBox _noteBox;
    private readonly Button _confirmButton;
    private bool _noteFocused;

    public PreviousQaWindow(
        string serial,
        string model,
        string? suggested = null,
        string? qaBy = null,
        bool lookupFailed = false,
        bool isResume = false,
        string? initialVerdict = null,
        string? initialOperatorNote = null)
    {
        bool hasSerial = !string.IsNullOrWhiteSpace(serial);
        string displaySerial = hasSerial ? serial.Trim() : "ID tự tạo khi đo";
        Title = $"Kết quả QA · {displaySerial} · {model}";
        Width = 700; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ConfigTheme.xaml", UriKind.Relative) });
        var body = new StackPanel(); Content = new Border { Background = Background, Padding = new Thickness(24), Child = body };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        string statusText = !hasSerial
            ? "Chưa nhập serial · không tra được QA"
            : lookupFailed
                ? "Offline · chưa tra được"
                : $"QA trên server: {(suggested is "PASS" or "FAIL" ? suggested : "Chưa có QA")}{(string.IsNullOrWhiteSpace(qaBy) ? "" : $" ({qaBy.Trim()})")}";
        Brush statusBrush = !hasSerial || lookupFailed
            ? Brushes.Gold
            : suggested == "PASS" ? Brushes.MediumSpringGreen : suggested == "FAIL" ? Brushes.Salmon : Brushes.LightGray;
        _serverStatus = new TextBlock
        {
            Name = "ServerQaStatus",
            Text = statusText,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = statusBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 6)
        };
        body.Children.Add(_serverStatus);
        body.Children.Add(new TextBlock
        {
            Text = "Xác nhận kết quả QA trước đó · P / F / N để chọn nhanh (ngoài ô ghi chú), Enter để bắt đầu đo",
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var row = new UniformGrid { Columns = 3 }; body.Children.Add(row);
        var template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='7'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");
        foreach (var (value, title, color) in new[] { ("PASS", "QA PASS", "#10B981"), ("FAIL", "QA FAIL", "#EF4444"), ("NONE", "Chưa có QA", "#52525B") })
        {
            var button = new Button
            {
                Name = "Qa" + value,
                Content = title,
                Background = (Brush)new BrushConverter().ConvertFromString(color)!,
                Foreground = Brushes.White,
                FontSize = 22,
                Height = 90,
                Margin = new Thickness(5),
                BorderThickness = new Thickness(3),
                Template = template
            };
            button.GotFocus += (_, _) => _noteFocused = false;
            button.Click += (_, _) => { _noteFocused = false; Select(value); };
            _choices.Add(value, button); row.Children.Add(button);
        }

        body.Children.Add(new TextBlock
        {
            Text = "Ghi chú (tùy chọn)",
            Foreground = Brushes.LightGray,
            Margin = new Thickness(5, 14, 5, 6)
        });
        _noteBox = new TextBox
        {
            Name = "OperatorNote",
            MaxLength = 4000,
            MinHeight = 64,
            MaxHeight = 120,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = false,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(5, 0, 5, 16),
            Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91)),
            CaretBrush = Brushes.White,
            Text = initialOperatorNote ?? ""
        };
        AutomationProperties.SetAutomationId(_noteBox, "OperatorNote");
        _noteBox.GotFocus += (_, _) => _noteFocused = true;
        _noteBox.LostFocus += (_, _) => _noteFocused = false;
        _noteBox.GotKeyboardFocus += (_, _) => _noteFocused = true;
        _noteBox.LostKeyboardFocus += (_, _) => _noteFocused = false;
        body.Children.Add(_noteBox);

        var actions = new DockPanel { Margin = new Thickness(5, 0, 5, 0) };
        var cancelButton = new Button
        {
            Name = "CancelQaStart",
            Content = "Hủy",
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelButton.GotFocus += (_, _) => _noteFocused = false;
        cancelButton.Click += (_, _) => Cancel();
        DockPanel.SetDock(cancelButton, Dock.Left);
        actions.Children.Add(cancelButton);

        _confirmButton = new Button
        {
            Name = "ConfirmQaStart",
            Content = isResume ? "TIẾP TỤC ĐO" : "BẮT ĐẦU ĐO",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(20, 10, 20, 10),
            Background = (Brush)new BrushConverter().ConvertFromString("#2563EB")!,
            Foreground = Brushes.White
        };
        _confirmButton.GotFocus += (_, _) => _noteFocused = false;
        _confirmButton.Click += (_, _) => Confirm();
        actions.Children.Add(_confirmButton);
        body.Children.Add(actions);

        Verdict = initialVerdict is "PASS" or "FAIL" or "NONE"
            ? initialVerdict
            : hasSerial && !lookupFailed && suggested is "PASS" or "FAIL" ? suggested : "NONE";
        Select(Verdict);
        PreviewKeyDown += HandlePreviewKeyDown;
    }

    public void FocusOperatorNote()
    {
        _noteFocused = true;
        _noteBox.Focus();
    }

    public void BlurOperatorNote()
    {
        _noteFocused = false;
        _confirmButton.Focus();
    }

    private bool IsOperatorNoteFocused(KeyEventArgs e)
        => _noteFocused || _noteBox.IsFocused || _noteBox.IsKeyboardFocused || _noteBox.IsKeyboardFocusWithin
            || ReferenceEquals(e.OriginalSource, _noteBox) || ReferenceEquals(Keyboard.FocusedElement, _noteBox);

    private void HandlePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }
        if (IsOperatorNoteFocused(e))
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    InsertNewLineInNote();
                }
                else
                {
                    Confirm();
                }
                e.Handled = true;
            }
            return;
        }
        if (e.Key is Key.P or Key.F or Key.N)
        {
            Select(e.Key == Key.P ? "PASS" : e.Key == Key.F ? "FAIL" : "NONE");
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    public void InsertNewLineInNote()
    {
        int caret = _noteBox.CaretIndex;
        string current = _noteBox.Text ?? "";
        if (_noteBox.SelectionLength > 0)
        {
            caret = _noteBox.SelectionStart;
            current = current.Remove(caret, _noteBox.SelectionLength);
        }
        if (caret < 0 || caret > current.Length) caret = current.Length;
        string next = current.Insert(caret, "\n");
        if (next.Length <= _noteBox.MaxLength)
        {
            _noteBox.Text = next;
            _noteBox.CaretIndex = caret + 1;
        }
    }

    private void Confirm()
    {
        ConfirmedResult = true;
        try { DialogResult = true; } catch (InvalidOperationException) { }
    }

    private void Cancel()
    {
        ConfirmedResult = false;
        try { DialogResult = false; } catch (InvalidOperationException) { }
    }

    private void Select(string value)
    {
        Verdict = value;
        foreach (var choice in _choices)
            choice.Value.BorderBrush = choice.Key == value ? Brushes.White : Brushes.Transparent;
    }
}
