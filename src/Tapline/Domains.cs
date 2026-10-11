namespace Tapline;

/// <summary>
///     Sources written together — what one unit of work may span: a group ingest's members, or sources a
///     <see cref="Transaction" /> has written together — so a watch reads them together, and reads anything else apart. A
///     source written alone has none (<see cref="Source.Domain" /> null). Replaced, never edited: every change makes new
///     domains, so a reader that saw one can tell, by identity, whether it changed since.
/// </summary>
internal sealed class LockDomain
{
    /// <summary>
    ///     Taken for every change of domains and of a source's ingest — never by a write within a domain, never by a source
    ///     written alone.
    /// </summary>
    internal static readonly Lock Changes = new();

    /// <summary>In lock order (<see cref="Source.SourceId" />).</summary>
    public readonly Source[] Members;

    private LockDomain(Source[] members)
    {
        Members = members;
    }

    /// <summary>Whether every one of <paramref name="sources" /> is in one domain already.</summary>
    public static bool Shared(ReadOnlySpan<ITransactional> sources)
    {
        var d = Volatile.Read(ref sources[0].Source.Domain);
        if (d is null) return false;
        foreach (var s in sources)
            if (!ReferenceEquals(Volatile.Read(ref s.Source.Domain), d))
                return false;
        return true;
    }

    /// <summary>
    ///     The sources, and every source sharing a domain with any of them, become one domain (a transaction across
    ///     them).
    /// </summary>
    public static void Merge(ReadOnlySpan<ITransactional> sources)
    {
        lock (Changes)
        {
            var all = new HashSet<Source>();
            foreach (var t in sources)
                if (t.Source.Domain is { } d) all.UnionWith(d.Members);
                else all.Add(t.Source);
            Assign(all);
        }
    }

    /// <summary>
    ///     Exactly <paramref name="members" /> become one domain (none, if one): each leaves the domain it was in, whose other
    ///     members stay together; <paramref name="removed" />, if any, is left alone. Under <see cref="Changes" />.
    /// </summary>
    public static void Regroup(IReadOnlyCollection<Source> members, Source? removed)
    {
        var rest = new HashSet<LockDomain>();
        foreach (var s in members)
            if (s.Domain is { } d)
                rest.Add(d);
        if (removed?.Domain is { } r) rest.Add(r);
        foreach (var d in rest)
        {
            var left = new HashSet<Source>(d.Members);
            left.ExceptWith(members);
            if (removed is not null) left.Remove(removed);
            Assign(left);
        }

        Assign(members);
        if (removed is not null)
        {
            Volatile.Write(ref removed.Domain, null);
            Interlocked.Increment(ref removed.DomainEpoch);
        }
    }

    private static void Assign(IReadOnlyCollection<Source> members)
    {
        var domain = members.Count < 2 ? null : new LockDomain(members.OrderBy(s => s.SourceId).ToArray());
        foreach (var s in members)
        {
            Volatile.Write(ref s.Domain, domain);
            Interlocked.Increment(ref s.DomainEpoch); // before any unit across the new domain: a reader sees it changed
        }
    }
}