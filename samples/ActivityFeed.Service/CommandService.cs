using ActivityFeed.Contracts;
using MagicOnion;
using MagicOnion.Server;

namespace ActivityFeed.Service;

/// <summary>The practice's commands over MagicOnion (<see cref="IPracticeCommands" />), as the app forwards them.</summary>
public sealed class CommandService(PracticeProvider practice) : ServiceBase<IPracticeCommands>, IPracticeCommands
{
    public UnaryResult<Persona[]> PersonasAsync()
    {
        return UnaryResult.FromResult(PracticeProvider.Personas);
    }

    public async UnaryResult<CommandAnswer> ArriveAsync(int person, long booking, byte[] correlation, CommandOrder? order)
    {
        return await practice.ArriveAsync(person, booking, correlation, order, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> CallAsync(int person, long booking, string room, byte[] correlation, CommandOrder? order)
    {
        return await practice.CallAsync(person, booking, room, correlation, order, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> AcknowledgeAsync(int person, long activity, byte[] correlation)
    {
        return await practice.AcknowledgeAsync(person, activity, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> SeenAsync(int person, long[] activities, byte[] correlation)
    {
        return await practice.SeenAsync(person, activities, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> MessageAsync(int person, string text, byte[] correlation, CommandOrder? order)
    {
        return await practice.MessageAsync(person, text, correlation, order, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> ReplyAsync(int person, long message, string text, byte[] correlation, CommandOrder? order)
    {
        return await practice.ReplyAsync(person, message, text, correlation, order, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> ShareAsync(int person, int gp, string tag, byte[] correlation)
    {
        return await practice.ShareAsync(person, gp, tag, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> WithdrawAsync(int person, long consent, byte[] correlation)
    {
        return await practice.WithdrawAsync(person, consent, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> BreakGlassAsync(int person, int patient, byte[] correlation)
    {
        return await practice.BreakGlassAsync(person, patient, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> ReleaseAsync(int person, long section, byte[] correlation)
    {
        return await practice.ReleaseAsync(person, section, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> RevokeCarerAsync(int person, int carer, byte[] correlation)
    {
        return await practice.RevokeCarerAsync(person, carer, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<Explanation> ExplainAsync(int person)
    {
        return await practice.ExplainAsync(person, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<NoteAnswer> NoteAsync(int person, long section)
    {
        return await practice.NoteAsync(person, section, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<AuditAnswer> AuditAsync(int person, byte part, long seq)
    {
        return await practice.AuditAsync(person, part, seq, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> NoticeAsync(int person, int site, string notice, byte[] correlation)
    {
        return await practice.NoticeAsync(person, site, notice, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> HandedOffAsync(string dispatcher, int shard, long seq, byte[] correlation)
    {
        return await practice.HandedOffAsync(dispatcher, shard, seq, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> FailedAsync(string dispatcher, int shard, long seq, string reason, byte[] correlation)
    {
        return await practice.FailedAsync(dispatcher, shard, seq, reason, correlation, Context.CallContext.CancellationToken);
    }

    public async UnaryResult<CommandAnswer> RecordedAsync(string dispatcher, int shard, long seq, byte[] correlation)
    {
        return await practice.RecordedAsync(dispatcher, shard, seq, correlation, Context.CallContext.CancellationToken);
    }
}
