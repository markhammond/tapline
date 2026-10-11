using System.Net;
using System.Text;
using ActivityFeed.Contracts;

namespace ActivityFeed.Service;

/// <summary>
///     The provider's view, as HTML for the router's viewer to frame (sandboxed): a page of its own, light or dark as the
///     viewer is, its values as the policy disclosed them (a withheld one says so).
/// </summary>
internal static class ViewPages
{
    public static string Esc(string text)
    {
        return WebUtility.HtmlEncode(text);
    }

    public static string Page(string title, string body)
    {
        return $$"""
                 <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                 <title>{{Esc(title)}}</title><style>
                 :root { --ink: #222; --muted: #6f6f6f; --rule: #e6e6e6; --paper: #fff; --held: #6b5a8a; --held-bg: rgba(110, 90, 150, .1) }
                 @media (prefers-color-scheme: dark) { :root { --ink: #ddd; --muted: #8a8a8a; --rule: #2c2c2c; --paper: #161616; --held: #b9a8dc; --held-bg: rgba(170, 150, 220, .12) } }
                 body { margin: 16px; font: 13px/1.45 system-ui, sans-serif; color: var(--ink); background: var(--paper) }
                 h1 { font-size: 15px; margin: 0 0 6px } h2 { font-size: 13px; margin: 14px 0 4px }
                 table { border-collapse: collapse; width: 100% } td, th { text-align: left; padding: 3px 6px; border-bottom: 1px solid var(--rule); vertical-align: top }
                 th { color: var(--muted); font-weight: 500 } .withheld { color: var(--held); background: var(--held-bg); padding: 0 4px; border-radius: 3px }
                 .note, .empty { color: var(--muted) } code { font-size: 12px }
                 </style></head><body><h1>{{Esc(title)}}</h1>{{body}}</body></html>
                 """;
    }

    public static string Activities(List<FeedKey> keys, List<FeedRow> rows)
    {
        if (rows.Count == 0) return "<p class=\"empty\">None they may see.</p>";
        var html = new StringBuilder("<table><tr><th>activity</th><th>at</th><th>what</th><th>who</th><th>basis</th><th>note</th><th>acknowledged</th></tr>");
        foreach (var (key, row) in keys.Zip(rows).OrderBy(r => r.Second.AtMicros))
            html.Append($"<tr><td>{key.Seq}</td><td>{row.At:HH:mm:ss}</td><td>{row.Kind.ToString().ToLowerInvariant()}</td>")
                .Append($"<td>{Shown(row.PatientDisclosed == Disclosed.Full ? $"{row.PatientName} ({row.Born})" : "", row.PatientDisclosed)}</td>")
                .Append($"<td>{row.Basis.Label()}</td>")
                .Append($"<td>{Shown(row.Detail.ToString(), row.DetailDisclosed)}</td>")
                .Append($"<td>{(row.AckedMicros > 0 ? $"{FeedRow.Time(row.AckedMicros):HH:mm} by {Shown(row.AckedBy.ToString(), row.StaffDisclosed)}" : "")}")
                .Append($"{(row.ReadMicros > 0 ? $" read {FeedRow.Time(row.ReadMicros):HH:mm} by {Shown(row.ReadBy.ToString(), row.StaffDisclosed)}" : "")}</td></tr>");
        return html.Append("</table>").ToString();
    }

    public static string Sections(List<FeedKey> keys, List<FeedRow> rows)
    {
        if (rows.Count == 0) return "<p class=\"empty\">None they may read: the policy shows them no section of this visit.</p>";
        var html = new StringBuilder("<table><tr><th>section</th><th>of</th><th>problem</th><th>reason</th><th>detail</th></tr>");
        foreach (var (key, row) in keys.Zip(rows))
            html.Append($"<tr><td>{key.Seq}</td><td>{row.Kind switch { ActivityKind.Result => "a result", _ => "the visit" }}</td><td>{row.Problem}</td>")
                .Append($"<td>{Shown(row.Reason.ToString(), row.ReasonDisclosed)}</td><td>{Shown(row.Detail.ToString(), row.DetailDisclosed)}" +
                        $"{(row.DetailCut != 0 ? "…" : "")}</td></tr>");
        return html.Append("</table>").ToString();
    }

    public static string ShownTo(ShownTo[] shown)
    {
        if (shown.Length == 0) return "<p class=\"empty\">Nobody yet (the log is written with the next refresh).</p>";
        var html = new StringBuilder("<table><tr><th>who</th><th>topic</th><th>basis</th><th>first</th><th>last</th><th>posts</th></tr>");
        foreach (var s in shown)
            html.Append($"<tr><td>{Esc(People.Find(s.Person)?.Name ?? s.User)}</td><td><code>{Esc(s.Topic)}</code></td>")
                .Append($"<td>{s.Basis.Label()}</td><td>{s.FirstShown:HH:mm:ss}</td><td>{s.LastShown:HH:mm:ss}</td><td>{s.Posts}</td></tr>");
        return html.Append("</table>").ToString();
    }

    private static string Shown(string value, Disclosed disclosed)
    {
        return disclosed == Disclosed.Full
            ? Esc(value)
            : $"<span class=\"withheld\" title=\"{disclosed.ToString().ToUpperInvariant()}\">{Esc(value.Length > 0 ? value : "withheld")}</span>";
    }
}
