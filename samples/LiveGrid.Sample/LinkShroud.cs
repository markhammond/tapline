using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace LiveGrid.Sample;

/// <summary>
///     Shrouds the values a view's link carries (its filters' values, and the row it's held at), so the link shows none of
///     them, as the ChalkQL blotter's does: encrypted and signed with ASP.NET Data Protection, by keys the host keeps in a
///     folder, so a link still opens after the host restarts. The host alone can read a shrouded value, and one altered is
///     refused.
/// </summary>
public sealed class LinkShroud(IDataProtectionProvider provider)
{
    // Its purpose ties a key to view links: a value shrouded for anything else doesn't open here.
    private readonly IDataProtector _protector = provider.CreateProtector("LiveGrid.Sample.ViewLink.v1");

    /// <summary>Text, shrouded: URL-safe, and different every time, though it opens the same.</summary>
    public string Shroud(string text)
    {
        return _protector.Protect(text);
    }

    /// <summary>The text a value shrouds.</summary>
    /// <exception cref="ArgumentException">The value was altered, or shrouded by keys the host doesn't have.</exception>
    public string Unshroud(string shrouded)
    {
        try
        {
            return _protector.Unprotect(shrouded);
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("the link isn't a view of this page: its shrouded part was altered, or shrouded by another host's keys");
        }
    }
}
