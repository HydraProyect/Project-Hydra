using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;

public record ObtenerTrabajadoresQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<TrabajadorListaDto>>;

public record TrabajadorListaDto(Guid Id, string Nombre, string Apellidos, string Dni, string EmpleadorNombre);

public class ObtenerTrabajadoresQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTrabajadoresQuery, ResultadoPaginado<TrabajadorListaDto>>
{
    public async Task<ResultadoPaginado<TrabajadorListaDto>> Handle(
        ObtenerTrabajadoresQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from trabajador in dbContext.Trabajadores
            join empresa in dbContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in dbContext.Subcontratas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new { trabajador, EmpleadorNombre = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial };

        // Este es el listado (tabla /trabajadores), no el selector de "elige
        // un trabajador ya existente" — se acota a los que tienen una
        // Asignación activa en un Centro visible (ver IAlcanceDatosService).
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            consulta = consulta.Where(x => trabajadorIdsVisibles.Contains(x.trabajador.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.trabajador.Nombre.ToUpper().Contains(busqueda) ||
                x.trabajador.Apellidos.ToUpper().Contains(busqueda) ||
                x.trabajador.Dni.ToUpper().Contains(busqueda));
        }

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.trabajador.EmpresaId == request.EmpresaId);

        if (request.SubcontrataId is not null)
            consulta = consulta.Where(x => x.trabajador.SubcontrataId == request.SubcontrataId);

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new TrabajadorListaDto(x.trabajador.Id, x.trabajador.Nombre, x.trabajador.Apellidos, x.trabajador.Dni, x.EmpleadorNombre))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<TrabajadorListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
