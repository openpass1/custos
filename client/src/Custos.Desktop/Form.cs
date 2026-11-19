using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Layout;

namespace Custos.Desktop;

/// <summary>
/// A piece of a form of the new window: the pages of the task editor, the dialogs and the settings are lists of these,
/// and FormView.axaml has a template for each kind. What a piece shows and holds is observable, so a form changes
/// what it shows by setting properties, as the forms of Custos.exe set their controls.
/// </summary>
internal abstract class FormItem : Observable
{
    private bool _visible = true;
    private bool _enabled = true;

    public bool IsVisible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    public bool IsEnabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>Space on the left, for what belongs to the choice above it.</summary>
    public double Indent { get; init; }

    public Thickness Offset => new(Indent, 0, 0, 0);
}

/// <summary>A heading of a part of a form, with a «?» that explains it.</summary>
internal sealed class HeadingItem(string text, string? help = null) : FormItem
{
    public string Text { get; } = text;

    public string? Help { get; } = help;

    public bool HasHelp => Help is not null;
}

internal enum Tone
{
    /// <summary>Grey text.</summary>
    Plain,

    /// <summary>A block with a warning sign.</summary>
    Warning,

    /// <summary>Red text: what is wrong.</summary>
    Error,

    /// <summary>Green text: what went well.</summary>
    Ok,

    /// <summary>Text in the colour of the warnings.</summary>
    Caution,
}

/// <summary>An explanation, a warning or the result of a check.</summary>
internal sealed class NoteItem : FormItem
{
    private string _text;
    private Tone _tone;

    public NoteItem(string text, Tone tone = Tone.Plain)
    {
        _text = text;
        _tone = tone;
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public Tone Tone
    {
        get => _tone;
        set
        {
            if (Set(ref _tone, value))
            {
                Changed(nameof(IsBox));
                Changed(nameof(IsLine));
                Changed(nameof(IsError));
                Changed(nameof(IsOk));
                Changed(nameof(IsCaution));
            }
        }
    }

    public bool IsBox => _tone == Tone.Warning;

    public bool IsLine => _tone != Tone.Warning;

    public bool IsError => _tone == Tone.Error;

    public bool IsOk => _tone == Tone.Ok;

    public bool IsCaution => _tone == Tone.Caution;

    /// <summary>Shows a text, or hides the note when there is none.</summary>
    public void Show(string? text, Tone? tone = null)
    {
        Text = text ?? string.Empty;
        if (tone is { } t)
        {
            Tone = t;
        }

        IsVisible = !string.IsNullOrEmpty(text);
    }
}

/// <summary>A piece of text inside a row («-й запуск», «МБ/с»).</summary>
internal sealed class LabelItem(string text, bool strong = false) : FormItem
{
    private string _text = text;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public bool Strong { get; } = strong;
}

/// <summary>Several pieces on one line.</summary>
internal sealed class RowItem(params FormItem[] items) : FormItem
{
    public IReadOnlyList<FormItem> Items { get; } = items;
}

/// <summary>Base of the fields with an outlined frame: the label on the frame, a hint or what is wrong under it.</summary>
internal abstract class FieldBase : FormItem
{
    private string? _label;
    private string? _hint;
    private string? _error;
    private string? _help;

    protected FieldBase(string? label, double width)
    {
        _label = label;
        Width = width;
    }

    public string? Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public string? Hint
    {
        get => _hint;
        set
        {
            if (Set(ref _hint, value))
            {
                Changed(nameof(ShownHint));
            }
        }
    }

