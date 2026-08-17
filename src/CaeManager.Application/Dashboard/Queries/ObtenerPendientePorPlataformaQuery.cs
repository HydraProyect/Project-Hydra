using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerPendientePorPlataformaQuery : IRequest<IReadOnlyList<PendientePorPlataformaDto>>;

/// <summary>Una fila por ProveedorPlataformaCae con acreditaciones pendientes o rechazadas visibles para el usuario actual — solo se listan portales con algo que resolver.</summary>
public record PendientePorPlataformaDto(
    Guid ProveedorPlataformaCaeId,
    string ProveedorNombre,
    int PendientesDeSubir,
    int Rechazadas);

/// <summary>
/// Cierra el hallazgo P-04 de la auditoría de producto 2026-08-16: la cadena
/// catálogo → canal → acreditación (Lotes 2-A/2-B/2-C/2-D) ya deriva y
/// persiste <see cref="AcreditacionDocumentoPlataforma"/> al crear/renovar un
/// Documento (ver <c>IDerivarCanalesAplicablesDocumentoService</c>), pero
/// hasta ahora ninguna query la agregaba para responder "¿qué me queda por
/// dejar acreditado en qué portal?" de un vistazo (grep verificado en la
/// auditoría). Solo cuenta PendienteDeSubir y Rechazada: Subida/Aceptada ya
/// no son trabajo pendiente, NoRequerida nunca lo fue.
///
/// Alcance por Centro (no por Trabajador/Empresa): la unidad real de "dónde
/// hay que subir esto" es el <see cref="CanalGestionDocumental"/>, que
/// cuelga de Centro — mismo criterio de alcance que
/// ObtenerKpisDashboardQueryHandler usa para "Centros".
/// </summary>
public class ObtenerPendientePorPlataformaQueryHandler(
    IDocumentosQueryContext documentosContext,
    ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPendientePorPlataformaQuery, IReadOnlyList<PendientePorPlataformaDto>>
{
    public async Task<IReadOnlyList<PendientePorPlataformaDto>> Handle(ObtenerPendientePorPlataformaQuery request, CancellationToken cancellationToken)
    {
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var canalesQuery = centrosContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma);
        if (centroIdsVisibles is not null)
            canalesQuery = canalesQuery.Where(c => centroIdsVisibles.Contains(c.CentroId));

        var conteosPorProveedor = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.Estado == EstadoAcreditacion.PendienteDeSubir || acreditacion.Estado == EstadoAcreditacion.Rechazada
            join canal in canalesQuery on acreditacion.CanalGestionDocumentalId equals canal.Id
            group acreditacion by canal.ProveedorPlataformaCaeId into porProveedor
            select new
            {
                ProveedorPlataformaCaeId = porProveedor.Key!.Value,
                PendientesDeSubir = porProveedor.Count(a => a.Estado == EstadoAcreditacion.PendienteDeSubir),
                Rechazadas = porProveedor.Count(a => a.Estado == EstadoAcreditacion.Rechazada)
            })
            .ToListAsync(cancellationToken);

        if (conteosPorProveedor.Count == 0) return [];

        var proveedorIds = conteosPorProveedor.Select(c => c.ProveedorPlataformaCaeId).ToList();
        var nombresPorProveedor = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return conteosPorProveedor
            .Select(c => new PendientePorPlataformaDto(
                c.ProveedorPlataformaCaeId,
                nombresPorProveedor.GetValueOrDefault(c.ProveedorPlataformaCaeId, "—"),
                c.PendientesDeSubir,
                c.Rechazadas))
            .OrderByDescending(d => d.PendientesDeSubir + d.Rechazadas)
            .ThenBy(d => d.ProveedorNombre)
            .ToList();
    }
}
