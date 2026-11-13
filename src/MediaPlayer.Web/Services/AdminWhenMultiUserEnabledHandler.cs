using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace media_player.Services;

public sealed class AdminWhenMultiUserEnabledHandler(UserAccountStore users) : AuthorizationHandler<AdminWhenMultiUserEnabledRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminWhenMultiUserEnabledRequirement requirement)
    {
        if ((await users.GetAllAsync()).Count == 0 || context.User.IsInRole("Administrator"))
            context.Succeed(requirement);
    }
}

public sealed class AdminWhenMultiUserEnabledRequirement : IAuthorizationRequirement;
