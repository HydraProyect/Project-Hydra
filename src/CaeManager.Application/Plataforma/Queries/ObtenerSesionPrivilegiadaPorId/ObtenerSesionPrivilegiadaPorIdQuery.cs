using CaeManager.Domain.Plataforma;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Queries.ObtenerSesionPrivilegiadaPorId;

/// <summary>
/// El detalle de una sesión privilegiada, para mostrar qué se va a cerrar
/// —tenant, motivo, desde cuándo, hasta cuándo— en la pantalla de cierre de
/// <c>/cuenta/soporte/salir</c> (H-2, plan de sesiones nocturnas 2026-09-02,
/// DEC-2: "observación con TTL").
/// </summary>
/// <param name="TenantObjetivoId">El tenant cuyos datos abre.</param>
/// <param name="Capacidad">Qué permite hacer.</param>
public record SesionPrivilegiadaDetalleDto(
    Guid SesionId,
    Guid TenantObjetivoId,
    CapacidadPrivilegio Capacidad,
    string Motivo,
    string? Ticket,
    DateTime InicioEnUtc,
    DateTime ExpiraEnUtc);

/// <summary>
/// Busca UNA sesión por Id, no una lista.
///
/// <b>Por qué esto no es el "listar sesiones" que
/// <see cref="IPlataformaQueryContext"/> declara que no debe existir.</b> Esa
/// frase habla de un catálogo navegable —"muéstrame todas las mías"—; esto es
/// una búsqueda puntual por un Id que ya se conoce de antemano (el que trae
/// <c>/cuenta/soporte/salir</c> en la redirección de salida, ver
/// <c>SesionSoporteEndpoints.SalirAsync</c>), exactamente el mismo patrón que
/// <c>ISesionPrivilegiadaActual</c> ya tenía autorizado: resolver LA sesión que
/// algo externo nombra, nunca enumerar. El Id no es autoridad —lo es la fila
/// que RLS entrega o no entrega—: si el Id fuera ajeno o inventado, la política
/// <c>privilegio_del_usuario</c> (F2b-5) no devuelve ninguna fila y el método
/// responde <c>null</c>, indistinguible de "no existe".
/// </summary>
public record ObtenerSesionPrivilegiadaPorIdQuery(Guid SesionPrivilegiadaId)
    : IRequest<SesionPrivilegiadaDetalleDto?>;

public class ObtenerSesionPrivilegiadaPorIdQueryHandler(IPlataformaQueryContext plataformaContext)
    : IRequestHandler<ObtenerSesionPrivilegiadaPorIdQuery, SesionPrivilegiadaDetalleDto?>
{
    public Task<SesionPrivilegiadaDetalleDto?> Handle(
        ObtenerSesionPrivilegiadaPorIdQuery request, CancellationToken cancellationToken) =>
        (from sesion in plataformaContext.SesionesPrivilegiadas
         join concesion in plataformaContext.ConcesionesPrivilegio
             on sesion.ConcesionPrivilegioId equals concesion.Id
         where sesion.Id == request.SesionPrivilegiadaId
         select new SesionPrivilegiadaDetalleDto(
             sesion.Id, sesion.TenantObjetivoId, concesion.Capacidad,
             sesion.Motivo, sesion.Ticket, sesion.InicioEnUtc, sesion.ExpiraEnUtc))
        .SingleOrDefaultAsync(cancellationToken);
}
