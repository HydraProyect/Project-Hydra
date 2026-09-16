using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoTratamientoIaActual;

/// <summary>
/// Estado de solo lectura del Nivel 0 para el tenant efectivo del ámbito actual.
/// La fecha de vigencia no forma parte del contrato de
/// <see cref="IInstruccionTratamientoIaService"/>: solo se expone si hay una
/// instrucción vigente.
/// </summary>
public record EstadoTratamientoIaActualDto(bool InstruccionVigente);

public record ObtenerEstadoTratamientoIaActualQuery : IRequest<EstadoTratamientoIaActualDto>;

public class ObtenerEstadoTratamientoIaActualQueryHandler(
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual)
    : IRequestHandler<ObtenerEstadoTratamientoIaActualQuery, EstadoTratamientoIaActualDto>
{
    public async Task<EstadoTratamientoIaActualDto> Handle(
        ObtenerEstadoTratamientoIaActualQuery request,
        CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId)
            return new EstadoTratamientoIaActualDto(false);

        return new EstadoTratamientoIaActualDto(
            await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken));
    }
}
