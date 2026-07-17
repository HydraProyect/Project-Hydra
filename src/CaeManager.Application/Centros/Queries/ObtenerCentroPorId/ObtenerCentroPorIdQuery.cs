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
    DateOnly? ContratoVigenteHasta);

public class ObtenerCentroPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerCentroPorIdQuery, CentroDetalleDto?>
{
    public Task<CentroDetalleDto?> Handle(ObtenerCentroPorIdQuery request, CancellationToken cancellationToken) =>
        (from centro in dbContext.Centros
         join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
         join empresa in dbContext.Empresas on centro.EmpresaId equals empresa.Id
         where centro.Id == request.Id
         select new CentroDetalleDto(
             centro.Id, centro.ClienteId, cliente.RazonSocial, centro.EmpresaId, empresa.RazonSocial, centro.Nombre,
             centro.CodigoCentro, centro.Direccion, centro.Contacto, centro.ContratoVigenteHasta))
        .FirstOrDefaultAsync(cancellationToken);
}
