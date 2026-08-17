#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using HotChocolate.Resolvers;
using MediatR;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Foundry.Api.Manifest;
using Foundry.Api.MediatR;

namespace Microsoft.Extensions.DependencyInjection;

public static class GraphQLConfiguration
{
    [RequiresUnreferencedCode("Uses runtime reflection to register dynamic GraphQL schemas.")]
    [RequiresDynamicCode("Uses runtime dynamic code or generics.")]
    public static IServiceCollection AddDynamicGraphQL(this IServiceCollection services, ApiManifest? manifest = null)
    {
        if (manifest == null)
        {
            if (System.IO.File.Exists("api-manifest.json"))
            {
                var json = System.IO.File.ReadAllText("api-manifest.json");
                manifest = System.Text.Json.JsonSerializer.Deserialize<ApiManifest>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }

        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        var entities = new List<(Type Type, EndpointConfig Config)>();
        if (manifest?.Endpoints != null)
        {
            foreach (var config in manifest.Endpoints)
            {
                var entityTypeName = $"{manifest.Namespace}.{config.Entity}";
                var entityType = allTypes.FirstOrDefault(t => t.FullName?.Equals(entityTypeName, StringComparison.OrdinalIgnoreCase) == true);
                if (entityType == null)
                {
                    entityType = allTypes.FirstOrDefault(t => t.Name.Equals(config.Entity, StringComparison.OrdinalIgnoreCase) == true
                        && typeof(IEntity<ObjectId>).IsAssignableFrom(t));
                }

                // An entity reaches GraphQL only by declaring enableGraphQL. Previously every entity
                // with a GET was exposed, so `enableGraphQL: false` -- and every entity that never
                // mentioned GraphQL at all -- was served over it anyway, and the one field in the
                // schema that decides this reached nothing.
                if (entityType != null && config.GraphQL)
                {
                    entities.Add((entityType, config));
                }
            }
        }

        // A GraphQL type with no fields is not a valid schema, and an invalid schema throws on the
        // first request rather than at startup — which is exactly the failure this whole area was in
        // before. So the shape of the manifest decides which root types exist at all.
        var readable = entities.Any(e => Exposes(e.Config, "GET") || Exposes(e.Config, "GET_BY_ID"));
        var mutable = entities.Any(e =>
            Exposes(e.Config, "POST") || Exposes(e.Config, "PUT") || Exposes(e.Config, "DELETE"));

        if (!readable)
        {
            throw new InvalidOperationException(
                "GraphQL is mapped but the API manifest exposes no readable entity, so the schema would " +
                "have no Query type and every request would fail. Set enableGraphQL on at least one " +
                "entity that declares GET or GET_BY_ID, or do not call MapGraphQL.");
        }

        var builder = services.AddGraphQLServer()
            .AddQueryType(d =>
            {
                d.Name("Query");
                foreach (var (entityType, config) in entities)
                {
                    var entityName = entityType.Name;
                    var helperType = typeof(GraphQLResolverHelper<>).MakeGenericType(entityType);
                    var resolveCollectionMethod = helperType.GetMethod(nameof(GraphQLResolverHelper<OrderPlaceholder>.ResolveCollection))!;
                    var resolveByIdMethod = helperType.GetMethod(nameof(GraphQLResolverHelper<OrderPlaceholder>.ResolveById))!;

                    // get{Entity}s
                    if (Exposes(config, "GET"))
                    {
                        var roles = RolesFor(config, "GET");
                        d.Field($"get{entityName}s")
                            .Type(ListOf(entityType))
                            .Resolve(ctx =>
                            {
                                GraphQLAccessGuard.Enforce(ctx, roles);
                                return resolveCollectionMethod.Invoke(null, new object[] { ctx });
                            })
                            // Both take the entity type explicitly. The resolver is built by
                            // reflection and returns `object`, so there is nothing for the parameterless
                            // overloads to infer an element type from, and they fail the schema build
                            // with "Cannot handle the specified filter type".
                            .UseFiltering(entityType)
                            .UseSorting(entityType);
                    }

                    // get{Entity}ById
                    if (Exposes(config, "GET_BY_ID"))
                    {
                        var roles = RolesFor(config, "GET_BY_ID");
                        d.Field($"get{entityName}ById")
                            .Type(OutputOf(entityType))
                            .Argument("id", a => a.Type<NonNullType<StringType>>())
                            .Resolve(async ctx =>
                            {
                                GraphQLAccessGuard.Enforce(ctx, roles);
                                var id = ctx.ArgumentValue<string>("id");
                                var task = (Task)resolveByIdMethod.Invoke(null, new object[] { ctx, id })!;
                                await task;
                                return ((dynamic)task).Result;
                            });
                    }
                }
            });

        if (mutable)
        {
            builder.AddMutationType(d =>
            {
                d.Name("Mutation");
                foreach (var (entityType, config) in entities)
                {
                    var entityName = entityType.Name;
                    var mutationHelperType = typeof(GraphQLMutationHelper<>).MakeGenericType(entityType);
                    var createMethod = mutationHelperType.GetMethod(nameof(GraphQLMutationHelper<OrderPlaceholder>.CreateEntity))!;
                    var updateMethod = mutationHelperType.GetMethod(nameof(GraphQLMutationHelper<OrderPlaceholder>.UpdateEntity))!;
                    var deleteMethod = mutationHelperType.GetMethod(nameof(GraphQLMutationHelper<OrderPlaceholder>.DeleteEntity))!;

                    // create{Entity}
                    if (Exposes(config, "POST"))
                    {
                        var roles = RolesFor(config, "POST");
                        d.Field($"create{entityName}")
                            .Type(OutputOf(entityType))
                            .Argument("input", a => a.Type(NonNullInputOf(entityType)))
                            .Resolve(async ctx =>
                            {
                                GraphQLAccessGuard.Enforce(ctx, roles);
                                var input = ctx.ArgumentValue<object>("input");
                                var task = (Task)createMethod.Invoke(null, new object[] { ctx, input })!;
                                await task;
                                return ((dynamic)task).Result;
                            });
                    }

                    // update{Entity}
                    if (Exposes(config, "PUT"))
                    {
                        var roles = RolesFor(config, "PUT");
                        d.Field($"update{entityName}")
                            .Type(OutputOf(entityType))
                            .Argument("id", a => a.Type<NonNullType<StringType>>())
                            .Argument("input", a => a.Type(NonNullInputOf(entityType)))
                            .Resolve(async ctx =>
                            {
                                GraphQLAccessGuard.Enforce(ctx, roles);
                                var id = ctx.ArgumentValue<string>("id");
                                var input = ctx.ArgumentValue<object>("input");
                                var task = (Task)updateMethod.Invoke(null, new object[] { ctx, id, input })!;
                                await task;
                                return ((dynamic)task).Result;
                            });
                    }

                    // delete{Entity}
                    if (Exposes(config, "DELETE"))
                    {
                        var roles = RolesFor(config, "DELETE");
                        d.Field($"delete{entityName}")
                            .Type<BooleanType>()
                            .Argument("id", a => a.Type<NonNullType<StringType>>())
                            .Resolve(async ctx =>
                            {
                                GraphQLAccessGuard.Enforce(ctx, roles);
                                var id = ctx.ArgumentValue<string>("id");
                                var task = (Task<bool>)deleteMethod.Invoke(null, new object[] { ctx, id })!;
                                return await task;
                            });
                    }
                }
            });
        }

        builder
            .AddMongoDbFiltering()
            .AddMongoDbSorting();

        return services;
    }

    /// <summary>
    /// Whether the manifest exposes this operation for this entity.
    /// </summary>
    /// <remarks>
    /// Every entity used to get all five operations regardless of what it declared, so an entity
    /// published read-only over REST accepted <c>create</c>, <c>update</c> and <c>delete</c> over
    /// GraphQL. The manifest is the single declaration of what an API offers; it has to mean the same
    /// thing on both transports.
    /// </remarks>
    private static bool Exposes(EndpointConfig config, string method)
        => config.Methods.Any(m => m.Equals(method, StringComparison.OrdinalIgnoreCase));

    /// <summary>The GraphQL output type for an entity.</summary>
    private static Type OutputOf(Type entityType) => typeof(EntityType<>).MakeGenericType(entityType);

    /// <summary>The GraphQL list-of-entity output type.</summary>
    /// <remarks>
    /// <c>ListType&lt;T&gt;</c> constrains <c>T</c> to <c>IType</c> — a *GraphQL* type, not the CLR
    /// entity. This was <c>ListType&lt;Order&gt;</c>, which does not satisfy the constraint, so
    /// <c>MakeGenericType</c> produced a type the schema builder rejected. That threw
    /// <c>SchemaException</c> on the first request and every request after it, which is why every
    /// GraphQL call returned 500.
    /// </remarks>
    private static Type ListOf(Type entityType)
        => typeof(ListType<>).MakeGenericType(OutputOf(entityType));

    /// <summary>The required GraphQL input type for an entity argument.</summary>
    /// <remarks>
    /// Arguments take input types, and an entity's output type is not one. The mutations passed
    /// <c>NonNullType&lt;Order&gt;</c> — a CLR type where a GraphQL input type belongs — which is the
    /// second reason the schema could not be built.
    /// </remarks>
    private static Type NonNullInputOf(Type entityType)
        => typeof(NonNullType<>).MakeGenericType(typeof(EntityInputType<>).MakeGenericType(entityType));

    /// <summary>The roles the manifest declares for an operation, or none.</summary>
    private static IReadOnlyList<string> RolesFor(EndpointConfig config, string method)
        => config.Roles.TryGetValue(method, out var roles) && roles is not null
            ? roles
            : Array.Empty<string>();

    // Dummy placeholder for generic mapping signature resolving
    private record OrderPlaceholder : BaseEntity<ObjectId>;
}

/// <summary>
/// Properties an entity keeps out of its wire contract.
/// </summary>
/// <remarks>
/// HotChocolate builds its types from CLR properties and does not read <c>System.Text.Json</c>
/// attributes, so <c>[JsonIgnore]</c> — the mechanism the REST contract uses to hide a property —
/// meant nothing to GraphQL. On the sample's <c>Order</c> that put <c>isDeleted</c> and
/// <c>deletedAt</c> in both the output type and the mutation input, and the comment on
/// <c>Order.IsDeleted</c> says exactly why that matters: hiding it "stops a PUT from setting it,
/// which would delete a record via the update route and skip whatever roles the manifest applies to
/// DELETE". <c>updateOrder(input: { isDeleted: true })</c> was that same bypass, reopened.
/// </remarks>
internal static class WireContract
{
    internal static IEnumerable<PropertyInfo> HiddenProperties(Type entityType)
        => entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: true));

    /// <summary>Lifecycle state the repository owns: identity, audit timestamps and the OCC version.</summary>
    internal static IEnumerable<PropertyInfo> ServerAssignedProperties(Type entityType)
    {
        var names = typeof(IEntity<>).MakeGenericType(typeof(ObjectId))
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        return entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => names.Contains(p.Name));
    }

    /// <summary>A <c>x =&gt; x.Property</c> selector, which is how a descriptor identifies a member.</summary>
    internal static System.Linq.Expressions.Expression<Func<T, object?>> Selector<T>(PropertyInfo property)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(T), "x");

        return System.Linq.Expressions.Expression.Lambda<Func<T, object?>>(
            System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Property(parameter, property), typeof(object)),
            parameter);
    }
}

