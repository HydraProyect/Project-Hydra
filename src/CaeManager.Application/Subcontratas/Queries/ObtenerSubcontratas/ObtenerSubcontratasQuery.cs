using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;

/// <param name="NivelServicio">Filtro exacto por nivel de servicio (Gestionada / Supervisada).</param>
/// <param name="EstadoDocumental">
/// Filtro de la franja de estado: nombres de <see cref="EstadoDocumento"/> separados por coma (el mismo valor
/// que viaja en la URL), sobre <see cref="SubcontrataListaDto.EstadoDocumental"/>. Un valor que no es un
/// estado se ignora; si ninguno lo es, no filtra (ver <see cref="EstadoDocumentalFiltro.Coincide"/>).
/// </param>
/// <param name="ConRecuentosPorEstado">
/// Rellena <c>ResultadoPaginado.RecuentosPorEstado</c>: Subcontratas por estado documental con los demás
/// filtros aplicados y sin el de estado.
/// </param>
public record ObtenerSubcontratasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, Guid? SubcontrataId = null,
    NivelServicioSubcontrata? NivelServicio = null,
    string? EstadoDocumental = null, bool ConRecuentosPorEstado = false)
    : IRequest<ResultadoPaginado<SubcontrataListaDto>>;

/// <param name="CumplimientoPorcentaje">
/// % de cumplimiento documental de los trabajadores de la subcontrata
/// ("Subcontrata 360", mismo patrón que <c>CentroListaDto.CumplimientoPorcentaje</c>
/// de Centro 360) — <c>null</c> cuando ningún trabajador tiene un par
/// Trabajador×TipoDocumento exigido por alguno de sus centros activos.
/// </param>
public record SubcontrataListaDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, NivelServicioSubcontrata NivelServicio,
    int? CumplimientoPorcentaje, RecuentosSubcontrataDto Recuentos)
{
    /// <summary>
    /// Estado documental de la fila: el peor de sus incidencias (<see cref="RecuentosSubcontrataDto.PeorEstado"/>).
    /// No está persistido; por eso filtrar por él obliga a calcularlo para todas las filas.
    /// </summary>
    public EstadoDocumento EstadoDocumental => Recuentos.PeorEstado;
}

