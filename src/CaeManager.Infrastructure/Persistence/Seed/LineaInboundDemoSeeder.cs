using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La línea Inbound del Producto Contratado del Tenant TCE de <see cref="InboundDemoSeeder"/>, la única fila de esa siembra
/// que se escribe con el contexto de <b>bootstrap</b> (rol propietario) y no con el de tráfico.
///
/// <para>
/// <b>Por qué con bootstrap</b> (decisión de Chris, DECISION_LOG 2026-11-16, «identidad de la siembra de la línea Inbound»):
/// en <c>ProductosContratados</c> cae_app_runtime y cae_app_soporte solo leen, y el único rol de la aplicación que inserta,
/// cae_app_aprovisionamiento, lo hace bajo una Sesión Privilegiada de Aprovisionamiento abierta por un Actor de Plataforma
/// TALVEG (<c>ContratarLineaInboundCommand</c>, B-04a). Una siembra de arranque no tiene esa sesión, así que se escribe como
/// la escriben los tests de integración: con el rol propietario y el Tenant del contexto. Sin esta fila, el gate DN-7 niega
/// al Tenant TCE toda lectura entre Tenants.
/// </para>
///
/// <para>
/// <b>Lo que acota la salida de RLS.</b> Solo en Development y con las mismas claves que <see cref="InboundDemoSeeder"/>;
/// solo sobre el Tenant que esa siembra aprovisiona, localizado por su nombre y con el marcador de datos de demo (un Tenant
/// real que se llamara igual no se toca); una sola fila, la línea Inbound vigente, dentro del
/// <see cref="AmbitoTenantExplicito"/> de ese Tenant, que la sella; e idempotente: si el Tenant ya tiene una línea Inbound
/// vigente o dada de baja no escribe nada. Aparte de localizar el Tenant, no lee ni escribe ninguna otra tabla (salvo el
/// registro de auditoría que el interceptor añade a toda escritura) ni ningún otro Tenant.
/// </para>
///
/// <para>
/// Las referencias a <see cref="InboundDemoSeeder"/> son constantes, que se incrustan al compilar: este tipo es del Núcleo
/// (plano Comercial) y no depende de la siembra Inbound, que es Composición.
/// </para>
/// </summary>
public static class LineaInboundDemoSeeder
{
    /// <summary>Customer de Stripe de demostración del Pagador TALVEG: no existe en Stripe y nada lo cobra.</summary>
    public const string PagadorDemo = "cus_DemoInboundLogisticaDeltaSur";

    public static async Task SeedAsync(
        CaeManagerDbContext dbContextBootstrap,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo") || !configuration.GetValue<bool>(InboundDemoSeeder.ClaveConfiguracion))
            return;

        if (!entorno.IsDevelopment())
            throw new InvalidOperationException(
                $"{InboundDemoSeeder.ClaveConfiguracion} solo puede activarse en Development (entorno actual: {entorno.EnvironmentName}): " +
                "la línea Inbound de demostración se escribe con el rol propietario.");

        var tenant = await dbContextBootstrap.Tenants
            .Where(t => t.Nombre == InboundDemoSeeder.NombreTenantTce)
            .Select(t => new { t.Id, t.DatosDemoCompletadosEnUtc })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"La línea Inbound de demostración necesita el Tenant «{InboundDemoSeeder.NombreTenantTce}», que siembra InboundDemoSeeder antes.");

        if (tenant.DatosDemoCompletadosEnUtc is null)
            throw new InvalidOperationException(
                $"El Tenant «{InboundDemoSeeder.NombreTenantTce}» no tiene el marcador de datos de demo: no se le contrata la línea Inbound.");

        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            // Cualquier línea Inbound, también una dada de baja: si un Actor de Plataforma la finalizó para revisar esa
            // pantalla, otro arranque no la vuelve a contratar por su cuenta.
            var yaContratada = await dbContextBootstrap.ProductosContratados.AnyAsync(
                p => p.TenantId == tenant.Id && p.Linea == LineaProductoContratado.Inbound, cancellationToken);
            if (yaContratada)
                return;

            // Vigente desde ayer: el gate DN-7 compara con el reloj de PostgreSQL, que en Docker o WSL puede ir por detrás del
            // de la aplicación; con un inicio de ahora mismo, la línea no valdría hasta que se pusieran de acuerdo.
            var ahora = DateTime.UtcNow;
            dbContextBootstrap.ProductosContratados.Add(ProductoContratado.Contratar(
                LineaProductoContratado.Inbound, PagadorDemo, TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual,
                vigenteDesdeUtc: ahora.AddDays(-1), ahoraUtc: ahora));
            await dbContextBootstrap.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Línea Inbound de demostración contratada para {Tenant}.", InboundDemoSeeder.NombreTenantTce);
    }
}
