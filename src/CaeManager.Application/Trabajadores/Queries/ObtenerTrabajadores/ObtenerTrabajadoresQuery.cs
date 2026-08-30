using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;

/// <summary>
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla.
/// Un Trabajador no tiene estado propio en el modelo: se deriva del peor
/// estado de vigencia de sus Documentos con
/// <see cref="ICalculoEstadoDocumentalService"/>. <c>null</c> en el DTO
/// significa que no tiene ningún Documento todavía.
/// </summary>
public record ObtenerTrabajadoresQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null)
    : IRequest<ResultadoPaginado<TrabajadorListaDto>>;

public record TrabajadorListaDto(
    Guid Id, string Nombre, string Apellidos, string? Dni, string EmpleadorNombre,
    EstadoDocumento? EstadoDocumental = null);

public class ObtenerTrabajadoresQueryHandler(IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoDocumentalService calculoEstadoDocumental)
    : IRequestHandler<ObtenerTrabajadoresQuery, ResultadoPaginado<TrabajadorListaDto>>
{
    public async Task<ResultadoPaginado<TrabajadorListaDto>> Handle(
        ObtenerTrabajadoresQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from trabajador in trabajadoresContext.Trabajadores
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
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
                (x.trabajador.Dni ?? "").ToUpper().Contains(busqueda) ||
                (x.trabajador.Alias != null && x.trabajador.Alias.ToUpper().Contains(busqueda)));
        }

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.trabajador.EmpresaId == request.EmpresaId);

        if (request.SubcontrataId is not null)
            consulta = consulta.Where(x => x.trabajador.SubcontrataId == request.SubcontrataId);

        var proyeccion = consulta.Select(x => new TrabajadorListaDto(
            x.trabajador.Id, x.trabajador.Nombre, x.trabajador.Apellidos, x.trabajador.Dni, x.EmpleadorNombre));

        // El estado documental se deriva de los Documentos, así que filtrar u
        // ordenar por él exige conocerlo de todos los que pasan los filtros
        // antes de paginar — mismo reparto en dos caminos que
        // ObtenerCentrosQuery, y por el mismo motivo (el estado no está
        // persistido, ver DATABASE.md).
        var necesitaEstadoCompleto =
            !string.IsNullOrWhiteSpace(request.EstadoDocumental) ||
            string.Equals(request.OrdenarPor, nameof(TrabajadorListaDto.EstadoDocumental), StringComparison.Ordinal);

        if (necesitaEstadoCompleto)
        {
            var todos = await proyeccion.ToListAsync(cancellationToken);
            var estadosTodos = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
                AmbitoAplicacion.Trabajador, todos.Select(t => t.Id).ToList(), cancellationToken);

            var conEstado = todos
                .Select(t => t with { EstadoDocumental = estadosTodos.GetValueOrDefault(t.Id) })
                .Where(t => EstadoDocumentalFiltro.Coincide(t.EstadoDocumental, request.EstadoDocumental));

            var ordenadosEnMemoria = (request.Descendente
                    ? conEstado.OrderByDescending(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental))
                    : conEstado.OrderBy(t => EstadoDocumentalFiltro.ClaveOrden(t.EstadoDocumental)))
                .ThenBy(t => t.Apellidos).ThenBy(t => t.Nombre).ThenBy(t => t.Id)
                .ToList();

            return new ResultadoPaginado<TrabajadorListaDto>(
                ordenadosEnMemoria.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList(),
                ordenadosEnMemoria.Count,
                request.Pagina,
                request.TamanoPagina);
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(TrabajadorListaDto.Apellidos), true) => consulta.OrderByDescending(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(TrabajadorListaDto.Nombre), false) => consulta.OrderBy(x => x.trabajador.Nombre).ThenBy(x => x.trabajador.Apellidos),
            (nameof(TrabajadorListaDto.Nombre), true) => consulta.OrderByDescending(x => x.trabajador.Nombre).ThenBy(x => x.trabajador.Apellidos),
            (nameof(TrabajadorListaDto.Dni), false) => consulta.OrderBy(x => x.trabajador.Dni),
            (nameof(TrabajadorListaDto.Dni), true) => consulta.OrderByDescending(x => x.trabajador.Dni),
            (nameof(TrabajadorListaDto.EmpleadorNombre), false) => consulta.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.trabajador.Apellidos),
            (nameof(TrabajadorListaDto.EmpleadorNombre), true) => consulta.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.trabajador.Apellidos),
            _ => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.trabajador.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new TrabajadorListaDto(x.trabajador.Id, x.trabajador.Nombre, x.trabajador.Apellidos, x.trabajador.Dni, x.EmpleadorNombre))
            .ToListAsync(cancellationToken);

        // Solo para los de la página: el badge de la tabla, no un filtro.
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Trabajador, elementos.Select(t => t.Id).ToList(), cancellationToken);

        return new ResultadoPaginado<TrabajadorListaDto>(
            elementos.Select(t => t with { EstadoDocumental = estados.GetValueOrDefault(t.Id) }).ToList(),
            total, request.Pagina, request.TamanoPagina);
    }
}
