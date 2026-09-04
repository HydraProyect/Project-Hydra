namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Políticas de autorización que exigen más que un rol — a diferencia de
/// <see cref="Roles"/>, que solo nombra roles para <c>RequireRole</c>. Hoy
/// solo hay una: DEC-36 (REC-099) pide «Administrador del Tenant propietario,
/// mediante permiso específico», no el rol Administrador a secas — ver
/// <c>Program.cs</c> (registro) y <c>TenantClaimsPrincipalFactory</c> (claim).
/// </summary>
public static class Policies
{
    public const string ConsultarAccesoDocumentosSensibles = "ConsultarAccesoDocumentosSensibles";
}
