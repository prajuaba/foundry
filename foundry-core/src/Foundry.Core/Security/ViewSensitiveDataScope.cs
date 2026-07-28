namespace Foundry.Core.Security;

/// <summary>
/// The claim that entitles a caller to read sensitive values unmasked.
/// </summary>
/// <remarks>
/// <para>
/// A property declared <c>[SensitiveData(Protection = Mask)]</c> comes back masked to every caller
/// except one presenting this scope. That is the point of the declaration — the value is stored in
/// full and shown in full only to whoever is entitled to it — and until masking was applied to the
/// read path it described a protection nothing performed.
/// </para>
/// <para>
/// Named here rather than written as a literal at the point of use because it is part of the contract
/// a deployment has to satisfy: the token minted for a support agent or a claims handler has to carry
/// it, and a constant that appears in one file is one an integrator can find.
/// </para>
/// </remarks>
public static class ViewSensitiveDataScope
{
    /// <summary>The claim type carrying the scope.</summary>
    public const string ClaimType = "scope";

    /// <summary>The value entitling the caller to unmasked reads.</summary>
    public const string ClaimValue = "view:pii";
}
