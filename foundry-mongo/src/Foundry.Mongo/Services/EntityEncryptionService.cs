using System.Collections.Concurrent;
using System.Reflection;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using MongoDB.Bson;

namespace Foundry.Mongo.Services;

/// <summary>
/// Internal service responsible for field-level encryption, decryption, and masking of entity properties
/// decorated with <see cref="SensitiveDataAttribute"/>. Caches reflection metadata per entity type
/// using a static <see cref="ConcurrentDictionary{TKey,TValue}"/> to avoid repeated reflection scans.
/// </summary>
internal sealed class EntityEncryptionService<T> where T : class, IEntity<ObjectId>
{
    private readonly IEncryptionProvider? _encryptionProvider;

    /// <summary>
    /// Cached per-type list of properties that carry a <see cref="SensitiveDataAttribute"/>.
    /// Key = property type, Value = list of (PropertyInfo, SensitiveDataAttribute) tuples.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<(PropertyInfo Prop, SensitiveDataAttribute Attr)>> _sensitivePropsCache = new();

    /// <summary>
    /// Cached per-type list of all readable public instance properties.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _allPropsCache = new();

    public EntityEncryptionService(IEncryptionProvider? encryptionProvider)
    {
        // Do NOT fall back to a zero-key provider — that silently encrypts with an all-zeros key.
        // If no provider is registered, encryption operations will be skipped for non-sensitive
        // entities and will throw for entities that require encryption.
        _encryptionProvider = encryptionProvider;
    }

    /// <summary>
    /// Gets the encryption provider for use by external callers (e.g., cross-collection decryption).
    /// May be null if no provider was registered.
    /// </summary>
    internal IEncryptionProvider? EncryptionProvider => _encryptionProvider;

    /// <summary>
    /// Returns all public instance properties for type T, cached.
    /// </summary>
    internal static PropertyInfo[] GetCachedProperties()
    {
        return _allPropsCache.GetOrAdd(typeof(T), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// Returns properties with <see cref="SensitiveDataAttribute"/> for type T, cached.
    /// </summary>
    internal static IReadOnlyList<(PropertyInfo Prop, SensitiveDataAttribute Attr)> GetSensitiveProperties()
    {
        return _sensitivePropsCache.GetOrAdd(typeof(T), t =>
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var result = new List<(PropertyInfo, SensitiveDataAttribute)>();
            foreach (var prop in props)
            {
                if (!prop.CanRead) continue;
                var attr = prop.GetCustomAttribute<SensitiveDataAttribute>();
                if (attr != null)
                    result.Add((prop, attr));
            }
            return result;
        });
    }

    /// <summary>
    /// Creates a clone of the entity with all <see cref="ProtectionType.Encrypt"/> properties encrypted.
    /// If no encrypting properties exist, returns the original entity unchanged.
    /// </summary>
    internal T EncryptEntityForWrite(T entity)
    {
        var sensitiveProps = GetSensitiveProperties();
        var hasEncryption = sensitiveProps.Any(sp => sp.Attr.Protection == ProtectionType.Encrypt);

        if (!hasEncryption) return entity;

        if (_encryptionProvider == null)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' has properties marked with [SensitiveData(Protection = Encrypt)] " +
                $"but no IEncryptionProvider is registered. Register an IEncryptionProvider in the DI container " +
                $"or remove the Encrypt protection from the entity properties.");
        }

        var clone = CloneEntity(entity);

        foreach (var (prop, attr) in sensitiveProps)
        {
            if (attr.Protection == ProtectionType.Encrypt)
            {
                var val = prop.GetValue(clone);
                if (val != null && prop.PropertyType == typeof(string))
                {
                    var encrypted = _encryptionProvider.Encrypt(val.ToString() ?? string.Empty);
                    if (prop.CanWrite) prop.SetValue(clone, encrypted);
                    else SetProperty(clone, prop.Name, encrypted);
                }
            }
        }

