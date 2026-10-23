using CaeManager.Application.Common;
using CaeManager.Domain.Plataforma;
using MediatR;

namespace CaeManager.Application.Plataforma.Queries.ObtenerIdentidadPlataforma;

/// <summary>
/// El contexto de identidad que la pantalla de administración global muestra
/// en modo lectura. No autoriza nada: la sesión privilegiada se revalida en
/// los puntos que mutan y la vía del registro de auditoría solo explica el
/// origen del contexto actual.
/// </summary>
public record IdentidadPlataformaDto(
    Guid? ActorRealUsuarioId,
    Guid? UsuarioSimuladoId,
    TipoViaAcceso ViaAcceso,
    Guid? ViaAccesoId,
    CapacidadPrivilegio? CapacidadActiva);

public record ObtenerIdentidadPlataformaQuery : IRequest<IdentidadPlataformaDto>;

public class ObtenerIdentidadPlataformaQueryHandler(
    IActorAuditoria actorAuditoria,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ObtenerIdentidadPlataformaQuery, IdentidadPlataformaDto>
{
    public async Task<IdentidadPlataformaDto> Handle(
        ObtenerIdentidadPlataformaQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorAuditoria.ObtenerAsync();
        var sesion = await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken);

        // Una concesión no es una sesión: solo la última sesión resuelta para
        // esta lectura representa una capacidad ejercida. Por eso la
        // coordenada de vía, cuando no hay sesión resuelta, nunca se convierte
        // aquí en capacidad. Esta consulta no revalida: la autorización de
        // escrituras la revalida en el punto de mutación.
        if (sesion is not { } activa)
            return new IdentidadPlataformaDto(
                actor.ActorRealUsuarioId, null, actor.Via, actor.ViaAccesoId, null);

        return new IdentidadPlataformaDto(
            actor.ActorRealUsuarioId,
            activa.UsuarioSimuladoId,
            TipoViaAcceso.SesionPrivilegiada,
            activa.SesionId,
            activa.Capacidad);
    }
}
