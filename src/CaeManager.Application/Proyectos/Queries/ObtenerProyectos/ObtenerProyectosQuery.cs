using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerProyectos;

/// <summary>
/// El listado de Proyectos, paginado en servidor. Sin <paramref name="ClienteId"/> lista los Proyectos de
/// todos los Clientes empresariales que el usuario alcanza; con él, solo los de ese Cliente empresarial.
/// La búsqueda y el estado se filtran aquí y no en memoria: sobre una página, un filtro en memoria
/// escondería filas que sí cumplen.
/// </summary>
/// <param name="ClienteId">
/// Cliente empresarial cuyos Proyectos se piden; <c>null</c>, los de todos los visibles. Es un filtro, no
/// una autoridad: uno fuera del alcance devuelve la lista vacía.
/// </param>
/// <param name="SoloAbiertos">
/// <c>true</c>, solo los abiertos (sin fecha de cierre real); <c>false</c>, solo los cerrados; <c>null</c>, todos.
/// </param>
/// <param name="Busqueda">
/// Texto que debe aparecer en el nombre del Proyecto o en el de su Centro, sin distinguir acentos ni
/// mayúsculas y sin contar los espacios de alrededor (<see cref="TextoDeBusqueda.Contiene"/>).
/// </param>
/// <param name="ConRecuentosPorEstado">
/// Rellena <c>ResultadoPaginado.RecuentosPorEstado</c>: Proyectos abiertos y cerrados con los demás filtros
/// aplicados y sin el de estado, para la franja de estado del listado.
/// </param>
public record ObtenerProyectosQuery(
    Guid? ClienteId = null, bool? SoloAbiertos = null, string? Busqueda = null,
    int Pagina = 1, int TamanoPagina = 20, bool ConRecuentosPorEstado = false)
    : IRequest<ResultadoPaginado<ProyectoListaDto>>
{
    /// <summary>Clave de <c>RecuentosPorEstado</c> de los Proyectos abiertos; es también el valor que viaja en la URL.</summary>
    public const string EstadoAbiertos = "abiertos";

    /// <summary>Clave de <c>RecuentosPorEstado</c> de los Proyectos cerrados; es también el valor que viaja en la URL.</summary>
    public const string EstadoCerrados = "cerrados";
}

public record ProyectoListaDto(
    Guid Id,
    string Nombre,
    Guid CentroId,
    string CentroNombre,
    DateOnly FechaInicio,
    DateOnly? FechaFinPrevista,
    DateOnly? FechaCierreReal,
    bool EstaAbierto)
{
    /// <summary>
    /// Cliente empresarial del Proyecto. Al listar los de todos, es lo que distingue dos filas con el
    /// mismo nombre de Proyecto o de Centro.
    /// </summary>
    public Guid ClienteId { get; init; }

    /// <summary>Razón social del Cliente empresarial del Proyecto.</summary>
    public string ClienteRazonSocial { get; init; } = string.Empty;

    /// <summary>
    /// Técnicos con alta vigente en el proyecto, por nombre. Es lo que enseña el recuento de la
    /// fila del listado; el historial completo (bajas incluidas) lo da ObtenerTecnicosProyectoQuery.
    /// </summary>
    public IReadOnlyList<TecnicoActivoListaDto> TecnicosActivos { get; init; } = [];
}

/// <summary>Técnico activo de un proyecto en la lista: la persona, escrita «Nombre Apellidos».</summary>
public record TecnicoActivoListaDto(Guid TrabajadorId, string NombreCompleto);