    /// <summary>What is wrong with the value: the frame turns red and this replaces the hint.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
            {
                Changed(nameof(ShownHint));
                Changed(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public string? ShownHint => _error ?? _hint;

    /// <summary>The width of the frame; NaN = the whole width of the form.</summary>
    public double Width { get; }

    public HorizontalAlignment Align => double.IsNaN(Width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

    /// <summary>A small frame without a label, inside a line of text.</summary>
    public bool Compact { get; init; }

    /// <summary>The explanation behind a «?» beside the field.</summary>
    public string? Help
    {
        get => _help;
        set
        {
            if (Set(ref _help, value))
            {
                Changed(nameof(HasHelp));
            }
        }
    }

    public bool HasHelp => _help is not null;

    /// <summary>Called when the user changed the value (not when the form set it).</summary>
    public Action? OnChange { get; set; }
}

/// <summary>A text field; with <see cref="Secret"/> a password with an eye to show it.</summary>
internal sealed class TextItem(string? label, string text = "", double width = double.NaN) : FieldBase(label, width)
{
    private string _text = text;
    private bool _reveal;
    private string? _placeholder;
    private bool _readOnly;

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty))
            {
                Error = null;
                OnChange?.Invoke();
            }
        }
    }

    /// <summary>The text without the spaces and quotes around it.</summary>
    public string Value => _text.Trim().Trim('"').Trim();

    public string? Placeholder
    {
        get => _placeholder;
        set => Set(ref _placeholder, value);
    }

    public bool ReadOnly
    {
        get => _readOnly;
        set => Set(ref _readOnly, value);
    }

    public bool Multiline { get; init; }

    /// <summary>The height of a field of several lines.</summary>
    public double Lines { get; init; } = 4;

    public double Height => Multiline ? Math.Max(2, Lines) * 22 + 24 : double.NaN;

    /// <summary>A field of program code: no wrapping, a fixed-width font.</summary>
    public bool Mono { get; init; }

    public bool Wrap => Multiline && !Mono;

    public bool Secret { get; init; }

    public char Mask => Secret && !_reveal ? '•' : '\0';

    public bool Reveal
    {
        get => _reveal;
        set
        {
            if (Set(ref _reveal, value))
            {
                Changed(nameof(Mask));
                Changed(nameof(RevealIcon));
            }
        }
    }

    public string RevealIcon => _reveal ? "visibility_off" : "visibility";

    public int MaxLength { get; init; }

    /// <summary>A button inside the frame on the right (browse, paste).</summary>
    public string? ActionIcon { get; init; }

    public string? ActionTip { get; init; }

    public Func<Task>? Action { get; init; }

    public bool HasAction => Action is not null;

    public bool HasIcon => Action is not null || Secret;

    /// <summary>Sets the text as the form shows it, without calling <see cref="FieldBase.OnChange"/>.</summary>
    public void Load(string? text)
    {
        _text = text ?? string.Empty;
        Changed(nameof(Text));
    }

    public void Run() => Safe.Run(() => Action?.Invoke() ?? Task.CompletedTask);
}

/// <summary>A whole number (or tenths) between two limits, typed into a small field.</summary>
internal sealed class NumberItem : FieldBase
{
    private string _text;

    public NumberItem(string? label, decimal value, decimal min, decimal max, double width = 120, int decimals = 0)
        : base(label, width)
    {
        Min = min;
        Max = max;
        Decimals = decimals;
        _text = Format(Math.Clamp(value, min, max));
    }

    public decimal Min { get; }

    public decimal Max { get; }

    public int Decimals { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty))
            {
                Error = Parse(_text) is null ? Range() : null;
                OnChange?.Invoke();
            }
        }
    }

    /// <summary>The number typed; the nearest allowed one when the text is not a number in range.</summary>
    public decimal Number
    {
        get => Parse(_text) ?? Min;
        set
        {
            _text = Format(Math.Clamp(value, Min, Max));
            Error = null;
            Changed(nameof(Text));
        }
    }

    public int Value
    {
        get => (int)Number;
        set => Number = value;
    }

    public bool IsValid => Parse(_text) is not null;

    /// <summary>«Введите число от 1 до 1000», for the field the user left wrong.</summary>
    public string Range() => Custos.Gui.Strings.Format(DesktopStrings.NumberRange, Format(Min), Format(Max));

    private string Format(decimal value) => value.ToString(Decimals == 0 ? "0" : "0.#", CultureInfo.CurrentCulture);

    private decimal? Parse(string text)
    {
        string clean = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace('.', ',');
        var culture = CultureInfo.CurrentCulture;
        clean = clean.Replace(",", culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);
        if (!decimal.TryParse(clean, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, culture, out decimal value))
        {
            return null;
        }

        value = Math.Round(value, Decimals);
        return value < Min || value > Max ? null : value;
    }
}

