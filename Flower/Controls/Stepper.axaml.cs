using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace Flower.Controls;

// See Stepper.axaml. Value binds two-way by default, like a NumericUpDown's.
public partial class Stepper : UserControl
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Minimum));

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<Stepper, int>(nameof(Maximum), 100);

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Stepper()
    {
        InitializeComponent();
        UpdateButtons();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty)
            UpdateButtons();
    }

    private void UpdateButtons()
    {
        DecreaseButton.IsEnabled = Value > Minimum;
        IncreaseButton.IsEnabled = Value < Maximum;
    }

    private void Decrease_Click(object? sender, RoutedEventArgs e) => Value = Math.Clamp(Value - 1, Minimum, Maximum);

    private void Increase_Click(object? sender, RoutedEventArgs e) => Value = Math.Clamp(Value + 1, Minimum, Maximum);
}
