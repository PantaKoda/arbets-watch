using Avalonia.Data.Converters;

namespace ArbetsWatch.Desktop.ViewModels;

public static class Converters
{
    /// <summary>Rows that left the results are dimmed until the list is updated.</summary>
    public static readonly IValueConverter GoneOpacity =
        new FuncValueConverter<bool, double>(gone => gone ? 0.45 : 1.0);

    public static readonly IValueConverter ExpandGlyph =
        new FuncValueConverter<bool, string>(expanded => expanded ? "▾" : "▸");
}
