using System.Windows;
using System.Windows.Controls;

namespace MultividStreamer.App;

/// <summary>
/// Confirmation for destructive actions that only enables OK once the user has TYPED
/// the required word (e.g. "Yes") — a click-through-proof guard, unlike a Yes/No box
/// that can be dismissed on reflex.
/// </summary>
public sealed class TypedConfirmationDialog : Window
{
    public TypedConfirmationDialog(string title, string question, string requiredWord)
    {
        Title = title;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        TextBlock questionText = new()
        {
            Text = question,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10)
        };

        TextBlock hintText = new()
        {
            Text = $"Tapez \"{requiredWord}\" pour confirmer:",
            Margin = new Thickness(0, 0, 0, 4)
        };

        TextBox confirmationInput = new()
        {
            Height = 26,
            Margin = new Thickness(0, 0, 0, 14)
        };

        Button okButton = new()
        {
            Content = "OK",
            Width = 92,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
            IsDefault = true
        };

        Button cancelButton = new()
        {
            Content = "Annuler",
            Width = 92,
            Height = 28,
            IsCancel = true
        };

        confirmationInput.TextChanged += (_, _) =>
            okButton.IsEnabled = string.Equals(confirmationInput.Text.Trim(), requiredWord, StringComparison.OrdinalIgnoreCase);
        okButton.Click += (_, _) => DialogResult = true;

        StackPanel buttonRow = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);

        StackPanel root = new() { Margin = new Thickness(16) };
        root.Children.Add(questionText);
        root.Children.Add(hintText);
        root.Children.Add(confirmationInput);
        root.Children.Add(buttonRow);
        Content = root;

        Loaded += (_, _) => confirmationInput.Focus();
    }
}
