namespace AegisScan.KubeOps.Api;

/// <summary>Authorization policy names; each maps to a "groups" claim role (see Program.cs AddAuthorizationBuilder).</summary>
public static class AegisRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    /// <summary>The raw "groups" claim values a role mapping may target — lowercase, matching the JWT group-name convention.</summary>
    public static readonly string[] KnownRoles = ["admin", "operator", "viewer"];
}
