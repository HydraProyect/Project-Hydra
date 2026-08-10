using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;

public record ObtenerSubcontratasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, Guid? SubcontrataId = null)
    : IRequest<ResultadoPaginado<SubcontrataListaDto>>;

/// <param name="CumplimientoPorcentaje">
/// % de cumplimiento documental de los trabajadores de la subcontrata
/// ("Subcontrata 360", mismo patrón que <c>CentroListaDto.CumplimientoPorcentaje</c>
/// de Centro 360) — <c>null</c> cuando ningún trabajador tiene un par
/// Trabajador×TipoDocumento exigido por alguno de sus centros activos.
/// </param>
public record SubcontrataListaDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, NivelServicioSubcontrata NivelServicio,
    int? CumplimientoPorcentaje, RecuentosSubcontrataDto Recuentos);

public class ObtenerSubcontratasQueryHandler(
    ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoSubcontrataService calculoEstado)
    : IRequestHandler<ObtenerSubcontratasQuery, ResultadoPaginado<SubcontrataListaDto>>
{
    public async Task<ResultadoPaginado<SubcontrataListaDto>> Handle(ObtenerSubcontratasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Subcontratas.AsQueryable();

        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        if (subcontrataIdsVisibles is not null)
            consulta = consulta.Where(s => subcontrataIdsVisibles.Contains(s.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(s => s.RazonSocial.ToUpper().Contains(busqueda)
                || (s.Cif != null && s.Cif.ToUpper().Contains(busqueda)));
        }

        // Drill-down por Id exacto — mismo criterio que ObtenerCentrosQuery.CentroId:
        // recargar una sola fila tras una acción en el acordeón sin colapsar el resto.
        if (request.SubcontrataId is not null)
            consulta = consulta.Where(s => s.Id == request.SubcontrataId);

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(SubcontrataListaDto.RazonSocial), true) => consulta.OrderByDescending(s => s.RazonSocial),
            (nameof(SubcontrataListaDto.Cif), false) => consulta.OrderBy(s => s.Cif),
            (nameof(SubcontrataListaDto.Cif), true) => consulta.OrderByDescending(s => s.Cif),
            (nameof(SubcontrataListaDto.CreadoEnUtc), false) => consulta.OrderBy(s => s.CreadoEnUtc),
            (nameof(SubcontrataListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(s => s.CreadoEnUtc),
            _ => consulta.OrderBy(s => s.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(s => s.Id);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(s => new { s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, s.NivelServicio })
            .ToListAsync(cancellationToken);

        var idsPagina = pagina.Select(s => s.Id).ToList();
        var cumplimiento = await calculoEstado.CalcularCumplimientoAsync(idsPagina, cancellationToken);
        var causas = await calculoEstado.CalcularAsync(idsPagina, cancellationToken);

        var elementos = pagina
            .Select(s => new SubcontrataListaDto(
                s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, s.NivelServicio,
                cumplimiento.TryGetValue(s.Id, out var fraccion) ? fraccion.Porcentaje : null,
                causas.TryGetValue(s.Id, out var incidencias) ? Desglosar(incidencias) : RecuentosSubcontrataDto.Vacio))
            .ToList();

        return new ResultadoPaginado<SubcontrataListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>"Faltante" cuenta como vencido — mismo criterio que ObtenerCentrosQuery.Desglosar: un requisito sin documento no está al día.</summary>
    private static RecuentosSubcontrataDto Desglosar(IReadOnlyList<IncidenciaSubcontrataDto> incidencias)
    {
        var vencidas = incidencias.Where(i => i.Estado is Domain.Documentos.EstadoDocumento.Vencido or Domain.Documentos.EstadoDocumento.Faltante).ToList();
        var proximas = incidencias.Where(i => i.Estado is Domain.Documentos.EstadoDocumento.Proximo).ToList();
        return new RecuentosSubcontrataDto(vencidas, proximas);
    }
}