/// <summary>An entity's GraphQL output type, minus whatever its wire contract hides.</summary>
public class EntityType<T> : ObjectType<T> where T : class
{
    protected override void Configure(IObjectTypeDescriptor<T> descriptor)
    {
        foreach (var property in WireContract.HiddenProperties(typeof(T)))
        {
            descriptor.Ignore(WireContract.Selector<T>(property));
        }
    }
}

/// <summary>
/// An entity's GraphQL input type: its wire contract, minus the state the server assigns.
/// </summary>
/// <remarks>
/// A CLR property that cannot be null becomes a required GraphQL input field, so the mutations asked
/// the caller to supply <c>id</c>, <c>createdAtUtc</c>, <c>updatedAtUtc</c> and <c>version</c> —
/// every one of which the repository sets itself and overwrites. <c>createOrder</c> was therefore
/// impossible to call correctly: a caller had to invent the server's own identifier. The REST POST
/// binds the same entity from JSON, where an absent field is simply defaulted, so dropping them here
/// makes the two transports agree rather than inventing a contract for one of them.
/// </remarks>
public class EntityInputType<T> : InputObjectType<T> where T : class
{
    protected override void Configure(IInputObjectTypeDescriptor<T> descriptor)
    {
        foreach (var property in WireContract.HiddenProperties(typeof(T))
                     .Concat(WireContract.ServerAssignedProperties(typeof(T))))
        {
            descriptor.Ignore(WireContract.Selector<T>(property));
        }
    }
}

