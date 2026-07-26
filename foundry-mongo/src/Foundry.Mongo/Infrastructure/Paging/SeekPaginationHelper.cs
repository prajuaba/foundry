using MongoDB.Bson;
using Foundry.Core.Search;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Foundry.Core.Paging;

/// <summary>
/// Seek pagination helper for O(1) cursor-based navigation on large collections.
/// Builds filter expressions from the last-seen key value and sort direction.
/// </summary>
public static class SeekPaginationHelper
{
    public static Expression<Func<T, bool>> BuildSeekFilter<T>(
        string fieldName,
        object? lastSeenValue,
        bool ascending = true) where T : class
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("Field name is required for seek pagination", nameof(fieldName));

        if (lastSeenValue == null)
            return _ => true;

        var parameter = Expression.Parameter(typeof(T), "x");
        var memberAccess = AccessMember<T>(parameter, fieldName);
        var typedValue = ConvertValue(memberAccess.Type, lastSeenValue);
        var constant = Expression.Constant(typedValue, memberAccess.Type);

        Expression memberAccessExpr = memberAccess;
        if (memberAccess.Type != constant.Type)
            memberAccessExpr = Expression.Convert(memberAccess, constant.Type);

        return ascending
            ? Expression.Lambda<Func<T, bool>>(Expression.GreaterThan(memberAccessExpr, constant), parameter)
            : Expression.Lambda<Func<T, bool>>(Expression.LessThan(memberAccessExpr, constant), parameter);
    }

    public static Expression<Func<T, bool>> BuildCompoundSeekFilter<T>(
        SearchCriterion[] criteria,
        object?[] values,
        bool ascending = true) where T : class
    {
        if (criteria.Length != values.Length || criteria.Length == 0)
            throw new ArgumentException("Criteria and values must match and be non-empty", nameof(criteria));

        var parameter = Expression.Parameter(typeof(T), "x");
        var conditions = new List<Expression>();

        for (int i = 0; i < criteria.Length; i++)
        {
            var memberAccess = AccessMember<T>(parameter, criteria[i].Field);
            var typedValue = ConvertValue(memberAccess.Type, values[i]);
            var constant = Expression.Constant(typedValue, memberAccess.Type);

            Expression memberAccessExpr = memberAccess;
            if (memberAccess.Type != constant.Type)
                memberAccessExpr = Expression.Convert(memberAccess, constant.Type);

            var andExpressions = new List<Expression>();
            for (int j = 0; j < i; j++)
            {
                var prevMember = AccessMember<T>(parameter, criteria[j].Field);
                var prevTypedValue = ConvertValue(prevMember.Type, values[j]);
                Expression prevMemberExpr = prevMember;
                if (prevMember.Type != Expression.Constant(prevTypedValue, prevMember.Type).Type)
                    prevMemberExpr = Expression.Convert(prevMember, Expression.Constant(prevTypedValue, prevMember.Type).Type);
                
                andExpressions.Add(Expression.Equal(prevMemberExpr, Expression.Constant(prevTypedValue, prevMember.Type)));
            }

            var thisComparison = ascending 
                ? Expression.GreaterThan(memberAccessExpr, constant)
                : Expression.LessThan(memberAccessExpr, constant);

            if (andExpressions.Count > 0)
            {
                andExpressions.Add(thisComparison);
                conditions.Add(andExpressions.Aggregate(Expression.AndAlso));
            }
            else
            {
                conditions.Add(thisComparison);
            }
        }

        var finalExpression = conditions[0];
        for (int i = 1; i < conditions.Count; i++)
            finalExpression = Expression.OrElse(finalExpression, conditions[i]);

        return Expression.Lambda<Func<T, bool>>(finalExpression, parameter);
    }

    private static MemberExpression AccessMember<T>(ParameterExpression parameter, string fieldName)
    {
        var parts = fieldName.Split('.');
        Expression current = parameter;
        foreach (var part in parts)
        {
            var member = current.Type.GetProperty(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (member == null || !member.CanRead)
                throw new ArgumentException($"Property '{part}' not found on type {current.Type.Name}", nameof(fieldName));
            current = Expression.Property(current, member);
        }
        return (MemberExpression)current;
    }

    private static object ConvertValue(Type targetType, object? value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var actualTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (actualTarget == typeof(ObjectId))
        {
            if (value is ObjectId oid)
                return oid;
            if (value is string s && ObjectId.TryParse(s, out var parsedOid))
                return parsedOid;
        }
        return Convert.ChangeType(value, actualTarget, CultureInfo.InvariantCulture);
    }
}
