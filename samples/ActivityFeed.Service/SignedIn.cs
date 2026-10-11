using System.Text;
using ActivityFeed.Contracts;
using Tapline.Client;

namespace ActivityFeed.Service;

/// <summary>A connection signed in to the practice's realm by passthrough (<see cref="PracticeSignIn" />), before its step 1.</summary>
internal static class SignedIn
{
    /// <summary>
    ///     Signs this connection in as <paramref name="user" />, unless it is already: true once it is (false: refused, or not
    ///     answered within 10 s — try again). Its ticket resumes it on a new session by itself.
    /// </summary>
    public static async Task<bool> SignedInAsync(this TaplineClient client, string user, CancellationToken token)
    {
        if (client.Identity is { Realm: PracticeSignIn.Realm } who && who.UserId == user) return true;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return (await client.SignInAsync(PracticeSignIn.Realm, PracticeSignIn.Module, Encoding.UTF8.GetBytes(user),
                cancellationToken: limit.Token)).Accepted;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }
}
