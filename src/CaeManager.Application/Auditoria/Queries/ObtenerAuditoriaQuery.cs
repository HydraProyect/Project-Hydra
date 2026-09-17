using CaeManager.Application.Auditoria;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
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

public class ObtenerAuditoriaQueryHandler(
    IAuditoriaQueryContext dbContext,
    IEmpresasQueryContext empresasContext,
    ICentrosQueryContext centrosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext,
    ITenantActual tenantActual)
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

        // Antes se traía DatosAntes/DatosDespues —el snapshot JSON completo—
        // de cada fila de la página (o del lote de exportación) solo para
        // calcular los dos booleanos de abajo y descartar el resto (hallazgo
        // Módulo 8/9). Los dos se traducen aquí a un substring determinista
        // sobre el TEXT ya almacenado, sin traer la columna entera: ambas
        // claves ("EstaEliminado", "ArchivoUrl") son nombres de propiedad del
        // CLR y AuditoriaInterceptor.SerializarValores serializa con
        // System.Text.Json por defecto — compacto, sin indentar, sin
        // reescribir mayúsculas — así que el marcador es exacto y no un
        // parseo aproximado. AuditoriaProyeccionSqlTests compara este
        // resultado contra JsonDocument.Parse para que las dos definiciones
        // no diverjan en silencio si el interceptor cambia de formato.
        //
        // StartsWith("{") es la única validación de forma que se puede pedir
        // a SQL sin traer la columna completa (hallazgo de Codex: el JSON
        // histórico anterior trataba cualquier texto no parseable como "no
        // candidato", y un substring puro no distingue eso de un TEXT
        // corrupto que por casualidad contenga el marcador). Sigue habiendo
        // un hueco residual frente a JsonDocument.Parse — un TEXT corrupto
        // que empiece por "{" y además contenga el marcador seguiría dando
        // un falso positivo — aceptado a propósito porque cerrarlo del todo
        // exige traer la columna entera, que es exactamente lo que este
        // cambio evita, y porque esta columna solo la escribe
        // AuditoriaInterceptor (JSON válido siempre): el caso solo se da ante
        // corrupción manual de la base, no ante datos que la aplicación
        // pueda producir.
        var candidatosPagina = await consulta
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
                EsCandidataARestaurar =
                    EntidadesRestaurables.Contains(r.EntidadTipo) && r.Accion == "Modificado"
                    && r.DatosDespues != null && r.DatosDespues.StartsWith("{")
                    && r.DatosDespues.Contains("\"EstaEliminado\":true"),
                TieneArchivoAnteriorCandidato =
                    r.EntidadTipo == "Documento" && r.Accion == "Modificado"
                    && r.DatosAntes != null && r.DatosAntes.StartsWith("{")
                    && r.DatosAntes.Contains("\"ArchivoUrl\":\"")
            })
            .ToListAsync(cancellationToken);

        // El JSON histórico de DatosDespues solo dice que ESE cambio puso
        // EstaEliminado=true — no que la entidad siga eliminada HOY. Si se
        // restauró después, esta fila de auditoría sigue siendo el registro
        // de aquel borrado (es historia), pero el botón "Restaurar" de la
        // pantalla ya no tiene nada que hacer: el Restaurar*Command lo
        // rechazaría porque la entidad ya no está eliminada. Por eso el
        // candidato histórico se cruza aquí con el estado actual, con una
        // única consulta por lote y por tabla (evita N+1 en TamanoPagina
        // filas) y siempre acotada a TenantId — la misma frontera de
        // aislamiento que exige cualquier IgnoreQueryFilters() nuevo.
        var candidatos = candidatosPagina
            .Where(r => r.EsCandidataARestaurar)
            .Select(r => (r.EntidadTipo, r.EntidadId))
            .ToList();

        var siguenEliminadasHoy = await ObtenerEliminadasActualmenteAsync(candidatos, cancellationToken);

        var elementos = candidatosPagina
            .Select(r => new RegistroAuditoriaListaDto(
                r.Id, r.EntidadTipo, r.EntidadId, r.Accion, r.UsuarioId, r.FechaUtc,
                r.EsCandidataARestaurar
                    && siguenEliminadasHoy.TryGetValue(r.EntidadTipo, out var idsEliminados)
                    && idsEliminados.Contains(r.EntidadId),
                r.TieneArchivoAnteriorCandidato))
            .ToList();

        return new ResultadoPaginado<RegistroAuditoriaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// Estado ACTUAL de cada candidato (agrupado por tabla, un lote por
    /// tipo — nunca una consulta por fila). "Cliente" y "Empresa" son el
    /// mismo tipo persistido desde F3b (ver RestaurarClienteCommand): ambos
    /// EntidadTipo se resuelven contra Empresas.
    ///
    /// <c>IgnoreQueryFilters()</c> hace falta porque el filtro global de
    /// soft-delete excluiría justo las filas que se buscan (las eliminadas);
    /// como ese filtro combina tenant + soft-delete en una sola condición,
    /// ignorarlo también deja de filtrar por tenant — por eso TenantId se
    /// compara aquí a mano, igual que en cada Restaurar*Command. Sin esa
    /// comprobación, una entidad de OTRO tenant con el mismo Id contaría
    /// como "sigue eliminada" y el botón de restaurar en el registro de
    /// auditoría de un tenant ofrecería recuperar la fila de otro.
    /// </summary>
    private async Task<Dictionary<string, HashSet<Guid>>> ObtenerEliminadasActualmenteAsync(
        List<(string EntidadTipo, Guid EntidadId)> candidatos, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<string, HashSet<Guid>>();
        var tenantId = tenantActual.TenantId;

        var idsEmpresa = candidatos
            .Where(c => c.EntidadTipo is "Cliente" or "Empresa")
            .Select(c => c.EntidadId)
            .Distinct()
            .ToArray();
        if (idsEmpresa.Length > 0)
        {
            var eliminadas = (await empresasContext.Empresas
                .IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && idsEmpresa.Contains(e.Id) && e.EstaEliminado)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
            resultado["Cliente"] = eliminadas;
            resultado["Empresa"] = eliminadas;
        }

        var idsCentro = candidatos.Where(c => c.EntidadTipo == "Centro").Select(c => c.EntidadId).Distinct().ToArray();
        if (idsCentro.Length > 0)
        {
            resultado["Centro"] = (await centrosContext.Centros
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && idsCentro.Contains(c.Id) && c.EstaEliminado)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        var idsTrabajador = candidatos.Where(c => c.EntidadTipo == "Trabajador").Select(c => c.EntidadId).Distinct().ToArray();
        if (idsTrabajador.Length > 0)
        {
            resultado["Trabajador"] = (await trabajadoresContext.Trabajadores
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && idsTrabajador.Contains(t.Id) && t.EstaEliminado)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        var idsDocumento = candidatos.Where(c => c.EntidadTipo == "Documento").Select(c => c.EntidadId).Distinct().ToArray();
        if (idsDocumento.Length > 0)
        {
            resultado["Documento"] = (await documentosContext.Documentos
                .IgnoreQueryFilters()
                .Where(d => d.TenantId == tenantId && idsDocumento.Contains(d.Id) && d.EstaEliminado)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        return resultado;
    }
}
