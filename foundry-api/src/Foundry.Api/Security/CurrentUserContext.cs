using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Foundry.Core.User;

namespace Foundry.Api.Security;

public class CurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string OperatorId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                ?? user?.FindFirst("sub")?.Value 
                ?? "anonymous";
        }
    }

    public string? OperatorName => _httpContextAccessor.HttpContext?.User?.Identity?.Name;

    public ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;
}
