using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia.Data.Converters;

using Flower.ViewModels;

namespace Flower.Converters;

// An artist's name to the icon the desktop Artists column draws beside it,
// from the one place every artist's icon comes from (ArtistIcons). The column's
// rows are the names themselves, which its selection and drag work in, so
// the picture is looked up rather than carried on a row of its own.
public sealed class ArtistCoverConverter : IMultiValueConverter
{
    public static readonly ArtistCoverConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string name, ArtistIcons icons] ? icons.For(name, thumbnail: true) : null;
}
