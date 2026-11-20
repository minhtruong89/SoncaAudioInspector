using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>Owner 2026-11-19 (A4): popup when clicking an INVALID/NO_SIGNAL/INCONCLUSIVE step after a C360 run.
/// Shows the failure reason and current levels, suggests +20 % volume for this retest only (never mutating config),
/// and guards against stray Enter/Space for 800 ms (A7).</summary>
public sealed class RetestInvalidStepDialog : Window
{
    private readonly RetestVolumePlan _plan;
    private readonly TextBox _txtWinPlayback;
    private readonly TextBox? _txtBleMusic;
    private readonly TextBlock _txtError;
    private DateTime _acceptKeyboardAfterUtc;

    public RetestVolumeOverride? ResultOverride { get; private set; }
    public bool IsKeyboardConfirmGuarded => DateTime.UtcNow < _acceptKeyboardAfterUtc;

    public RetestInvalidStepDialog(RetestVolumePlan plan)
    {
        _plan = plan;
        Title = $"Đo lại {plan.TestId}";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(15, 15, 17));
        Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 245));

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 23)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20)
        };
        var panel = new StackPanel();
        root.Child = panel;
        Content = root;

        panel.Children.Add(new TextBlock
        {
            Text = $"Đo lại {plan.TestId} — {plan.TestName}",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 245)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var reasonBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(51, 38, 13)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12)
        };
        reasonBorder.Child = new TextBlock
        {
            Text = "Lý do: " + plan.Reason,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77)),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(reasonBorder);

        var currentBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var currentStack = new StackPanel();
        currentStack.Children.Add(new TextBlock
        {
            Text = "MỨC HIỆN TẠI (CONFIG)",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        currentStack.Children.Add(new TextBlock
        {
            Text = plan.CurrentLevelsSummary(),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)),
            TextWrapping = TextWrapping.Wrap
        });
        currentBorder.Child = currentStack;
        panel.Children.Add(currentBorder);

        panel.Children.Add(new TextBlock
        {
            Text = "Mức chỉ áp dụng cho lần đo lại này (tự khôi phục sau đo, không sửa config):",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var form = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var lblWin = new TextBlock
        {
            Text = "Windows phát (% · tối đa 100 %)",
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4)
        };
        Grid.SetRow(lblWin, 0); Grid.SetColumn(lblWin, 0);
        form.Children.Add(lblWin);

        _txtWinPlayback = new TextBox
        {
            Name = "TxtRetestWindowsPlayback",
            Text = plan.SuggestedWindowsPlaybackPercent.ToString("0.#", CultureInfo.InvariantCulture),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
            Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 4, 6, 4),
            TextAlignment = TextAlignment.Right
        };
        Grid.SetRow(_txtWinPlayback, 0); Grid.SetColumn(_txtWinPlayback, 1);
        form.Children.Add(_txtWinPlayback);

        var hintWin = new TextBlock
        {
            Text = $"config {plan.ConfigWindowsPlaybackPercent:0.#} %",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(hintWin, 0); Grid.SetColumn(hintWin, 2);
        form.Children.Add(hintWin);

        if (plan.UsesBleMusic)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lblBle = new TextBlock
            {
                Text = $"BLE MUSIC (% · tối đa {plan.MaxBleMusicPercent:0} %)",
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 4)
            };
            Grid.SetRow(lblBle, 1); Grid.SetColumn(lblBle, 0);
            form.Children.Add(lblBle);

            _txtBleMusic = new TextBox
            {
                Name = "TxtRetestBleMusic",
                Text = plan.SuggestedBleMusicPercent?.ToString("0.#", CultureInfo.InvariantCulture) ?? "",
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
                Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 6, 6, 4),
                TextAlignment = TextAlignment.Right
            };
            Grid.SetRow(_txtBleMusic, 1); Grid.SetColumn(_txtBleMusic, 1);
            form.Children.Add(_txtBleMusic);

            var hintBle = new TextBlock
            {
                Text = plan.ConfigBleMusicPercent is double bm ? $"config {bm:0.#} %" : "để trống = giữ",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(hintBle, 1); Grid.SetColumn(hintBle, 2);
            form.Children.Add(hintBle);
        }

        panel.Children.Add(form);

        _txtError = new TextBlock
        {
            Foreground = Brushes.Salmon,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(_txtError);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var btnCancel = new Button
        {
            Name = "BtnRetestCancel",
            Content = "Hủy",
            IsCancel = true,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand
        };
        btnCancel.Click += (_, _) => { ResultOverride = null; CloseDialog(false); };

        var btnRetest = new Button
        {
            Name = "BtnRetestConfirm",
            Content = "Đo lại",
            IsDefault = false,
            Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            Foreground = Brushes.Black,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(20, 7, 20, 7),
            Cursor = Cursors.Hand
        };
        btnRetest.Click += (_, _) => TryConfirm();

        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnRetest);
        panel.Children.Add(buttons);

        var guard = ModernMessageBox.DefaultImportantDecisionGuard;
        _acceptKeyboardAfterUtc = DateTime.UtcNow + guard;
        Loaded += (_, _) => _acceptKeyboardAfterUtc = DateTime.UtcNow + guard;
        PreviewKeyDown += (_, e) =>
        {
            if ((e.Key is Key.Enter or Key.Return or Key.Space) && IsKeyboardConfirmGuarded)
                e.Handled = true;
        };
    }

    public bool TryConfirm()
    {
        if (!TryParseDouble(_txtWinPlayback.Text, out double winPlay))
        {
            ShowError("Nhập số hợp lệ cho Windows phát (1–100%).");
            return false;
        }
        double? bleMusic = null;
        if (_txtBleMusic != null && !string.IsNullOrWhiteSpace(_txtBleMusic.Text))
        {
            if (!TryParseDouble(_txtBleMusic.Text, out double parsedBle))
            {
                ShowError($"Nhập số hợp lệ cho BLE MUSIC (1–{_plan.MaxBleMusicPercent:0}%).");
                return false;
            }
            bleMusic = parsedBle;
        }
        try
        {
            ResultOverride = _plan.BuildOverride(winPlay, bleMusic);
            _txtError.Visibility = Visibility.Collapsed;
            CloseDialog(true);
            return true;
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    private void ShowError(string message)
    {
        _txtError.Text = message;
        _txtError.Visibility = Visibility.Visible;
    }

    private void CloseDialog(bool accepted)
    {
        try { DialogResult = accepted; }
        catch (InvalidOperationException) { Close(); }
    }

    private static bool TryParseDouble(string? raw, out double value)
    {
        string cleaned = (raw ?? "").Trim().Replace("%", "").Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}
