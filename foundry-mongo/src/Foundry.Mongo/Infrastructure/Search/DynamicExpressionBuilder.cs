using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Foundry.Core.Search;
using MongoDB.Bson;

namespace Foundry.Mongo.Infrastructure.Search;

/// <summary>
/// Compiles an array of SearchCriterion rules into a compiled Expression&lt;Func&lt;T, bool>> using expression tree building at runtime.
/// </summary>
public static class DynamicExpressionBuilder
{
    private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, PropertyInfo>> _propertyCache = new();

    /// <summary>
    /// Compiles SearchCriterion[] into a single Expression&lt;Func&lt;T, bool>> combined with AND logic.
    /// </summary>
    public static Expression<Func<T, bool>> BuildExpression<T>(SearchCriterion[] criteria) where T : class
    {
        var type = typeof(T);
        var properties = GetProperties(type);

        if (criteria == null || criteria.Length == 0)
            return x => true;

        var parameter = Expression.Parameter(type, "x");
        Expression? combined = null;

        foreach (var criterion in criteria)
        {
            if (!properties.TryGetValue(criterion.Field, out var propertyInfo) || propertyInfo == null || !propertyInfo.CanRead)
                throw new InvalidOperationException($"Property '{criterion.Field}' not found or not readable on type '{type.Name}'");

            var memberAccess = Expression.Property(parameter, propertyInfo);
            Expression condition;

            if (criterion.Operator == SearchOperator.In)
            {
                condition = BuildInExpression(memberAccess, criterion.Value);
            }
            else
            {
                var constant = BuildConstantFromValue(propertyInfo.PropertyType, criterion.Value);
                condition = criterion.Operator switch
                {
                    SearchOperator.Equals => Expression.Equal(memberAccess, constant),
                    SearchOperator.NotEquals => Expression.NotEqual(memberAccess, constant),
                    SearchOperator.GreaterThan => Expression.GreaterThan(memberAccess, constant),
                    SearchOperator.LessThan => Expression.LessThan(memberAccess, constant),
                    SearchOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(memberAccess, constant),
                    SearchOperator.LessThanOrEqual => Expression.LessThanOrEqual(memberAccess, constant),
                    SearchOperator.Contains when propertyInfo.PropertyType == typeof(string) =>
                        Expression.Call(memberAccess, typeof(string).GetMethod("Contains", [typeof(string)])!, constant),
                    SearchOperator.StartsWith when propertyInfo.PropertyType == typeof(string) =>
                        Expression.Call(memberAccess, typeof(string).GetMethod("StartsWith", [typeof(string)])!, constant),
                    SearchOperator.EndsWith when propertyInfo.PropertyType == typeof(string) =>
                        Expression.Call(memberAccess, typeof(string).GetMethod("EndsWith", [typeof(string)])!, constant),
                    _ => throw new NotSupportedException($"Operator '{criterion.Operator}' is not supported in expressions.")
                };
            }

            combined = combined == null ? condition : Expression.AndAlso(combined, condition);
        }

        return Expression.Lambda<Func<T, bool>>(combined ?? Expression.Constant(true), parameter);
    }

    private static object? ConvertToTargetType(Type targetType, object? value)
    {
        value = FromJson(value);
        if (value == null) return null;
        var actualTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (actualTarget == typeof(ObjectId))
        {
            if (value is ObjectId oid)
                return oid;
            if (value is string s && ObjectId.TryParse(s, out var parsedOid))
                return parsedOid;
        }

        if (value is string text)
        {
            // Convert.ChangeType reads "2026-09-01T00:00:00Z" as the host's local time, and
            // Expression.Equal compares ticks, so a date filter matched rows shifted by the host
            // offset. A value without a zone is taken as UTC, which is how this framework stores it.
            if (actualTarget == typeof(DateTime))
                return DateTime.Parse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (actualTarget == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (actualTarget == typeof(Guid))
                return Guid.Parse(text);
            if (actualTarget.IsEnum)
                return Enum.Parse(actualTarget, text, ignoreCase: true);
        }

        if (actualTarget.IsEnum)
            return Enum.ToObject(actualTarget, value);

        return Convert.ChangeType(value, actualTarget, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The CLR value inside a <see cref="JsonElement"/>, or the value unchanged.
    /// </summary>
    /// <remarks>
    /// <see cref="SearchCriterion.Value"/> is <c>object?</c>, so a criterion deserialized from a
    /// request carries a <c>JsonElement</c>. That is not <c>IConvertible</c>, and every filter with a
    /// concrete value answered 500 over HTTP while every in-process test passed, because the static
    /// factories are called with real typed values. An array is not <c>IEnumerable</c> either, so an
    /// <c>In</c> filter quietly matched nothing. An object has no scalar form and is left as it is,
    /// so the conversion refuses it rather than dropping the filter.
    /// </remarks>
    private static object? FromJson(object? value) => value is not JsonElement element
        ? value
        : element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(item => FromJson(item)).ToList(),
            _ => element
        };

    private static Expression BuildInExpression(Expression memberAccess, object? value)
    {
        value = FromJson(value);
        if (value == null)
            return Expression.Constant(false);

        if (value is System.Collections.IEnumerable enumerable)
        {
            var listType = typeof(List<>).MakeGenericType(memberAccess.Type);
            var list = Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod("Add")!;

            foreach (var item in enumerable)
            {
                if (item == null) continue;
                var convertedItem = ConvertToTargetType(memberAccess.Type, item);
                addMethod.Invoke(list, [convertedItem]);
            }

            var containsMethod = listType.GetMethod("Contains", [memberAccess.Type])!;
            return Expression.Call(Expression.Constant(list), containsMethod, memberAccess);
        }

        return Expression.Constant(false);
    }

    private static ConcurrentDictionary<string, PropertyInfo> GetProperties(Type type)
    {
        return _propertyCache.GetOrAdd(type, t => new ConcurrentDictionary<string, PropertyInfo>(
            t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
             .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase)
        ));
    }

    private static ConstantExpression BuildConstantFromValue(Type targetType, object? value)
    {
        if (value == null)
            return Expression.Constant(null, targetType);

        var convertedValue = ConvertToTargetType(targetType, value);
        return Expression.Constant(convertedValue, targetType);
    }
}
