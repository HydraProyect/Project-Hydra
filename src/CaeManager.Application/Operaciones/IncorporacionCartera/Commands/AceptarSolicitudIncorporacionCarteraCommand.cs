using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Commands;

/// <summary>
/// Un Coordinador CAE acepta la solicitud de un Gestor CAE de su mismo
/// Operador CAE. Crea la Asignación de Cartera: universal, con rol Gestor CAE
/// y sin caducidad, sumada a las que ya hubiera sobre la misma operación. La
/// otras vías que crean esa misma cartera, con la misma escritura del catálogo,
/// son el alta del Gestor CAE con su cartera ya elegida (<c>CrearUsuarioCommand</c>) y
/// «Asignar empresas» a un Gestor CAE que ya existe (<c>AsignarCarteraGestorCaeCommand</c>,
/// que también la retira).
/// </summary>
[AreaDelEncargo("Operaciones")]
public record AceptarSolicitudIncorporacionCarteraCommand(Guid SolicitudId) : ICommand;

public class AceptarSolicitudIncorporacionCarteraCommandHandler(
    ICurrentUserService currentUserService,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio,
    IDirectorioUsuariosService directorioUsuarios,
    INotificacionUsuarioRepository notificaciones,
    ITenantsQueryContext tenants,
    IUnitOfWork unitOfWork,
    ILogger<AceptarSolicitudIncorporacionCarteraCommandHandler> logger,
    ITransaccionDeComando transaccion,
    CaeManager.Application.Clientes.IBloqueoCarteraUsuario bloqueoCartera,
    IOutboundCerradoTenantTec outboundCerrado)
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
            SolicitudIncorporacionCartera? aceptada = null;
            Error? anulada = null;

            // Comprobación del amparo de origen, emisión de la cartera y de su fila heredada y
            // guardado, en una transacción (ronda 2 de Codex en la PR #2253): el catálogo bloquea
            // con FOR SHARE la DelegacionTenant del par antes de decidir, y ese bloqueo solo dura
            // lo que la transacción. Sin ella, una revocación confirmada entre la lectura y el
            // guardado dejaría una fila heredada que nadie retira. La transacción deshace todo
            // lo escrito si el resultado falla; por eso la anulación, que sí se guarda, sale como
            // éxito y su error se devuelve fuera.
            var resultado = await transaccion.EjecutarAsync(async ct =>
            {
                aceptada = null;
                anulada = null;

                // Se carga filtrando por el Operador CAE del Coordinador CAE: una
                // solicitud de otro Operador CAE no existe para él.
                var solicitud = await repositorio.ObtenerPorIdAsync(request.SolicitudId, ctx.OperadorTenantId, ct);
                if (solicitud is null)
                    return Result.Fallo(ErroresSolicitudCartera.NoEncontrada);
                if (solicitud.Estado != EstadoSolicitudIncorporacionCartera.Pendiente)
                    return Result.Fallo(ErroresSolicitudCartera.YaResuelta);
                if (solicitud.SolicitanteUsuarioId == ctx.UsuarioId)
                    return Result.Fallo(ErroresSolicitudCartera.PropiaSolicitud);

                var ahora = DateTime.UtcNow;

                // Candado compartido sobre la cuenta del solicitante, antes de comprobar que sigue
                // activa (revisión de la PR A de la baja de un Gestor CAE con reasignación de cartera):
                // la desactivación toma el exclusivo y decide sobre la cartera que ve. Sin este
                // candado, una desactivación confirmada entre la comprobación y el guardado no vería
                // la cartera aún sin confirmar y dejaría a una cuenta desactivada con una Asignación
                // de Cartera vigente sobre la que nadie ha decidido. Con él, o la desactivación espera
                // a esta aceptación y la ve, o esta espera a la desactivación y anula la solicitud.
                await bloqueoCartera.BloquearCompartidoAsync([solicitud.SolicitanteUsuarioId], ct);

                // La solicitud pudo esperar días: quien la pidió puede haber dejado
                // de ser Gestor CAE, o su cuenta estar desactivada. Aceptar a ciegas
                // le daría una cartera universal que ya no le corresponde.
                if (!await directorioUsuarios.EsCuentaActivaConRolAsync(
                        solicitud.SolicitanteUsuarioId, ctx.OperadorTenantId, ContextoOperadorCae.RolGestorCae, ct))
                {
                    solicitud.Anular(MotivoAnulacionSolicitudCartera.SolicitanteNoDisponible, ahora);
                    if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                        return Result.Fallo(ErroresSolicitudCartera.YaResuelta);

                    anulada = ErroresSolicitudCartera.SolicitanteNoDisponible;
                    return Result.Exito();
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
                    // D3 (DN-4, DN-7): la Asignación de Cartera es un alta en el Tenant beneficiario de la solicitud, que
                    // aquí ya es el Tenant activo; si Outbound está cerrado para él, no se acepta (la solicitud sigue
                    // pendiente: la transacción se deshace).
                    if (await outboundCerrado.EstaCerradoAsync(solicitud.PropietarioTenantId, ct))
                        return Result.Fallo(ErroresLimiteTenantTec.OutboundCerrado);

                    var incorporacion = await catalogo.IncorporarAsync(solicitud, ct);

                    if (incorporacion.MotivoAnulacion is { } motivo)
                    {
                        solicitud.Anular(motivo, ahora);
                        if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                            return Result.Fallo(ErroresSolicitudCartera.YaResuelta);

                        anulada = motivo == MotivoAnulacionSolicitudCartera.YaEnCartera
                            ? ErroresSolicitudCartera.YaEnCartera
                            : ErroresSolicitudCartera.OperacionNoVigente;
                        return Result.Exito();
                    }

                    solicitud.Aceptar(ctx.UsuarioId, incorporacion.Cartera!, incorporacion.AsignacionOperadorDelegadoId, ahora);

                    // Dos Coordinadores CAE aceptando a la vez: la versión de la
                    // solicitud y el índice único de cartera universal dejan pasar
                    // una sola aceptación.
                    if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                        return Result.Fallo(ErroresSolicitudCartera.YaResuelta);
                }

                aceptada = solicitud;
                return Result.Exito();
            }, cancellationToken);

            if (resultado.EsFallido)
                return resultado;
            if (anulada is { } error)
                return Result.Fallo(error);

            await NotificarAlSolicitanteAsync(aceptada!, cancellationToken);
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
