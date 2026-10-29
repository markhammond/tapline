using System.Linq.Expressions;
using System.Reflection;
using Chalk.Client;
using Chalk.Sources.Poco;
using Tapline.Grid.Provider;

namespace Tapline.Grid.ChalkQL;

/// <summary>
///     The state tail as ChalkQL tables, for the views' filters to read (<see cref="ChalkScopes{TKey,TRow}" />): the
///     stream's newest versions (<see cref="Newest" />) and the store's rows as last seen (<see cref="Stored" />), keyed,
///     their columns named as the store's. Its <see cref="Source" /> goes beside the store's as the engine is made; one
///     to a <see cref="StoreAndStream{TKey,TRow,TStamp}" />.
/// </summary>
/// <remarks>
///     The tables are of the row itself, when ChalkQL maps each of its fields as the store has the column; else of a record
///     the store's columns map to (<see cref="Of{TRecord}" />), each row made one as the tail takes it — text inline as a
///     string, say, which ChalkQL holds as the database's text.
/// </remarks>
public sealed class ChalkTail<TKey, TRow> where TKey : unmanaged, IEquatable<TKey> where TRow : unmanaged
{
    private readonly Tables _tables;

    /// <param name="rows">The row's columns.</param>
    /// <param name="sourceId">The source's id, and its schema: the tables are <c>{sourceId}.newest</c> and <c>{sourceId}.stored</c>.</param>
    public ChalkTail(ChalkRows<TKey, TRow> rows, string sourceId = "tail")
        : this(sourceId, Tables<TRow>.Make(sourceId, rows.Naming, rows.KeyExpression(), null))
    {
    }

    private ChalkTail(string sourceId, Tables tables)
    {
        _tables = tables;
        Source = tables.Source;
        (Newest, Stored) = ($"{sourceId}.newest", $"{sourceId}.stored");
    }

    /// <summary>The tables' source: one of the engine's.</summary>
    public PocoSource Source { get; }

    /// <summary>The stream's newest version of each key the tail holds.</summary>
    public string Newest { get; }

    /// <summary>The store's row of each key the tail holds, as last seen (none for a key not seen in the store).</summary>
    public string Stored { get; }

    /// <summary>
    ///     A tail whose tables are of <typeparamref name="TRecord" />: the store's record (a class or record, its members
    ///     named as the row's fields), each row made one by the record's widest constructor, or its settable members — a
    ///     field taken as it is, or, text inline (<see cref="GridText" />), as its string.
    /// </summary>
    public static ChalkTail<TKey, TRow> Of<TRecord>(ChalkRows<TKey, TRow> rows, string sourceId = "tail") where TRecord : class
    {
        var member = Members(typeof(TRecord)).FirstOrDefault(m => Named(m.Name, rows.KeyField)) ??
                     throw new ArgumentException($"{typeof(TRecord).Name} has no member {rows.KeyField} for the key, {rows.KeyColumn}.", nameof(TRecord));
        var record = Expression.Parameter(typeof(TRecord), "record");
        var keyOf = Expression.Lambda<Func<TRecord, object?>>(Expression.Convert(Expression.MakeMemberAccess(record, member), typeof(object)), record);
        return new ChalkTail<TKey, TRow>(sourceId, Tables<TRecord>.Make(sourceId, rows.Naming, keyOf, Converter<TRecord>()));
    }

    /// <summary>A change's rows into a refresh: those let go deleted, the newest versions and the store's rows upserted.</summary>
    internal void Plan(RefreshBuilder refresh, TRow[] goneNewest, TRow[] goneStored, TRow[] newest, TRow[] stored)
    {
        _tables.Plan(refresh, goneNewest, goneStored, newest, stored);
    }

    /// <summary>The tables made these rows, whole.</summary>
    internal void Replace(RefreshBuilder refresh, TRow[] newest, TRow[] stored)
    {
        _tables.Replace(refresh, newest, stored);
    }

