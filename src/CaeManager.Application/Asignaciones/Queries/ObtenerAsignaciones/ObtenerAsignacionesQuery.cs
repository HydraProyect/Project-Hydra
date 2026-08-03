using CaeManager.Application.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;

public record ObtenerAsignacionesQuery(string? Busqueda, int Pagina = 1, int TamanoPagina = 20)
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

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderByDescending(x => x.asignacion.FechaAlta)
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
