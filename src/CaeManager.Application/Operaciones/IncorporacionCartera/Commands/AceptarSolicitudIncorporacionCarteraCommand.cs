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
/// Un Coordinador CAE acepta la solicitud de un Gestor CAE de su mismo
/// Operador CAE. Es lo único que crea la Asignación de Cartera: universal,
/// con rol Gestor CAE y sin caducidad, sumada a las que ya hubiera sobre la
/// misma operación.
/// </summary>
public record AceptarSolicitudIncorporacionCarteraCommand(Guid SolicitudId) : ICommand;

public class AceptarSolicitudIncorporacionCarteraCommandHandler(
    ICurrentUserService currentUserService,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio,
    IDirectorioUsuariosService directorioUsuarios,
    INotificacionUsuarioRepository notificaciones,
    ITenantsQueryContext tenants,
    IUnitOfWork unitOfWork,
    ILogger<AceptarSolicitudIncorporacionCarteraCommandHandler> logger)
    : IRequestHandler<AceptarSolicitudIncorporacionCarteraCommand, Result>
{
    public async Task<Result> Handle(AceptarSolicitudIncorporacionCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido) return Result.Fallo(contexto.Error);
        var ctx = contexto.Valor;

        if (!ctx.EsCoordinadorCae)
            return Result.Fallo(ErroresSolicitudCartera.SinPermiso);

        using (ctx.EnOrigen())
        {
            // Se carga filtrando por el Operador CAE del Coordinador CAE: una
            // solicitud de otro Operador CAE no existe para él.
            var solicitud = await repositorio.ObtenerPorIdAsync(request.SolicitudId, ctx.OperadorTenantId, cancellationToken);
            if (solicitud is null)
                return Result.Fallo(ErroresSolicitudCartera.NoEncontrada);
            if (solicitud.Estado != EstadoSolicitudIncorporacionCartera.Pendiente)
                return Result.Fallo(ErroresSolicitudCartera.YaResuelta);
            if (solicitud.SolicitanteUsuarioId == ctx.UsuarioId)
                return Result.Fallo(ErroresSolicitudCartera.PropiaSolicitud);

            var ahora = DateTime.UtcNow;

            // La solicitud pudo esperar días: quien la pidió puede haber dejado
            // de ser Gestor CAE, o su cuenta estar desactivada. Aceptar a ciegas
            // le daría una cartera universal que ya no le corresponde.
            if (!await directorioUsuarios.EsCuentaActivaConRolAsync(
                    solicitud.SolicitanteUsuarioId, ctx.OperadorTenantId, ContextoOperadorCae.RolGestorCae, cancellationToken))
            {
                solicitud.Anular(MotivoAnulacionSolicitudCartera.SolicitanteNoDisponible, ahora);
                return await catalogo.GuardarDetectandoCarreraAsync(cancellationToken)
                    ? Result.Fallo(ErroresSolicitudCartera.SolicitanteNoDisponible)
                    : Result.Fallo(ErroresSolicitudCartera.YaResuelta);
            }

            // La cartera se escribe en el Tenant propietario: la política RLS de
            // las carteras solo deja escribir sobre el propietario contextual.
            // El Guid sale de la solicitud recién cargada del propio Operador CAE
            // y ya autorizada por rol, no de un parámetro. La solicitud se marca
            // en la misma transacción —su política va por el tenant de origen,
            // que dentro de este ámbito sigue siendo el del Coordinador CAE—, así
            // que o se crean cartera, fila heredada y aceptación, o nada.
            using (AmbitoTenantExplicito.Establecer(solicitud.PropietarioTenantId))
            {
                var incorporacion = await catalogo.IncorporarAsync(solicitud, cancellationToken);

                if (incorporacion.MotivoAnulacion is { } motivo)
                {
                    solicitud.Anular(motivo, ahora);
                    if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                        return Result.Fallo(ErroresSolicitudCartera.YaResuelta);

                    return Result.Fallo(motivo == MotivoAnulacionSolicitudCartera.YaEnCartera
                        ? ErroresSolicitudCartera.YaEnCartera
                        : ErroresSolicitudCartera.OperacionNoVigente);
                }

                solicitud.Aceptar(ctx.UsuarioId, incorporacion.Cartera!, incorporacion.AsignacionOperadorDelegadoId, ahora);

                // Dos Coordinadores CAE aceptando a la vez: la versión de la
                // solicitud y el índice único de cartera universal dejan pasar
                // una sola aceptación.
                if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                    return Result.Fallo(ErroresSolicitudCartera.YaResuelta);
            }

            await NotificarAlSolicitanteAsync(solicitud, cancellationToken);
            return Result.Exito();
        }
    }

    /// <summary>
    /// Segundo guardado, en el tenant de origen: la notificación es del
    /// Gestor CAE y se sella con su organización, no con la del propietario.
    /// Si falla, la aceptación ya está hecha y no se deshace por un aviso: se
    /// registra y la bandeja del Gestor CAE sigue mostrando el estado real.
    /// </summary>
    private async Task NotificarAlSolicitanteAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken)
    {
        try
        {
            var empresa = await tenants.Tenants
                .Where(t => t.Id == solicitud.PropietarioTenantId)
                .Select(t => t.Nombre)
                .FirstOrDefaultAsync(cancellationToken) ?? "la empresa";

            notificaciones.Agregar(new NotificacionUsuario(
                solicitud.SolicitanteUsuarioId,
                "Incorporación a cartera aceptada",
                $"Ya tienes «{empresa}» en tu cartera.",
                RutasIncorporacionCartera.Bandeja,
                "Ver mis solicitudes"));

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            catalogo.DescartarPendientes();
            logger.LogWarning(ex,
                "La solicitud de incorporación a cartera {SolicitudId} se aceptó, pero no se pudo notificar al solicitante.",
                solicitud.Id);
        }
    }
}
