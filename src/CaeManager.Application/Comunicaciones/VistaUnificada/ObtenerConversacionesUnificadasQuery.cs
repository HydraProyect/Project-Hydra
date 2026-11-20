using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Usuarios;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.VistaUnificada;

/// <summary>
/// La lista unificada de la bandeja de Comunicaciones entre Tenants (D-6 b, R-3; diseño en
/// Project-Hydra-Negocio/tecnico/DISENO-D6B-COMUNICACIONES-MULTITENANT-2026-11-17.md, § 4.2 y § 4.3). Solo lectura: la
/// vista no ejecuta ningún comando (§ 4.4, DN-6 (a)) y no redacta (DN-7).
///
/// <para>
/// Recorrido, con la estructura de Mi trabajo: conjunto de la vista (<see cref="PredicadoVistaUnificada.ConjuntoAsync"/>)
/// ∩ <see cref="TenantIds"/> → por Tenant, secuencial, re-comprobación y <see cref="AmbitoTenantAutorizado"/> → la regla de
/// visibilidad de la bandeja de un Tenant (<see cref="ConversacionesVisibles.FiltrarAsync"/>, R-3a) bajo la RLS de ese
/// Tenant → cierre del ámbito → fusión en memoria. Ninguna fila sale de su Tenant propietario ni se guarda.
/// </para>
///
/// <para>
/// <see cref="TenantIds"/> es una coordenada, nunca autoridad (§ 4.1): se interseca con el conjunto calculado en el
/// servidor y un Tenant de fuera se descarta sin distinguirlo de uno inexistente.
/// </para>
///
/// <para>
/// Paginación por cursor (§ 4.3, M4): orden global (<c>FechaUltimoMensajeUtc</c> DESC, <c>Id</c> DESC), con el
/// <c>Id</c> comparado como <c>uuid</c> de PostgreSQL (<see cref="OrdenConversacionesUnificadas"/>). El cursor es la
/// última fila emitida más la cota superior <c>T0</c> de la primera página, fija para todo el recorrido: cada página
/// pide a cada Tenant N + 1 filas con fecha <c>&lt;= T0</c> y por debajo del cursor, y emite las N primeras. Sin
/// <c>OFFSET</c>. Una conversación que recibe un mensaje durante el recorrido sube por encima de <c>T0</c> y sale de él.
/// </para>
/// </summary>
/// <param name="TenantIds">Filtro por Empresa gestionada; <c>null</c> o vacío, todas las del conjunto de la vista.</param>
/// <param name="Cursor"><c>null</c> en la primera página; después, <see cref="PaginaConversacionesUnificadasDto.Siguiente"/>.</param>
[AreaDelEncargo("Comunicaciones")]
public record ObtenerConversacionesUnificadasQuery(
    IReadOnlyList<Guid>? TenantIds = null,
    CursorConversacionesUnificadas? Cursor = null,
    int TamanoPagina = 20)
    : IRequest<PaginaConversacionesUnificadasDto>;

/// <param name="CotaSuperiorUtc"><c>T0</c>: la fecha tope de todo el recorrido, tomada en la primera página.</param>
/// <param name="FechaUltimoMensajeUtc">Fecha de la última fila emitida.</param>
/// <param name="Id">Id de la última fila emitida.</param>
public record CursorConversacionesUnificadas(DateTime CotaSuperiorUtc, DateTime FechaUltimoMensajeUtc, Guid Id);

/// <summary>
/// Una fila de la vista unificada. <see cref="TenantId"/> sale de la columna de la fila, no del ámbito.
/// <see cref="TenantNombre"/> se rotula «Empresa gestionada» (DN-8) y el buzón se lee en el ámbito de su Tenant.
/// </summary>
public record ConversacionUnificadaDto(
    Guid Id,
    Guid TenantId,
    string TenantNombre,
    long NumeroTicket,
    string Asunto,
    EstadoConversacion Estado,
    CanalConversacion Canal,
    DateTime FechaUltimoMensajeUtc,
    Guid? ClienteEmpresarialId,
    Guid? EjecutivoAsignadoId,
    Guid? ConexionIntegracionId,
    string? BuzonNombre);

/// <param name="Filas">Hasta <c>TamanoPagina</c> filas, en el orden global.</param>
/// <param name="NoConsultados">
/// Tenants del conjunto que no se pudieron leer: fallaron, salieron del conjunto entre la enumeración y su vuelta, o
/// dentro del ámbito no tienen rol por Asignación de Operación. Nunca son «al día» (§ 4.3).
/// </param>
/// <param name="Siguiente">El cursor de la página siguiente, o <c>null</c> si no hay más.</param>
public record PaginaConversacionesUnificadasDto(
    IReadOnlyList<ConversacionUnificadaDto> Filas,
    IReadOnlyList<TenantNoConsultadoDto> NoConsultados,
    CursorConversacionesUnificadas? Siguiente);