        return clone;
    }

    /// <summary>
    /// Decrypts all <see cref="ProtectionType.Encrypt"/> properties on the entity in-place.
    /// </summary>
    internal void DecryptEntity(T? entity)
    {
        if (entity == null) return;
        if (_encryptionProvider == null) return;

        var sensitiveProps = GetSensitiveProperties();
        foreach (var (prop, attr) in sensitiveProps)
        {
            if (attr.Protection == ProtectionType.Encrypt)
            {
                var val = prop.GetValue(entity);
                if (val != null && prop.PropertyType == typeof(string))
                {
                    var decrypted = _encryptionProvider.Decrypt(val.ToString() ?? string.Empty);
                    if (prop.CanWrite) prop.SetValue(entity, decrypted);
                    else SetProperty(entity, prop.Name, decrypted);
                }
            }
        }
    }

    /// <summary>
    /// Returns a shallow clone of the entity with its <see cref="ProtectionType.Mask"/> properties
    /// masked according to their configured <see cref="MaskingType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProtectionType.Encrypt"/> properties are left alone, which is what the two values
    /// mean: <c>Mask</c> is documented as masking "for presentation/logs", and <c>Encrypt</c> as
    /// encrypting at rest "and decrypts it on read". Masking both made the two settings do the same
    /// thing on the way out, so an encrypted field — declared that way to protect the database, not
    /// to hide the value from its own API — came back as a row of asterisks.
    /// </para>
    /// <para>
    /// It went unnoticed because nothing called this method: the masking machinery was written,
    /// tested in isolation, and wired to no read path at all.
    /// </para>
    /// </remarks>
    internal T MaskSensitiveFields(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var clone = CloneEntity(entity);
        var sensitiveProps = GetSensitiveProperties();

        foreach (var (prop, attr) in sensitiveProps)
        {
            if (attr.Protection != ProtectionType.Mask) continue;

            var val = prop.GetValue(clone);
            if (val != null)
            {
                var masked = attr.MaskValue(val);
                if (prop.PropertyType == typeof(string))
                {
                    if (prop.CanWrite) prop.SetValue(clone, masked);
                    else SetProperty(clone, prop.Name, masked);
                }
            }
        }

        return clone;
    }

    /// <summary>
    /// Returns the masked value of a property for audit diff purposes.
    /// If the property has a <see cref="SensitiveDataAttribute"/>, the value is masked; otherwise returned as-is.
    /// </summary>
    internal static object? GetDiffValue(PropertyInfo prop, object? val)
    {
        var sensitiveProps = GetSensitiveProperties();
        var match = sensitiveProps.FirstOrDefault(sp => sp.Prop == prop);
        if (match.Attr != null)
        {
            return match.Attr.MaskValue(val);
        }
        return val;
    }

    /// <summary>
    /// Creates a shallow clone of an entity using copy constructor, default constructor, or uninitialized object fallback.
    /// </summary>
    internal static T CloneEntity(T entity)
    {
        var copyConstructor = typeof(T).GetConstructor(new[] { typeof(T) });
        T clone;
        if (copyConstructor != null)
        {
            clone = (T)copyConstructor.Invoke(new object[] { entity });
        }
        else
        {
            var defaultConstructor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (defaultConstructor != null)
            {
                clone = (T)defaultConstructor.Invoke(null);
            }
            else
            {
                clone = (T)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(T));
            }
        }

        var properties = GetCachedProperties();
        foreach (var prop in properties)
        {
            if (!prop.CanRead) continue;

            var val = prop.GetValue(entity);
            if (prop.CanWrite)
            {
                prop.SetValue(clone, val);
            }
            else
            {
                SetProperty(clone, prop.Name, val);
            }
        }

        return clone;
    }

    /// <summary>
    /// Sets a property value using direct setter, backing field, or field name conventions.
    /// Handles init-only properties on records by accessing compiler-generated backing fields.
    /// </summary>
    internal static void SetProperty(object obj, string propertyName, object? value)
    {
        var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(obj, value);
        }
        else
        {
            var backingField = obj.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (backingField != null)
            {
                backingField.SetValue(obj, value);
            }
            else
            {
                var fields = obj.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                var field = fields.FirstOrDefault(f => f.Name.Contains($"<{propertyName}>") || f.Name.Equals($"_{propertyName}", StringComparison.OrdinalIgnoreCase));
                field?.SetValue(obj, value);
            }
        }
    }
}
