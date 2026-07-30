using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra el escenario de demo de ADR-004-delegacion-consultoras-cae.md: el
/// tenant #1 (<see cref="TenantSeedData.IdPorDefecto"/>) pasa a jugar el
/// papel de Consultora — sin datos operativos propios (ADR-004 § 5.1) — y
/// todos los datos de prueba de CAE (<see cref="DatosPruebaSeeder"/>) se
/// siembran en un tenant Cliente Delegante nuevo, unido al tenant #1 por una
/// <see cref="DelegacionTenant"/> activa y una
/// <see cref="AsignacionOperadorDelegado"/> para el Administrador inicial
/// (<see cref="IdentitySeeder"/>) — así el selector "Cliente activo" tiene
/// algo real que mostrar nada más arrancar en desarrollo.
///
/// Sustituye la siembra anterior, que ponía los 200 clientes/etc. de prueba
/// directamente en el tenant #1: esos datos eran siempre de prueba, nunca
/// datos reales, así que no hace falta preservarlos ni migrarlos — decisión
/// explícita para esta ronda (ver DESIGN_SYSTEM.md/ROADMAP.md). Sigue abierta
/// (ADR-004 § 12.6) la pregunta de qué pasa con el tenant #1 real de
/// producción, que no encaja tal cual en "Consultora sin datos propios" —
/// esta siembra es exclusivamente para desarrollo/demo, no toca ningún
/// entorno desplegado.
///
/// Apagado por defecto, mismo principio "inerte por defecto" que
/// <see cref="SegundoTenantSeeder"/> — corre exactamente cuando
/// <c>DatosPrueba:Activo</c> es true (sustituye la llamada directa a
/// <see cref="DatosPruebaSeeder"/> en Program.cs).
/// </summary>
public static class DelegacionDemoSeeder
{
    public const string NombreTenantClienteDemo = "Ibertec S.A. (Cliente Delegante demo)";
    public const string RolOperadorDelegadoDemo = "GestorCae";

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
            return;

        var tenantClienteExistente = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Nombre == NombreTenantClienteDemo, cancellationToken);

        Guid tenantClienteId;
        if (tenantClienteExistente is null)
        {
            var tenantCliente = new Tenant(NombreTenantClienteDemo);
            tenantClienteId = tenantCliente.Id;

            // Mismo motivo que SegundoTenantSeeder: hace falta un tenant
            // resuelto ya para este primer guardado (el interceptor de
            // auditoría necesita sellar contra algo), antes incluso de que
            // el propio Tenant exista en la base de datos — el Id ya se
            // conoce porque se genera en el constructor (ver Entity).
            using (AmbitoTenantExplicito.Establecer(tenantClienteId))
            {
                dbContext.Tenants.Add(tenantCliente);

                // Todo tenant necesita su propia fila de ParametroSistema —
                // ObtenerKpisDashboardQuery/ObtenerDesgloseDashboardQuery la
                // leen con SingleAsync() y fallan si no existe ninguna. Al
                // tenant #1 se la da un HasData de migración; un tenant
                // creado en tiempo de ejecución (como este) tiene que
                // sembrarla explícitamente — mismos umbrales por defecto que
                // ParametroSistemaSeedData.
                dbContext.ParametrosSistema.Add(new ParametroSistema(
                    ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("Tenant Cliente Delegante de demo sembrado: {TenantId}.", tenantClienteId);
        }
        else
        {
            tenantClienteId = tenantClienteExistente.Id;
        }

        // Todos los datos operativos de prueba (clientes, empresas, centros,
        // trabajadores, documentos, usuarios prueba.<rol><n>@...) se siembran
        // dentro del tenant Cliente Delegante, nunca en el tenant #1 — ver
        // ADR-004 § 5.1, "la Consultora es un Tenant sin datos operativos
        // propios".
        using (AmbitoTenantExplicito.Establecer(tenantClienteId))
        {
            await DatosPruebaSeeder.SeedAsync(dbContext, userManager, configuration, logger, cancellationToken);
        }

        if (await dbContext.DelegacionesTenant.AnyAsync(
                d => d.TenantConsultoraId == TenantSeedData.IdPorDefecto && d.TenantClienteId == tenantClienteId,
                cancellationToken))
        {
            return;
        }

        var delegacion = new DelegacionTenant(TenantSeedData.IdPorDefecto, tenantClienteId);

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            dbContext.DelegacionesTenant.Add(delegacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var emailAdministradorConsultora = configuration["AdministradorInicial:Email"] ?? IdentitySeeder.EmailAdministradorInicial;
        var administradorConsultora = await userManager.FindByEmailAsync(emailAdministradorConsultora);

        if (administradorConsultora is null)
        {
            logger.LogWarning(
                "No se encontró el Administrador inicial ({Email}) para asignarlo como Operador Delegado de demo.",
                emailAdministradorConsultora);
            return;
        }

        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, administradorConsultora.Id, RolOperadorDelegadoDemo);

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            dbContext.AsignacionesOperadorDelegado.Add(asignacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Delegated Workspace de demo sembrado: {Administrador} puede operar {TenantCliente} como {Rol}.",
            administradorConsultora.Email, NombreTenantClienteDemo, RolOperadorDelegadoDemo);
    }
}
