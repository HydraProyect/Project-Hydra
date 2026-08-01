using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Crea el primer usuario Administrador si todavía no existe ninguno. Se
/// invoca una vez al arrancar la aplicación (ver Program.cs) — usa
/// UserManager en vez de HasData porque el hash de contraseña de Identity
/// no es determinista y requiere las APIs reales para calcularse bien.
///
/// Email/contraseña son configurables (AdministradorInicial:Email /
/// AdministradorInicial:Contrasena) para que un despliegue compartido (ver
/// DEPLOY.md) no arranque con las credenciales por defecto, públicas en
/// este mismo archivo — en desarrollo local, sin configurar nada, se usan
/// esos valores por defecto tal cual siempre.
///
/// El Administrador inicial nace con 2FA ya activo (P1-13 de
/// docs/business/MATURITY_REVIEW.md exige 2FA para todo Administrador —
/// sembrarlo sin ella dejaría la propia cuenta bootstrap fuera de su
/// propia regla, y MainLayout la redirigiría a /cuenta/configurar-2fa en
/// cuanto iniciara sesión). La clave TOTP es fija y pública a propósito
/// (no una credencial real — un despliegue compartido debe reconfigurar
/// el autenticador desde /cuenta/configurar-2fa igual que cambiaría la
/// contraseña por defecto) para que los tests E2E (Ayudas.cs, que no
/// referencia este proyecto) puedan calcular el código sin acceso a BD.
/// </summary>
public static class IdentitySeeder
{
    public const string EmailAdministradorInicial = "admin@caemanager.local";
    public const string ContrasenaAdministradorInicial = "CaeManager#2026";
    public const string ClaveTotpAdministradorInicial = "JBSWY3DPEHPK3PXP";

    public static async Task SeedAsync(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        ILogger logger,
        IConfiguration configuration)
    {
        foreach (var rol in Identity.Roles.Todos)
        {
            if (!await roleManager.RoleExistsAsync(rol))
                await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
        }

        var email = configuration["AdministradorInicial:Email"] ?? EmailAdministradorInicial;
        var contrasena = configuration["AdministradorInicial:Contrasena"] ?? ContrasenaAdministradorInicial;

        if (await userManager.FindByEmailAsync(email) is not null)
            return;

        var administrador = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            // No es una contraseña temporal: el despliegue la eligió a
            // propósito (o acepta el default documentado en DEPLOY.md), no
            // hay ningún tercero esperando cambiarla en su primer acceso.
            DebeCambiarContrasena = false,
            // Tenant #1 (ver ADR-003-saas-multitenant.md) — no hay
            // aprovisionamiento de tenants nuevos todavía (sin self-signup,
            // ver ADR-001), así que el Administrador inicial siempre
            // pertenece al tenant por defecto.
            TenantId = TenantSeedData.IdPorDefecto
        };

        var resultado = await userManager.CreateAsync(administrador, contrasena);
        if (!resultado.Succeeded)
        {
            logger.LogError(
                "No se pudo crear el usuario administrador inicial: {Errores}",
                string.Join(", ", resultado.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(administrador, Identity.Roles.Administrador);

        if (userManager.Store is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
        {
            await claveStore.SetAuthenticatorKeyAsync(administrador, ClaveTotpAdministradorInicial, CancellationToken.None);
            await userManager.UpdateAsync(administrador);
        }

        await userManager.SetTwoFactorEnabledAsync(administrador, true);
    }
}
