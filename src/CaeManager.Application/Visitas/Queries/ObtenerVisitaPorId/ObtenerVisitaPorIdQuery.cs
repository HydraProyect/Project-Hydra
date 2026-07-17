using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;

public record ObtenerVisitaPorIdQuery(Guid Id) : IRequest<VisitaDetalleDto?>;

public record VisitaDetalleDto(
    Guid Id,
    Guid CentroId,
    string CentroNombre,
    string ClienteRazonSocial,
    string EmpresaRazonSocial,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    IReadOnlyList<Guid> TrabajadorIds,
    bool NotificadoCliente,
    string? Notas);

public class ObtenerVisitaPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerVisitaPorIdQuery, VisitaDetalleDto?>
{
    public async Task<VisitaDetalleDto?> Handle(ObtenerVisitaPorIdQuery request, CancellationToken cancellationToken)
    {
        var visita = await (
            from v in dbContext.Visitas
            join centro in dbContext.Centros on v.CentroId equals centro.Id
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in dbContext.Empresas on centro.EmpresaId equals empresa.Id
            where v.Id == request.Id
            select new
            {
                v.Id,
                v.CentroId,
                CentroNombre = centro.Nombre,
                ClienteRazonSocial = cliente.RazonSocial,
                EmpresaRazonSocial = empresa.RazonSocial,
                v.FechaInicio,
                v.FechaFin,
                v.NotificadoCliente,
                v.Notas
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return null;

        var trabajadorIds = await dbContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.Id)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        return new VisitaDetalleDto(
            visita.Id, visita.CentroId, visita.CentroNombre, visita.ClienteRazonSocial, visita.EmpresaRazonSocial,
            visita.FechaInicio, visita.FechaFin, trabajadorIds, visita.NotificadoCliente, visita.Notas);
    }
}
