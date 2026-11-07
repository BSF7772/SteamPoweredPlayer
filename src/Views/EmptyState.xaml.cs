// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SteamPoweredPlayer.Views;

public sealed partial class EmptyState : UserControl
{
    public EmptyState() => InitializeComponent();

    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, e) => ((EmptyState)d).EmojiText.Text = (string)e.NewValue));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, e) => ((EmptyState)d).TitleText.Text = (string)e.NewValue));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata("", (d, e) => ((EmptyState)d).MessageText.Text = (string)e.NewValue));

    public string Emoji { get => (string)GetValue(EmojiProperty); set => SetValue(EmojiProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
}
