using System.Text.Json;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Auditoria.Queries;

/// <summary>
/// Historial completo de auditoría, hoy solo visible embebido en el detalle
/// de cada entidad (ver ROADMAP.md, Fase 4). No resuelve el nombre del
/// usuario aquí — Application no conoce Identity/ApplicationUser (vive en
/// Infrastructure); Web resuelve UsuarioId → email/nombre después, con
/// UserManager, igual que ya hace con el resto de pantallas de Identity.
/// </summary>
public record ObtenerAuditoriaQuery(
    string? EntidadTipo,
    Guid? UsuarioId,
    int Pagina = 1,
    int TamanoPagina = 30,
    Guid? EntidadId = null) : IRequest<ResultadoPaginado<RegistroAuditoriaListaDto>>;

/// <summary>
/// Proyección para el listado paginado — sin <c>DatosAntes</c>/<c>DatosDespues</c>
/// (hallazgo del Módulo 8/9, auditoría 2026-08-30): el listado mostraba el
/// snapshot JSON completo de la entidad en cada una de las filas cargadas
/// (hasta 30 en pantalla, o el lote entero de <c>PaginadorExportacion</c> en
/// la exportación), cuando lo único que la UI necesita de ese JSON son estos
/// dos booleanos. El detalle completo —usado solo por
/// <c>/auditoria/{id}/archivo-anterior</c>, un registro a la vez— sigue
/// viviendo en <see cref="RegistroAuditoriaDto"/> vía
/// <c>ObtenerRegistroAuditoriaPorIdQuery</c>, sin tocar.
/// </summary>
public record RegistroAuditoriaListaDto(
    Guid Id,
    string EntidadTipo,
    Guid EntidadId,
    string Accion,
    Guid? UsuarioId,
    DateTime FechaUtc,
    bool PuedeRestaurar,
    bool TieneArchivoAnterior);

/// <summary>
/// Detalle completo de una fila de auditoría, con el snapshot JSON —usado
/// por <c>ObtenerRegistroAuditoriaPorIdQuery</c> para servir un único
/// registro a la vez (p. ej. <c>/auditoria/{id}/archivo-anterior</c>), nunca
/// para el listado paginado (ver <see cref="RegistroAuditoriaListaDto"/>).
/// </summary>
public record RegistroAuditoriaDto(
    Guid Id,
    string EntidadTipo,
    Guid EntidadId,
    string Accion,
    Guid? UsuarioId,
    DateTime FechaUtc,
    string? DatosAntes,
    string? DatosDespues);

public class ObtenerAuditoriaQueryHandler(IAuditoriaQueryContext dbContext)
    : IRequestHandler<ObtenerAuditoriaQuery, ResultadoPaginado<RegistroAuditoriaListaDto>>
{
    // Solo estas 5 tienen Restaurar*Command (patrón "Deshacer", ver
    // UX_PATTERNS.md § Eliminar) — son las únicas para las que la Auditoría
    // puede ofrecer una restauración real (H1, docs/ux-audit/14-administracion.md).
    private static readonly HashSet<string> EntidadesRestaurables =
        ["Cliente", "Empresa", "Centro", "Trabajador", "Documento"];

    public async Task<ResultadoPaginado<RegistroAuditoriaListaDto>> Handle(ObtenerAuditoriaQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.RegistrosAuditoria.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.EntidadTipo))
            consulta = consulta.Where(r => r.EntidadTipo == request.EntidadTipo);

        if (request.UsuarioId is not null)
            consulta = consulta.Where(r => r.UsuarioId == request.UsuarioId);

        if (request.EntidadId is not null)
            consulta = consulta.Where(r => r.EntidadId == request.EntidadId);

        var total = await consulta.CountAsync(cancellationToken);

        // DatosAntes/DatosDespues se traen aquí solo para calcular los dos
        // booleanos de abajo — no salen de este método. El listado (esta
        // página, o el lote de la exportación) no necesita retener el
        // snapshot JSON completo de cada fila, solo si hay archivo anterior o
        // si el borrado es reversible.
        var filas = await consulta
            .OrderByDescending(r => r.FechaUtc)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(r => new
            {
                r.Id,
                r.EntidadTipo,
                r.EntidadId,
                r.Accion,
                r.UsuarioId,
                r.FechaUtc,
                r.DatosAntes,
                r.DatosDespues
            })
            .ToListAsync(cancellationToken);

        var elementos = filas
            .Select(r => new RegistroAuditoriaListaDto(
                r.Id, r.EntidadTipo, r.EntidadId, r.Accion, r.UsuarioId, r.FechaUtc,
                PuedeRestaurar(r.EntidadTipo, r.Accion, r.DatosDespues),
                TieneArchivoAnterior(r.EntidadTipo, r.Accion, r.DatosAntes)))
            .ToList();

        return new ResultadoPaginado<RegistroAuditoriaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// H1 (docs/ux-audit/14-administracion.md): esto es lo que hace real la
    /// promesa "Podrás recuperarlas desde Auditoría" del borrado en lote de
    /// Cliente/Empresa/Centro/Trabajador/Documento. El borrado es lógico
    /// (<c>MarcarComoEliminado()</c> solo cambia un flag), así que
    /// <c>AuditoriaInterceptor</c> lo registra como "Modificado" — "Eliminado"
    /// en <c>Accion</c> solo existe para un borrado físico que este dominio no
    /// hace — por eso hay que mirar el JSON de <c>DatosDespues</c>, igual que
    /// <see cref="TieneArchivoAnterior"/> con <c>DatosAntes</c>.
    /// </summary>
    private static bool PuedeRestaurar(string entidadTipo, string accion, string? datosDespues)
    {
        if (!EntidadesRestaurables.Contains(entidadTipo) || accion != "Modificado" || datosDespues is null)
            return false;

        try
        {
            using var documento = JsonDocument.Parse(datosDespues);
            return documento.RootElement.TryGetProperty("EstaEliminado", out var valor) && valor.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// El interceptor de auditoría ya guarda el ArchivoUrl anterior en el
    /// JSON de DatosAntes de cada Modificado de Documento — esto solo
    /// comprueba si hay uno para decidir si mostrar el enlace.
    /// </summary>
    private static bool TieneArchivoAnterior(string entidadTipo, string accion, string? datosAntes)
    {
        if (entidadTipo != "Documento" || accion != "Modificado" || datosAntes is null)
            return false;

        try
        {
            using var documento = JsonDocument.Parse(datosAntes);
            return documento.RootElement.TryGetProperty("ArchivoUrl", out var valor) && valor.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
