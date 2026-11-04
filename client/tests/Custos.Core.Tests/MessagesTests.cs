using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;

namespace Custos.Core.Tests;

/// <summary>Every text of Resources\Messages.tsv exists in English and in Russian and can be formatted.</summary>
public sealed partial class MessagesTests
{
    private static readonly object?[] Arguments = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];

    [Theory]
    [InlineData("")]
    [InlineData("ru")]
    public void Every_message_exists_and_can_be_formatted(string culture)
    {
        // Read straight from the resources: Messages.Culture is shared by all tests and stays untouched.
        var resources = new ResourceManager("Custos.Core.Resources.Messages", typeof(Messages).Assembly);
        var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);

        var names = typeof(Messages).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToList();
        Assert.True(names.Count > 100, $"Only {names.Count} messages found.");
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

    [GeneratedRegex(@"\{\d+(,-?\d+)?(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
