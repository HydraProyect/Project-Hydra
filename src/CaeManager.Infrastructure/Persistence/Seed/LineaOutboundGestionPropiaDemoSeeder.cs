using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
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
/// <b>Para qué.</b> Sin backfill en producción (DECISION_LOG 2026-11-17, opción (c), decisión de Chris), un Tenant sin
/// el marcador de TEC participante y sin línea P es «Outbound heredado». Inbound (EP-4) tratará a un Tenant TEC participante sin P vigente como «sin Outbound»: por
/// eso los Tenants de demostración de gestión propia tienen que llevarla, y los demás no.
/// </para>
///
/// <para>
/// <b>Por qué con bootstrap.</b> En <c>ProductosContratados</c> cae_app_runtime y cae_app_soporte solo leen y el único rol de
/// la aplicación que inserta, cae_app_aprovisionamiento, solo contrata la línea Inbound bajo una Sesión Privilegiada de
/// Aprovisionamiento (<c>ContratarLineaInboundCommand</c>); una siembra de arranque no la tiene. <b>La identidad de bootstrap
/// para la línea P es una deducción, no una decisión registrada</b>: se deduce del precedente de la línea Inbound de
/// demostración (DECISION_LOG 2026-11-16, «identidad de la siembra de la línea Inbound»), que tiene la misma razón; la
/// decisión de Chris del 2026-11-17 es la opción (c), sin backfill y solo sembrada en Development.
/// </para>
///
/// <para>
/// <b>Qué Tenants.</b> Lista cerrada, <see cref="NombresTenantsDeGestionPropia"/>. Hoy solo el Tenant Beta de la siembra de
/// cobertura: es el único Tenant de las siembras de Development que, sin ser de plataforma, Operador CAE externo, Tenant
/// propietario con gestión externa ni Inbound, gestiona su propia CAE (no tiene <c>DelegacionTenant</c> ni Asignación de
/// Operación externa). La tabla completa de Tenants sembrados y por qué cada uno entra o no está en la PR que lo introdujo.
/// Un Tenant nuevo de demostración de gestión propia se añade aquí a mano; ninguno se deduce de su perfil de vocabulario, que
/// dice cómo se ve el Tenant a sí mismo y no quién gestiona su CAE.
/// </para>
///
/// <para>
/// <b>Guarda de gestión propia.</b> La lista no basta: un Tenant de la lista se omite, con un aviso en el log, si en la base
/// resulta ser de plataforma, estar habilitado como Operador CAE externo, tener una <c>DelegacionTenant</c> activa con
/// propósito de Operador CAE externo como Tenant propietario o tener una Asignación de Operación Outbound externa, abierta o
/// cerrada (ver <see cref="MotivoParaOmitir"/>). Es la protección contra un Tenant de la lista cuyo perfil cambió después
/// de redactarla. Un Tenant de la lista sin el marcador de datos de demo también se omite con un aviso, no en silencio.
/// </para>
///
/// <para>
/// <b>Lo que acota la salida de RLS.</b> Solo con <c>DatosPrueba:Activo</c> y solo en Development: en cualquier otro entorno
/// no hace nada (no lanza, porque <c>DatosPrueba:Activo</c> es legítimo en Staging); solo sobre Tenants de la lista, localizados
/// por su nombre y con el marcador de datos de demo (uno que se llamara igual sin él no se toca); una fila por Tenant, dentro
/// del <see cref="AmbitoTenantExplicito"/> de ese Tenant, que la sella (y dentro del cual se hacen las dos lecturas de la guarda,
/// sobre las filas del propio Tenant); e idempotente: si el Tenant ya tiene una línea P, vigente o dada de baja, no escribe
/// nada. Si la siembra que crea el Tenant no está activa, el Tenant no existe y no se escribe nada.
/// </para>
/// </summary>
public static class LineaOutboundGestionPropiaDemoSeeder
{
    /// <summary>
    /// Customer de Stripe de demostración del Pagador TALVEG de esta línea: no existe en Stripe y nada lo cobra. Dato de
    /// demostración, no el de ningún Pagador TALVEG real.
    /// </summary>
    public const string PagadorDemo = "cus_DemoOutboundGestionPropia";

    /// <summary>
    /// Los Tenants de demostración que gestionan su CAE con su propio personal. Constantes, que se incrustan al compilar.
    /// </summary>
    public static readonly IReadOnlyList<string> NombresTenantsDeGestionPropia = [CoberturaPantallasSeeder.NombreTenantBeta];