/// <summary>
/// Enforces, on a GraphQL field, the access its entity declares in the manifest.
/// </summary>
/// <remarks>
/// GraphQL reaches the same entities through the same repositories as the generated REST endpoints,
/// so it has to enforce the same declaration — otherwise the roles are enforced on one transport and
/// merely documented on the other.
///
/// <c>SecurityBehavior</c> does not cover this. It reads the <c>EndpointConfig</c> from the matched
/// ASP.NET Core endpoint's metadata, and the GraphQL endpoint carries none, so it returns early for
/// every GraphQL request — including mutations. The check has to live here.
/// </remarks>
public static class GraphQLAccessGuard
{
    /// <summary>Throws unless the caller is authenticated and holds one of the declared roles.</summary>
    /// <remarks>
    /// An operation with no declared roles still requires an authenticated caller, matching the
    /// generated endpoints: a declared-but-empty role list means "any signed-in caller", never
    /// "anyone".
    /// </remarks>
    public static void Enforce(IResolverContext context, IReadOnlyList<string> roles)
    {
        var user = context.Service<Foundry.Core.User.ICurrentUserContext>().User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("The current user is not authenticated.")
                .SetCode("AUTH_NOT_AUTHENTICATED")
                .Build());
        }

        if (roles.Count == 0) return;

        // Same three ways of carrying a role as SecurityBehavior recognises. They have to agree: a
        // token that authorises a REST call must authorise the equivalent GraphQL field.
        var permitted = roles.Any(role =>
            user.IsInRole(role) ||
            user.HasClaim(c => c.Type == System.Security.Claims.ClaimTypes.Role
                && c.Value.Equals(role, StringComparison.OrdinalIgnoreCase)) ||
            user.HasClaim(c => c.Type == "role"
                && c.Value.Equals(role, StringComparison.OrdinalIgnoreCase)));

        if (!permitted)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage($"The current user holds none of the required roles: {string.Join(", ", roles)}.")
                .SetCode("AUTH_NOT_AUTHORIZED")
                .Build());
        }
    }
}

