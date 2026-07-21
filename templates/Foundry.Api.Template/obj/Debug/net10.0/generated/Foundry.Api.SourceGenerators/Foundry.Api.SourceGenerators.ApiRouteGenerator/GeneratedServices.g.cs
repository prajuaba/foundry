#nullable enable
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using System.Collections.Generic;
using MongoDB.Bson;
using Foundry.Core.Paging;
using Foundry.Api.MediatR;

namespace Foundry.Api.Endpoints;

public static class GeneratedServices
{
    public static IServiceCollection AddGeneratedHandlers(this IServiceCollection services)
    {
        // DI Registrations for TodoItem
        services.AddTransient<IRequestHandler<InsertCommand<Foundry.Api.Template.Domain.TodoItem>, Foundry.Api.Template.Domain.TodoItem>, InsertCommandHandler<Foundry.Api.Template.Domain.TodoItem>>();
        services.AddTransient<IRequestHandler<UpdateCommand<Foundry.Api.Template.Domain.TodoItem>, Foundry.Api.Template.Domain.TodoItem>, UpdateCommandHandler<Foundry.Api.Template.Domain.TodoItem>>();
        services.AddTransient<IRequestHandler<DeleteCommand<Foundry.Api.Template.Domain.TodoItem>, bool>, DeleteCommandHandler<Foundry.Api.Template.Domain.TodoItem>>();
        services.AddTransient<IRequestHandler<GetByIdQuery<Foundry.Api.Template.Domain.TodoItem>, Foundry.Api.Template.Domain.TodoItem?>, GetByIdQueryHandler<Foundry.Api.Template.Domain.TodoItem>>();
        services.AddTransient<IRequestHandler<FindManyQuery<Foundry.Api.Template.Domain.TodoItem>, IReadOnlyList<Foundry.Api.Template.Domain.TodoItem>>, FindManyQueryHandler<Foundry.Api.Template.Domain.TodoItem>>();
        services.AddTransient<IRequestHandler<SearchPagedQuery<Foundry.Api.Template.Domain.TodoItem>, PagedResult<Foundry.Api.Template.Domain.TodoItem>>, SearchPagedQueryHandler<Foundry.Api.Template.Domain.TodoItem>>();

        return services;
    }
}