/// <summary>A drop-down list.</summary>
internal sealed class ChoiceItem : FieldBase
{
    private IReadOnlyList<string> _options;
    private int _selected;
    private bool _replacing;

    public ChoiceItem(string? label, IReadOnlyList<string> options, int selected = 0, double width = 360)
        : base(label, width)
    {
        _options = options;
        _selected = selected;
    }

    public IReadOnlyList<string> Options
    {
        get => _options;
        set
        {
            // A new list resets the choice of the drop-down: the choice of the form stays.
            int selected = _selected;
            _replacing = true;
            _options = value;
            Changed();
            _replacing = false;
            _selected = Math.Min(selected, value.Count - 1);
            Changed(nameof(Selected));
        }
    }

    public int Selected
    {
        get => _selected;
        set
        {
            if (_replacing || value < 0)
            {
                return;
            }

            if (Set(ref _selected, value))
            {
                OnChange?.Invoke();
            }
        }
    }

    /// <summary>Selects without calling <see cref="FieldBase.OnChange"/>.</summary>
    public void Load(int selected)
    {
        _selected = selected;
        Changed(nameof(Selected));
    }
}

/// <summary>A field with a list to pick from, where any other text can be typed as well (a name template, a label).</summary>
internal sealed class ComboItem(string? label, IReadOnlyList<string> options, string text = "", double width = double.NaN) : FieldBase(label, width)
{
    private IReadOnlyList<string> _options = options;
    private string _text = text;

    public IReadOnlyList<string> Options
    {
        get => _options;
        set => Set(ref _options, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty))
            {
                Error = null;
                OnChange?.Invoke();
            }
        }
    }

    public string Value => _text.Trim();

    public void Load(string? text)
    {
        _text = text ?? string.Empty;
        Changed(nameof(Text));
    }
}

/// <summary>A switch with its title and description; in a tonal block when <see cref="Card"/>.</summary>
internal sealed class SwitchItem(string title, string? description = null, bool on = false, bool card = false) : FormItem
{
    private bool _on = on;
    private string? _description = description;

    public string Title { get; } = title;

    public string? Description
    {
        get => _description;
        set
        {
            if (Set(ref _description, value))
            {
                Changed(nameof(HasDescription));
            }
        }
    }

    public bool HasDescription => !string.IsNullOrEmpty(_description);

    public bool Card { get; } = card;

    public bool On
    {
        get => _on;
        set
        {
            if (Set(ref _on, value))
            {
                OnChange?.Invoke();
            }
        }
    }

    public Action? OnChange { get; set; }
}

/// <summary>A check box with its title and an explanation under it.</summary>
internal sealed class CheckItem(string title, bool on = false, string? description = null) : FormItem
{
    private bool _on = on;
    private string _title = title;
    private string? _description = description;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string? Description
    {
        get => _description;
        set
        {
            if (Set(ref _description, value))
            {
                Changed(nameof(HasDescription));
            }
        }
    }

    public bool HasDescription => !string.IsNullOrEmpty(_description);

    /// <summary>On one line with a field: the text in the middle of the line.</summary>
    public bool Inline { get; init; }

    /// <summary>Some other value the box stands for (a database in a list).</summary>
    public object? Tag { get; init; }

    public bool On
    {
        get => _on;
        set
        {
            if (Set(ref _on, value))
            {
                OnChange?.Invoke();
            }
        }
    }

    public Action? OnChange { get; set; }

    public void Load(bool on)
    {
        _on = on;
        Changed(nameof(On));
    }
}

/// <summary>The radio buttons of one choice: checking one unchecks the others.</summary>
internal sealed class RadioGroup
{
    private readonly List<RadioItem> _items = [];

