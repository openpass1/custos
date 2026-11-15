using System.ComponentModel;

namespace Custos.Gui;

/// <summary>
/// A speed limit typed in MB/s, to a tenth, and kept in KB/s (1 MB = 1024 KB) as the task and the protocol store it.
/// A limit that was not changed comes back exactly as it was loaded, so that 500 KB/s does not turn into 512.
/// </summary>
internal sealed class SpeedBox : NumericUpDown
{
    /// <summary>The highest limit the task takes, in KB/s.</summary>
    public const int MaxKbps = 10_000_000;

    private int _kbps;

    public SpeedBox()
    {
        DecimalPlaces = 1;
        Increment = 1;
        Minimum = 0;
        Maximum = Math.Floor(MaxKbps / 102.4m) / 10;
    }

    /// <summary>The limit in KB/s; 0 = none.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Kbps
    {
        get => Value == Shown(_kbps) ? _kbps : (int)Math.Min(MaxKbps, Math.Round(Value * 1024));
        set
        {
            _kbps = Math.Clamp(value, 0, MaxKbps);
            Value = Shown(_kbps);
        }
    }

    /// <summary>KB/s as MB/s to a tenth; a limit under 0.1 MB/s shows as 0.1, not as "no limit".</summary>
    private decimal Shown(int kbps) => kbps <= 0 ? 0 : Math.Clamp(Math.Round(kbps / 1024m, 1), 0.1m, Maximum);
}
