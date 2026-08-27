using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
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
///
/// <paramref name="Texto"/> busca por <c>Nombre</c> o por cualquiera de los
/// <see cref="TipoDocumentoAlias"/> — es la razón de ser del campo de alias
/// (taxonomía documental CAE §2bis): sin él, buscar "TC2" solo encontraba
/// nada a menos que "TC2" estuviera escrito dentro del nombre.
/// </summary>
public record ObtenerTiposDocumentoQuery(
    Guid? ClienteId = null, Guid? EmpresaId = null, Guid? CentroId = null, AmbitoAplicacion? AmbitoAplicacion = null,
    string? Texto = null)
    : IRequest<IReadOnlyList<TipoDocumentoListaDto>>;

public record TipoDocumentoListaDto(
    Guid Id,
    string Nombre,
    int? VigenciaMeses,
    bool AplicaVencimientoAutomatico,
    int Orden,
    AmbitoAplicacion AmbitoAplicacion,
    RequisitoDocumental Requerido,
    NaturalezaJuridica Naturaleza,
    string? Descripcion,
    string? CriteriosValidacion,
    string? SeSolicitaA,
    string? Observaciones,
    bool LecturaIaActiva,
    bool DeteccionTrabajadoresActiva,
    bool VerificacionIaActiva,
    PerfilDocumentoOficial PerfilDocumentoOficial,
    IReadOnlyList<string> Aliases);

public class ObtenerTiposDocumentoQueryHandler(ICentrosQueryContext centrosContext, ITiposDocumentoQueryContext tiposDocumentoContext)
    : IRequestHandler<ObtenerTiposDocumentoQuery, IReadOnlyList<TipoDocumentoListaDto>>
{
    public async Task<IReadOnlyList<TipoDocumentoListaDto>> Handle(
        ObtenerTiposDocumentoQuery request, CancellationToken cancellationToken)
    {
        var consulta = tiposDocumentoContext.TiposDocumento.AsQueryable();

        if (request.AmbitoAplicacion is not null)
            consulta = consulta.Where(t => t.AmbitoAplicacion == request.AmbitoAplicacion);

        if (!string.IsNullOrWhiteSpace(request.Texto))
        {
            var textoMayus = request.Texto.Trim().ToUpper();
            consulta = consulta.Where(t =>
                t.Nombre.ToUpper().Contains(textoMayus) ||
                t.Aliases.Any(a => a.Texto.ToUpper().Contains(textoMayus)));
        }

        if (request.ClienteId is not null || request.EmpresaId is not null || request.CentroId is not null)
        {
            var centrosFiltrados = centrosContext.Centros.AsQueryable();
            if (request.CentroId is not null) centrosFiltrados = centrosFiltrados.Where(c => c.Id == request.CentroId);
            if (request.EmpresaId is not null) centrosFiltrados = centrosFiltrados.Where(c => c.EmpresaId == request.EmpresaId);
            if (request.ClienteId is not null) centrosFiltrados = centrosFiltrados.Where(c => c.ClienteId == request.ClienteId);

            var centroIdsFiltrados = await centrosFiltrados.Select(c => c.Id).ToListAsync(cancellationToken);

            var candidatos = await consulta
                .Select(t => new { t.Id, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
                .ToListAsync(cancellationToken);
            var tipoIdsCandidatos = candidatos.Select(t => t.Id).ToHashSet();

            var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIdsFiltrados.Contains(tc.CentroId))
                .ToListAsync(cancellationToken))
                .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

            // Aplica a la combinación filtrada si aplica a ALGUNO de los Centros
            // que coinciden (mismo criterio que antes: Cliente/Empresa agregan
            // varios Centros, y basta con que uno de ellos lo exija).
            var tipoIdsAplicables = candidatos
                .Where(t => centroIdsFiltrados.Any(centroId => ResolucionTipoDocumentoCentro.Aplica(filasPorPar, t.Id, centroId, t.CuentaParaCumplimiento)))
                .Select(t => t.Id)
                .ToHashSet();

            consulta = consulta.Where(t => tipoIdsAplicables.Contains(t.Id));
        }

        return await consulta
            .OrderBy(t => t.Orden)
            .Select(t => new TipoDocumentoListaDto(
                t.Id, t.Nombre, t.VigenciaMeses, t.AplicaVencimientoAutomatico, t.Orden, t.AmbitoAplicacion, t.Requerido, t.Naturaleza,
                t.Descripcion, t.CriteriosValidacion, t.SeSolicitaA, t.Observaciones, t.LecturaIaActiva, t.DeteccionTrabajadoresActiva,
                t.VerificacionIaActiva, t.PerfilDocumentoOficial, t.Aliases.Select(a => a.Texto).ToList()))
            .ToListAsync(cancellationToken);
    }
}
