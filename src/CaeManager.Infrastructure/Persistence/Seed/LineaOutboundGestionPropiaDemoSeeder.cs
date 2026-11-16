using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La línea <see cref="LineaProductoContratado.OutboundGestionPropia"/> (P) del Producto Contratado de los Tenants de
/// demostración que gestionan su CAE con su propio personal (<c>TipoTenant.GestionPropia</c>), escrita con el contexto de
/// <b>bootstrap</b> (rol propietario) y no con el de tráfico, igual que <see cref="LineaInboundDemoSeeder"/>.
///
/// <para>
/// <b>Para qué.</b> Sin backfill en producción (opción (c), decisión de la coordinadora superior del 2026-11-16), un Tenant sin
/// línea P es «Outbound heredado». Inbound (EP-4) tratará a un Tenant TEC participante sin P vigente como «sin Outbound»: por
/// eso los Tenants de demostración de gestión propia tienen que llevarla, y los demás no.
/// </para>
///
/// <para>
/// <b>Por qué con bootstrap.</b> En <c>ProductosContratados</c> cae_app_runtime y cae_app_soporte solo leen y el único rol de
/// la aplicación que inserta, cae_app_aprovisionamiento, solo contrata la línea Inbound bajo una Sesión Privilegiada de
/// Aprovisionamiento (<c>ContratarLineaInboundCommand</c>); una siembra de arranque no la tiene. Misma identidad que la línea
/// Inbound de demostración y misma razón (DECISION_LOG 2026-11-16, «identidad de la siembra de la línea Inbound»).
/// </para>
///
/// <para>
/// <b>Qué Tenants.</b> Lista cerrada, <see cref="NombresTenantsDeGestionPropia"/>. Hoy solo el Tenant Beta de la siembra de
/// cobertura: es el único Tenant de las siembras de Development que, sin ser de plataforma, Operador CAE externo, cliente
/// delegante ni Inbound, gestiona su propia CAE (no tiene <c>DelegacionTenant</c> ni Asignación de Operación externa). La
/// tabla completa de Tenants sembrados y por qué cada uno entra o no está en la PR que lo introdujo. Un Tenant nuevo de
/// demostración de gestión propia se añade aquí a mano; ninguno se deduce de su perfil de vocabulario, que dice cómo se
/// ve el Tenant a sí mismo y no quién gestiona su CAE.
/// </para>
///
/// <para>
/// <b>Lo que acota la salida de RLS.</b> Solo con <c>DatosPrueba:Activo</c> y solo en Development: en cualquier otro entorno
/// no hace nada (no lanza, porque <c>DatosPrueba:Activo</c> es legítimo en Staging); solo sobre Tenants de la lista, localizados
/// por su nombre y con el marcador de datos de demo (uno que se llamara igual sin él no se toca); una fila por Tenant, dentro
/// del <see cref="AmbitoTenantExplicito"/> de ese Tenant, que la sella; e idempotente: si el Tenant ya tiene una línea P, vigente
/// o dada de baja, no escribe nada. Si la siembra que crea el Tenant no está activa, el Tenant no existe y no se escribe nada.
/// </para>
/// </summary>
public static class LineaOutboundGestionPropiaDemoSeeder
{
    /// <summary>
    /// Customer de Stripe de demostración del Pagador TALVEG de esta línea: no existe en Stripe y nada lo cobra. Dato de
    /// demostración, no el de ningún cliente.
    /// </summary>
    public const string PagadorDemo = "cus_DemoOutboundGestionPropia";

    /// <summary>
    /// Los Tenants de demostración que gestionan su CAE con su propio personal. Constantes, que se incrustan al compilar.
    /// </summary>
    public static readonly IReadOnlyList<string> NombresTenantsDeGestionPropia = [CoberturaPantallasSeeder.NombreTenantBeta];

    public static async Task SeedAsync(
        CaeManagerDbContext dbContextBootstrap,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo") || !entorno.IsDevelopment())
            return;

        var tenants = await dbContextBootstrap.Tenants
            .Where(t => NombresTenantsDeGestionPropia.Contains(t.Nombre) && t.DatosDemoCompletadosEnUtc != null)
            .Select(t => new { t.Id, t.Nombre })
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                // Cualquier línea P, también una dada de baja: si un Actor de Plataforma la finalizó para revisar una
                // pantalla, otro arranque no la vuelve a contratar por su cuenta.
                var yaContratada = await dbContextBootstrap.ProductosContratados.AnyAsync(
                    p => p.TenantId == tenant.Id && p.Linea == LineaProductoContratado.OutboundGestionPropia, cancellationToken);
                if (yaContratada)
                    continue;

                // Tramo, modalidad y Pagador TALVEG: datos de demostración, los mismos que LineaInboundDemoSeeder.
                // Vigente desde ayer: la base compara con su propio reloj, que en Docker o WSL puede ir por detrás del de la
                // aplicación; con un inicio de ahora mismo, la línea no valdría hasta que se pusieran de acuerdo.
                var ahora = DateTime.UtcNow;
                dbContextBootstrap.ProductosContratados.Add(ProductoContratado.Contratar(
                    LineaProductoContratado.OutboundGestionPropia, PagadorDemo, TramoTrabajadoresActivos.De1A10,
                    ModalidadSuscripcionAlta.Anual, vigenteDesdeUtc: ahora.AddDays(-1), ahoraUtc: ahora));
                await dbContextBootstrap.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("Línea Outbound de gestión propia de demostración contratada para {Tenant}.", tenant.Nombre);
        }
    }
}
