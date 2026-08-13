using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;

/// <summary>
/// <paramref name="Activa"/> es el filtro de estado de la pantalla: una
/// Asignación no tiene columna de estado — "activa" es no tener
/// <c>FechaBaja</c> (ver <c>DOMAIN.md</c>), así que el filtro se traduce a esa
/// condición en SQL en vez de inventar un enum en el dominio.
/// </summary>
public record ObtenerAsignacionesQuery(
    string? Busqueda, bool? Activa = null, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<AsignacionListaDto>>;

public record AsignacionListaDto(
    Guid Id,
    Guid TrabajadorId,
    string TrabajadorNombre,
    Guid CentroId,
    string CentroNombre,
    Guid ClienteId,
    string ClienteNombre,
    DateOnly FechaAlta,
    DateOnly? FechaBaja);

public class ObtenerAsignacionesQueryHandler(IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAsignacionesQuery, ResultadoPaginado<AsignacionListaDto>>
{
    public async Task<ResultadoPaginado<AsignacionListaDto>> Handle(
        ObtenerAsignacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from asignacion in asignacionesContext.Asignaciones
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            select new { asignacion, trabajador, centro, cliente };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.trabajador.Nombre.ToUpper().Contains(busqueda) ||
                x.trabajador.Apellidos.ToUpper().Contains(busqueda) ||
                x.centro.Nombre.ToUpper().Contains(busqueda));
        }

        if (request.Activa is not null)
            consulta = request.Activa.Value
                ? consulta.Where(x => x.asignacion.FechaBaja == null)
                : consulta.Where(x => x.asignacion.FechaBaja != null);

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(AsignacionListaDto.TrabajadorNombre), false) => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(AsignacionListaDto.TrabajadorNombre), true) => consulta.OrderByDescending(x => x.trabajador.Apellidos).ThenByDescending(x => x.trabajador.Nombre),
            (nameof(AsignacionListaDto.CentroNombre), false) => consulta.OrderBy(x => x.centro.Nombre),
            (nameof(AsignacionListaDto.CentroNombre), true) => consulta.OrderByDescending(x => x.centro.Nombre),
            (nameof(AsignacionListaDto.ClienteNombre), false) => consulta.OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre),
            (nameof(AsignacionListaDto.ClienteNombre), true) => consulta.OrderByDescending(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre),
            (nameof(AsignacionListaDto.FechaAlta), false) => consulta.OrderBy(x => x.asignacion.FechaAlta),
            // Una asignación activa (sin FechaBaja) va primero en ascendente:
            // es lo vigente, y es lo que el gestor quiere ver arriba.
            (nameof(AsignacionListaDto.FechaBaja), false) => consulta.OrderBy(x => x.asignacion.FechaBaja == null ? 0 : 1).ThenBy(x => x.asignacion.FechaBaja),
            (nameof(AsignacionListaDto.FechaBaja), true) => consulta.OrderByDescending(x => x.asignacion.FechaBaja == null ? 0 : 1).ThenByDescending(x => x.asignacion.FechaBaja),
            _ => consulta.OrderByDescending(x => x.asignacion.FechaAlta)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.asignacion.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new AsignacionListaDto(
                x.asignacion.Id,
                x.trabajador.Id,
                x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.centro.Id,
                x.centro.Nombre,
                x.cliente.Id,
                x.cliente.RazonSocial,
                x.asignacion.FechaAlta,
                x.asignacion.FechaBaja))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<AsignacionListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
