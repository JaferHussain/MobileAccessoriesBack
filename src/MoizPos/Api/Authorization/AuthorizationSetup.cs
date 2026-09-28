namespace MoizPos.Api.Authorization;

/// <summary>Role names as they appear in the JWT role claim.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Staff = "Staff";
}

/// <summary>Authorization policy names.</summary>
public static class Policies
{
    /// <summary>
    /// Required on every endpoint or field that exposes cost price, profit, or a financial
    /// report (FR-040). Applied per-endpoint rather than per-controller so adding an action to
    /// an existing controller cannot silently inherit the wrong access.
    /// </summary>
    public const string AdminOnly = "AdminOnly";
}
