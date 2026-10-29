using Tapline.Alerts;

namespace LiveGrid.Sample.Components;

/// <summary>
///     A page's alerts: its browser's rules (kept there, and set on the desk, which evaluates them), why any aren't running,
///     whether this page may sound them and notify, and how many rows have fired alerts on each tab since it opened — for
///     the bell, its dot, and the bell on "new above".
/// </summary>
public sealed class PageAlerts
{
    private readonly Dictionary<string, long> _fired = new(StringComparer.Ordinal);

    public IReadOnlyList<AlertRule> Rules { get; set; } = [];

    /// <summary>The rules the desk refused, and why, by rule.</summary>
    public IReadOnlyDictionary<Guid, string> Refused { get; set; } = new Dictionary<Guid, string>();

    /// <summary>Whether the page may make sound: someone has said so, by a click.</summary>
    public bool SoundOn { get; set; }

    /// <summary>Whether the splash that asks for sound has been answered, either way.</summary>
    public bool Answered { get; set; }

    /// <summary>Whether to notify through the system while the page is in the background.</summary>
    public bool Notify { get; set; }

    /// <summary>Whether an alert has fired since the alerts were last looked at: the red dot on the bell.</summary>
    public bool Unseen { get; private set; }

    /// <summary>Whether the alerts panel is open, where an alert is seen as it fires.</summary>
    public bool Looking { get; set; }

    /// <summary>Keeps the rules: on the desk, and in the browser's local settings. Null till the page has its alerts.</summary>
    public Func<IReadOnlyList<AlertRule>, Task>? SaveAsync { get; set; }

    /// <summary>Rows fired on a tab (by its name): told on the page's own thread.</summary>
    public event Action<string>? Fired;

    /// <summary>How many rows have fired alerts on the tab named <paramref name="tab" /> since the page opened.</summary>
    public long FiredOn(string tab)
    {
        return _fired.GetValueOrDefault(tab);
    }

    public void Count(string tab, long rows)
    {
        _fired[tab] = FiredOn(tab) + rows;
        Unseen |= !Looking;
        Fired?.Invoke(tab);
    }

    /// <summary>The alerts were looked at: the bell's dot goes.</summary>
    public void Seen()
    {
        Unseen = false;
    }
}
