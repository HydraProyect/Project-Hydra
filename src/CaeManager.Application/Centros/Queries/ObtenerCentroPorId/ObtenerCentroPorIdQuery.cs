using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentroPorId;

public record ObtenerCentroPorIdQuery(Guid Id) : IRequest<CentroDetalleDto?>;

public record CentroDetalleDto(
    Guid Id,
    Guid ClienteId,
    string ClienteRazonSocial,
    Guid EmpresaId,
    string EmpresaRazonSocial,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta,
    Guid Version);

public class ObtenerCentroPorIdQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentroPorIdQuery, CentroDetalleDto?>
{
    public async Task<CentroDetalleDto?> Handle(ObtenerCentroPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.Id, cancellationToken)) return null;

        return await (
            from centro in dbContext.Centros
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in dbContext.Empresas on centro.EmpresaId equals empresa.Id
            where centro.Id == request.Id
            select new CentroDetalleDto(
                centro.Id, centro.ClienteId, cliente.RazonSocial, centro.EmpresaId, empresa.RazonSocial, centro.Nombre,
                centro.CodigoCentro, centro.Direccion, centro.Contacto, centro.ContratoVigenteHasta, centro.Version))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
