using System.ComponentModel;
using System.Globalization;

namespace Custos.Gui;

/// <summary>
/// The days of a monthly schedule as a drop-down. The box shows the chosen days ("1, 15, last day"); a click, F4,
/// Space or Alt+Down opens a small calendar of 1–30 and «last day of the month», where any number of days are ticked.
/// There is no 31: a month without a chosen day runs on its last day
/// (<see cref="Core.Scheduling.ScheduleCalculator.IsMonthDay"/>), so the 31st and the last day would be the same.
/// </summary>
internal sealed class MonthDaysBox : ComboBox
{
    /// <summary>The highest day offered as a number; after it comes «last day of the month» (-1).</summary>
    public const int LastNumber = 30;

    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;
    private const int Columns = 7;

    /// <summary>A click on the box that closed the calendar must not open it again at once.</summary>
    private const int ReopenDelayMs = 300;

    private readonly SortedSet<int> _days = [];
    private bool _lastDay;
    private ToolStripDropDown? _calendar;
    private long? _closedAt;

    public MonthDaysBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        AccessibleName = Strings.ScheduleMonthDays.TrimEnd(':');
        ShowText();
    }

    /// <summary>Raised when a day is ticked or unticked in the calendar.</summary>
    public event EventHandler? DaysChanged;

    /// <summary>
    /// The chosen days in order, -1 (the last day) at the end. A 31 given here comes back as -1, which it means;
    /// anything else that is not a day is dropped.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<int> Days
    {
        get => _lastDay ? [.. _days, -1] : [.. _days];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _days.Clear();
            _days.UnionWith(value.Where(d => d is >= 1 and <= LastNumber));
            _lastDay = value.Any(d => d is -1 or 31);
            ShowText();
        }
    }

    /// <summary>The calendar while it is open, for tests.</summary>
    internal ToolStripDropDown? Calendar => _calendar;

    /// <summary>Opens the calendar under the box.</summary>
    internal void OpenCalendar()
    {
        if (_calendar is not null)
        {
            return;
        }

        var boxes = new List<CheckBox>();
        var grid = BuildGrid(boxes);
        var host = new ToolStripControlHost(grid) { AutoSize = false, Size = grid.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        var calendar = new ToolStripDropDown
        {
            AutoClose = true,
            Padding = new Padding(1),
            RenderMode = ToolStripRenderMode.System,
            BackColor = SystemColors.Window,
        };
        calendar.Items.Add(host);
        calendar.Closed += (_, e) =>
        {
            _calendar = null;
            _closedAt = Environment.TickCount64;
            if (e.CloseReason == ToolStripDropDownCloseReason.Keyboard && CanFocus)
            {
                Focus();
            }

            // Not disposed inside its own event; when the box itself goes, it disposes the calendar.
            if (IsHandleCreated && !Disposing && !IsDisposed)
            {
                BeginInvoke(() => calendar.Dispose());
            }
        };

        _calendar = calendar;
        calendar.Show(this, new Point(0, Height));
        (boxes.FirstOrDefault(b => b.Checked) ?? boxes[0]).Focus();
    }

    protected override void WndProc(ref Message m)
    {
        // The list of the combo box is never shown: a click opens the calendar instead.
        if (m.Msg is WmLButtonDown or WmLButtonDblClk)
        {
            if (CanFocus)
            {
                Focus();
            }

            // The click that closed the calendar (outside it, on the box) arrives here next.
            if (_closedAt is not { } closed || Environment.TickCount64 - closed >= ReopenDelayMs)
            {
                OpenCalendar();
            }

            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        bool open = e.KeyCode is Keys.F4 or Keys.Space || (e.Alt && e.KeyCode is Keys.Down or Keys.Up);
        bool moves = e.KeyCode is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End;
        if (open || moves)
        {
            // Handled here, so the combo box neither drops its own list nor changes the shown text.
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (open)
            {
                OpenCalendar();
            }

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnDropDown(EventArgs e)
    {
        // Any other way the list opens: close it and show the calendar.
        base.OnDropDown(e);
        BeginInvoke(() =>
        {
            DroppedDown = false;
            OpenCalendar();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var calendar = _calendar;
            _calendar = null;
            calendar?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The text of the box: the days in order, then «last day».</summary>
    internal static string Describe(IReadOnlyList<int> days)
    {
        var parts = days.Where(d => d > 0).Select(d => d.ToString(CultureInfo.CurrentCulture)).ToList();
        if (days.Contains(-1))
        {
            parts.Add(Strings.LastDayShort);
        }

        return string.Join(", ", parts);
    }

    private void ShowText()
    {
        Items.Clear();
        Items.Add(Describe(Days));
        SelectedIndex = 0;
    }

    /// <summary>
    /// 1–30 in rows of seven like a calendar page, «last day of the month» across the whole last row. The days are
    /// toggle buttons coloured like a selection, so that they read the same in the light and the dark theme.
    /// </summary>
    private Panel BuildGrid(List<CheckBox> boxes)
    {
        int gap = LogicalToDeviceUnits(2);
        var digits = TextRenderer.MeasureText("30", Font);
        int cell = Math.Max(digits.Width, digits.Height) + LogicalToDeviceUnits(12);
        int width = Columns * cell + (Columns - 1) * gap;
        int lastWidth = TextRenderer.MeasureText(Strings.LastDayOfMonth, Font).Width + LogicalToDeviceUnits(16);
        width = Math.Max(width, lastWidth);
        int rows = (LastNumber + Columns - 1) / Columns;
        var grid = new Panel { BackColor = SystemColors.Window, Margin = Padding.Empty, Padding = Padding.Empty };

        for (int day = 1; day <= LastNumber; day++)
        {
            int index = day - 1;
            var box = DayBox(day.ToString(CultureInfo.CurrentCulture), _days.Contains(day));
            box.Bounds = new Rectangle(index % Columns * (cell + gap), index / Columns * (cell + gap), cell, cell);
            int value = day;
            box.CheckedChanged += (_, _) => Toggle(value, box.Checked);
            boxes.Add(box);
        }

        var last = DayBox(Strings.LastDayOfMonth, _lastDay);
        last.Bounds = new Rectangle(0, rows * (cell + gap), width, cell);
        last.CheckedChanged += (_, _) => Toggle(-1, last.Checked);
        boxes.Add(last);

        for (int i = 0; i < boxes.Count; i++)
        {
            boxes[i].TabIndex = i;
            int at = i;
            boxes[i].PreviewKeyDown += (_, e) => e.IsInputKey = e.KeyCode is Keys.Enter or Keys.Escape or Keys.Left or Keys.Right or Keys.Up or Keys.Down;
            boxes[i].KeyDown += (_, e) => MoveOrClose(boxes, at, e);
        }

        grid.Controls.AddRange([.. boxes]);
        grid.Size = new Size(width, (rows + 1) * (cell + gap) - gap);
        return grid;
    }

    private static CheckBox DayBox(string text, bool chosen)
    {
        var box = new CheckBox
        {
            Text = text,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Margin = Padding.Empty,
            UseVisualStyleBackColor = false,
        };
        box.FlatAppearance.BorderColor = SystemColors.ControlDark;
        box.CheckedChanged += (_, _) => Colour(box);
        box.Checked = chosen;
        Colour(box);
        return box;

        static void Colour(CheckBox box)
        {
            var back = box.Checked ? SystemColors.Highlight : SystemColors.Window;
            box.BackColor = back;
            box.ForeColor = box.Checked ? SystemColors.HighlightText : SystemColors.WindowText;
            box.FlatAppearance.CheckedBackColor = SystemColors.Highlight;
            box.FlatAppearance.MouseOverBackColor = box.Checked ? SystemColors.Highlight : SystemColors.ControlLight;
            box.FlatAppearance.MouseDownBackColor = SystemColors.Highlight;
        }
    }

    private void Toggle(int day, bool chosen)
    {
        if (day == -1)
        {
            _lastDay = chosen;
        }
        else if (chosen)
        {
            _days.Add(day);
        }
        else
        {
            _days.Remove(day);
        }

        ShowText();
        DaysChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Arrows move over the calendar (a row is seven days), Enter and Esc close it.</summary>
    private void MoveOrClose(List<CheckBox> boxes, int at, KeyEventArgs e)
    {
        int last = boxes.Count - 1;
        int? next = e.KeyCode switch
        {
            Keys.Left => Math.Max(0, at - 1),
            Keys.Right => Math.Min(last, at + 1),
            Keys.Up => at == last ? last - 1 : Math.Max(0, at - Columns),
            Keys.Down => Math.Min(last, at + Columns),
            _ => null,
        };

        if (next is { } index)
        {
            boxes[index].Focus();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Escape)
        {
            _calendar?.Close(ToolStripDropDownCloseReason.Keyboard);
            e.Handled = true;
        }
    }
}