    public IReadOnlyList<RadioItem> Items => _items;

    public int Selected => _items.FindIndex(i => i.On);

    internal void Add(RadioItem item) => _items.Add(item);

    internal void Checked(RadioItem item)
    {
        foreach (var other in _items.Where(i => i != item && i.On))
        {
            other.Uncheck();
        }
    }
}

/// <summary>A radio button with a title and a hint under it.</summary>
internal sealed class RadioItem : FormItem
{
    private readonly RadioGroup _group;
    private bool _on;
    private string _title;
    private string? _hint;

    public RadioItem(RadioGroup group, string title, string? hint = null, bool on = false, bool inline = false)
    {
        _group = group;
        _title = title;
        _hint = hint;
        Inline = inline;
        group.Add(this);
        if (on)
        {
            Load();
        }
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string? Hint
    {
        get => _hint;
        set
        {
            if (Set(ref _hint, value))
            {
                Changed(nameof(HasHint));
            }
        }
    }

    public bool HasHint => !string.IsNullOrEmpty(_hint);

    public bool Inline { get; }

    public object? Tag { get; init; }

    public bool On
    {
        get => _on;
        set
        {
            // A radio button is unchecked only by checking another one.
            if (!value || _on)
            {
                Changed();
                return;
            }

            _on = true;
            _group.Checked(this);
            Changed();
            OnChange?.Invoke();
        }
    }

    public Action? OnChange { get; set; }

    /// <summary>Checks this one without calling <see cref="OnChange"/>.</summary>
    public void Load()
    {
        _on = true;
        _group.Checked(this);
        Changed(nameof(On));
    }

    internal void Uncheck()
    {
        _on = false;
        Changed(nameof(On));
        OnChange?.Invoke();
    }
}

/// <summary>One button of a segmented row.</summary>
internal sealed class Segment(SegmentsItem owner, int index, string text) : Observable
{
    public string Text { get; } = text;

    public bool On
    {
        get => owner.Selected == index;
        set
        {
            if (value)
            {
                owner.Selected = index;
            }
            else
            {
                Changed();
            }
        }
    }

    internal void Refresh() => Changed(nameof(On));
}

/// <summary>Segmented buttons: one of a few choices (the kind of a schedule).</summary>
internal sealed class SegmentsItem : FormItem
{
    private int _selected;

    public SegmentsItem(string label, IReadOnlyList<string> options, int selected)
    {
        Label = label;
        _selected = selected;
        Segments = [.. options.Select((o, i) => new Segment(this, i, o))];
    }

    public string Label { get; }

    public IReadOnlyList<Segment> Segments { get; }

    public int Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                foreach (var segment in Segments)
                {
                    segment.Refresh();
                }

                OnChange?.Invoke();
            }
        }
    }

    public Action? OnChange { get; set; }

    public void Load(int selected)
    {
        _selected = selected;
        Changed(nameof(Selected));
        foreach (var segment in Segments)
        {
            segment.Refresh();
        }
    }
}

/// <summary>A filter chip of a <see cref="ChipsItem"/> (a day of the week).</summary>
internal sealed class Chip(string text, bool on, Action? changed) : Observable
{
    private bool _on = on;

    public string Text { get; } = text;

    public bool On
    {
        get => _on;
        set
        {
            if (Set(ref _on, value))
            {
                changed?.Invoke();
            }
        }
    }

    public void Load(bool on)
    {
        _on = on;
        Changed(nameof(On));
    }
}

internal sealed class ChipsItem : FormItem
{
    public ChipsItem(string label, IEnumerable<string> texts)
    {
        Label = label;
        Chips = [.. texts.Select(t => new Chip(t, false, () => OnChange?.Invoke()))];
    }

    public string Label { get; }

    public IReadOnlyList<Chip> Chips { get; }

    public Action? OnChange { get; set; }
}

internal sealed record CardTag(string Icon, string Text, bool Accent = false);

