using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Gestiones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones.Queries.ObtenerGestiones;

public record ObtenerGestionesQuery(
    string? Busqueda, EstadoGestion? Estado, Guid? TrabajadorId, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, bool ConRecuentosPorEstado = false)
    : IRequest<ResultadoPaginado<GestionListaDto>>;

/// <summary>
/// Una Gestión del listado. Los cuatro últimos datos dicen DÓNDE hay que hacerla cuando es un trámite en una
/// Plataforma CAE externa: la Gestión no guarda ninguna plataforma (solo Trabajador, Centro y Tipo de documento),
/// pero el Centro cuelga de un Cliente empresarial y la Plataforma CAE es de ese Cliente empresarial
/// (<see cref="Domain.Empresas.PlataformaCaeClienteEmpresarial"/>, caja «Plataforma CAE» de Cliente 360). Se leen,
/// pues, por ese camino; nada se guarda en la Gestión.
/// </summary>
/// <param name="ClienteEmpresarialId">El Cliente empresarial del Centro (<c>Centro.ClienteId</c>, nombre heredado).</param>
/// <param name="ClienteEmpresarialNombre">Su razón social; vacía si el Cliente empresarial no se pudo leer.</param>
/// <param name="PlataformaCaeNombre">Nombre de la Plataforma CAE del Cliente empresarial; <c>null</c> si aún no tiene una registrada.</param>
/// <param name="PlataformaCaeUrlPortal">Dirección del portal (http o https); <c>null</c> si no se registró. Nunca el código de cliente ni accesos.</param>
public record GestionListaDto(
    Guid Id, Guid TrabajadorId, string TrabajadorNombre, Guid CentroId, string CentroNombre,
    Guid TipoDocumentoId, string TipoDocumentoNombre, EstadoGestion Estado, DateTime CreadoEnUtc,
    Guid ClienteEmpresarialId = default, string ClienteEmpresarialNombre = "",
    string? PlataformaCaeNombre = null, string? PlataformaCaeUrlPortal = null);

public class ObtenerGestionesQueryHandler(
    IGestionesQueryContext gestionesContext, ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerGestionesQuery, ResultadoPaginado<GestionListaDto>>
{
    public async Task<ResultadoPaginado<GestionListaDto>> Handle(ObtenerGestionesQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from gestion in gestionesContext.Gestiones
            join trabajador in trabajadoresContext.Trabajadores on gestion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on gestion.CentroId equals centro.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on gestion.TipoDocumentoId equals tipoDocumento.Id
            select new { gestion, trabajador, centro, tipoDocumento };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.gestion.TrabajadorId == request.TrabajadorId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda;
            consulta = consulta.Where(x =>
                TextoDeBusqueda.Contiene(x.trabajador.Nombre + " " + x.trabajador.Apellidos, busqueda) ||
                TextoDeBusqueda.Contiene(x.centro.Nombre, busqueda) ||
                TextoDeBusqueda.Contiene(x.tipoDocumento.Nombre, busqueda));
        }

        // Para la franja de estado del listado: gestiones por estado con los demás filtros aplicados y ANTES de
        // filtrar por estado, de modo que cada cifra diga cuántas quedarían al marcar ese estado. Lleva los dos
        // estados aunque uno no tenga ninguna (0): «no hay» no es «no se contó».
        IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
        if (request.ConRecuentosPorEstado)
        {
            var filasPorEstado = await consulta
                .GroupBy(x => x.gestion.Estado)
                .Select(grupo => new { Estado = grupo.Key, Filas = grupo.Count() })
                .ToDictionaryAsync(grupo => grupo.Estado, grupo => grupo.Filas, cancellationToken);
            recuentosPorEstado = Enum.GetValues<EstadoGestion>()
                .ToDictionary(estado => estado.ToString(), estado => filasPorEstado.GetValueOrDefault(estado));
        }

        if (request.Estado is not null)
            consulta = consulta.Where(x => x.gestion.Estado == request.Estado);

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(GestionListaDto.TrabajadorNombre), false) => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(GestionListaDto.TrabajadorNombre), true) => consulta.OrderByDescending(x => x.trabajador.Apellidos).ThenByDescending(x => x.trabajador.Nombre),
            (nameof(GestionListaDto.CentroNombre), false) => consulta.OrderBy(x => x.centro.Nombre),
            (nameof(GestionListaDto.CentroNombre), true) => consulta.OrderByDescending(x => x.centro.Nombre),
            (nameof(GestionListaDto.TipoDocumentoNombre), false) => consulta.OrderBy(x => x.tipoDocumento.Nombre),
            (nameof(GestionListaDto.TipoDocumentoNombre), true) => consulta.OrderByDescending(x => x.tipoDocumento.Nombre),
            (nameof(GestionListaDto.Estado), false) => consulta.OrderBy(x => x.gestion.Estado).ThenByDescending(x => x.gestion.CreadoEnUtc),
            (nameof(GestionListaDto.Estado), true) => consulta.OrderByDescending(x => x.gestion.Estado).ThenByDescending(x => x.gestion.CreadoEnUtc),
            (nameof(GestionListaDto.CreadoEnUtc), false) => consulta.OrderBy(x => x.gestion.CreadoEnUtc),
            _ => consulta.OrderByDescending(x => x.gestion.CreadoEnUtc)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.gestion.Id);

        // La Plataforma CAE es del Cliente empresarial del Centro (ver GestionListaDto): subconsultas escalares sobre
        // la página, sin unir las dos tablas a la consulta de filtrado ni de recuento.
        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new GestionListaDto(
                x.gestion.Id, x.trabajador.Id, x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.centro.Id, x.centro.Nombre, x.tipoDocumento.Id, x.tipoDocumento.Nombre,
                x.gestion.Estado, x.gestion.CreadoEnUtc,
                x.centro.ClienteId,
                empresasContext.Empresas.Where(e => e.Id == x.centro.ClienteId).Select(e => e.RazonSocial).FirstOrDefault() ?? string.Empty,
                empresasContext.PlataformasCaeClienteEmpresarial
                    .Where(p => p.ClienteEmpresarialId == x.centro.ClienteId).Select(p => p.Nombre).FirstOrDefault(),
                empresasContext.PlataformasCaeClienteEmpresarial
                    .Where(p => p.ClienteEmpresarialId == x.centro.ClienteId).Select(p => p.UrlPortal).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        // Como ObtenerPlataformaCaeClienteQuery: la plataforma con la que se acredita al Cliente empresarial es un
        // artefacto interno de la gestión, no se le enseña a quien no opera desde ese lado.
        if (!await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            elementos = elementos.Select(g => g with { PlataformaCaeNombre = null, PlataformaCaeUrlPortal = null }).ToList();

        return new ResultadoPaginado<GestionListaDto>(elementos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado
        };
    }
}
