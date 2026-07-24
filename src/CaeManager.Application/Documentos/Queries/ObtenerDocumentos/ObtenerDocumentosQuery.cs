using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentos;

public record ObtenerDocumentosQuery(
    Guid? TrabajadorId, AmbitoAplicacion? Ambito, string? Busqueda, EstadoDocumento? Estado = null,
    int Pagina = 1, int TamanoPagina = 20, Guid? PropietarioId = null)
    : IRequest<ResultadoPaginado<DocumentoListaDto>>;

public record DocumentoListaDto(
    Guid Id,
    AmbitoAplicacion Ambito,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl);

/// <summary>
/// Une los Documentos de Trabajador/Cliente/Empresa (cada uno vive en la
/// misma tabla pero con un único FK de propietario relleno) en una sola
/// lista paginada — se resuelven por separado (cada ámbito hace su propio
/// join al propietario que le corresponde) y se combinan con Concat antes
/// de paginar, en vez de un LEFT JOIN triple. El semáforo (Estado) se
/// calcula en memoria con CalculadoraEstadoDocumento — la misma función que
/// usan el Dashboard y RenovarDocumentoCommand — para que nunca haya dos
/// sitios de la aplicación mostrando vigencias distintas (ver DATABASE.md).
/// </summary>
public class ObtenerDocumentosQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentosQuery, ResultadoPaginado<DocumentoListaDto>>
{
    public async Task<ResultadoPaginado<DocumentoListaDto>> Handle(ObtenerDocumentosQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var deTrabajador =
            from documento in dbContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in dbContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Trabajador,
                TrabajadorId = (Guid?)documento.TrabajadorId,
                PropietarioId = (Guid?)documento.TrabajadorId,
                PropietarioNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deCliente =
            from documento in dbContext.Documentos
            where documento.ClienteId != null
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(documento.ClienteId!.Value)
            join cliente in dbContext.Clientes on documento.ClienteId!.Value equals cliente.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Cliente,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ClienteId,
                PropietarioNombre = cliente.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deEmpresa =
            from documento in dbContext.Documentos
            where documento.EmpresaId != null
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join empresa in dbContext.Empresas on documento.EmpresaId!.Value equals empresa.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Empresa,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.EmpresaId,
                PropietarioNombre = empresa.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deVehiculo =
            from documento in dbContext.Documentos
            where documento.VehiculoId != null
            where vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(documento.VehiculoId!.Value)
            join vehiculo in dbContext.Vehiculos on documento.VehiculoId!.Value equals vehiculo.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Vehiculo,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.VehiculoId,
                PropietarioNombre = vehiculo.Nombre + " (" + vehiculo.NumeroPlaca + ")",
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var consulta = deTrabajador.Concat(deCliente).Concat(deEmpresa).Concat(deVehiculo);

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.TrabajadorId == request.TrabajadorId);

        if (request.Ambito is not null)
            consulta = consulta.Where(x => x.Ambito == request.Ambito);

        if (request.PropietarioId is not null)
            consulta = consulta.Where(x => x.PropietarioId == request.PropietarioId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.PropietarioNombre.ToUpper().Contains(busqueda) ||
                x.TipoDocumentoNombre.ToUpper().Contains(busqueda));
        }

        // El Estado se calcula en memoria (nunca en SQL, ver comentario de
        // clase) — cuando se filtra por Estado, la paginación también tiene
        // que hacerse en memoria, después de calcularlo, así que se trae el
        // conjunto ya filtrado por ámbito/búsqueda completo en vez de paginar
        // en SQL. El volumen de Documentos de esta app (miles, no millones)
        // hace esto viable sin necesidad de una columna de estado persistida.
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var todos = await consulta
            .OrderByDescending(x => x.FechaEmision)
            .Select(x => new DocumentoListaDto(
                x.Id, x.Ambito, x.PropietarioNombre, x.TipoDocumentoNombre, x.FechaEmision, x.FechaVencimiento,
                EstadoDocumento.Vigente, x.ArchivoUrl))
            .ToListAsync(cancellationToken);

        var conEstado = todos
            .Select(d => d with
            {
                Estado = CalculadoraEstadoDocumento.Calcular(
                    d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
            });

        var filtrados = request.Estado is not null
            ? conEstado.Where(d => d.Estado == request.Estado)
            : conEstado;

        var listaFiltrada = filtrados.ToList();
        var total = listaFiltrada.Count;

        var elementos = listaFiltrada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .ToList();

        return new ResultadoPaginado<DocumentoListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