public class ObtenerProyectosQueryHandler(
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext,
    ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerProyectosQuery, ResultadoPaginado<ProyectoListaDto>>
{
    public async Task<ResultadoPaginado<ProyectoListaDto>> Handle(ObtenerProyectosQuery request, CancellationToken cancellationToken)
    {
        // El join con Empresas es interno a propósito, y Empresas lleva sus filtros de consulta (Tenant
        // propietario y baja lógica): un Proyecto cuyo Cliente empresarial está dado de baja no se lista ni
        // se cuenta, y el filtro de Cliente empresarial de la pantalla tampoco lo ofrece. El detalle no se
        // comporta igual: ObtenerProyectoPorIdQueryHandler lee la razón social con FirstAsync y lanza para
        // ese Proyecto. Desde este listado no se llega a él, porque aquí no sale.
        //
        // Hueco conocido: el join no exige que la Empresa sea Cliente empresarial (EsCritico != null) y el
        // selector del filtro (ObtenerClientesParaSelectorQuery) sí. Un Proyecto cuyo ClienteId apunte a una
        // Empresa con EsCritico nulo sale en «Todos» y no se puede aislar con el filtro. No es una fuga: el
        // alcance filtra por proyecto.ClienteId y el join va por esa misma clave. Igualarlo aquí sería una
        // lectura nueva del rol por discriminador, que el trinquete Discriminador-nulo no admite.
        var consulta =
            from proyecto in proyectosContext.Proyectos
            join centro in centrosContext.Centros on proyecto.CentroId equals centro.Id
            join cliente in empresasContext.Empresas on proyecto.ClienteId equals cliente.Id
            select new { proyecto, centro, cliente };

        // El alcance va en la consulta y no solo como comprobación del Cliente empresarial pedido: sin
        // ClienteId la lista es la de todos, y «todos» son los que este usuario alcanza.
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(x => clienteIdsVisibles.Contains(x.proyecto.ClienteId));

        if (request.ClienteId is { } pedido)
            consulta = consulta.Where(x => x.proyecto.ClienteId == pedido);

        // El criterio es el de todos los listados (TextoDeBusqueda): sin acentos ni mayúsculas, y con «%» y
        // «_» literales. El Cliente empresarial no se busca.
        var busqueda = request.Busqueda?.Trim();
        if (!string.IsNullOrEmpty(busqueda))
        {
            consulta = consulta.Where(x =>
                TextoDeBusqueda.Contiene(x.proyecto.Nombre, busqueda) || TextoDeBusqueda.Contiene(x.centro.Nombre, busqueda));
        }

        // Para la franja de estado: abiertos y cerrados con los demás filtros aplicados y ANTES de filtrar por
        // estado, de modo que cada cifra diga cuántos quedarían al marcar ese estado. Lleva los dos estados
        // aunque uno no tenga ninguno (0): «no hay» no es «no se contó».
        IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
        if (request.ConRecuentosPorEstado)
        {
            var filasPorEstado = await consulta
                .GroupBy(x => x.proyecto.FechaCierreReal == null)
                .Select(grupo => new { EstaAbierto = grupo.Key, Filas = grupo.Count() })
                .ToDictionaryAsync(grupo => grupo.EstaAbierto, grupo => grupo.Filas, cancellationToken);
            recuentosPorEstado = new Dictionary<string, int>
            {
                [ObtenerProyectosQuery.EstadoAbiertos] = filasPorEstado.GetValueOrDefault(true),
                [ObtenerProyectosQuery.EstadoCerrados] = filasPorEstado.GetValueOrDefault(false)
            };
        }

        if (request.SoloAbiertos is { } soloAbiertos)
            consulta = soloAbiertos
                ? consulta.Where(x => x.proyecto.FechaCierreReal == null)
                : consulta.Where(x => x.proyecto.FechaCierreReal != null);

        // Los dos estados parten la lista: con los recuentos ya contados, el total de la selección sale de
        // ellos y no hace falta otra consulta.
        var total = recuentosPorEstado is null
            ? await consulta.CountAsync(cancellationToken)
            : request.SoloAbiertos switch
            {
                true => recuentosPorEstado[ObtenerProyectosQuery.EstadoAbiertos],
                false => recuentosPorEstado[ObtenerProyectosQuery.EstadoCerrados],
                null => recuentosPorEstado.Values.Sum()
            };

        // Desempate estable: sin un criterio total, PostgreSQL puede devolver las filas empatadas en
        // distinto orden entre una página y otra, y al paginar en SQL eso hace que una fila aparezca dos
        // veces o no aparezca nunca. Varios Proyectos con la misma fecha de inicio son lo normal.
        var proyectos = await consulta
            .OrderByDescending(x => x.proyecto.FechaInicio)
            .ThenBy(x => x.proyecto.Id)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new ProyectoListaDto(
                x.proyecto.Id, x.proyecto.Nombre, x.proyecto.CentroId, x.centro.Nombre,
                x.proyecto.FechaInicio, x.proyecto.FechaFinPrevista, x.proyecto.FechaCierreReal, x.proyecto.FechaCierreReal == null)
            {
                ClienteId = x.proyecto.ClienteId,
                ClienteRazonSocial = x.cliente.RazonSocial
            })
            .ToListAsync(cancellationToken);

        if (proyectos.Count > 0)
        {
            // Una sola consulta para los técnicos de la página devuelta (no una por fila, ni los de las
            // páginas que no se enseñan). El join con Trabajadores aplica sus filtros de consulta (Tenant
            // propietario, baja lógica), igual que ObtenerTecnicosProyectoQuery. No se cruza con
            // ObtenerTrabajadorIdsVisiblesAsync: los roles que entran en Proyectos y ven el Cliente
            // empresarial del proyecto alcanzan hoy a todos los Trabajadores del Tenant (ver el comentario
            // de ObtenerTecnicosProyectoQueryHandler y su test bajo RLS). El rol Cliente (portal) no entra
            // en Proyectos: si algún día se le abre esta lectura, hay que cruzarla antes.
            var proyectoIds = proyectos.Select(p => p.Id).ToList();
            var tecnicos = await (
                from proyectoTecnico in proyectosContext.ProyectosTecnicos
                where proyectoIds.Contains(proyectoTecnico.ProyectoId) && proyectoTecnico.FechaBaja == null
                join trabajador in trabajadoresContext.Trabajadores on proyectoTecnico.TrabajadorId equals trabajador.Id
                orderby trabajador.Nombre, trabajador.Apellidos
                select new { proyectoTecnico.ProyectoId, trabajador.Id, Nombre = trabajador.Nombre + " " + trabajador.Apellidos })
                .ToListAsync(cancellationToken);

            var tecnicosPorProyecto = tecnicos.ToLookup(t => t.ProyectoId, t => new TecnicoActivoListaDto(t.Id, t.Nombre));
            proyectos = proyectos
                .Select(p => p with { TecnicosActivos = tecnicosPorProyecto[p.Id].ToList() })
                .ToList();
        }

        return new ResultadoPaginado<ProyectoListaDto>(proyectos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado
        };
    }
}
