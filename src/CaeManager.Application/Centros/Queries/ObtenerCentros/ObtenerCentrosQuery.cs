using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentros;

/// <param name="CentroId">
/// Filtro exacto por Centro (Centro 360, PLAN-EJECUCION-UX.md § 0.11) — para
/// el drill-down desde el desplegable de Centros con actividad de una
/// Empresa a <c>/centros?centroId=…</c>, donde una coincidencia por texto
/// (<paramref name="Busqueda"/>) podría ser ambigua entre varios Centros con
/// nombre parecido. Se combina con el resto de filtros, aunque en la
/// práctica ya identifica una única fila.
/// </param>
public record ObtenerCentrosQuery(
    string? Busqueda, Guid? ClienteId, EstadoCentro? Estado = null,
    string? OrdenarPor = null, bool Descendente = false, int Pagina = 1, int TamanoPagina = 20,
    Guid? CentroId = null)
    : IRequest<ResultadoPaginado<CentroListaDto>>;

/// <param name="CumplimientoPorcentaje">
/// % de cumplimiento documental de los trabajadores del centro (Centro 360,
/// PLAN-EJECUCION-UX.md § 0.5) — <c>null</c> cuando no hay ningún par
/// Trabajador×TipoDocumento obligatorio aplicable, ver <see cref="FraccionCumplimiento"/>.
/// </param>
/// <summary>
/// Una incidencia concreta del centro, con el ámbito al que pertenece. Es lo
/// que permite que la ventana de contexto de un recuento declare cuántas son
/// de Empresa y cuántas de Trabajadores (blueprint Centro 360 § 3.2,
/// DDL-031/DDL-047) en vez de dar un número desnudo.
/// </summary>
public record IncidenciaCentroDto(string Descripcion, AmbitoCausa Ambito);

/// <summary>
/// Desglose de las incidencias de un centro por estado. No lleva contadores
/// propios: se derivan de las listas, para que no puedan desincronizarse del
/// detalle que muestra la ventana de contexto.
/// </summary>
public record RecuentosCentroDto(
    IReadOnlyList<IncidenciaCentroDto> Vencidas,
    IReadOnlyList<IncidenciaCentroDto> Proximas)
{
    public static readonly RecuentosCentroDto Vacio = new([], []);

    public int TotalVencidas => Vencidas.Count;
    public int TotalProximas => Proximas.Count;
}

public record CentroListaDto(
    Guid Id, string Nombre, string? CodigoCentro, Guid ClienteId, string ClienteRazonSocial,
    Guid EmpresaId, string EmpresaRazonSocial,
    EstadoCentro Estado, int? CumplimientoPorcentaje, RecuentosCentroDto Recuentos);

