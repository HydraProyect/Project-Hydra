using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Crea el primer usuario Administrador si todavía no existe ninguno. Se
/// invoca una vez al arrancar la aplicación (ver Program.cs) — usa
/// UserManager en vez de HasData porque el hash de contraseña de Identity
/// no es determinista y requiere las APIs reales para calcularse bien.
///
/// Email/contraseña son configurables (AdministradorInicial:Email /
/// AdministradorInicial:Contrasena). En desarrollo local, sin configurar
/// nada, se usan los valores por defecto públicos en este mismo archivo.
/// En producción los defaults NO se usan nunca: si falta la configuración,
/// el arranque falla con instrucciones (hallazgo P0-2 de
/// docs/business/MATURITY_REVIEW.md — nada impedía que producción arrancara
/// con las credenciales hardcodeadas del repo). Fallar el arranque es
/// deliberado: un despliegue de producción accesible con una contraseña
/// pública es peor que un despliegue caído.
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
        IUserStore<ApplicationUser> userStore,
        ILogger logger,
        IConfiguration configuration,
        IHostEnvironment entorno,
        Persistence.CaeManagerDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        foreach (var rol in Identity.Roles.Todos)
        {
            if (!await roleManager.RoleExistsAsync(rol))
                await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
        }

        var emailConfigurado = configuration["AdministradorInicial:Email"];
        var contrasenaConfigurada = configuration["AdministradorInicial:Contrasena"];

        if (entorno.IsProduction()
            && (string.IsNullOrWhiteSpace(emailConfigurado) || string.IsNullOrWhiteSpace(contrasenaConfigurada)))
        {
            throw new InvalidOperationException(
                "En producción es obligatorio configurar AdministradorInicial:Email y "
                + "AdministradorInicial:Contrasena (variables AdministradorInicial__Email / "
                + "AdministradorInicial__Contrasena, ver DEPLOY.md) — las credenciales por "
                + "defecto son públicas en el código fuente y no se usan fuera de desarrollo.");
        }

        var email = emailConfigurado ?? EmailAdministradorInicial;
        var contrasena = contrasenaConfigurada ?? ContrasenaAdministradorInicial;

        // NO se retorna sin pasar por la designación de la raíz. Antes de A2
        // este return era el final del camino cuando el usuario ya existía, y
        // eso habría dejado sin raíz a TODO despliegue en marcha: producción ya
        // tiene su administrador creado, así que la designación no habría
        // ocurrido nunca y el bootstrap de plataforma sería inalcanzable.
        var administradorExistente = await userManager.FindByEmailAsync(email);
        if (administradorExistente is not null)
        {
            await DesignarRaizDePlataformaAsync(
                administradorExistente, dbContext, userManager, logger, cancellationToken);
            return;
        }

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

        if (userStore is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
        {
            await claveStore.SetAuthenticatorKeyAsync(administrador, ClaveTotpAdministradorInicial, CancellationToken.None);
            await userManager.UpdateAsync(administrador);
        }

        await userManager.SetTwoFactorEnabledAsync(administrador, true);

        await DesignarRaizDePlataformaAsync(
            administrador, dbContext, userManager, logger, cancellationToken);

        // Solo fuera de producción: el admin real de producción debe ver el
        // modal de AceptacionTerminosGate igual que cualquier usuario, pero
        // el admin de desarrollo/E2E/CI no debe quedar bloqueado por un
        // diálogo ajeno a lo que esos entornos verifican (ver
        // AceptacionTerminosSeedHelper).
        if (!entorno.IsProduction())
        {
            await Persistence.Seed.AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(
                dbContext, administrador.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Fija la identidad raíz de plataforma <b>una sola vez</b>, y solo eso.
    ///
    /// <para>
    /// <b>No crea ninguna concesión.</b> El seeder designa quién puede ejecutar
    /// el acto fundacional; la autoridad sigue naciendo exclusivamente de
    /// <c>AutoConcederPrivilegioCommand</c>, que es el único punto de creación
    /// que vigila el ratchet de concesiones.
    /// </para>
    ///
    /// <para>
    /// <b>La configuración designa, no gobierna.</b> <c>AdministradorInicial:Email</c>
    /// sirve para resolver la identidad mientras la raíz está sin fijar. Una vez
    /// fijada, cambiar ese correo no la reasigna <i>ni tumba el arranque</i>:
    /// hacer que una variable de despliegue mutable controle la disponibilidad de
    /// una identidad que hemos hecho deliberadamente inmutable sería una mala
    /// dependencia. Se registra como deriva y se sigue.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que sí tumba el arranque</b> es que la raíz persistida ya no
    /// exista: ahí no hay deriva de configuración sino un estado imposible, y
    /// arrancar con él dejaría una plataforma cuya autoridad fundacional apunta
    /// a nadie.
    /// </para>
    /// </summary>
    private static async Task DesignarRaizDePlataformaAsync(
        ApplicationUser candidato,
        Persistence.CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var estado = await dbContext.EstadoBootstrapPlataforma
            .FirstOrDefaultAsync(cancellationToken);

        if (estado is null)
        {
            dbContext.EstadoBootstrapPlataforma.Add(
                Domain.Plataforma.EstadoBootstrapPlataforma.Designar(candidato.Id, DateTime.UtcNow));

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Identidad raíz de plataforma designada: {UsuarioId}. A partir de aquí la configuración " +
                "AdministradorInicial:Email deja de determinarla.", candidato.Id);
            return;
        }

        if (await userManager.FindByIdAsync(estado.UsuarioRaizId.ToString()) is null)
            throw new InvalidOperationException(
                $"La identidad raíz de plataforma persistida ({estado.UsuarioRaizId}) ya no existe. " +
                "El bootstrap no se reasigna automáticamente: recuperarla es un procedimiento " +
                "administrativo explícito, fuera de la aplicación.");

        if (estado.UsuarioRaizId != candidato.Id)
            logger.LogWarning(
                "AdministradorInicial:Email apunta a {UsuarioConfigurado}, pero la identidad raíz de " +
                "plataforma es {UsuarioRaiz} y no se reasigna. La configuración solo designa la raíz " +
                "mientras está sin fijar.", candidato.Id, estado.UsuarioRaizId);
    }
}
