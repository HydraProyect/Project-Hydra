using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentros;

public record ObtenerCentrosQuery(
    string? Busqueda, Guid? ClienteId, EstadoCentro? Estado = null,
    string? OrdenarPor = null, bool Descendente = false, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<CentroListaDto>>;

public record CentroListaDto(
    Guid Id, string Nombre, string? CodigoCentro, Guid ClienteId, string ClienteRazonSocial, string EmpresaRazonSocial,
    EstadoCentro Estado);

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

        var proyeccion = consulta.Select(x => new FilaCentro(
            x.centro.Id,
            x.centro.Nombre,
            x.centro.CodigoCentro,
            x.centro.ClienteId,
            x.cliente.RazonSocial,
            x.empresa.RazonSocial));

        var necesitaEstadoCompleto =
            request.Estado is not null ||
            string.Equals(request.OrdenarPor, nameof(CentroListaDto.Estado), StringComparison.Ordinal);

        return necesitaEstadoCompleto
            ? await ResolverConEstadoAsync(proyeccion, request, cancellationToken)
            : await ResolverEnSqlAsync(proyeccion, request, cancellationToken);
    }

    private async Task<ResultadoPaginado<CentroListaDto>> ResolverEnSqlAsync(
        IQueryable<FilaCentro> proyeccion, ObtenerCentrosQuery request, CancellationToken cancellationToken)
    {
        var total = await proyeccion.CountAsync(cancellationToken);

        // ThenBy(Id) cierra el orden: sin un criterio total, las filas
        // empatadas pueden salir en distinto orden entre página y página y,
        // al paginar en SQL, una fila se repetiría o no aparecería nunca.
        var pagina = await Ordenar(proyeccion, request.OrdenarPor, request.Descendente)
            .ThenBy(c => c.Id)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .ToListAsync(cancellationToken);

        var estados = await calculoEstadoCentro.CalcularAsync(pagina.Select(c => c.Id).ToList(), cancellationToken);

        return new ResultadoPaginado<CentroListaDto>(
            pagina.Select(c => AplicarEstado(c, estados)).ToList(), total, request.Pagina, request.TamanoPagina);
    }

    private async Task<ResultadoPaginado<CentroListaDto>> ResolverConEstadoAsync(
        IQueryable<FilaCentro> proyeccion, ObtenerCentrosQuery request, CancellationToken cancellationToken)
    {
        var filas = await proyeccion.ToListAsync(cancellationToken);
        var estados = await calculoEstadoCentro.CalcularAsync(filas.Select(c => c.Id).ToList(), cancellationToken);

        var conEstado = filas.Select(c => AplicarEstado(c, estados));

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

    private static CentroListaDto AplicarEstado(FilaCentro fila, IReadOnlyDictionary<Guid, ResultadoEstadoCentro> estados) =>
        new(fila.Id, fila.Nombre, fila.CodigoCentro, fila.ClienteId, fila.ClienteRazonSocial, fila.EmpresaRazonSocial,
            estados.TryGetValue(fila.Id, out var resultado) ? resultado.Estado : EstadoCentro.Vigente);

    /// <summary>
    /// Lista blanca de columnas ordenables: un <c>OrdenarPor</c> desconocido
    /// cae al orden por defecto en vez de fallar o de acabar interpolado en
    /// SQL. Los nombres son los de las propiedades de
    /// <see cref="CentroListaDto"/>, que es lo que QuickGrid envía.
    /// </summary>
    private static IOrderedQueryable<FilaCentro> Ordenar(IQueryable<FilaCentro> consulta, string? ordenarPor, bool descendente) =>
        (ordenarPor, descendente) switch
        {
            (nameof(CentroListaDto.Nombre), false) => consulta.OrderBy(x => x.Nombre),
            (nameof(CentroListaDto.Nombre), true) => consulta.OrderByDescending(x => x.Nombre),
            (nameof(CentroListaDto.CodigoCentro), false) => consulta.OrderBy(x => x.CodigoCentro),
            (nameof(CentroListaDto.CodigoCentro), true) => consulta.OrderByDescending(x => x.CodigoCentro),
            (nameof(CentroListaDto.ClienteRazonSocial), false) => consulta.OrderBy(x => x.ClienteRazonSocial).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.ClienteRazonSocial), true) => consulta.OrderByDescending(x => x.ClienteRazonSocial).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.EmpresaRazonSocial), false) => consulta.OrderBy(x => x.EmpresaRazonSocial).ThenBy(x => x.Nombre),
            (nameof(CentroListaDto.EmpresaRazonSocial), true) => consulta.OrderByDescending(x => x.EmpresaRazonSocial).ThenBy(x => x.Nombre),
            _ => consulta.OrderBy(x => x.ClienteRazonSocial).ThenBy(x => x.Nombre)
        };

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
        Guid Id, string Nombre, string? CodigoCentro, Guid ClienteId, string ClienteRazonSocial, string EmpresaRazonSocial);
}
