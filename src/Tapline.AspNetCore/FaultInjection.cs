using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Tapline.AspNetCore;

/// <summary>
///     Where, how often and how long test connections see injected outages (see <see cref="FaultInjectionService" />
///     ).
/// </summary>
public sealed class FaultInjectionOptions
{
    /// <summary>
    ///     Connections opened at or under this base-relative path (e.g. <c>/test</c>, <c>/test/book</c>) tap the fault views:
    ///     for Tapline.BlazorRealtime, the page a circuit opens on.
    /// </summary>
    public string Path { get; set; } = "/test";

    /// <summary>An outage starts this often...</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>...and lasts this long.</summary>
    public TimeSpan Outage { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The fault code test clients see.</summary>
    public FaultCode Code { get; set; } = FaultCode.SourceFailed;
}

/// <summary>
///     Injected outages for test connections — those opened at or under <see cref="FaultInjectionOptions.Path" /> (for
///     Tapline.BlazorRealtime, circuits opened on a page there) — on one lifecycle shared by all of them and independent of the
///     sources' own: healthy for <see cref="FaultInjectionOptions.Every" /> − <see cref="FaultInjectionOptions.Outage" />,
///     then
///     every source looks faulted to every test connection (their taps get their error policy and end) for
///     <see cref="FaultInjectionOptions.Outage" />. Test and production connections tap the same sources through the same
///     data
///     pump; production connections never join the <see cref="Injector" />. With
///     <see cref="TaplineOptions.FaultInjection" />
///     null (the default) it does nothing.
/// </summary>
public sealed class FaultInjectionService : BackgroundService
{
    private readonly FaultInjectionOptions? _options;
    private readonly string _path;

    public FaultInjectionService(IOptions<TaplineOptions> options)
    {
        _options = options.Value.FaultInjection;
        _path = _options?.Path.Trim('/') ?? "";
        if (_options is not null && (_path.Length == 0 || _options.Outage <= TimeSpan.Zero ||
                                     _options.Every <= _options.Outage || _options.Code == FaultCode.None))
            throw new ArgumentOutOfRangeException(nameof(options),
                "Fault simulation needs a path of its own (not the root), 0 < Outage < Every and a fault code.");
    }

    /// <summary>The shared lifecycle test connections join.</summary>
    public FaultInjector Injector { get; } = new();

    /// <summary>
    ///     When a transport opens a connection: a <paramref name="path" /> under the test path makes its outlet a test
    ///     connection, for good. The path is base-relative, a leading <c>/</c> or none; its query and fragment don't count.
    /// </summary>
    public void Enlist(Outlet outlet, string path)
    {
        if (_options is not null && IsTestPage(path)) outlet.JoinFaultInjector(Injector);
    }

    private bool IsTestPage(string relative)
    {
        var path = relative.AsSpan().TrimStart('/');
        var end = path.IndexOfAny('?', '#');
        if (end >= 0) path = path[..end];
        path = path.TrimEnd('/');
        return path.Equals(_path, StringComparison.OrdinalIgnoreCase)
               || (path.Length > _path.Length && path.StartsWith(_path, StringComparison.OrdinalIgnoreCase) &&
                   path[_path.Length] == '/');
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options is null) return;
        try
        {
            while (true)
            {
                await Task.Delay(_options.Every - _options.Outage, stoppingToken);
                Injector.Fail(_options.Code, "Injected outage.");
                await Task.Delay(_options.Outage, stoppingToken);
                Injector.Recover();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}