/// <summary>
/// F3b-Subcontrata (revisión adversaria del 2026-08-26, evidencia real de
/// IntegrationTests): a diferencia de las otras dos consultas semánticas de
/// D2 (<c>ObtenerSubcontratasParaSelectorQuery</c> y la rama Subcontrata de
/// <c>BuscarGlobalQuery</c>, que siguen congeladas), esta se adelanta a leer
/// Empresas — congelarla dejaba el % de cumplimiento activamente incorrecto
/// (no solo desactualizado) para cualquier Subcontrata creada tras el
/// freeze, porque <see cref="Domain.Trabajadores.Trabajador.SubcontrataId"/>
/// ya solo puede apuntar a filas de Empresas. Ver
/// Project-Hydra-Negocio/tecnico/f3b-subcontrata-inventario-fresco-2026-08-26.md.
/// </summary>
public class ObtenerSubcontratasQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoSubcontrataService calculoEstado)
    : IRequestHandler<ObtenerSubcontratasQuery, ResultadoPaginado<SubcontrataListaDto>>
{
    public async Task<ResultadoPaginado<SubcontrataListaDto>> Handle(ObtenerSubcontratasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.Where(e => e.NivelServicio != null);

        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        if (subcontrataIdsVisibles is not null)
            consulta = consulta.Where(s => subcontrataIdsVisibles.Contains(s.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda;
            consulta = consulta.Where(s => TextoDeBusqueda.Contiene(s.RazonSocial, busqueda)
                || (s.Cif != null && TextoDeBusqueda.Contiene(s.Cif, busqueda)));
        }

        // Drill-down por Id exacto — mismo criterio que ObtenerCentrosQuery.CentroId:
        // recargar una sola fila tras una acción en el acordeón sin colapsar el resto.
        if (request.SubcontrataId is not null)
            consulta = consulta.Where(s => s.Id == request.SubcontrataId);

        // La columna guarda el nombre del enum como texto (deuda de F3, ver Empresa.NivelServicio).
        // Es un filtro por VALOR: no decide el rol de la Empresa por la nulidad de la columna.
        var nivelTexto = request.NivelServicio?.ToString();
        if (nivelTexto is not null)
            consulta = consulta.Where(s => s.NivelServicio == nivelTexto);

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

        // Camino con estado (franja de estado): el estado documental no está persistido, así que para
        // filtrar por él o contar por él hay que calcularlo para TODAS las filas que pasan los demás
        // filtros, y paginar después en memoria — mismo planteamiento que ObtenerCentrosQuery. El
        // alcance es el de arriba: no se calcula nada de una Subcontrata que el usuario no ve.
        var filtraPorEstado = EstadoDocumentalFiltro.ClavesDeOrden(request.EstadoDocumental) is not null;
        if (filtraPorEstado || request.ConRecuentosPorEstado)
        {
            var todas = await ordenada
                .Select(s => new FilaSubcontrata(s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, s.NivelServicio!))
                .ToListAsync(cancellationToken);
            var conEstado = await ConEstadoAsync(todas, cancellationToken);

            IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
            if (request.ConRecuentosPorEstado)
            {
                recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(conEstado
                    .GroupBy(s => EstadoDocumentalFiltro.ClaveOrden(s.EstadoDocumental))
                    .ToDictionary(grupo => grupo.Key, grupo => grupo.Count()));
            }

            var filtradas = filtraPorEstado
                ? conEstado.Where(s => EstadoDocumentalFiltro.Coincide(s.EstadoDocumental, request.EstadoDocumental)).ToList()
                : conEstado;

            return new ResultadoPaginado<SubcontrataListaDto>(
                filtradas.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList(),
                filtradas.Count, request.Pagina, request.TamanoPagina)
            {
                RecuentosPorEstado = recuentosPorEstado
            };
        }

        var total = await consulta.CountAsync(cancellationToken);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(s => new FilaSubcontrata(s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, s.NivelServicio!))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<SubcontrataListaDto>(
            await ConEstadoAsync(pagina, cancellationToken), total, request.Pagina, request.TamanoPagina);
    }

    private async Task<List<SubcontrataListaDto>> ConEstadoAsync(IReadOnlyList<FilaSubcontrata> filas, CancellationToken cancellationToken)
    {
        var resumenes = await calculoEstado.CalcularResumenAsync(filas.Select(s => s.Id).ToList(), cancellationToken);

        return filas
            .Select(s =>
            {
                var resumen = resumenes.GetValueOrDefault(s.Id);
                return new SubcontrataListaDto(
                    s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, Enum.Parse<NivelServicioSubcontrata>(s.NivelServicio),
                    resumen?.Fraccion.Porcentaje,
                    resumen is null ? RecuentosSubcontrataDto.Vacio : Desglosar(resumen));
            })
            .ToList();
    }

    private record FilaSubcontrata(Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, string NivelServicio);

    /// <summary>
    /// "Faltante" cuenta como vencido, y Urgente va con "próximas" — mismo
    /// criterio que <c>ObtenerCentrosQuery.Desglosar</c>: un requisito sin
    /// documento no está al día, y Urgente es más severo que Próximo pero el
    /// documento aún no venció. Estos dos buckets se leen como texto literal
    /// ("N vencido(s)") en la UI, no solo como un tono de color — meter
    /// Urgente en "vencidas" afirmaría una fecha vencida que no lo está
    /// (hallazgo de Codex, oleada 3). La severidad de color se resuelve en el
    /// badge de cada incidencia, no en el bucket del recuento. Antes de que
    /// <c>ObtenerCentrosQuery</c> lo corrigiera (D-7, piloto Outbound), este
    /// comentario afirmaba paridad mientras Urgente se descartaba en silencio
    /// aquí igual que allí.
    /// </summary>
    private static RecuentosSubcontrataDto Desglosar(ResumenEstadoSubcontrata resumen)
    {
        var vencidas = resumen.Incidencias.Where(i => i.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante).ToList();
        var proximas = resumen.Incidencias.Where(i => i.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo).ToList();
        return new RecuentosSubcontrataDto(vencidas, proximas) { SinConfirmar = resumen.SinConfirmar };
    }
}
