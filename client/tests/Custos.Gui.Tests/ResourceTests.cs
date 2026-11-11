using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Custos.Core.Cobian;

namespace Custos.Gui.Tests;

/// <summary>Texts in both languages, the remarks of the import, the drawn pictures and the embedded icons.</summary>
public sealed partial class ResourceTests
{
    private static readonly object?[] Arguments = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];

    [Theory]
    [InlineData("")]
    [InlineData("ru")]
    public void Every_text_of_the_interface_exists_and_can_be_formatted(string culture)
    {
        var resources = new ResourceManager("Custos.Gui.Resources.Strings", typeof(Strings).Assembly);
        var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);

        var names = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToList();
        Assert.True(names.Count > 300, $"Only {names.Count} texts found.");
        foreach (string name in names)
        {
            string text = set.GetString(name) ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(text), $"{name} has no text in '{culture}'.");
            if (Placeholder().IsMatch(text))
            {
                var error = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, text, Arguments));
                Assert.True(error is null, $"{name} in '{culture}' cannot be formatted: {error?.Message}");
            }
        }
    }

    [Fact]
    public void Russian_texts_are_found_by_the_interface()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            string russian = Strings.ButtonCancel;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            string english = Strings.ButtonCancel;

            Assert.Equal("Cancel", english);
            Assert.NotEqual(english, russian);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void Every_remark_of_the_import_is_explained_in_words(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var codes = typeof(ImportWarningCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();
            Assert.NotEmpty(codes);
            foreach (string code in codes)
            {
                // Without a text of its own, a remark is shown as "code: argument".
                Assert.NotEqual(code + ": x", CobianImportForm.Describe(new ImportWarning(code, "x")));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(40)]
    public void Every_picture_is_drawn_at_every_size(int size)
    {
        foreach (var glyph in Enum.GetValues<Glyph>())
        {
            using var bitmap = Glyphs.Draw(glyph, size);
            Assert.Equal(new Size(size, size), bitmap.Size);
        }

        foreach (var state in Enum.GetValues<StateIcon>())
        {
            using var bitmap = Glyphs.Draw(state, size);
            Assert.Equal(new Size(size, size), bitmap.Size);
        }

        // The lists find their icons by the name of the state.
        using var images = Glyphs.StateImages(size);
        Assert.All(Enum.GetValues<StateIcon>(), state => Assert.True(images.Images.ContainsKey(state.ToString()), state.ToString()));
    }

    [Fact]
    public void Every_icon_is_embedded()
    {
        foreach (var load in new Func<Icon>[] { AppIcons.App, AppIcons.TrayOk, AppIcons.TrayRunning, AppIcons.TrayWarning, AppIcons.TrayError, AppIcons.TrayDown })
        {
            using var icon = load();
            Assert.True(icon.Width > 0);
        }
    }

    [GeneratedRegex(@"\{\d+(,-?\d+)?(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
