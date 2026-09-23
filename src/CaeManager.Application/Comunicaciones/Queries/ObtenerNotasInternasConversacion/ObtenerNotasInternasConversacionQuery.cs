using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerNotasInternasConversacion;

public record ObtenerNotasInternasConversacionQuery(Guid ConversacionId) : IRequest<IReadOnlyList<NotaInternaDetalleDto>>;

/// <summary>
/// <paramref name="Texto"/> es texto plano: se pinta como texto, nunca como
/// <c>MarkupString</c>. <paramref name="AutorUsuarioId"/> se resuelve a nombre
/// en la capa Web con el directorio del tenant.
/// </summary>
public record NotaInternaDetalleDto(Guid Id, Guid AutorUsuarioId, string Texto, DateTime FechaUtc);

/// <summary>
/// Notas internas de una conversación, en orden cronológico. Consulta aparte
/// del detalle (<c>ObtenerConversacionPorIdQuery</c>) a propósito: la nota
/// tiene una audiencia más estrecha que el hilo, y mezclarla en el mismo DTO
/// obligaría a recortarla dentro de un lector que hoy sirve a más roles.
///
/// <b>Matriz de visibilidad</b> (decisiones D3 del MVP de mensajería interna):
/// <list type="bullet">
/// <item>Administrador (del Tenant propietario), DireccionCae, CoordinadorCae y
/// GestorCae — los roles operativos — ven las notas de los hilos que tienen en
/// alcance, con el mismo criterio que el propio hilo.</item>
/// <item>Consulta y Cliente no las ven, aunque Consulta sí vea el hilo: la nota
/// es conversación del equipo que opera, no registro del caso.</item>
/// <item>Ninguna sesión privilegiada de plataforma (Soporte TALVEG,
/// impersonación, break-glass, administración de plataforma, aprovisionamiento)
/// las ve. D3-Soporte admite una concesión expresa, pero ese camino no existe
/// todavía; hasta que exista, se deniega a todas sin excepción.</item>
/// </list>
/// Se deniega devolviendo lista vacía, igual que un hilo sin notas: la
/// respuesta no delata si las hay.
/// </summary>
public class ObtenerNotasInternasConversacionQueryHandler(
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ObtenerNotasInternasConversacionQuery, IReadOnlyList<NotaInternaDetalleDto>>
{
    // Mismos literales que AutorizacionEscrituraBehavior, mismo motivo:
    // Application no puede referenciar Infrastructure.Identity.Roles.
    private static readonly string[] RolesQueVenNotasInternas =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<IReadOnlyList<NotaInternaDetalleDto>> Handle(
        ObtenerNotasInternasConversacionQuery request, CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null)
            return [];

        var rol = await currentUserService.ObtenerRolActualAsync();
        if (rol is null || !RolesQueVenNotasInternas.Contains(rol))
            return [];

        var conversacion = await comunicacionesContext.Conversaciones
            .Where(c => c.Id == request.ConversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return [];

        return await comunicacionesContext.NotasInternasConversacion
            .Where(n => n.ConversacionId == request.ConversacionId)
            .OrderBy(n => n.FechaUtc)
            .Select(n => new NotaInternaDetalleDto(n.Id, n.AutorUsuarioId, n.Texto, n.FechaUtc))
            .ToListAsync(cancellationToken);
    }
}
