using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    public const string ContrasenaAdministradorSegundoTenant = "SegundoTenant#2026";
    public const string NombreSegundoTenant = "Tenant de verificación B";

    public static async Task<Guid?> SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("SegundoTenant:Activo"))
            return null;

        var tenantExistente = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Nombre == NombreSegundoTenant, cancellationToken);

        Guid tenantId;
        if (tenantExistente is null)
        {
            var tenant = new Tenant(NombreSegundoTenant);
            tenantId = tenant.Id;

            // AuditoriaInterceptor registra un RegistroAuditoria (EntidadConTenant)
            // por cada SaveChanges — hace falta un tenant resuelto ya para este
            // primer guardado, antes incluso de que el propio Tenant exista en la
            // base de datos. El Id se genera en el constructor (ver Entity), así
            // que ya se conoce aquí.
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                dbContext.Tenants.Add(tenant);
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

            var resultado = await userManager.CreateAsync(administrador, ContrasenaAdministradorSegundoTenant);
            if (resultado.Succeeded)
            {
                await userManager.AddToRoleAsync(administrador, Roles.Administrador);
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
