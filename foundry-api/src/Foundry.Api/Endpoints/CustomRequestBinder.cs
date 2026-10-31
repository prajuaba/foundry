#pragma warning disable IL2026, IL2067, IL2070, IL2072, IL2075
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Foundry.Api.Endpoints;

/// <summary>
/// Populates a custom endpoint's request record from the route and query string.
/// </summary>
/// <remarks>
/// <para>
/// GET and DELETE carry no body, so the generated endpoint used to construct the request with
/// <c>new TRequest()</c> and send it unpopulated. Every property therefore held its type default,
/// and since the compiler types those properties from the entity property each one filters, the
/// generated handler compared against <c>0</c>, <c>default(DateTime)</c> or
/// <c>ObjectId.Empty</c> — whatever the caller had actually asked for.
/// </para>
/// <para>
/// The endpoints did not fail. They answered 200 with the wrong rows: a query declared
/// <c>UtilizationPercent GreaterThan MinimumUtilizationPercent</c> returned everything, because it
/// ran as <c>&gt; 0</c>; one declared <c>AllocatedHours LessThan MaximumAllocatedHours</c> returned
/// nothing, because it ran as <c>&lt; 0</c>. Ten such endpoints in the first application built on
/// this framework were all quietly wrong, and the compiler's own comment about
/// <c>filterOperator</c> names this exact failure — "the quietest way a generator can be wrong" —
/// one layer above where it had been fixed.
/// </para>
/// <para>
/// Route values win over query-string values for the same name: a value in the path was matched by
/// the route template and is not the caller's to contradict with a duplicate query parameter.
/// </para>
/// <para>
/// Binding is by reflection because the request type is generated and this is a runtime component;
/// the request records are ordinary records whose properties are all <c>init</c> with defaults, so
/// they construct parameterlessly and take values through <see cref="PropertyInfo.SetValue"/> —
/// <c>init</c> is a compile-time restriction, not a runtime one.
/// </para>
/// </remarks>
public static class CustomRequestBinder
{
    /// <summary>
    /// Creates a <typeparamref name="TRequest"/> and fills it from the request's route and query.
    /// </summary>
    /// <exception cref="System.ComponentModel.DataAnnotations.ValidationException">
    /// A supplied value is not valid for the property it names; see <see cref="QueryValueBinder"/>.
    /// </exception>
    [RequiresUnreferencedCode("Binds generated request records by reflecting over their properties.")]
    public static TRequest Bind<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TRequest>(
        HttpContext context)
        where TRequest : new()
    {
        var request = new TRequest();

        var properties = typeof(TRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToArray();

        if (properties.Length == 0) return request;

        foreach (var (name, raw) in Supplied(context))
        {
            var property = Array.Find(
                properties, p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            // A name the request does not have is not an error. Callers append tracing and cache
            // busting parameters, and the generated list endpoints already ignore what they do not
            // recognise.
            if (property is null) continue;

            property.SetValue(
                request,
                QueryValueBinder.Convert(raw, property.PropertyType, name, property.Name));
        }

        return request;
    }

    /// <summary>
    /// Reconciles a body-bound request with its route values: a property the body left at its
    /// default takes the route's value, and one the body set differently is refused.
    /// </summary>
    /// <remarks>
    /// PUT and POST custom endpoints bound the request from the JSON body alone, so
    /// <c>/sprints/{id}/points</c> with a different <c>Id</c> in the body changed the body's row,
    /// and one with no body <c>Id</c> answered 500. The route and the row it changed could
    /// disagree; a caller trusting the URL was misled. A mismatch is refused (400) rather than
    /// silently resolved either way, because either guess may be the one the caller did not mean.
    /// </remarks>
    /// <exception cref="System.ComponentModel.DataAnnotations.ValidationException">
    /// A route value is unparseable, or the body names a different value for the same property.
    /// </exception>
    [RequiresUnreferencedCode("Binds generated request records by reflecting over their properties.")]
    public static TRequest ApplyRoute<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TRequest>(
        TRequest? request, HttpContext context)
        where TRequest : class
    {
        if (request is null)
        {
            // A positional record cannot be built from nothing; such a request needs its body.
            if (typeof(TRequest).GetConstructor(Type.EmptyTypes) is null)
                throw new System.ComponentModel.DataAnnotations.ValidationException("A request body is required.");
            request = Activator.CreateInstance<TRequest>();
        }

        var properties = typeof(TRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        foreach (var entry in context.Request.RouteValues)
        {
            var raw = entry.Value?.ToString();
            if (string.IsNullOrEmpty(raw)) continue;

            var property = Array.Find(
                properties, p => p.Name.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));
            if (property is null) continue;

            var fromRoute = QueryValueBinder.Convert(raw, property.PropertyType, entry.Key, property.Name);
            var fromBody = property.GetValue(request);

            if (IsUnset(fromBody, property.PropertyType))
            {
                property.SetValue(request, fromRoute);
            }
            else if (!Equals(fromBody, fromRoute))
            {
                throw new System.ComponentModel.DataAnnotations.ValidationException(
                    $"The body's '{property.Name}' ('{fromBody}') does not match the route's '{entry.Key}' ('{raw}').");
            }
        }

        return request;
    }

    private static bool IsUnset(object? value, Type type)
    {
        if (value is null) return true;
        if (value is string s) return s.Length == 0;
        return type.IsValueType && value.Equals(Activator.CreateInstance(type));
    }

    /// <summary>
    /// Every name/value the caller supplied, query string first so route values overwrite them.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<(string Name, string Value)> Supplied(HttpContext context)
    {
        foreach (var entry in context.Request.Query)
        {
            var value = entry.Value.ToString();
            if (!string.IsNullOrEmpty(value)) yield return (entry.Key, value);
        }

        foreach (var entry in context.Request.RouteValues)
        {
            var value = entry.Value?.ToString();
            if (!string.IsNullOrEmpty(value)) yield return (entry.Key, value);
        }
    }
}