/// <summary>
/// El <see cref="CentroListaDto.Estado"/> no está persistido — lo calcula
/// <see cref="ICalculoEstadoCentroService"/> a partir de los Documentos de la
/// Empresa, los de cada Trabajador con Asignación activa y los
/// RequisitosDocumentales bloqueantes (ver <c>DATABASE.md</c>: guardarlo lo
/// desincronizaría de los umbrales configurables). Eso parte el handler en
/// dos caminos:
///
/// - <b>Camino normal</b>: se ordena y pagina en SQL y el estado se calcula
///   solo para los 20 centros de la página, como se venía haciendo.
/// - <b>Camino con estado</b> (filtrar por Estado u ordenar por Estado): hace
///   falta conocer el estado de todos los centros que pasan los filtros antes
///   de poder paginar, así que se materializa esa proyección — corta, seis
///   columnas — y se filtra, ordena y pagina en memoria. Está acotado por el
///   alcance del usuario y por la búsqueda; si algún día un tenant tiene
///   tantos centros que esto pese, la salida es una vista materializada, no
///   persistir el estado en una columna.
/// </summary>
public class ObtenerCentrosQueryHandler(
    ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerCentrosQuery, ResultadoPaginado<CentroListaDto>>
{
    public async Task<ResultadoPaginado<CentroListaDto>> Handle(ObtenerCentrosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from centro in centrosContext.Centros
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
            select new { centro, cliente, empresa };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x => x.centro.Nombre.ToUpper().Contains(busqueda));
        }

        if (request.ClienteId is not null)
            consulta = consulta.Where(x => x.centro.ClienteId == request.ClienteId);

        if (request.CentroId is not null)
            consulta = consulta.Where(x => x.centro.Id == request.CentroId);

        var necesitaEstadoCompleto =
            request.Estado is not null ||
            string.Equals(request.OrdenarPor, nameof(CentroListaDto.Estado), StringComparison.Ordinal);

        if (necesitaEstadoCompleto)
        {
            var todas = await consulta
                .Select(x => new FilaCentro(
                    x.centro.Id, x.centro.Nombre, x.centro.CodigoCentro, x.centro.ClienteId,
                    x.cliente.RazonSocial, x.centro.EmpresaId, x.empresa.RazonSocial))
                .ToListAsync(cancellationToken);

            var idsTodas = todas.Select(c => c.Id).ToList();
            var estadosTodas = await calculoEstadoCentro.CalcularAsync(idsTodas, cancellationToken);
            var cumplimientoTodas = await calculoEstadoCentro.CalcularCumplimientoAsync(idsTodas, cancellationToken);
            var conEstado = todas.Select(c => AplicarEstado(c, estadosTodas, cumplimientoTodas));

            if (request.Estado is not null)
                conEstado = conEstado.Where(c => c.Estado == request.Estado);

            var ordenados = OrdenarEnMemoria(conEstado, request.OrdenarPor, request.Descendente)
                .ThenBy(c => c.Id)
                .ToList();

            return new ResultadoPaginado<CentroListaDto>(
                ordenados.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList(),
                ordenados.Count,
                request.Pagina,
                request.TamanoPagina);
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables: un OrdenarPor desconocido cae
        // al orden por defecto en vez de fallar o de acabar interpolado en
        // SQL. Los nombres son los de las propiedades de CentroListaDto, que
        // es lo que QuickGrid envía. Se ordena sobre las entidades del join,
        // no sobre la proyección a FilaCentro: EF Core no traduce un OrderBy
        // que llega después de un Select con constructor (a diferencia de un
        // tipo anónimo), así que la proyección va al final, tras paginar.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(CentroListaDto.Nombre), false) => consulta.OrderBy(x => x.centro.Nombre),
            (nameof(CentroListaDto.Nombre), true) => consulta.OrderByDescending(x => x.centro.Nombre),
            (nameof(CentroListaDto.CodigoCentro), false) => consulta.OrderBy(x => x.centro.CodigoCentro),
            (nameof(CentroListaDto.CodigoCentro), true) => consulta.OrderByDescending(x => x.centro.CodigoCentro),
            (nameof(CentroListaDto.ClienteRazonSocial), false) => consulta.OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre),
            (nameof(CentroListaDto.ClienteRazonSocial), true) => consulta.OrderByDescending(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre),
            (nameof(CentroListaDto.EmpresaRazonSocial), false) => consulta.OrderBy(x => x.empresa.RazonSocial).ThenBy(x => x.centro.Nombre),
            (nameof(CentroListaDto.EmpresaRazonSocial), true) => consulta.OrderByDescending(x => x.empresa.RazonSocial).ThenBy(x => x.centro.Nombre),
            _ => consulta.OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.centro.Id);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new FilaCentro(
                x.centro.Id, x.centro.Nombre, x.centro.CodigoCentro, x.centro.ClienteId,
                x.cliente.RazonSocial, x.centro.EmpresaId, x.empresa.RazonSocial))
            .ToListAsync(cancellationToken);

        var idsPagina = pagina.Select(c => c.Id).ToList();
        var estados = await calculoEstadoCentro.CalcularAsync(idsPagina, cancellationToken);
        var cumplimiento = await calculoEstadoCentro.CalcularCumplimientoAsync(idsPagina, cancellationToken);

        return new ResultadoPaginado<CentroListaDto>(
            pagina.Select(c => AplicarEstado(c, estados, cumplimiento)).ToList(), total, request.Pagina, request.TamanoPagina);
    }

    private static CentroListaDto AplicarEstado(
        FilaCentro fila, IReadOnlyDictionary<Guid, ResultadoEstadoCentro> estados,
        IReadOnlyDictionary<Guid, FraccionCumplimiento> cumplimiento) =>
        new(fila.Id, fila.Nombre, fila.CodigoCentro, fila.ClienteId, fila.ClienteRazonSocial,
            fila.EmpresaId, fila.EmpresaRazonSocial,
            estados.TryGetValue(fila.Id, out var resultado) ? resultado.Estado : EstadoCentro.Vigente,
            cumplimiento.TryGetValue(fila.Id, out var fraccion) ? fraccion.Porcentaje : null,
            resultado is null ? RecuentosCentroDto.Vacio : Desglosar(resultado));

    /// <summary>
    /// Las causas ya venían calculadas para decidir el estado del centro; aquí
    /// solo se agrupan por estado. No hay consulta nueva: es el mismo dato que
    /// ya viajaba, que hasta ahora la lista descartaba.
    /// "Faltante" cuenta como vencido — un requisito sin documento no está al
    /// día, y el lexico cerrado no tiene una tercera casilla en la fila.
    /// </summary>
    private static RecuentosCentroDto Desglosar(ResultadoEstadoCentro resultado)
    {
        var vencidas = new List<IncidenciaCentroDto>();
        var proximas = new List<IncidenciaCentroDto>();

        foreach (var causa in resultado.Causas)
        {
            var incidencia = new IncidenciaCentroDto(causa.Descripcion, causa.Ambito);
            switch (causa.Estado)
            {
                case EstadoDocumento.Vencido or EstadoDocumento.Faltante:
                    vencidas.Add(incidencia);
                    break;
                case EstadoDocumento.Proximo:
                    proximas.Add(incidencia);
                    break;
            }
        }

        return new RecuentosCentroDto(vencidas, proximas);
    }

    private static IOrderedEnumerable<CentroListaDto> OrdenarEnMemoria(
        IEnumerable<CentroListaDto> elementos, string? ordenarPor, bool descendente) =>
        (ordenarPor, descendente) switch
        {
            (nameof(CentroListaDto.Nombre), false) => elementos.OrderBy(x => x.Nombre),
            (nameof(CentroListaDto.Nombre), true) => elementos.OrderByDescending(x => x.Nombre),
            (nameof(CentroListaDto.CodigoCentro), false) => elementos.OrderBy(x => x.CodigoCentro),
            (nameof(CentroListaDto.CodigoCentro), true) => elementos.OrderByDescending(x => x.CodigoCentro),
            (nameof(CentroListaDto.ClienteRazonSocial), false) => elementos.OrderBy(x => x.ClienteRazonSocial).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.ClienteRazonSocial), true) => elementos.OrderByDescending(x => x.ClienteRazonSocial).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.EmpresaRazonSocial), false) => elementos.OrderBy(x => x.EmpresaRazonSocial).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.EmpresaRazonSocial), true) => elementos.OrderByDescending(x => x.EmpresaRazonSocial).ThenBy(x => x.Nombre),
            // El orden del enum va de mejor a peor (Vigente … Bloqueado), así
            // que descendente deja arriba lo que más urge — que es lo que el
            // gestor espera al ordenar por cumplimiento.
            (nameof(CentroListaDto.Estado), false) => elementos.OrderBy(x => x.Estado).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.Estado), true) => elementos.OrderByDescending(x => x.Estado).ThenBy(x => x.Nombre),
            _ => elementos.OrderBy(x => x.ClienteRazonSocial).ThenBy(x => x.Nombre)
        };

    private record FilaCentro(
        Guid Id, string Nombre, string? CodigoCentro, Guid ClienteId, string ClienteRazonSocial,
        Guid EmpresaId, string EmpresaRazonSocial);
}
