using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoTratamientoIaActual;

/// <summary>
/// Estado de solo lectura del Nivel 0 para el tenant efectivo del ámbito actual.
/// La fecha de vigencia no forma parte del contrato de
/// <see cref="IInstruccionTratamientoIaService"/>: solo se expone si hay una
/// instrucción vigente.
/// <para>
/// <c>InstruccionVigente</c> mide solo la instrucción: una objeción a un subencargado (#2010, D-7) no la revoca.
/// <c>IaExcluidaPorObjecion</c> dice que, con la instrucción vigente, la IA está apagada para este Tenant por su
/// objeción a un subencargado activo.
/// </para>
/// </summary>
public record EstadoTratamientoIaActualDto(bool InstruccionVigente, bool IaExcluidaPorObjecion = false);

[AreaDelEncargo("Cumplimiento")]
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

        var vigente = await instruccionTratamientoIa.TieneInstruccionVigenteAsync(tenantId, cancellationToken);
        return new EstadoTratamientoIaActualDto(
            vigente,
            IaExcluidaPorObjecion: vigente && !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken));
    }
}