/// <summary>
/// El orden global de la vista: fecha DESC y, a igualdad, <c>Id</c> DESC con el orden del <c>uuid</c> de PostgreSQL,
/// que compara los dieciséis bytes en el orden en que se escriben. La fusión en memoria no se apoya en
/// <see cref="Guid.CompareTo(Guid)"/> (§ 4.3, M4): el orden se fija aquí por bytes, sin depender de la representación
/// interna de <see cref="Guid"/>.
/// </summary>
public static class OrdenConversacionesUnificadas
{
    /// <summary>Compara dos <c>Guid</c> como los compara PostgreSQL en una columna <c>uuid</c>.</summary>
    public static int CompararComoUuid(Guid a, Guid b)
    {
        Span<byte> x = stackalloc byte[16];
        Span<byte> y = stackalloc byte[16];
        a.TryWriteBytes(x, bigEndian: true, out _);
        b.TryWriteBytes(y, bigEndian: true, out _);
        return x.SequenceCompareTo(y);
    }

    /// <summary>Negativo si <paramref name="a"/> va antes que <paramref name="b"/> en la vista (más reciente primero).</summary>
    public static int Comparar(DateTime fechaA, Guid idA, DateTime fechaB, Guid idB)
    {
        var porFecha = fechaB.CompareTo(fechaA);
        return porFecha != 0 ? porFecha : CompararComoUuid(idB, idA);
    }

    /// <summary>La precisión de <c>timestamp</c> de PostgreSQL: microsegundos.</summary>
    public static DateTime TruncarAMicrosegundos(DateTime fecha) =>
        new(fecha.Ticks - fecha.Ticks % 10, DateTimeKind.Utc);
}

public class ObtenerConversacionesUnificadasQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IIntegracionesQueryContext integracionesContext,
    IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, ITenantActual tenantActual,
    ICoordenadaSesionPrivilegiada coordenadaSesionPrivilegiada,
    ILogger<ObtenerConversacionesUnificadasQueryHandler> logger,
    IVistaDemoActual? vistaDemo = null)
    : IRequestHandler<ObtenerConversacionesUnificadasQuery, PaginaConversacionesUnificadasDto>
{
    /// <summary>Tope de filas por página, para que una coordenada del navegador no pida una página sin límite.</summary>
    public const int TamanoPaginaMaximo = 100;

    public async Task<PaginaConversacionesUnificadasDto> Handle(
        ObtenerConversacionesUnificadasQuery request, CancellationToken cancellationToken)
    {
        var identidad = await RechazarPlanosExcluidosAsync(cancellationToken);
        var tamano = Math.Clamp(request.TamanoPagina, 1, TamanoPaginaMaximo);
        var cota = request.Cursor?.CotaSuperiorUtc
                   ?? OrdenConversacionesUnificadas.TruncarAMicrosegundos(DateTime.UtcNow);

        var conjunto = await PredicadoVistaUnificada.ConjuntoAsync(
            identidad, operaciones, tenants, DateTime.UtcNow, cancellationToken);
        conjunto = await SoloLosDeLaLenteDeDemoAsync(conjunto, identidad, cancellationToken);
        if (request.TenantIds is { Count: > 0 } filtro)
            conjunto = conjunto.Where(filtro.Contains).ToList();

        var nombres = conjunto.Count == 0
            ? new Dictionary<Guid, string>()
            : await tenants.Tenants
                .Where(t => conjunto.Contains(t.Id))
                .Select(t => new { t.Id, t.Nombre })
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var filas = new List<ConversacionUnificadaDto>();
        var noConsultados = new List<TenantNoConsultadoDto>();
        foreach (var tenantId in conjunto)
        {
            var nombre = nombres.GetValueOrDefault(tenantId, string.Empty);
            var esOrigen = tenantId == identidad.TenantOrigenId;
            try
            {
                var (autorizado, delTenant) = await AmbitoTenantAutorizado.EjecutarAsync(
                    identidad, operaciones, tenants, tenantId,
                    ambito => LeerTenantAsync(ambito, nombre, esOrigen, cota, request.Cursor, tamano, cancellationToken),
                    cancellationToken);
                if (autorizado && delTenant is not null)
                    filas.AddRange(delTenant);
                else
                    noConsultados.Add(new TenantNoConsultadoDto(tenantId, nombre, esOrigen));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Vista unificada de Comunicaciones: no se pudo leer el Tenant {TenantId}; se devuelve el resto.", tenantId);
                noConsultados.Add(new TenantNoConsultadoDto(tenantId, nombre, esOrigen));
            }
        }

        filas.Sort((a, b) => OrdenConversacionesUnificadas.Comparar(a.FechaUltimoMensajeUtc, a.Id, b.FechaUltimoMensajeUtc, b.Id));
        var pagina = filas.Take(tamano).ToList();
        var siguiente = filas.Count > tamano
            ? new CursorConversacionesUnificadas(cota, pagina[^1].FechaUltimoMensajeUtc, pagina[^1].Id)
            : null;
        return new PaginaConversacionesUnificadasDto(pagina, noConsultados, siguiente);
    }

    /// <summary>
    /// Exclusiones de plano (§ 4.2), en el propio handler: la vista no existe con Sesión Privilegiada seleccionada, con
    /// papel Inbound (DN-1 (a): no se sirve una mezcla), para el Usuario de Cliente ni para el Soporte TALVEG (Tenant de
    /// origen de plataforma). Sin usuario o sin Tenant de origen, tampoco. El Usuario simulado no existe hoy: su guarda es
    /// futura y no está aquí.
    /// </summary>
    private async Task<IdentidadVistaUnificada> RechazarPlanosExcluidosAsync(CancellationToken cancellationToken)
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
    /// simulado tiene cartera: la lente solo estrecha, igual que en el selector.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SoloLosDeLaLenteDeDemoAsync(
        IReadOnlyList<Guid> conjunto, IdentidadVistaUnificada identidad, CancellationToken cancellationToken)
    {
        var acotados = vistaDemo is null ? null : await vistaDemo.ObtenerTenantIdsAcotadosAsync(cancellationToken);
        return acotados is null
            ? conjunto
            : conjunto.Where(t => t == identidad.TenantOrigenId || acotados.Contains(t)).ToList();
    }

    /// <summary>
    /// La página de un Tenant, dentro de su <see cref="AmbitoTenantAutorizado"/>. Segunda línea (§ 4.3, M1): fuera del
    /// Tenant de origen, solo se lee si la persona entra por una Asignación de Operación; si no, el Tenant queda como no
    /// consultado (<c>null</c>), nunca como vacío. Sentencias fijas por Tenant, sin depender de cuántas filas trae: el
    /// alcance, las conexiones excluidas, la página y los nombres de sus buzones.
    /// </summary>
    private async Task<List<ConversacionUnificadaDto>?> LeerTenantAsync(
        AmbitoTenantAutorizado ambito, string tenantNombre, bool esOrigen, DateTime cota,
        CursorConversacionesUnificadas? cursor, int tamano, CancellationToken cancellationToken)
    {
        ambito.ExigirAbierto();
        if (!esOrigen
            && !(currentUserService is IEncargoDeAdministracionActual senal && await senal.OperaPorAsignacionDeOperacionAsync()))
            return null;

        var consulta = await ConversacionesVisibles.FiltrarAsync(
            comunicacionesContext.Conversaciones, alcanceDatos, currentUserService, integracionesContext, tenantActual,
            cancellationToken);
        consulta = consulta.Where(c => c.FechaUltimoMensajeUtc <= cota);
        if (cursor is not null)
        {
            var fecha = cursor.FechaUltimoMensajeUtc;
            var id = cursor.Id;
            consulta = consulta.Where(c => c.FechaUltimoMensajeUtc < fecha
                                           || (c.FechaUltimoMensajeUtc == fecha && c.Id.CompareTo(id) < 0));
        }

        var filas = await consulta
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .ThenByDescending(c => c.Id)
            .Take(tamano + 1)
            .Select(c => new
            {
                c.Id,
                c.TenantId,
                c.NumeroTicket,
                c.Asunto,
                c.Estado,
                c.Canal,
                c.FechaUltimoMensajeUtc,
                c.ClienteId,
                c.EjecutivoAsignadoId,
                c.ConexionIntegracionId,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var conexionIds = filas.Where(f => f.ConexionIntegracionId is not null)
            .Select(f => f.ConexionIntegracionId!.Value).Distinct().ToList();
        var buzones = conexionIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await integracionesContext.ConexionesIntegracion
                .Where(c => conexionIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Nombre })
                .ToDictionaryAsync(c => c.Id, c => c.Nombre, cancellationToken);

        return filas.Select(f => new ConversacionUnificadaDto(
                f.Id, f.TenantId, tenantNombre, f.NumeroTicket, f.Asunto, f.Estado, f.Canal, f.FechaUltimoMensajeUtc,
                f.ClienteId, f.EjecutivoAsignadoId, f.ConexionIntegracionId,
                f.ConexionIntegracionId is { } conexion ? buzones.GetValueOrDefault(conexion) : null))
            .ToList();
    }
}
