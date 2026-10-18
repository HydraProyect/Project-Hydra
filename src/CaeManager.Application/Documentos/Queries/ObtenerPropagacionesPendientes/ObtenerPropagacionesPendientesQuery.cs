using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerPropagacionesPendientes;

public record ObtenerPropagacionesPendientesQuery : IRequest<IReadOnlyList<PropagacionPendienteDto>>;

public record PropagacionPendienteDto(
    Guid DocumentoId, Guid CanalId, Guid CentroId, Guid ClienteEmpresarialId,
    string Documento, string Centro, TipoCanalGestion Via, string? UrlPortal,
    bool BloqueaAcceso, DateOnly? Vencimiento, Guid? AcreditacionId = null);

/// <summary>Consulta del Tenant propietario actual. No interpreta la ausencia de canales como una entrega pendiente.</summary>
public sealed class ObtenerPropagacionesPendientesQueryHandler(
    IDocumentosQueryContext documentos, ICentrosQueryContext centros, IAsignacionesQueryContext asignaciones,
    ITrabajadoresQueryContext trabajadores, ITiposDocumentoQueryContext tipos,
    IPropagacionesDocumentoQueryContext propagaciones, IAlcanceDatosService alcance, IProyectosQueryContext proyectos)
    : IRequestHandler<ObtenerPropagacionesPendientesQuery, IReadOnlyList<PropagacionPendienteDto>>
{
    public async Task<IReadOnlyList<PropagacionPendienteDto>> Handle(ObtenerPropagacionesPendientesQuery request, CancellationToken ct)
    {
        var visibles = await alcance.ObtenerCentroIdsParaGestionAsync(ct);
        var destinos = await (from canal in centros.CanalesGestionDocumental
                              join centro in centros.Centros on canal.CentroId equals centro.Id
                              where centro.GestionCae == ModalidadGestionCae.ConGestionCae
                                  && (visibles == null || visibles.Contains(centro.Id))
                              select new { Canal = canal, Centro = centro }).AsNoTracking().ToListAsync(ct);
        if (destinos.Count == 0) return [];

        var centroIds = destinos.Select(d => d.Centro.Id).Distinct().ToArray();
        var actividad = await (from a in asignaciones.Asignaciones
                               join t in trabajadores.Trabajadores on a.TrabajadorId equals t.Id
                               where a.FechaBaja == null && centroIds.Contains(a.CentroId)
                               select new { a.CentroId, Trabajador = t }).AsNoTracking().ToListAsync(ct);
        var aprobados = await documentos.Documentos.Where(d => d.SustituidoPorDocumentoId == null
                && d.ArchivoUrl != null && d.AnonimizadoEnUtc == null
                && documentos.AprobacionesDocumento.Any(a => a.DocumentoId == d.Id))
            .AsNoTracking().ToListAsync(ct);
        var tiposPorId = await tipos.TiposDocumento.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var requisitos = (await tipos.TiposDocumentoCentros.Where(t => centroIds.Contains(t.CentroId))
            .AsNoTracking().ToListAsync(ct)).ToDictionary(t => (t.TipoDocumentoId, t.CentroId));
        var hechas = await propagaciones.Propagaciones.Where(p => p.HechoEnUtc != null)
            .Select(p => new { p.DocumentoId, p.CanalGestionDocumentalId }).ToListAsync(ct);
        var acreditadas = await documentos.AcreditacionesDocumentoPlataforma.AsNoTracking().ToListAsync(ct);
        var resultado = new List<PropagacionPendienteDto>();
        foreach (var doc in aprobados)
        {
            if (!tiposPorId.TryGetValue(doc.TipoDocumentoId, out var tipo)
                || !await alcance.DocumentoVisibleAsync(doc, proyectos, ct)) continue;
            foreach (var destino in destinos)
            {
                var centroId = destino.Centro.Id;
                var personas = actividad.Where(a => a.CentroId == centroId
                    && ((doc.TrabajadorId is not null && a.Trabajador.Id == doc.TrabajadorId)
                        || (doc.EmpresaId is not null && a.Trabajador.EmpresaId == doc.EmpresaId))).ToList();
                if (personas.Count == 0) continue;
                var aplica = doc.TrabajadorId is not null
                    ? personas.Any(a => ResolucionTipoDocumentoCentro.Aplica(requisitos, tipo.Id, centroId,
                        tipo.Requerido == RequisitoDocumental.Si, tipo.TiposContratacionDeSerie, a.Trabajador.TipoContratacion))
                    : ResolucionTipoDocumentoCentro.Aplica(requisitos, tipo.Id, centroId, tipo.Requerido == RequisitoDocumental.Si);
                if (!aplica || hechas.Any(p => p.DocumentoId == doc.Id && p.CanalGestionDocumentalId == destino.Canal.Id)) continue;
                var acreditacion = acreditadas.FirstOrDefault(a => a.DocumentoId == doc.Id && a.CanalGestionDocumentalId == destino.Canal.Id);
                if (acreditacion is not null && acreditacion.Estado != EstadoAcreditacion.PendienteDeSubir) continue;
                requisitos.TryGetValue((tipo.Id, centroId), out var requisito);
                var vencimiento = doc.FechaVencimiento;
                if (requisito?.PeriodicidadEspecialMeses is { } meses)
                {
                    var especial = doc.FechaEmision.AddMonths(meses);
                    if (vencimiento is null || especial < vencimiento) vencimiento = especial;
                }
                resultado.Add(new(doc.Id, destino.Canal.Id, centroId, destino.Centro.ClienteId,
                    tipo.Nombre + (doc.TrabajadorId is not null ? " · " + personas[0].Trabajador.NombreCompleto : ""),
                    destino.Centro.Nombre, destino.Canal.Tipo, destino.Canal.UrlAcceso,
                    requisito?.BloqueaAcceso == true, vencimiento, acreditacion?.Id));
            }
        }
        return resultado.OrderByDescending(p => p.BloqueaAcceso).ThenBy(p => p.Vencimiento ?? DateOnly.MaxValue)
            .ThenBy(p => p.Centro).ThenBy(p => p.Documento).ToList();
    }
}
