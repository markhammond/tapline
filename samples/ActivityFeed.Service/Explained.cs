using ActivityFeed.Contracts;
using Chalk.Entitlements;

namespace ActivityFeed.Service;

/// <summary>
///     ChalkQL's explanation of a statement (<c>ExplainAsync</c>: what the policy folds to, nothing run), as a page shows
///     it: each table's row predicate and each column's disclosure, mask and placeholder, already SQL over the columns'
///     names (<c>site_id = 1</c>; a <c>Through</c> child's parent as <c>EXISTS (…)</c>).
/// </summary>
internal static class Explained
{
    public static ExplainedStatement Of(string name, ulong plan, EntitlementsExplanation explanation)
    {
        return new ExplainedStatement
        {
            Name = name, Plan = plan,
            Tables =
            [
                .. explanation.Tables.Select(t => new ExplainedSource
                {
                    Table = t.Table, Rows = t.RowPredicate,
                    Columns =
                    [
                        .. t.Columns.Select(c => new ExplainedField
                        {
                            Column = c.Column, Disclosure = c.Disclosure, Placeholder = c.Placeholder, Mask = c.Mask,
                            Floor = c.MinGroupSize
                        })
                    ]
                })
            ]
        };
    }
}
