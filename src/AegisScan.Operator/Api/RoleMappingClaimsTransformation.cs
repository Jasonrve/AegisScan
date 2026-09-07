using System.Security.Claims;
using AegisScan.KubeOps.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

/// <summary>
/// After JWT validation, looks up each IdP group the token carries against the admin-managed
/// role-mapping table and adds a synthesized role claim for any match. A group literally named
/// "admin"/"operator"/"viewer" already grants that role directly (see Program.cs's RoleClaimType)
/// — this only adds *extra* mappings, e.g. "security-team" → "operator".
/// </summary>
public sealed class RoleMappingClaimsTransformation(IServiceScopeFactory scopeFactory) : IClaimsTransformation
{
    public const string RoleClaimType = "groups";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated) return principal;

        var groups = identity.FindAll(RoleClaimType).Select(c => c.Value).ToList();
        if (groups.Count == 0) return principal;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        var mappings = await db.RoleMappings.AsNoTracking().Where(m => groups.Contains(m.IdpGroup)).ToListAsync();

        foreach (var mapping in mappings)
        {
            if (!identity.HasClaim(RoleClaimType, mapping.AegisRole))
                identity.AddClaim(new Claim(RoleClaimType, mapping.AegisRole));
        }
        return principal;
    }
}
