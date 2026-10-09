using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Commands;

/// <summary>
/// El destinatario de una propuesta de apoyo la acepta, y solo él (ADR-011 § 2.7, enmienda
/// 2026-10-08). Emite su Asignación de Cartera de apoyo: del Tenant entero, rol Gestor CAE,
/// sin caducidad y <b>sin la marca de principal</b>. Ninguna de las tres cosas es parámetro.
///
/// <para>
/// <b>Todo se vuelve a decidir al aceptar</b>, porque la propuesta pudo esperar días: que el
/// destinatario siga siendo una cuenta activa con rol Gestor CAE del Operador CAE; que quien
/// propuso siga siendo una cuenta activa de gestión CAE <b>y siga llevando la marca de
/// principal</b> en una cartera vigente de esa operación; que la operación siga vigente con su
/// delegación viva; y que el destinatario no tenga ya el Tenant. Si algo de eso ya no se
/// cumple, la propuesta se <b>anula</b> —y la anulación se guarda— en vez de emitir nada.
/// </para>
///
/// <para>
/// <b>Atomicidad y concurrencia</b>: cartera, fila heredada y aceptación se escriben en una
/// transacción (<see cref="ITransaccionDeComando"/>), con el Tenant propietario como Tenant
/// activo para la cartera. Antes de leer ninguna de las dos cuentas se toma el candado
/// compartido de cartera sobre ambas (<see cref="IBloqueoCarteraUsuario"/>): desactivar una
/// cuenta toma el exclusivo, así que una desactivación en curso termina antes de que aquí se
/// decida y una que llegue después encuentra ya la cartera emitida. La pérdida simultánea de
/// la marca por designación, relevo o retirada la detiene la versión de la cartera del
/// principal, que el catálogo escribe al emitir.
/// </para>
/// </summary>
public record AceptarPropuestaApoyoCarteraCommand(Guid PropuestaId) : ICommand;

public class AceptarPropuestaApoyoCarteraCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    IPropuestaApoyoCarteraRepository repositorio,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera,
    ILogger<AceptarPropuestaApoyoCarteraCommandHandler> logger)
    : IRequestHandler<AceptarPropuestaApoyoCarteraCommand, Result>
{
    public async Task<Result> Handle(AceptarPropuestaApoyoCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || contexto.Valor.Rol is null)
            return Result.Fallo(ErroresPropuestaApoyo.SinPermiso);
        var ctx = contexto.Valor;

        // Una anulación tiene que quedar escrita, y la transacción deshace todo lo que acabe
        // en un Result fallido: se confirma como éxito y el motivo viaja aparte.
        MotivoAnulacionPropuestaApoyo? anulada = null;

        Result resultado;
        try
        {
            resultado = await transaccion.EjecutarAsync(async ct =>
            {
                anulada = null;

                using (ctx.EnOrigen())
                {
                    // Se carga filtrando por el Operador CAE de quien acepta: una propuesta de
                    // otro Operador CAE no existe para él. Y la de otro destinatario, tampoco.
                    var propuesta = await repositorio.ObtenerPorIdAsync(request.PropuestaId, ctx.OperadorTenantId, ct);
                    if (propuesta is null || propuesta.DestinatarioUsuarioId != ctx.UsuarioId)
                        return Result.Fallo(ErroresPropuestaApoyo.NoEncontrada);
                    if (propuesta.Estado != EstadoPropuestaApoyoCartera.Pendiente)
                        return Result.Fallo(ErroresPropuestaApoyo.YaResuelta);

                    // Antes de leer ninguna cuenta: la del destinatario, a quien se concede la
                    // cartera, y la de quien propuso, de cuya marca depende la concesión.
                    await bloqueoCartera.BloquearCompartidoAsync(
                        [propuesta.DestinatarioUsuarioId, propuesta.ProponenteUsuarioId], ct);

                    var ahora = DateTime.UtcNow;

                    if (!await directorioUsuarios.EsCuentaActivaConRolAsync(
                            propuesta.DestinatarioUsuarioId, ctx.OperadorTenantId, ContextoOperadorCae.RolGestorCae, ct))
                        return await AnularAsync(propuesta, MotivoAnulacionPropuestaApoyo.DestinatarioNoDisponible, ahora, ct);

                    if (!await EsCuentaActivaDeGestionCaeAsync(propuesta.ProponenteUsuarioId, ctx.OperadorTenantId, ct))
                        return await AnularAsync(propuesta, MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal, ahora, ct);

                    // La cartera se escribe en el Tenant propietario: la política RLS de las
                    // carteras solo deja escribir sobre el propietario contextual. El Guid sale
                    // de la propuesta recién cargada del propio Operador CAE, no de un
                    // parámetro. La propuesta se marca en la misma transacción: su política va
                    // por el Tenant de origen, que dentro de este ámbito sigue siendo el suyo.
                    using (AmbitoTenantExplicito.Establecer(propuesta.PropietarioTenantId))
                    {
                        var apoyo = await catalogo.IncorporarApoyoAsync(propuesta, ct);
                        if (apoyo.MotivoAnulacion is { } motivo)
                            return await AnularAsync(propuesta, motivo, ahora, ct);

                        propuesta.Aceptar(ctx.UsuarioId, apoyo.Cartera!, apoyo.AsignacionOperadorDelegadoId, ahora);

                        // Pierde quien llega cuando la propuesta ya cambió (versión), cuando el
                        // destinatario ya recibió el Tenant por otra vía (índice de cartera), o
                        // cuando la marca de principal cambió de manos a la vez (versión de la
                        // cartera del principal).
                        if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                            return Result.Fallo(ErroresPropuestaApoyo.CambioMientrasDecidias);
                    }

                    return Result.Exito();
                }
            }, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // La transacción ya se deshizo y el contexto quedó vacío (ITransaccionDeComando).
            // A quien acepta se le dice que algo cambió, que es lo habitual; pero un fallo
            // determinista (una clave ajena, un tipo) llegaría aquí con el mismo aspecto, así
            // que la excepción se registra siempre: sin rastro no se distinguirían.
            logger.LogWarning(ex,
                "La propuesta de apoyo de cartera {PropuestaId} no se pudo aceptar: la base de datos rechazó la escritura.",
                request.PropuestaId);
            return Result.Fallo(ErroresPropuestaApoyo.CambioMientrasDecidias);
        }

        if (resultado.EsExitoso && anulada is { } motivoAnulacion)
            return Result.Fallo(ErroresPropuestaApoyo.DeAnulacion(motivoAnulacion));

        return resultado;

        async Task<Result> AnularAsync(
            PropuestaApoyoCartera propuesta, MotivoAnulacionPropuestaApoyo motivo, DateTime ahora, CancellationToken ct)
        {
            propuesta.Anular(motivo, ahora);
            if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                return Result.Fallo(ErroresPropuestaApoyo.YaResuelta);

            anulada = motivo;
            return Result.Exito();
        }
    }

    /// <summary>Quien propuso sigue siendo una cuenta activa con rol Gestor CAE o Coordinador CAE de este Operador CAE.</summary>
    private async Task<bool> EsCuentaActivaDeGestionCaeAsync(Guid usuarioId, Guid operadorTenantId, CancellationToken cancellationToken) =>
        await directorioUsuarios.EsCuentaActivaConRolAsync(usuarioId, operadorTenantId, ContextoOperadorCae.RolGestorCae, cancellationToken)
        || await directorioUsuarios.EsCuentaActivaConRolAsync(usuarioId, operadorTenantId, ContextoOperadorCae.RolCoordinadorCae, cancellationToken);
}
