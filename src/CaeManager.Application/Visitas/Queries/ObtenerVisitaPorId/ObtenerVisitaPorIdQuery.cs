using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Application.Visitas;
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
    string? Notas,
    TimeOnly? HoraEstimadaAcceso,
    Guid Version);

public class ObtenerVisitaPorIdQueryHandler(ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVisitaPorIdQuery, VisitaDetalleDto?>
{
    public async Task<VisitaDetalleDto?> Handle(ObtenerVisitaPorIdQuery request, CancellationToken cancellationToken)
    {
        var visita = await (
            from v in visitasContext.Visitas
            join centro in centrosContext.Centros on v.CentroId equals centro.Id
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
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
                v.Notas,
                v.HoraEstimadaAcceso,
                v.Version
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return null;
        if (!await alcanceDatos.CentroVisibleAsync(visita.CentroId, cancellationToken)) return null;

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.Id)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        return new VisitaDetalleDto(
            visita.Id, visita.CentroId, visita.CentroNombre, visita.ClienteRazonSocial, visita.EmpresaRazonSocial,
            visita.FechaInicio, visita.FechaFin, trabajadorIds, visita.NotificadoCliente, visita.Notas, visita.HoraEstimadaAcceso, visita.Version);
    }
}
