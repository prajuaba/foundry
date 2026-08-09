using System;
using System.ComponentModel.DataAnnotations;
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

            // Rejected, not skipped -- and converted by the same table the custom endpoints' own
            // binder uses, so one query value cannot be legal on a generated list route and
            // illegal on a declared one.
            //
            // The rejection is the older lesson: this logged to Debug.WriteLine, which is compiled
            // out entirely in Release, and then `continue`d. A query parameter the caller could not
            // have known was unparseable was dropped from the filter, so the request returned 200
            // with a *wider* result set than asked for and nothing recorded that a filter had been
            // discarded. Silently widening a result set is the worst direction to fail in a
            // framework whose main claim is tenant isolation.
            var strVal = item.Value.ToString();
            var val = QueryValueBinder.Convert(strVal, prop.PropertyType, key, prop.Name);

            var propExpr = Expression.Property(parameter, prop);
            var valExpr = Expression.Constant(val, prop.PropertyType);
            var eqExpr = Expression.Equal(propExpr, valExpr);

            body = body == null ? eqExpr : Expression.AndAlso(body, eqExpr);
        }

        if (body == null) return null;
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }
}
