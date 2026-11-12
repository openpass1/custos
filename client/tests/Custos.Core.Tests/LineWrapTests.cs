using Custos.Core.Text;

namespace Custos.Core.Tests;

/// <summary>Long hints are broken into lines of a readable width.</summary>
public sealed class LineWrapTests
{
    [Fact]
    public void A_short_hint_stays_as_it_is()
    {
        Assert.Equal("Уровень 5 — обычный баланс.", LineWrap.Wrap("Уровень 5 — обычный баланс."));
    }

    [Fact]
    public void A_long_hint_is_broken_at_spaces_into_lines_no_longer_than_the_limit()
    {
        const string text = "Отмеченная база попадает в копию. «Все базы» — все базы сервера, и новые тоже; если снять галочку с базы, выбор переключится на «Выбранные базы». Системные базы не выгружаются; пользователи и права попадают в _users.sql.";

        string wrapped = LineWrap.Wrap(text);

        string[] lines = wrapped.Split(Environment.NewLine);
        Assert.True(lines.Length >= 3, wrapped);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, LineWrap.HintLineLength));
        Assert.Equal(text, string.Join(" ", lines));
        // Lines are filled: each but the last would not take the next word.
        for (int i = 0; i < lines.Length - 1; i++)
        {
            Assert.True(lines[i].Length + 1 + lines[i + 1].Split(' ')[0].Length > LineWrap.HintLineLength, wrapped);
        }
    }

    [Fact]
    public void Line_breaks_of_the_text_stay_and_list_items_keep_their_indent()
    {
        string wrapped = LineWrap.Wrap("Включено: копия в своей папке.\n• Первый пункт списка, достаточно длинный, чтобы не уместиться в одну строку.", 40);

        Assert.Equal(
            string.Join(Environment.NewLine,
                "Включено: копия в своей папке.",
                "• Первый пункт списка, достаточно",
                "  длинный, чтобы не уместиться в одну",
                "  строку."),
            wrapped);
    }

    [Fact]
    public void A_word_longer_than_a_line_gets_a_line_of_its_own()
    {
        string wrapped = LineWrap.Wrap(@"Путь C:\ProgramData\CUSTOS\logs\very-long-folder-name\service.log здесь", 20);

        Assert.Equal(string.Join(Environment.NewLine, "Путь", @"C:\ProgramData\CUSTOS\logs\very-long-folder-name\service.log", "здесь"), wrapped);
    }
}
