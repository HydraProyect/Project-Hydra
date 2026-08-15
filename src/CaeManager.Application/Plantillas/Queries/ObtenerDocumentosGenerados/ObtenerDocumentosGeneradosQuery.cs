using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;

/// <summary>
/// Pantalla de trazabilidad (ADR-010 § MVP scope) — qué se generó, para
/// quién, desde qué plantilla y cuándo. Sin paginación de servidor: el
/// volumen esperado de <see cref="DocumentoGenerado"/> en este MVP no lo
/// necesita todavía (mismo criterio que <c>ObtenerPlantillasDocumentoQuery</c>).
/// </summary>
public record ObtenerDocumentosGeneradosQuery(Guid? PlantillaDocumentoId = null, Guid? TrabajadorId = null)
    : IRequest<IReadOnlyList<DocumentoGeneradoListaDto>>;

public record DocumentoGeneradoListaDto(
    Guid DocumentoGeneradoId,
    Guid DocumentoId,
    Guid PlantillaDocumentoId,
    string PlantillaNombre,
    Guid? TrabajadorId,
    string? TrabajadorNombreCompleto,
    Guid? EmpresaId,
    string? EmpresaRazonSocial,
    DateTime GeneradoEnUtc,
    EstadoDocumentoGenerado Estado);

public class ObtenerDocumentosGeneradosQueryHandler(
    IPlantillasQueryContext plantillasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentosGeneradosQuery, IReadOnlyList<DocumentoGeneradoListaDto>>
{
    public async Task<IReadOnlyList<DocumentoGeneradoListaDto>> Handle(
        ObtenerDocumentosGeneradosQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);

        var consulta = plantillasContext.DocumentosGenerados.AsQueryable();

        if (request.TrabajadorId is { } trabajadorId)
            consulta = consulta.Where(d => d.TrabajadorId == trabajadorId);

        if (trabajadorIdsVisibles is not null)
            consulta = consulta.Where(d => d.TrabajadorId == null || trabajadorIdsVisibles.Contains(d.TrabajadorId!.Value));
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(d => d.EmpresaId == null || empresaIdsVisibles.Contains(d.EmpresaId!.Value));

        var generados = await consulta
            .OrderByDescending(d => d.GeneradoEnUtc)
            .ToListAsync(cancellationToken);

        if (generados.Count == 0)
            return [];

        var idsVersiones = generados.Select(d => d.PlantillaDocumentoVersionId).Distinct().ToList();
        var versiones = await plantillasContext.PlantillasDocumentoVersion
            .Where(v => idsVersiones.Contains(v.Id))
            .Select(v => new { v.Id, v.PlantillaDocumentoId })
            .ToListAsync(cancellationToken);
        var plantillaIdPorVersion = versiones.ToDictionary(v => v.Id, v => v.PlantillaDocumentoId);

        var idsPlantillas = versiones.Select(v => v.PlantillaDocumentoId).Distinct().ToList();
        var plantillas = await plantillasContext.PlantillasDocumento
            .Where(p => idsPlantillas.Contains(p.Id))
            .Select(p => new { p.Id, p.Nombre })
            .ToListAsync(cancellationToken);
        var nombrePorPlantilla = plantillas.ToDictionary(p => p.Id, p => p.Nombre);

        if (request.PlantillaDocumentoId is { } plantillaFiltro)
            generados = generados.Where(d => plantillaIdPorVersion.GetValueOrDefault(d.PlantillaDocumentoVersionId) == plantillaFiltro).ToList();

        var idsTrabajadores = generados.Where(d => d.TrabajadorId is not null).Select(d => d.TrabajadorId!.Value).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => idsTrabajadores.Contains(t.Id))
            .Select(t => new { t.Id, t.NombreCompleto })
            .ToListAsync(cancellationToken);
        var nombreTrabajadorPorId = trabajadores.ToDictionary(t => t.Id, t => t.NombreCompleto);

        var idsEmpresas = generados.Where(d => d.EmpresaId is not null).Select(d => d.EmpresaId!.Value).Distinct().ToList();
        var empresas = await empresasContext.Empresas
            .Where(e => idsEmpresas.Contains(e.Id))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToListAsync(cancellationToken);
        var razonSocialPorEmpresa = empresas.ToDictionary(e => e.Id, e => e.RazonSocial);

        return generados
            .Select(d =>
            {
                var plantillaId = plantillaIdPorVersion.GetValueOrDefault(d.PlantillaDocumentoVersionId);
                return new DocumentoGeneradoListaDto(
                    d.Id, d.DocumentoId, plantillaId, nombrePorPlantilla.GetValueOrDefault(plantillaId, "—"),
                    d.TrabajadorId, d.TrabajadorId is { } tId ? nombreTrabajadorPorId.GetValueOrDefault(tId) : null,
                    d.EmpresaId, d.EmpresaId is { } eId ? razonSocialPorEmpresa.GetValueOrDefault(eId) : null,
                    d.GeneradoEnUtc, d.Estado);
            })
            .ToList();
    }
}
