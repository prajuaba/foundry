namespace Foundry.E2E.Showcase;

/// <summary>
/// A marker for records the showcase archives to cold storage.
/// </summary>
/// <remarks>
/// <para>
/// Declared by hand and named from the schema: <c>LedgerEntry</c> sets <c>"baseClass":
/// "IShowcaseArchivable"</c>, and the compiler adds whatever that names to the entity's base list.
/// It is the one place the schema points *out* of itself, which is why the showcase exercises it —
/// a name that resolves to nothing is a build error rather than a silent omission, and that is worth
/// demonstrating on a type that exists.
/// </para>
/// <para>
/// Deliberately empty. Its purpose is to let application code say "these are the archivable ones"
/// without the schema having to know what archiving means.
/// </para>
/// </remarks>
public interface IShowcaseArchivable
{
}
