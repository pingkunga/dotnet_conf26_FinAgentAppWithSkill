using MudBlazor;

namespace FinanceApp.Web.Components.Layout;

internal static class AppTheme
{
    internal static readonly MudTheme Default = new()
    {
        PaletteLight = new PaletteLight
        {
            // Colors.Green.Darken3 (#2E7D32)
            Primary = Colors.Green.Darken3,
            AppbarBackground = Colors.Green.Darken3,
        },
        PaletteDark = new PaletteDark
        {
            Primary = Colors.Green.Lighten1,
            PrimaryContrastText = Colors.Shades.Black,
        },
    };
}
