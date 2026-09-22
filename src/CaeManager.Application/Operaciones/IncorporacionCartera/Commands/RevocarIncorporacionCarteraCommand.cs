using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Commands;

/// <summary>
/// Retira la cartera que un Gestor CAE obtuvo por una solicitud aceptada. La
/// puede revocar un Coordinador CAE del mismo Operador CAE, o el propio
/// Gestor CAE sobre la suya; nadie más.
///
/// <para>
/// En el circuito de quien revoca, el alcance se invalida al terminar
/// (<see cref="InvalidacionAlcanceBehavior"/>). En un circuito <b>distinto</b>
/// ya abierto por el Gestor CAE, el alcance memoizado sigue vivo hasta que ese
/// circuito termine un Command o se cierre: mientras tanto, la lectura y los
/// Commands que autorizan con <c>IAlcanceDatosService</c> todavía ven la
/// cartera retirada. El rol efectivo dentro del workspace sí se consulta sin
/// memo. Cerrar ese hueco no depende de este Command, sino del alcance.
/// </para>
/// </summary>
public record RevocarIncorporacionCarteraCommand(Guid SolicitudId) : ICommand;

public class RevocarIncorporacionCarteraCommandHandler(
    ICurrentUserService currentUserService,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio,
    INotificacionUsuarioRepository notificaciones,
    ITenantsQueryContext tenants,
    IUnitOfWork unitOfWork,
    ILogger<RevocarIncorporacionCarteraCommandHandler> logger)
    : IRequestHandler<RevocarIncorporacionCarteraCommand, Result>
{
    public async Task<Result> Handle(RevocarIncorporacionCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService);
        if (contexto.EsFallido) return Result.Fallo(contexto.Error);
        var ctx = contexto.Valor;

        using (ctx.EnOrigen())
        {
            var solicitud = await repositorio.ObtenerPorIdAsync(request.SolicitudId, ctx.OperadorTenantId, cancellationToken);
            if (solicitud is null)
                return Result.Fallo(ErroresSolicitudCartera.NoEncontrada);

            var esLaSuya = solicitud.SolicitanteUsuarioId == ctx.UsuarioId;
            if (!ctx.EsCoordinadorCae && !esLaSuya)
                return Result.Fallo(ErroresSolicitudCartera.SinPermiso);

            if (solicitud.Estado != EstadoSolicitudIncorporacionCartera.Aceptada)
                return Result.Fallo(ErroresSolicitudCartera.NoRevocable);

            // Mismo motivo que al aceptar: la cartera se cierra en el Tenant
            // propietario, y el Guid sale de la solicitud ya cargada y
            // autorizada. Cierre, borrado de la fila heredada y revocación, en
            // un solo guardado.
            using (AmbitoTenantExplicito.Establecer(solicitud.PropietarioTenantId))
            {
                await catalogo.RetirarAsync(solicitud, cancellationToken);
                solicitud.Revocar(ctx.UsuarioId, DateTime.UtcNow);

                if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                    return Result.Fallo(ErroresSolicitudCartera.YaResuelta);
            }

            if (!esLaSuya)
                await NotificarAlGestorAsync(solicitud, cancellationToken);

            return Result.Exito();
        }
    }

    /// <summary>Best-effort, como en la aceptación: la revocación ya está hecha.</summary>
    private async Task NotificarAlGestorAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken)
    {
        try
        {
            var empresa = await tenants.Tenants
                .Where(t => t.Id == solicitud.PropietarioTenantId)
                .Select(t => t.Nombre)
                .FirstOrDefaultAsync(cancellationToken) ?? "la empresa";

            notificaciones.Agregar(new NotificacionUsuario(
                solicitud.SolicitanteUsuarioId,
                "Empresa retirada de tu cartera",
                $"Un Coordinador CAE ha retirado «{empresa}» de tu cartera.",
                RutasIncorporacionCartera.Bandeja,
                "Ver mis solicitudes"));

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            catalogo.DescartarPendientes();
            logger.LogWarning(ex,
                "La incorporación a cartera {SolicitudId} se revocó, pero no se pudo avisar al Gestor CAE.",
                solicitud.Id);
        }
    }
}
