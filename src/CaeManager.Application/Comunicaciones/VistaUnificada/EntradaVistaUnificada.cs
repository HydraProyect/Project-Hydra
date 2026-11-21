using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Usuarios;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.VistaUnificada;

/// <summary>
/// La entrada y la lectura por Tenant que comparten todas las consultas de la vista unificada de Comunicaciones (D-6 b,
/// § 4.2 y § 4.3): la lista (<see cref="ObtenerConversacionesUnificadasQuery"/>) y el selector de Tenants beneficiarios
/// con sus recuentos (<see cref="ObtenerTenantsBeneficiariosVistaUnificadaQuery"/>). Estar en un solo sitio es lo que
/// garantiza que el selector no ofrezca un Tenant que la lista no leería, y que el recuento de un Tenant cuente lo mismo
/// que la lista filtrada por él.
/// </summary>
public static class EntradaVistaUnificada
{
    /// <summary>
    /// Exclusiones de plano (§ 4.2), conjunto de la vista (<see cref="PredicadoVistaUnificada.ConjuntoAsync"/>), lente de
    /// demo y, si llega, el filtro por Tenant beneficiario. Las exclusiones las rechaza el propio handler que llama aquí:
    /// la vista no existe con Sesión Privilegiada seleccionada, con papel Inbound (DN-1 (a): no se sirve una mezcla), para
    /// el Usuario de Cliente ni para el Soporte TALVEG (Tenant de origen de plataforma). Sin usuario o sin Tenant de
    /// origen, tampoco. El Usuario simulado no existe hoy: su guarda es futura y no está aquí.
    /// </summary>
    /// <param name="filtroTenantIds">Coordenada del navegador; se interseca (<see cref="Intersecar"/>), nunca autoriza.</param>
    public static async Task<(IdentidadVistaUnificada Identidad, IReadOnlyList<Guid> Conjunto)> AbrirAsync(
        IReadOnlyList<Guid>? filtroTenantIds, ICurrentUserService currentUserService,
        IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
        ICoordenadaSesionPrivilegiada coordenadaSesionPrivilegiada, IVistaDemoActual? vistaDemo,
        CancellationToken cancellationToken)
    {
        var identidad = await RechazarPlanosExcluidosAsync(
            currentUserService, tenants, coordenadaSesionPrivilegiada, cancellationToken);
        var conjunto = await PredicadoVistaUnificada.ConjuntoAsync(
            identidad, operaciones, tenants, DateTime.UtcNow, cancellationToken);
        conjunto = await SoloLosDeLaLenteDeDemoAsync(conjunto, identidad, vistaDemo, cancellationToken);
        return (identidad, Intersecar(conjunto, filtroTenantIds));
    }

    /// <summary>
    /// El filtro por Tenant beneficiario es una coordenada, nunca autoridad (§ 4.1): solo estrecha el conjunto calculado en
    /// el servidor y conserva su orden. Un Tenant de fuera se descarta sin distinguirlo de uno inexistente; <c>null</c> o
    /// vacío, el conjunto entero.
    /// </summary>
    public static IReadOnlyList<Guid> Intersecar(IReadOnlyList<Guid> conjunto, IReadOnlyList<Guid>? filtroTenantIds) =>
        filtroTenantIds is { Count: > 0 } filtro ? conjunto.Where(filtro.Contains).ToList() : conjunto;

    /// <summary>El nombre de cada Tenant del conjunto (<c>Tenants.Nombre</c>), rótulo «Empresa gestionada» (DN-8).</summary>
    public static async Task<Dictionary<Guid, string>> NombresAsync(
        ITenantsQueryContext tenants, IReadOnlyList<Guid> conjunto, CancellationToken cancellationToken) =>
        conjunto.Count == 0
            ? new Dictionary<Guid, string>()
            : await tenants.Tenants
                .Where(t => conjunto.Contains(t.Id))
                .Select(t => new { t.Id, t.Nombre })
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

