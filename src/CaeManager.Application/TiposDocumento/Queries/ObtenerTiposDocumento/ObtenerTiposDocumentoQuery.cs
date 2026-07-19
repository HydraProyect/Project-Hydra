using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;

/// <summary>
/// Catálogo pequeño (15 filas semilla) — se devuelve completo, sin
/// paginación server-side. Los filtros de ámbito son opcionales y
/// acumulativos (Cliente → Empresa → Centro, igual que el resto de
/// selectores en cascada de la aplicación): un Tipo de Documento sin
/// ninguna asociación a Centro (<see cref="TipoDocumentoCentro"/>) es
/// global y aparece siempre, independientemente del filtro.
/// </summary>
public record ObtenerTiposDocumentoQuery(
    Guid? ClienteId = null, Guid? EmpresaId = null, Guid? CentroId = null, AmbitoAplicacion? AmbitoAplicacion = null)
    : IRequest<IReadOnlyList<TipoDocumentoListaDto>>;

public record TipoDocumentoListaDto(
    Guid Id,
    string Nombre,
    int? VigenciaMeses,
    bool AplicaVencimientoAutomatico,
    int Orden,
    AmbitoAplicacion AmbitoAplicacion,
    bool EsObligatorio,
    string? Descripcion,
    string? CriteriosValidacion,
    string? SeSolicitaA,
    string? Observaciones,
    bool LecturaIaActiva,
    bool DeteccionTrabajadoresActiva);

public class ObtenerTiposDocumentoQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerTiposDocumentoQuery, IReadOnlyList<TipoDocumentoListaDto>>
{
    public async Task<IReadOnlyList<TipoDocumentoListaDto>> Handle(
        ObtenerTiposDocumentoQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.TiposDocumento.AsQueryable();

        if (request.AmbitoAplicacion is not null)
            consulta = consulta.Where(t => t.AmbitoAplicacion == request.AmbitoAplicacion);

        if (request.ClienteId is not null || request.EmpresaId is not null || request.CentroId is not null)
        {
            var centrosFiltrados = dbContext.Centros.AsQueryable();
            if (request.CentroId is not null) centrosFiltrados = centrosFiltrados.Where(c => c.Id == request.CentroId);
            if (request.EmpresaId is not null) centrosFiltrados = centrosFiltrados.Where(c => c.EmpresaId == request.EmpresaId);
            if (request.ClienteId is not null) centrosFiltrados = centrosFiltrados.Where(c => c.ClienteId == request.ClienteId);

            var centroIdsFiltrados = centrosFiltrados.Select(c => c.Id);

            var tipoDocumentoIdsGlobales = dbContext.TiposDocumento
                .Where(t => !dbContext.TiposDocumentoCentros.Any(tc => tc.TipoDocumentoId == t.Id))
                .Select(t => t.Id);

            var tipoDocumentoIdsAsociados = dbContext.TiposDocumentoCentros
                .Where(tc => centroIdsFiltrados.Contains(tc.CentroId))
                .Select(tc => tc.TipoDocumentoId);

            consulta = consulta.Where(t => tipoDocumentoIdsGlobales.Contains(t.Id) || tipoDocumentoIdsAsociados.Contains(t.Id));
        }

        return await consulta
            .OrderBy(t => t.Orden)
            .Select(t => new TipoDocumentoListaDto(
                t.Id, t.Nombre, t.VigenciaMeses, t.AplicaVencimientoAutomatico, t.Orden, t.AmbitoAplicacion, t.EsObligatorio,
                t.Descripcion, t.CriteriosValidacion, t.SeSolicitaA, t.Observaciones, t.LecturaIaActiva, t.DeteccionTrabajadoresActiva))
            .ToListAsync(cancellationToken);
    }
}
