using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Gestiones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones.Queries.ObtenerGestiones;

public record ObtenerGestionesQuery(string? Busqueda, EstadoGestion? Estado, Guid? TrabajadorId, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<GestionListaDto>>;

public record GestionListaDto(
    Guid Id, Guid TrabajadorId, string TrabajadorNombre, Guid CentroId, string CentroNombre,
    Guid TipoDocumentoId, string TipoDocumentoNombre, EstadoGestion Estado, DateTime CreadoEnUtc);

public class ObtenerGestionesQueryHandler(
    IGestionesQueryContext gestionesContext, ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
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

        if (request.Estado is not null)
            consulta = consulta.Where(x => x.gestion.Estado == request.Estado);

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.gestion.TrabajadorId == request.TrabajadorId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                (x.trabajador.Nombre + " " + x.trabajador.Apellidos).ToUpper().Contains(busqueda) ||
                x.centro.Nombre.ToUpper().Contains(busqueda) ||
                x.tipoDocumento.Nombre.ToUpper().Contains(busqueda));
        }

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderByDescending(x => x.gestion.CreadoEnUtc)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new GestionListaDto(
                x.gestion.Id, x.trabajador.Id, x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.centro.Id, x.centro.Nombre, x.tipoDocumento.Id, x.tipoDocumento.Nombre,
                x.gestion.Estado, x.gestion.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<GestionListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
