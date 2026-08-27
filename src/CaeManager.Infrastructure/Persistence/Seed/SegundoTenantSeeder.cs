using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Sembrado opcional de un segundo tenant — exclusivamente para poder
/// verificar en desarrollo/E2E que el aislamiento multi-tenant funciona de
/// extremo a extremo con un navegador real, no solo en tests de integración
/// (ver PLAN-MIGRACION-MULTITENANT.md § 6, Etapa 5). Apagado por defecto,
/// mismo principio "inerte por defecto" que <see cref="DatosPruebaSeeder"/>
/// — nunca se ejecuta salvo que <c>SegundoTenant:Activo</c> sea true.
/// </summary>
public static class SegundoTenantSeeder
{
    public const string EmailAdministradorSegundoTenant = "admin-segundo-tenant@caemanager.local";
    /// <summary>
    /// Valor por defecto fuera de Producción. En Producción tiene que venir
    /// de <c>SegundoTenant:Contrasena</c> o la siembra falla — mismo motivo
    /// que en <see cref="CredencialesDemo"/>: es una constante de un
    /// repositorio público, y eso solo es inocuo mientras no abra una sesión
    /// en un servidor público.
    /// </summary>
    public const string ContrasenaAdministradorSegundoTenant = "SegundoTenant#2026";

    public const string ClaveContrasenaConfiguracion = "SegundoTenant:Contrasena";
    public const string NombreSegundoTenant = "Tenant de verificación B";

    public static async Task<Guid?> SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("SegundoTenant:Activo"))
            return null;

        var contrasena = CredencialesDemo.ResolverCredencial(
            configuration, entorno, ClaveContrasenaConfiguracion, ContrasenaAdministradorSegundoTenant);

        var tenantExistente = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Nombre == NombreSegundoTenant, cancellationToken);

        Guid tenantId;
        if (tenantExistente is null)
        {
            var tenant = new Tenant(NombreSegundoTenant, PerfilVocabularioTenant.ClienteDirecto);
            tenantId = tenant.Id;

            // AuditoriaInterceptor registra un RegistroAuditoria (EntidadConTenant)
            // por cada SaveChanges — hace falta un tenant resuelto ya para este
            // primer guardado, antes incluso de que el propio Tenant exista en la
            // base de datos. El Id se genera en el constructor (ver Entity), así
            // que ya se conoce aquí.
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                dbContext.Tenants.Add(tenant);

                // Todo tenant necesita su fila de ParametroSistema y su copia
                // del catálogo de TipoDocumento — mismo motivo que en
                // DelegacionDemoSeeder (queries con SingleAsync() y el filtro
                // global de tenant). Este seeder no las creaba y cualquier
                // query de parámetros reventaba al entrar con este tenant.
                dbContext.ParametrosSistema.Add(new ParametroSistema(
                    ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));
                dbContext.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("Segundo tenant de verificación sembrado: {TenantId}.", tenantId);
        }
        else
        {
            tenantId = tenantExistente.Id;
        }

        if (await userManager.FindByEmailAsync(EmailAdministradorSegundoTenant) is not null)
            return tenantId;

        // Este usuario pertenece al segundo tenant — el ámbito explícito
        // asegura que, si alguna vez este alta empezara a escribir entidades
        // de dominio (hoy solo crea el ApplicationUser, que no pasa por el
        // interceptor), quedarían selladas al tenant correcto.
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var administrador = new ApplicationUser
            {
                UserName = EmailAdministradorSegundoTenant,
                Email = EmailAdministradorSegundoTenant,
                NombreCompleto = "Administrador (segundo tenant, solo verificación)",
                EmailConfirmed = true,
                DebeCambiarContrasena = false,
                TenantId = tenantId,
            };

            var resultado = await userManager.CreateAsync(administrador, contrasena);
            if (resultado.Succeeded)
            {
                await userManager.AddToRoleAsync(administrador, Roles.Administrador);

                // Mismo motivo que IdentitySeeder: P1-13 de
                // docs/business/MATURITY_REVIEW.md exige 2FA para todo
                // Administrador, así que este también nace con ella activa
                // (misma clave fija, reutilizada por Ayudas.IniciarSesionAsync
                // en el proyecto E2E).
                if (userStore is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
                {
                    await claveStore.SetAuthenticatorKeyAsync(
                        administrador, IdentitySeeder.ClaveTotpAdministradorInicial, cancellationToken);
                    await userManager.UpdateAsync(administrador);
                }
                await userManager.SetTwoFactorEnabledAsync(administrador, true);
                await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, administrador.Id, cancellationToken);

                logger.LogInformation("Administrador del segundo tenant de verificación sembrado.");
            }
            else
            {
                logger.LogError(
                    "No se pudo crear el administrador del segundo tenant de verificación: {Errores}",
                    string.Join(", ", resultado.Errors.Select(e => e.Description)));
            }
        }

        return tenantId;
    }
}