    /// <summary>
    /// Las conversaciones que la vista lee de un Tenant, dentro de su <see cref="AmbitoTenantAutorizado"/> y bajo la RLS de
    /// ese Tenant. Segunda línea (§ 4.3, M1): fuera del Tenant de origen, solo si la persona entra por una Asignación de
    /// Operación; en el de origen, solo con rol efectivo. Si no, <c>null</c>: el Tenant queda como no consultado, nunca
    /// como vacío. Después, la regla de visibilidad de la lista de un Tenant (<see cref="ConversacionesVisibles.FiltrarAsync"/>,
    /// R-3a), la cota <c>T0</c> y, si llega, el grupo de estados (DN-O), todo sumado con AND: ningún filtro amplía.
    /// La consulta queda sin ejecutar y ligada al ámbito: quien la ejecuta vuelve a llamar a
    /// <see cref="AmbitoTenantAutorizado.ExigirAbierto"/> justo antes, dentro del mismo callback.
    /// </summary>
    public static async Task<IQueryable<Conversacion>?> ConversacionesDelTenantAsync(
        AmbitoTenantAutorizado ambito, bool esOrigen, DateTime cota, GrupoEstadoConversacion? grupoEstado,
        IComunicacionesQueryContext comunicacionesContext, IIntegracionesQueryContext integracionesContext,
        IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, ITenantActual tenantActual,
        CancellationToken cancellationToken)
    {
        ambito.ExigirAbierto();
        var autorizaDentro = esOrigen
            ? await currentUserService.ObtenerRolEfectivoAsync() is not null
            : currentUserService is IEncargoDeAdministracionActual senal && await senal.OperaPorAsignacionDeOperacionAsync();
        if (!autorizaDentro)
            return null;

        var consulta = await ConversacionesVisibles.FiltrarAsync(
            comunicacionesContext.Conversaciones, alcanceDatos, currentUserService, integracionesContext, tenantActual,
            cancellationToken);
        consulta = consulta.Where(c => c.FechaUltimoMensajeUtc <= cota);
        if (grupoEstado is { } grupo)
        {
            var estadosDelGrupo = GruposEstadoConversacion.Estados(grupo);
            consulta = consulta.Where(c => estadosDelGrupo.Contains(c.Estado));
        }
        return consulta;
    }

    private static async Task<IdentidadVistaUnificada> RechazarPlanosExcluidosAsync(
        ICurrentUserService currentUserService, ITenantsQueryContext tenants,
        ICoordenadaSesionPrivilegiada coordenadaSesionPrivilegiada, CancellationToken cancellationToken)
    {
        if (coordenadaSesionPrivilegiada.HaySeleccionada)
            throw new UnauthorizedAccessException("La vista unificada de Comunicaciones no se abre con una Sesión Privilegiada.");
        if (await currentUserService.ObtenerPapelActivoAsync() == PapelActivo.Inbound)
            throw new UnauthorizedAccessException("La vista unificada de Comunicaciones es solo de Outbound.");

        var identidad = await IdentidadVistaUnificada.ResolverAsync(currentUserService)
                        ?? throw new UnauthorizedAccessException("La vista unificada de Comunicaciones necesita una sesión de usuario.");
        if (identidad.RolDeOrigen == AutoridadSobreCuentas.RolCliente)
            throw new UnauthorizedAccessException("La vista unificada de Comunicaciones no es del Usuario de Cliente.");
        if (await tenants.Tenants.AnyAsync(t => t.Id == identidad.TenantOrigenId && t.EsPlataforma, cancellationToken))
            throw new UnauthorizedAccessException("La vista unificada de Comunicaciones no es del Soporte TALVEG.");
        return identidad;
    }

    /// <summary>
    /// Con la lente de demo Gestor (<see cref="IVistaDemoActual"/>), solo el origen y los Tenants donde el Gestor CAE
    /// simulado tiene cartera: la lente solo estrecha, igual que en el selector de la cabecera.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SoloLosDeLaLenteDeDemoAsync(
        IReadOnlyList<Guid> conjunto, IdentidadVistaUnificada identidad, IVistaDemoActual? vistaDemo,
        CancellationToken cancellationToken)
    {
        var acotados = vistaDemo is null ? null : await vistaDemo.ObtenerTenantIdsAcotadosAsync(cancellationToken);
        return acotados is null
            ? conjunto
            : conjunto.Where(t => t == identidad.TenantOrigenId || acotados.Contains(t)).ToList();
    }
}
