using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentos;

public record ObtenerDocumentosQuery(Guid? TrabajadorId, string? Busqueda, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<DocumentoListaDto>>;

public record DocumentoListaDto(
    Guid Id,
    Guid TrabajadorId,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl);

/// <summary>
/// El semáforo (Estado) se calcula en memoria con CalculadoraEstadoDocumento
/// — la misma función que usan el Dashboard y RenovarDocumentoCommand — para
/// que nunca haya dos sitios de la aplicación mostrando vigencias distintas
/// (ver DATABASE.md).
/// </summary>
public class ObtenerDocumentosQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerDocumentosQuery, ResultadoPaginado<DocumentoListaDto>>
{
    public async Task<ResultadoPaginado<DocumentoListaDto>> Handle(ObtenerDocumentosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from documento in dbContext.Documentos
            join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { documento, trabajador, tipoDocumento };

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.documento.TrabajadorId == request.TrabajadorId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.trabajador.Nombre.ToUpper().Contains(busqueda) ||
                x.trabajador.Apellidos.ToUpper().Contains(busqueda) ||
                x.tipoDocumento.Nombre.ToUpper().Contains(busqueda));
        }

        var total = await consulta.CountAsync(cancellationToken);

        var filas = await consulta
            .OrderByDescending(x => x.documento.FechaEmision)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new
            {
                x.documento.Id,
                x.documento.TrabajadorId,
                TrabajadorNombre = x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.tipoDocumento.Nombre,
                x.documento.FechaEmision,
                x.documento.FechaVencimiento,
                x.documento.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var elementos = filas
            .Select(f => new DocumentoListaDto(
                f.Id, f.TrabajadorId, f.TrabajadorNombre, f.Nombre, f.FechaEmision, f.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                f.ArchivoUrl))
            .ToList();

        return new ResultadoPaginado<DocumentoListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
