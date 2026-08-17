#pragma warning disable IL2070, IL2075
using System;
using System.Linq;
using System.Reflection;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;

namespace Foundry.Core.Outbox;

/// <summary>
/// Removes protected field values from an object before it leaves the application.
/// </summary>
/// <remarks>
/// <para>
/// The repository decrypts and masks on read, so anything served through it is protected. The outbox
/// does not read: it captures the entity from the caller's command, before the repository has
/// encrypted anything, and publishes that. A field declared <c>Encrypt</c> therefore reached Kafka
/// as the plaintext the caller sent, on a topic no schema had asked for.
/// </para>
/// <para>
/// Redaction rather than encryption. A consumer cannot be given the key, so ciphertext on the wire
/// would be unreadable to every legitimate subscriber and still a copy of the protected data. The
/// value is removed instead, and a consumer that needs it can read it back through the API, where
/// the caller's own permissions decide what they see.
/// </para>
/// </remarks>
public static class SensitiveFieldRedactor
{
    /// <summary>
    /// Returns a copy of <paramref name="source"/> with every <see cref="SensitiveDataAttribute"/>
    /// property emptied. Returns the original when the type declares none.
    /// </summary>
    public static T Redact<T>(T source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);

        var type = source.GetType();
        var sensitive = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SensitiveDataAttribute>() is not null)
            .ToArray();

        if (sensitive.Length == 0) return source;

        var clone = Clone(source, type);
        if (clone is null) return source;

        foreach (var prop in sensitive)
        {
            if (prop.PropertyType != typeof(string)) continue;

            // Emptied, not masked. A mask pattern still describes the value's shape -- length, the
            // last four digits -- and there is no caller here whose permissions could justify it.
            if (prop.CanWrite) prop.SetValue(clone, string.Empty);
            else SetBackingField(clone, prop);
        }

        return (T)clone;
    }

    private static object? Clone(object source, Type type)
    {
        // Records get a copy constructor, which is the accurate way to copy one. The fallbacks
        // matter because an entity is not required to be a record.
        var copyCtor = type.GetConstructor(new[] { type });
        if (copyCtor is not null) return copyCtor.Invoke(new[] { source });

        var parameterless = type.GetConstructor(Type.EmptyTypes);
        if (parameterless is null) return null;

        var clone = parameterless.Invoke(null);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            prop.SetValue(clone, prop.GetValue(source));
        }

        return clone;
    }

    private static void SetBackingField(object target, PropertyInfo prop)
    {
        // init-only properties have no setter after construction; the compiler-generated backing
        // field is the only way to reach them, and it is named predictably.
        var field = target.GetType().GetField(
            $"<{prop.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);

        field?.SetValue(target, string.Empty);
    }
}
