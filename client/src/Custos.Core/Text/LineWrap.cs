using System.Text;

namespace Custos.Core.Text;

/// <summary>
/// Breaks long text into lines for tooltips. Windows shows a tooltip of the interface in one line as wide as the
/// screen, which is hard to read; broken at about 70 characters it is a comfortable block of about 450 pixels.
/// </summary>
public static class LineWrap
{
    /// <summary>The longest line of a hint, in characters (the usual 60–75 of a book page).</summary>
    public const int HintLineLength = 70;

    /// <summary>
    /// Breaks <paramref name="text"/> at spaces into lines of at most <paramref name="lineLength"/> characters. Line
    /// breaks in the text stay; the next lines of a «• » list item are indented to stand under its text. A word longer
    /// than a line (a path) gets a line of its own and is not cut.
    /// </summary>
    public static string Wrap(string text, int lineLength = HintLineLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(text.Length + 16);
        string[] paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0)
            {
                result.Append(Environment.NewLine);
            }

            string paragraph = paragraphs[p];
            string indent = paragraph.StartsWith("• ", StringComparison.Ordinal) ? "  " : string.Empty;
            int column = 0;
            bool lineEmpty = true;
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!lineEmpty && column + 1 + word.Length > lineLength)
                {
                    result.Append(Environment.NewLine).Append(indent);
                    column = indent.Length;
                    lineEmpty = true;
                }

                if (!lineEmpty)
                {
                    result.Append(' ');
                    column++;
                }

                result.Append(word);
                column += word.Length;
                lineEmpty = false;
            }
        }

        return result.ToString();
    }
}
