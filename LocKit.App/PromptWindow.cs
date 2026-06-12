using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace LocKit.App
{
    public class PromptWindow : Window
    {
        private readonly TextBox _inputTextBox;
        public string Result { get; private set; } = string.Empty;

        public PromptWindow(string title, string promptText, string defaultValue = "")
        {
            Title = title;
            Width = 360;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = SolidColorBrush.Parse("#0D0D12");
            CanResize = false;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
            WindowDecorations = WindowDecorations.Full;

            var mainGrid = new Grid { RowDefinitions = new RowDefinitions("30, *") };

            // Drag title bar
            var titlebar = new Border { Background = SolidColorBrush.Parse("#0A0A0D"), Height = 30 };
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 11,
                Foreground = SolidColorBrush.Parse("#5B5E6B"),
                FontWeight = FontWeight.Medium,
                Margin = new Thickness(12, 8, 0, 0)
            };
            titlebar.Child = titleText;
            mainGrid.Children.Add(titlebar);
            Grid.SetRow(titlebar, 0);

            var contentPanel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };

            var textBlock = new TextBlock
            {
                Text = promptText,
                Foreground = SolidColorBrush.Parse("#E2E8F0"),
                FontSize = 12
            };
            contentPanel.Children.Add(textBlock);

            _inputTextBox = new TextBox
            {
                Text = defaultValue,
                Background = SolidColorBrush.Parse("#0F0F14"),
                BorderBrush = SolidColorBrush.Parse("#252535"),
                Foreground = SolidColorBrush.Parse("#FFFFFF"),
                FontSize = 13,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            contentPanel.Children.Add(_inputTextBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            var okButton = new Button
            {
                Content = "OK",
                Padding = new Thickness(16, 6),
                Background = SolidColorBrush.Parse("#2E1B4E"),
                Foreground = SolidColorBrush.Parse("#E9D5FF")
            };
            okButton.Click += (s, e) => { Result = _inputTextBox.Text ?? string.Empty; Close(); };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(16, 6),
                Background = SolidColorBrush.Parse("#16161F"),
                Foreground = SolidColorBrush.Parse("#CCCCCC")
            };
            cancelButton.Click += (s, e) => { Result = string.Empty; Close(); };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            contentPanel.Children.Add(buttonPanel);

            mainGrid.Children.Add(contentPanel);
            Grid.SetRow(contentPanel, 1);

            Content = mainGrid;
        }
    }
}