public static class GraphQLResolverHelper<TEntity> where TEntity : class, IEntity<ObjectId>
{
    /// <remarks>
    /// <c>Query()</c>, not <c>Collection.AsQueryable()</c>. The raw collection applies no soft-delete
    /// filter, no tenant filter and no owner scope, so this resolver returned every tenant's rows and
    /// every deleted row — the exact isolation failure that was found and fixed on the REST path,
    /// reachable through a different door.
    /// </remarks>
    public static IQueryable<TEntity> ResolveCollection(IResolverContext context)
    {
        var repo = context.Service<IRepository<TEntity>>();
        var query = repo.Query();

        // Query() is an IQueryable, so it carries the tenant, owner and soft-delete filters but
        // cannot decrypt or mask -- there are no materialised entities to work on until it is
        // enumerated. GetByIdAsync and FindManyAsync do that work after materialising; this
        // resolver did not, so `getResources { email phoneNumber }` answered with raw ciphertext
        // and an unredacted phone number, while the same fields over REST were protected.
        //
        // Entity types that declare nothing sensitive keep the IQueryable untouched, so filtering
        // and paging still reach the database. Only the types that actually declare Encrypt or Mask
        // pay for materialising, which is the trade this makes deliberately: a protected field
        // returned in the clear is not a performance question.
        if (!repo.HasProtectedProperties) return query;

        return repo.ProtectForRead(query.ToList()).AsQueryable();
    }

    public static async Task<TEntity?> ResolveById(IResolverContext context, string id)
    {
        var repo = context.Service<IRepository<TEntity>>();
        if (!ObjectId.TryParse(id, out var objectId)) return null;
        return await repo.GetByIdAsync(objectId, ct: context.RequestAborted);
    }
}

public static class GraphQLMutationHelper<TEntity> where TEntity : class, IEntity<ObjectId>
{
    public static async Task<TEntity> CreateEntity(IResolverContext context, TEntity input)
    {
        var sender = context.Service<ISender>();
        return await sender.Send(new InsertCommand<TEntity>(input), context.RequestAborted);
    }

    public static async Task<TEntity> UpdateEntity(IResolverContext context, string id, TEntity input)
    {
        var sender = context.Service<ISender>();
        if (!ObjectId.TryParse(id, out var objectId)) throw new ArgumentException("Invalid ID");
        
        var idProp = typeof(TEntity).GetProperty("Id");
        if (idProp != null && idProp.CanWrite)
        {
            idProp.SetValue(input, objectId);
        }
        
        return await sender.Send(new UpdateCommand<TEntity>(input), context.RequestAborted);
    }

    public static async Task<bool> DeleteEntity(IResolverContext context, string id)
    {
        var sender = context.Service<ISender>();
        if (!ObjectId.TryParse(id, out var objectId)) throw new ArgumentException("Invalid ID");

        var userContext = context.Service<Foundry.Core.User.ICurrentUserContext>();
        var operatorId = userContext.OperatorId ?? "anonymous";

        return await sender.Send(new DeleteCommand<TEntity>(objectId, operatorId), context.RequestAborted);
    }
}