/// <summary>A source, a destination, an event or an account: an icon, two lines, tags, and its buttons.</summary>
internal sealed class CardItem(string icon, string title, string? subtitle, IReadOnlyList<CardTag>? tags = null) : FormItem
{
    public string Icon { get; } = icon;

    public string Title { get; } = title;

    public string? Subtitle { get; } = subtitle;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public IReadOnlyList<CardTag> Tags { get; } = tags ?? [];

    public bool HasTags => Tags.Count > 0;

    /// <summary>The line under the title is a warning (an account that is gone).</summary>
    public bool Warning { get; init; }

    public Action? Edit { get; init; }

    public Action? Delete { get; init; }

    public Action? Up { get; init; }

    public Action? Down { get; init; }

    public bool CanEdit => Edit is not null;

    public bool CanDelete => Delete is not null;

    public bool CanMove => Up is not null;

    public bool IsFirst { get; init; }

    public bool IsLast { get; init; }

    public bool CanUp => !IsFirst;

    public bool CanDown => !IsLast;

    public void DoEdit() => Edit?.Invoke();

    public void DoDelete() => Delete?.Invoke();

    public void DoUp() => Up?.Invoke();

    public void DoDown() => Down?.Invoke();
}

/// <summary>A list of cards that changes (the sources of a task); a line of text when it is empty.</summary>
internal sealed class CardsItem(string? empty = null) : FormItem
{
    private bool _isEmpty = true;

    public ObservableCollection<CardItem> Cards { get; } = [];

    public string? Empty { get; } = empty;

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public bool ShowEmpty => _isEmpty && Empty is not null;

    public void Fill(IEnumerable<CardItem> cards)
    {
        Cards.Clear();
        foreach (var card in cards)
        {
            Cards.Add(card);
        }

        IsEmpty = Cards.Count == 0;
        Changed(nameof(ShowEmpty));
    }
}

/// <summary>A list of check boxes in a frame (the databases of a server).</summary>
internal sealed class CheckListItem(double height = 150) : FormItem
{
    public ObservableCollection<CheckItem> Items { get; } = [];

    public double Height { get; } = height;
}

internal enum ButtonLook
{
    Tonal,
    Outlined,
    Text,
    Filled,
}

/// <summary>A button of a form; while its work runs it is off.</summary>
internal sealed class ButtonItem(string? icon, string text, Func<Task> action, ButtonLook look = ButtonLook.Tonal) : FormItem
{
    private string _text = text;
    private bool _busy;

    public string? Icon { get; } = icon;

    public bool HasIcon => Icon is not null;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public ButtonLook Look { get; } = look;

    public bool IsTonal => Look == ButtonLook.Tonal;

    public bool IsOutlined => Look == ButtonLook.Outlined;

    public bool IsText => Look == ButtonLook.Text;

    public bool IsFilled => Look == ButtonLook.Filled;

    /// <summary>Beside a field: the button sits on the middle of the frame, under the label.</summary>
    public bool ByField { get; init; }

    public Thickness Margin => ByField ? new Thickness(0, 8, 0, 0) : default;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Changed(nameof(IsFree));
            }
        }
    }

    public bool IsFree => !_busy;

    /// <summary>What went wrong in the work, for the form to show; null shows nothing.</summary>
    public Action<Exception>? Failed { get; set; }

    public async void Press()
    {
        if (_busy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A button whose work is not awaited.</summary>
    public static ButtonItem Of(string? icon, string text, Action action, ButtonLook look = ButtonLook.Tonal) =>
        new(icon, text, () =>
        {
            action();
            return Task.CompletedTask;
        }, look);
}

/// <summary>A block with an icon: the schedule in plain words, a key of a server.</summary>
internal sealed class InfoItem(string icon, string title, string? detail = null) : FormItem
{
    private string _title = title;
    private string? _detail = detail;

    public string Icon { get; } = icon;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string? Detail
    {
        get => _detail;
        set
        {
            if (Set(ref _detail, value))
            {
                Changed(nameof(HasDetail));
            }
        }
    }

    public bool HasDetail => !string.IsNullOrEmpty(_detail);
}
