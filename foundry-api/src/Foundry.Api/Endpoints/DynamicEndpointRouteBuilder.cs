using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using MongoDB.Bson;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Core.Paging;
using Foundry.Api.Manifest;
using Foundry.Api.MediatR;

namespace Foundry.Api.Endpoints;

public static class DynamicEndpointRouteBuilder
{
    [RequiresUnreferencedCode("Uses runtime reflection for compiling parameter filter expressions.")]
    public static Expression<Func<TEntity, bool>>? BuildFilterExpression<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEntity>(HttpContext context) where TEntity : class
    {
        var query = context.Request.Query;
        if (query.Count == 0) return null;

        var parameter = Expression.Parameter(typeof(TEntity), "x");
        Expression? body = null;

        var properties = typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var item in query)
        {
            var key = item.Key;
            // Ignore system routing and page settings parameters
            if (key.Equals("sortBy", StringComparison.OrdinalIgnoreCase) || 
                key.Equals("limit", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("sortOrder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prop = properties.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (prop == null) continue;

            object? val = null;
            try
            {
                var strVal = item.Value.ToString();
                if (prop.PropertyType == typeof(ObjectId))
                {
                    val = ObjectId.Parse(strVal);
                }
                else if (prop.PropertyType == typeof(int))
                {
                    val = int.Parse(strVal);
                }
                else if (prop.PropertyType == typeof(decimal))
                {
                    val = decimal.Parse(strVal);
                }
                else if (prop.PropertyType == typeof(bool))
                {
                    val = bool.Parse(strVal);
                }
                else if (prop.PropertyType.IsEnum)
                {
                    val = Enum.Parse(prop.PropertyType, strVal, true);
                }
                else
                {
                    val = Convert.ChangeType(strVal, prop.PropertyType);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse query parameter '{key}': {ex.Message}");
                continue;
            }

            var propExpr = Expression.Property(parameter, prop);
            var valExpr = Expression.Constant(val, prop.PropertyType);
            var eqExpr = Expression.Equal(propExpr, valExpr);

            body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);
        }

        if (body == null) return null;
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }
}
