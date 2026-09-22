using System;
using System.Windows;
using System.Windows.Controls;

namespace FModel.Views.Resources.Controls;

public class HotkeyPresenter : ItemsControl
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyPresenter), new PropertyMetadata(null, OnGestureChanged));

    public string Gesture
    {
        get => (string) GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HotkeyPresenter) d).ItemsSource = e.NewValue is string gesture
            ? gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
    }
}
