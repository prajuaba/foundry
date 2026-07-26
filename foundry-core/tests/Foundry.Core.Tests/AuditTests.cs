using Foundry.Core.Audit;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Audit entry construction and diff rendering.
/// </summary>
/// <remarks>
/// The audit trail is a compliance artefact for the regulated buyers this framework targets, so the
/// rendered text has to be both accurate and complete. A diff line that misdescribes a change, or
/// that omits the value that changed, is worse than no line at all — it looks like evidence.
/// </remarks>
public class AuditTests
{
    private enum Grade
    {
        Standard,
        Premium
    }

    // ---- factories ----

    [Fact]
    public void ForInsert_RecordsTheAction()
    {
        var entry = AuditLogEntry.ForInsert("ada", "Customer", "abc", "Customers");

        Assert.Equal(AuditAction.Inserted, entry.Action);
        Assert.Equal("ada", entry.OperatorId);
        Assert.Equal("Customer", entry.EntityType);
        Assert.Equal("abc", entry.EntityId);
        Assert.Equal("Customers", entry.CollectionName);
    }

    [Fact]
    public void ForSoftDelete_RecordsTheFlagFlip()
    {
        // A soft delete is invisible in the document's other fields, so the IsDeleted transition is
        // the only evidence that a delete happened at all.
        var entry = AuditLogEntry.ForSoftDelete("ada", "Customer", "abc", "Customers");

        Assert.Equal(AuditAction.DeletedSoft, entry.Action);
        var diff = Assert.Single(entry.PropertyDiffs);
        Assert.Equal("IsDeleted", diff.PropertyName);
        Assert.Equal(false, diff.OldValue);
        Assert.Equal(true, diff.NewValue);
    }

    [Fact]
    public void ForRestore_RecordsTheReverseFlip()
    {
        var entry = AuditLogEntry.ForRestore("ada", "Customer", "abc", "Customers");

        Assert.Equal(AuditAction.Restored, entry.Action);
        var diff = Assert.Single(entry.PropertyDiffs);
        Assert.Equal(true, diff.OldValue);
        Assert.Equal(false, diff.NewValue);
    }

    [Fact]
    public void ForUpdate_WithNullDiffs_YieldsAnEmptyListNotNull()
    {
        var entry = AuditLogEntry.ForUpdate("ada", "Customer", "abc", "Customers", null!);
        Assert.Empty(entry.PropertyDiffs);
    }

    [Fact]
    public void ForHardDeleteAndRead_CarryNoDiffs()
    {
        Assert.Empty(AuditLogEntry.ForHardDelete("ada", "Customer", "abc", "Customers").PropertyDiffs);
        Assert.Empty(AuditLogEntry.ForRead("ada", "Customer", "abc", "Customers").PropertyDiffs);
    }

    // ---- change detection ----

    [Fact]
    public void HasChanged_ComparesByValue()
    {
        Assert.False(new PropertyDiff("Name", "Ada", "Ada").HasChanged);
        Assert.True(new PropertyDiff("Name", "Ada", "Grace").HasChanged);
        Assert.False(new PropertyDiff("Name", null, null).HasChanged);
        Assert.True(new PropertyDiff("Name", null, "Ada").HasChanged);
    }

    // ---- rendering ----

    [Fact]
    public void UpdateDiff_ShowsBothValues()
    {
        var rendered = new PropertyDiff("Name", "Ada", "Grace").ToString();

        Assert.Contains("Ada", rendered);
        Assert.Contains("Grace", rendered);
    }

    [Fact]
    public void InsertDiff_DoesNotClaimThereWasNoChange()
    {
        // The insert branch rendered "(no change → ...)", which states the opposite of what it
        // records. An auditor reading that line would conclude nothing happened.
        var rendered = PropertyDiff.Inserted("Email", "ada@example.com").ToString();

        Assert.DoesNotContain("no change", rendered);
    }

    [Fact]
    public void InsertDiff_ShowsTheValueThatWasSet()
    {
        // String fell through to the catch-all arm and rendered as "<String>", so the audit line
        // for a newly-set field named the type instead of the value -- on exactly the branch used
        // for inserts and removals. The both-values branch printed it correctly, so the trail was
        // inconsistent as well as incomplete.
        var rendered = PropertyDiff.Inserted("Email", "ada@example.com").ToString();

        Assert.Contains("ada@example.com", rendered);
    }

    [Fact]
    public void RemovalDiff_ShowsTheValueThatWasRemoved()
    {
        var rendered = PropertyDiff.Removed("Email", "ada@example.com").ToString();

        Assert.Contains("ada@example.com", rendered);
        Assert.Contains("removed", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(42.5)]
    [InlineData(true)]
    public void InsertDiff_RendersScalarValues(object value)
    {
        var rendered = PropertyDiff.Inserted("Field", value).ToString();
        Assert.DoesNotContain("<", rendered);
    }

    [Fact]
    public void InsertDiff_RendersEnumsByName()
    {
        Assert.Contains("Premium", PropertyDiff.Inserted("Grade", Grade.Premium).ToString());
    }

    [Fact]
    public void NullToNullDiff_IsRenderedExplicitly()
    {
        var rendered = new PropertyDiff("Name", null, null).ToString();
        Assert.Contains("null", rendered);
    }

    [Fact]
    public void DecimalValues_RenderInvariantly()
    {
        // Audit text is a record, not a UI string: it must not change with the server's locale.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            Assert.Contains("42.5", PropertyDiff.Inserted("Amount", 42.5m).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
