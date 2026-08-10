using CaeManager.Application.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Asegura que todo tenant tiene su delegación de soporte aprovisionada —
/// <b>apagada</b>— hacia el tenant de plataforma.
///
/// Se ejecuta al arrancar y es idempotente. Se resuelve así, y no como parte
/// de un alta de tenants, porque hoy no existe tal alta: los tenants se crean
/// por seeder, y el aprovisionamiento sigue siendo una condición de salida
/// abierta de ADR-003. Cuando exista, este mismo servicio se invoca desde
/// ahí y este barrido deja de encontrar nada que hacer.
///
/// Aprovisionar no concede nada: la delegación nace inactiva y abrirla exige
/// motivo y ventana (ver <c>DelegacionTenant.ActivarParaSoporte</c>). Lo que
/// se evita es tener que montar la relación con prisa cuando entra una queja.
/// </summary>
public static class DelegacionesSoporteSeeder
{
    public static async Task SeedAsync(
        CaeManagerDbContext dbContext, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        var tenantPlataforma = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.EsPlataforma, cancellationToken);

        if (tenantPlataforma is null)
        {
            // Sin tenant de plataforma marcado no hay a quién delegar. No es
            // un error: un despliegue puede no querer acceso de soporte.
            logger.LogInformation("Ningún tenant está marcado como plataforma — no se aprovisionan delegaciones de soporte.");
            return;
        }

        var tenantIds = await dbContext.Tenants
            .Where(t => !t.EsPlataforma)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (tenantIds.Count == 0) return;

        var yaAprovisionados = await dbContext.DelegacionesTenant
            .Where(d => d.TenantConsultoraId == tenantPlataforma.Id && d.Proposito == PropositoDelegacion.Soporte)
            .Select(d => d.TenantClienteId)
            .ToListAsync(cancellationToken);

        var pendientes = tenantIds.Except(yaAprovisionados).ToList();
        if (pendientes.Count > 0)
        {
            foreach (var tenantId in pendientes)
                dbContext.DelegacionesTenant.Add(DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantId));

            // DelegacionTenant es catálogo global (no extiende EntidadConTenant),
            // pero el interceptor de sellado exige un tenant resuelto para
            // cualquier guardado — al arrancar no hay sesión, así que se establece
            // explícitamente, mismo patrón que el resto de seeders.
            using (AmbitoTenantExplicito.Establecer(tenantPlataforma.Id))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation(
                "Aprovisionadas {Cantidad} delegaciones de soporte (inactivas) hacia el tenant de plataforma.", pendientes.Count);
        }

        // Variantes de demo (PLAN-DATOS-PRUEBA.md, Tanda 4): solo con la
        // siembra de datos de prueba activa. El aprovisionamiento anterior
        // sigue siendo "inactiva por defecto" para cualquier otro despliegue.
        if (configuration.GetValue<bool>("DatosPrueba:Activo"))
            await SembrarVariantesDemoAsync(dbContext, configuration, tenantPlataforma.Id, logger, cancellationToken);
    }

    /// <summary>
    /// Una ventana de soporte vigente (motivo + caducidad futura) sobre el
    /// tenant demo 1, una ya caducada sobre el demo 2 (activa pero no
    /// vigente — <c>EstaVigente</c> false), y la traza completa de una visita
    /// de soporte (los 5 tipos de actividad) en el tenant demo 1, que es el
    /// dueño del registro. Guards propios: solo actúa sobre delegaciones de
    /// soporte aún nunca activadas y si no hay actividad registrada.
    /// </summary>
    private static async Task SembrarVariantesDemoAsync(
        CaeManagerDbContext dbContext, IConfiguration configuration, Guid tenantPlataformaId,
        ILogger logger, CancellationToken cancellationToken)
    {
        var demo1 = await dbContext.Tenants.FirstOrDefaultAsync(
            t => t.Nombre == DelegacionDemoSeeder.NombreTenantClienteDemo, cancellationToken);
        var demo2 = await dbContext.Tenants.FirstOrDefaultAsync(
            t => t.Nombre == DelegacionDemoSeeder.NombreTenantClienteDemo2, cancellationToken);
        if (demo1 is null || demo2 is null)
            return;

        var soporteDemo1 = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
            d => d.TenantConsultoraId == tenantPlataformaId && d.TenantClienteId == demo1.Id
                 && d.Proposito == PropositoDelegacion.Soporte, cancellationToken);
        var soporteDemo2 = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
            d => d.TenantConsultoraId == tenantPlataformaId && d.TenantClienteId == demo2.Id
                 && d.Proposito == PropositoDelegacion.Soporte, cancellationToken);
        if (soporteDemo1 is null || soporteDemo2 is null)
            return;

        var ahora = DateTime.UtcNow;
        var huboCambios = false;

        if (!soporteDemo1.Activa && soporteDemo1.MotivoActivacion is null)
        {
            soporteDemo1.ActivarParaSoporte(
                "Incidencia DEMO-1234: documentos que no cargan en el listado.", ahora.AddHours(48), ahora);
            huboCambios = true;
        }

        if (!soporteDemo2.Activa && soporteDemo2.MotivoActivacion is null)
        {
            // Ventana abierta hace dos días y caducada ayer: queda Activa
            // pero no vigente — la variante que la pantalla /delegaciones
            // debe distinguir de una vigente.
            soporteDemo2.ActivarParaSoporte(
                "Incidencia DEMO-0987: error al exportar el reporte mensual.", ahora.AddDays(-1), ahora.AddDays(-2));
            huboCambios = true;
        }

        if (huboCambios)
        {
            using (AmbitoTenantExplicito.Establecer(tenantPlataformaId))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            logger.LogInformation("Ventanas de soporte de demo activadas (una vigente, una caducada).");
        }

        // Traza de una visita de soporte completa en el tenant visitado.
        var emailSoporte = configuration["AdministradorInicial:Email"] ?? IdentitySeeder.EmailAdministradorInicial;
        var usuarioSoporte = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == emailSoporte, cancellationToken);
        if (usuarioSoporte is null)
            return;

        using (AmbitoTenantExplicito.Establecer(demo1.Id))
        {
            if (await dbContext.RegistrosActividadSoporte.AnyAsync(cancellationToken))
                return;

            dbContext.RegistrosActividadSoporte.Add(new RegistroActividadSoporte(
                usuarioSoporte.Id, soporteDemo1.Id, TipoActividadSoporte.AccesoConcedido,
                "Incidencia DEMO-1234: documentos que no cargan en el listado."));
            dbContext.RegistrosActividadSoporte.Add(new RegistroActividadSoporte(
                usuarioSoporte.Id, soporteDemo1.Id, TipoActividadSoporte.WorkspaceActivado));
            dbContext.RegistrosActividadSoporte.Add(new RegistroActividadSoporte(
                usuarioSoporte.Id, soporteDemo1.Id, TipoActividadSoporte.Navegacion, "/documentos"));
            dbContext.RegistrosActividadSoporte.Add(new RegistroActividadSoporte(
                usuarioSoporte.Id, soporteDemo1.Id, TipoActividadSoporte.Interaccion, "Exportar listado de documentos"));
            dbContext.RegistrosActividadSoporte.Add(new RegistroActividadSoporte(
                usuarioSoporte.Id, soporteDemo1.Id, TipoActividadSoporte.AccesoRevocado,
                "Fin de la revisión — incidencia reproducida y documentada."));

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Traza de visita de soporte de demo sembrada en el tenant demo 1.");
        }
    }
}
