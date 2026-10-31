using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DreamBooks.App.Resources;

namespace DreamBooks.App.Services;

/// <summary>
/// A small modal question with Italian button labels (a MessageBox cannot label its buttons). Enter presses
/// the question's default button, which also has the focus; Esc and the close box always give the cancel
/// answer; buttons are reachable with Tab and use the app's 40 px button style.
/// </summary>
internal sealed class ChoiceDialog : Window
{
    private const double _maxTextWidth = 420;
    private static readonly Thickness _padding = new(16);
    private static readonly Thickness _buttonPadding = new(12, 0, 12, 0);
    private static readonly Thickness _buttonsGap = new(0, 16, 0, 0);
    private readonly int _cancelIndex;

    public ChoiceDialog(ChoiceQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var labels = question.Labels;
        ArgumentOutOfRangeException.ThrowIfLessThan(labels.Count, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(question.DefaultIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(question.DefaultIndex, labels.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(question.CancelIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(question.CancelIndex, labels.Count);
        _cancelIndex = question.CancelIndex;

        Title = Strings.AppTitle;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttons = new List<Button>(labels.Count);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = _buttonsGap };
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = labels[i],
                IsDefault = i == question.DefaultIndex,
                IsCancel = i == question.CancelIndex,
                Padding = _buttonPadding,
            };
            AutomationProperties.SetName(button, labels[i]);
            button.Click += (_, _) =>
            {
                Choice = index;
                DialogResult = true;
            };
            buttons.Add(button);
            row.Children.Add(button);
        }

        Buttons = buttons;
        var panel = new StackPanel { Margin = _padding };
        panel.Children.Add(new TextBlock { Text = question.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = _maxTextWidth });
        panel.Children.Add(row);
        Content = panel;
        var focused = buttons[question.DefaultIndex];
        Loaded += (_, _) => focused.Focus();
    }

    /// <summary>The buttons, in label order.</summary>
    public IReadOnlyList<Button> Buttons { get; }

    /// <summary>Index of the clicked button; null until one is clicked.</summary>
    public int? Choice { get; private set; }

    /// <summary>Shows the question modally over the main window and returns the chosen index (Esc/close = cancel).</summary>
    public static int Ask(ChoiceQuestion question)
    {
        var dialog = new ChoiceDialog(question);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Choice ?? dialog._cancelIndex;
    }
}
