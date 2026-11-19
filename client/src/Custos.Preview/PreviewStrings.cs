using System.Globalization;

namespace Custos.Gui;

/// <summary>Texts of the trial window only (the others come from Custos.exe's Resources\Strings.tsv).</summary>
internal static partial class Strings
{
    private static bool Russian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    public static string PreviewTitle => Russian ? "CUSTOS — пробное окно Material 3" : "CUSTOS — Material 3 trial window";

    public static string PreviewReadOnly => Russian
        ? "Пробное окно только показывает. Изменять и запускать задания — в обычном окне CUSTOS."
        : "The trial window only shows. Change and run tasks in the usual CUSTOS window.";

    public static string PreviewOk => Russian ? "Понятно" : "OK";

    public static string PreviewNow => Russian ? "сейчас" : "now";

    public static string PreviewTasks => Russian ? "Задания" : "Tasks";

    public static string PreviewHistoryCount => Russian ? "{0}: запусков {1}" : "{0}: {1} runs";

    public static string PreviewTheme => Russian ? "Тема окна" : "Window theme";

    public static string PreviewThemeAuto => Russian ? "Как в настройках CUSTOS" : "As in the CUSTOS settings";

    public static string PreviewThemeLight => Russian ? "Светлая" : "Light";

    public static string PreviewThemeDark => Russian ? "Тёмная" : "Dark";

    public static string PreviewColor => Russian ? "Цвет" : "Colour";

    public static string PreviewColorSite => Russian ? "Как на сайте" : "As the site";

    public static string PreviewColorNeutral => Russian ? "Нейтральный серый" : "Neutral grey";

    public static string PreviewColorBlue => Russian ? "Синий" : "Blue";

    public static string PreviewColorTeal => Russian ? "Бирюзовый" : "Teal";

    public static string PreviewColorGraphite => Russian ? "Графит" : "Graphite";

    public static string PreviewSave => Russian ? "Сохранить" : "Save";

    public static string PreviewNotSaved => Russian
        ? "Пробное окно ничего не сохраняет: задание осталось как было."
        : "The trial window saves nothing: the task stays as it was.";

    public static string PreviewEditorReadOnly => Russian
        ? "В пробном окне эта кнопка ничего не делает."
        : "In the trial window this button does nothing.";

    public static string PreviewLinuxServer => Russian ? "Linux-сервер" : "Linux server";

    public static string PreviewEveryMinutes => Russian ? "минут" : "minutes";

    public static string PreviewWormTag => Russian ? "WORM {0} дн." : "WORM {0} days";

    public static string PreviewObjectLockTag => Russian ? "Object Lock {0} дн." : "Object Lock {0} days";

    public static string PreviewDemoComment => Russian
        ? "Документы отдела: диск USB, NAS с защитой от удаления и копия вне офиса."
        : "The department's documents: a USB disk, the NAS with protection from deletion, and a copy outside the office.";

    public static string PreviewDemoUsb => Russian ? "Диск BACKUP-USB не подключён." : "The disk BACKUP-USB is not connected.";

    public static string PreviewDemoStarted => Russian ? "запуск по расписанию" : "started by the schedule";

    public static string PreviewDemoDone => Russian ? "успешно — файлов {0}, {1}, {2}" : "success — {0} files, {1}, {2}";

    public static string PreviewDemoScan => Russian ? "поиск файлов: 12 640" : "looking for files: 12 640";

    public static string PreviewDemoLocked => Russian
        ? "файл занят другой программой, взят из теневой копии: Reports\\2026-11 budget.xlsx"
        : "the file is in use by another program, taken from the shadow copy: Reports\\2026-11 budget.xlsx";
}