    /// <summary>A row made a record: by the record's widest constructor, else member by member, each by its name.</summary>
    private static Func<TRow, TRecord> Converter<TRecord>()
    {
        var row = Expression.Parameter(typeof(TRow), "row");
        var fields = typeof(TRow).GetFields(BindingFlags.Public | BindingFlags.Instance);
        var constructor = typeof(TRecord).GetConstructors().MaxBy(c => c.GetParameters().Length) ??
                          throw new ArgumentException($"{typeof(TRecord).Name} has no public constructor.", nameof(TRecord));
        Expression made = constructor.GetParameters().Length > 0
            ? Expression.New(constructor, constructor.GetParameters().Select(p => Value(p.Name!, p.ParameterType)))
            : Expression.MemberInit(Expression.New(constructor), Members(typeof(TRecord))
                .Where(m => m is PropertyInfo { CanWrite: true } or FieldInfo { IsInitOnly: false })
                .Select(m => Expression.Bind(m, Value(m.Name, m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType))));
        return Expression.Lambda<Func<TRow, TRecord>>(made, row).Compile();

        Expression Value(string name, Type type)
        {
            var field = fields.FirstOrDefault(f => Named(f.Name, name)) ??
                        throw new ArgumentException($"{typeof(TRow).Name} has no field for {typeof(TRecord).Name}'s {name}.", nameof(TRecord));
            Expression value = Expression.Field(row, field);
            if (type == field.FieldType) return value;
            if (type == typeof(string) && InlineText.Is(field.FieldType)) return InlineText.StringOf(value);
            throw new ArgumentException($"{typeof(TRecord).Name}'s {name} is a {type.Name}; {typeof(TRow).Name}'s is a {field.FieldType.Name}.",
                nameof(TRecord));
        }
    }

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        return type.GetMembers(BindingFlags.Public | BindingFlags.Instance).Where(m => m is PropertyInfo or FieldInfo);
    }

    private static bool Named(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private abstract class Tables
    {
        public required PocoSource Source { get; init; }

        public abstract void Plan(RefreshBuilder refresh, TRow[] goneNewest, TRow[] goneStored, TRow[] newest, TRow[] stored);

        public abstract void Replace(RefreshBuilder refresh, TRow[] newest, TRow[] stored);
    }

    /// <summary>The two tables, of <typeparamref name="T" />: the row itself (no conversion), or a record each row is made.</summary>
    private sealed class Tables<T> : Tables
    {
        private Func<TRow, T>? _convert;
        private PocoTable<T> _newest, _stored;

        public static Tables<T> Make(string sourceId, PocoNamingPolicy naming, Expression<Func<T, object?>> key, Func<TRow, T>? convert)
        {
            var source = new PocoSourceBuilder(sourceId, sourceId)
                .NamingPolicy(naming)
                .AddTable("newest", (IReadOnlyList<T>)[], out var newest, t => t.Keyed(key))
                .AddTable("stored", (IReadOnlyList<T>)[], out var stored, t => t.Keyed(key))
                .Build();
            return new Tables<T> { Source = source, _newest = newest, _stored = stored, _convert = convert };
        }

        public override void Plan(RefreshBuilder refresh, TRow[] goneNewest, TRow[] goneStored, TRow[] newest, TRow[] stored)
        {
            if (goneNewest.Length > 0) refresh.Delete(_newest, Made(goneNewest));
            if (goneStored.Length > 0) refresh.Delete(_stored, Made(goneStored));
            if (newest.Length > 0) refresh.Upsert(_newest, Made(newest));
            if (stored.Length > 0) refresh.Upsert(_stored, Made(stored));
        }

        public override void Replace(RefreshBuilder refresh, TRow[] newest, TRow[] stored)
        {
            refresh.Replace(_newest, Made(newest)).Replace(_stored, Made(stored));
        }

        private T[] Made(TRow[] rows)
        {
            return _convert is null ? (T[])(object)rows : System.Array.ConvertAll(rows, r => _convert(r));
        }
    }
}