    /// <summary>
    /// Por qué un Tenant de la lista no gestiona su CAE con su propio personal, o <c>null</c> si nada lo contradice. Cada
    /// entrada es una de las cuatro formas en que un Tenant deja de ser de gestión propia: es el Tenant de plataforma, está
    /// habilitado como Operador CAE externo, un Operador CAE externo lo opera por una delegación activa o tiene una Asignación
    /// de Operación externa.
    /// </summary>
    public static string? MotivoParaOmitir(
        bool esPlataforma, bool puedeActuarComoOperadorCaeExterno, bool tieneOperadorCaeExternoActivo,
        bool tieneAsignacionDeOperacionExterna)
    {
        if (esPlataforma)
            return "es el Tenant de plataforma";
        if (puedeActuarComoOperadorCaeExterno)
            return "está habilitado como Operador CAE externo";
        if (tieneOperadorCaeExternoActivo)
            return "tiene una delegación activa con propósito de Operador CAE externo (Tenant propietario con gestión externa)";
        if (tieneAsignacionDeOperacionExterna)
            return "tiene una Asignación de Operación Outbound externa (Tenant propietario con gestión externa)";
        return null;
    }

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
            .Where(t => NombresTenantsDeGestionPropia.Contains(t.Nombre))
            .Select(t => new { t.Id, t.Nombre, t.DatosDemoCompletadosEnUtc, t.EsPlataforma, t.PuedeActuarComoOperadorCaeExterno })
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            if (tenant.DatosDemoCompletadosEnUtc is null)
            {
                logger.LogWarning(
                    "El Tenant {Tenant} de la lista de gestión propia no tiene el marcador de datos de demo: no se le contrata la línea Outbound de gestión propia.",
                    tenant.Nombre);
                continue;
            }

            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                // Las dos lecturas de la guarda, filtradas a mano por el propio Tenant propietario y dentro de su ámbito (como
                // HistoriaDelParEnSiembra). Estas dos tablas tienen RLS sin FORCE, así que el rol propietario no la aplica y el
                // ámbito no cambia el resultado: no hay falso negativo por RLS (el propietario ve más filas, no menos).
                // Una delegación solo cuenta si está activa; una operación externa cuenta aunque esté cerrada, porque ya hubo
                // gestión externa.
                var tieneOperadorExterno = await dbContextBootstrap.DelegacionesTenant.AnyAsync(
                    d => d.TenantClienteId == tenant.Id && d.Proposito == PropositoDelegacion.OperadorExterno && d.Activa,
                    cancellationToken);
                var tieneOperacionExterna = await dbContextBootstrap.AsignacionesOperacion.AnyAsync(
                    o => !o.EsRaiz && o.PropietarioTenantId == tenant.Id && o.OperadorTenantId != tenant.Id
                         && o.Servicio == ServicioCae.Outbound, cancellationToken);

                var motivo = MotivoParaOmitir(
                    tenant.EsPlataforma, tenant.PuedeActuarComoOperadorCaeExterno, tieneOperadorExterno, tieneOperacionExterna);
                if (motivo is not null)
                {
                    logger.LogWarning(
                        "El Tenant {Tenant} de la lista de gestión propia {Motivo}: no gestiona su CAE con su propio personal y no se le contrata la línea Outbound de gestión propia.",
                        tenant.Nombre, motivo);
                    continue;
                }

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
                // shortcut: la idempotencia es leer y luego escribir, sin índice único de la línea P ni pg_advisory_xact_lock
                // por Tenant: dos arranques de Development que coincidieran sobre la misma base podrían insertar dos líneas P.
                // Se acepta porque Development arranca de uno en uno, y porque el bloqueo exigiría una transacción propia con SQL
                // de PostgreSQL dentro de la estrategia de reintento y el índice, una migración que esta PR no lleva. Si dos
                // arranques concurrentes llegaran a ser reales, el índice único parcial de la línea P por Tenant beneficiario es
                // la corrección, no este bloqueo.
                dbContextBootstrap.ProductosContratados.Add(ProductoContratado.Contratar(
                    LineaProductoContratado.OutboundGestionPropia, PagadorDemo, TramoTrabajadoresActivos.De1A10,
                    ModalidadSuscripcionAlta.Anual, vigenteDesdeUtc: ahora.AddDays(-1), ahoraUtc: ahora));
                await dbContextBootstrap.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("Línea Outbound de gestión propia de demostración contratada para {Tenant}.", tenant.Nombre);
        }
    }
}
