using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// The multi-line composer behind the attach tray's text path: review/edit a paste before it becomes
/// a staged .txt attachment, or write multi-line prompt material from scratch (the CLI composer has
/// no pleasant way to do either - big pastes collapse to a chip and newlines need backslash+Enter).
/// Attach stages the text and pushes the @ chip exactly like every other attachment. Sibling of
/// <see cref="ReasonDialog"/>: code-built, VS-themed via <see cref="DialogWindow"/>, UI thread only.
/// </summary>
internal sealed class ComposeDialog : DialogWindow
{
    private readonly TextBox _box;
    private readonly TextBlock _estimate;

    private ComposeDialog(string initialText)
    {
        Title = "Compose attachment";
        Width = 640;
        Height = 440;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip; // pastes come in all sizes

        SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        FontScale.BindRoot(this);

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = "Edit freely - line breaks, trimming, whatever you need. Attach stages this as a .txt "
                 + "and drops an @ reference into the Claude composer. (Ctrl+Enter attaches.)",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0.85,
        };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Grid.SetRow(prompt, 0);

        _box = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap, // pasted code/logs read better unwrapped; both scrollbars live
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Padding = new Thickness(4),
        };
        _box.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        _box.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        _box.SetResourceReference(TextBox.CaretBrushProperty, VsBrushes.WindowTextKey);
        _box.SetResourceReference(BorderBrushProperty, VsBrushes.ComboBoxBorderKey);
        _box.TextChanged += (s, e) => UpdateEstimate();
        Grid.SetRow(_box, 1);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _estimate = Font(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 }, 0.96);
        _estimate.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        footer.Children.Add(_estimate);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Right);
        var ok = new Button { Content = "Attach", IsDefault = false, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 2, 8, 2) };
        ok.Click += (s, e) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        // Children order matters in DockPanel: docked buttons first, the estimate fills the rest.
        footer.Children.Clear();
        footer.Children.Add(buttons);
        footer.Children.Add(_estimate);
        Grid.SetRow(footer, 2);

        grid.Children.Add(prompt);
        grid.Children.Add(_box);
        grid.Children.Add(footer);
        Content = grid;

        // Enter must insert newlines (the whole point), so Attach's accelerator is Ctrl+Enter,
        // not IsDefault - a default button would swallow plain Enter.
        InputBindings.Add(new KeyBinding(new RelayCommand(() => DialogResult = true), Key.Enter, ModifierKeys.Control));

        UpdateEstimate();
        Loaded += (s, e) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }

    private void UpdateEstimate()
    {
        int chars = _box.Text?.Length ?? 0;
        long tokens = Math.Max(chars > 0 ? 1 : 0, chars / 4);
        _estimate.Text = chars == 0 ? "" : $"≈ {(tokens >= 1000 ? (tokens / 1000.0).ToString("0.0") + "k" : tokens.ToString())} tokens when read";
    }

    /// <summary>Show the composer (optionally pre-filled). Returns the final text, or null if cancelled/empty.</summary>
    public static string? Prompt(string initialText)
    {
        var dlg = new ComposeDialog(initialText);
        if (dlg.ShowDialog() != true) return null;
        var text = dlg._box.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Scales an element's FontSize relative to VS's Environment Font size (see <see cref="FontScale"/>).</summary>
    private static T Font<T>(T el, double ratio) where T : FrameworkElement
        => FontScale.Bind(el, typeof(ComposeDialog), ratio);

    /// <summary>Minimal ICommand for the Ctrl+Enter accelerator (no framework helper in scope).</summary>
    private sealed class RelayCommand : ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action action) => _action = action;
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
