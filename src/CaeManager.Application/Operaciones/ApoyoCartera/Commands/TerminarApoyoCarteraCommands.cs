using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Commands;

/// <summary>
/// <b>Desasignarme</b>: el propio Gestor CAE de apoyo cierra la Asignación de Cartera de apoyo
/// que aceptó (ADR-011 § 2.7, enmienda 2026-10-08, punto 6). <b>El principal no se desasigna
/// por aquí</b>: si esa cartera lleva hoy la marca, ya no es de apoyo y el comando falla sin
/// tocar nada (soltar el principal es una retirada con relevo, y la decide un Coordinador CAE).
/// </summary>
public record DesasignarmeDeApoyoCommand(Guid PropuestaId) : ICommand;

/// <summary>
/// Quien concedió un apoyo lo retira (D-6): solo el apoyo que nació de <b>una propuesta suya</b>
/// aceptada, y solo mientras <b>siga siendo el principal</b> de esa Asignación de Operación.
/// Quien dejó de serlo ya no responde de ese Tenant propietario y no decide quién entra.
/// </summary>
public record RetirarApoyoConcedidoCommand(Guid PropuestaId) : ICommand;

/// <summary>
/// Un Coordinador CAE o un superior revoca un apoyo (D-4): el Administrador y la Dirección CAE
/// del Operador CAE, cualquiera; el Coordinador CAE, si le reporta el Gestor CAE de apoyo
/// <b>o quien propuso el apoyo</b> (<see cref="AutoridadSobreCarteraDeGestorCae.PuedeRevocarApoyoAsync"/>).
/// </summary>
public record RevocarApoyoCarteraCommand(Guid PropuestaId) : ICommand;

/// <summary>
/// Las tres formas de terminar un apoyo por retirada. Cambia <b>quién</b> puede; lo que se hace
/// es lo mismo, y por eso vive en un solo sitio.
///
/// <para>
/// <b>Qué es un apoyo aquí</b>: la Asignación de Cartera que emitió una propuesta de apoyo
/// aceptada. El comando lleva la propuesta y nada más: el Tenant propietario, la cartera y las
/// dos personas salen de ella, cargada filtrando por el Operador CAE de quien actúa (la de otro
/// Operador CAE no existe para él).
/// </para>
///
/// <para>
/// <b>Autoridad</b>, resuelta sobre el Tenant de origen —una cuenta desactivada no debe seguir
/// escribiendo—. En las tres vías decide el rol efectivo de la sesión en origen e Identity lo
/// confirma: para revocar, con <see cref="AutoridadSobreCarteraDeGestorCae.RolDeAutoridadAsync"/>;
/// para desasignarse y retirar lo concedido, con <see cref="ContextoOperadorCae"/>, que aplica la
/// misma regla. Se resuelve una vez antes de abrir la transacción, para fallar pronto y saber en
/// qué Operador CAE cargar la propuesta, y <b>se vuelve a resolver dentro, ya con el candado</b>:
/// si el rol o la activación de la cuenta cambiaron entre medias, no escribe. La jerarquía para
/// revocar también se lee dentro. Ventana que queda, entre esa segunda lectura y el cierre de esta
/// transacción: el único candado es el exclusivo del Gestor CAE de apoyo. Al desasignarse, quien
/// actúa es ese Gestor CAE, y la desactivación de su cuenta, que toma el mismo exclusivo, sí se
/// serializa; al revocar o retirar lo concedido, la desactivación de quien actúa no se serializa.
/// Un cambio de rol no toma ningún candado de cartera, así que en ninguna vía se serializa. No se
/// añade un candado compartido sobre quien actúa: con retiradas y revocaciones cruzadas entre dos
/// personas formaría un ciclo con el exclusivo del apoyo (OWASP ASVS 4.0.3 V4.1.3 y V4.2.1).
/// </para>
///
/// <para>
/// <b>El cierre</b> pasa por <see cref="ICatalogoIncorporacionCartera.RetirarCarteraDeApoyoAsync"/>,
/// que comparte camino con la retirada de «Asignar empresas»: mismo motivo de cierre, misma
/// regla para la fila heredada de Operador Delegado y, por tanto, el mismo corte en el circuito
/// vivo de quien pierde el Tenant. <b>No toca la marca de principal ni releva a nadie</b>, y no
/// cierra ninguna otra cartera: el apoyo es una cartera propia, subordinada a la operación y no
/// a la de quien la propuso.
/// </para>
///
/// <para>
/// <b>Candado</b>: el exclusivo de cartera sobre el Gestor CAE de apoyo, como toda retirada
/// (decide si la fila heredada sobra mirando sus demás carteras). Es el único que se toma.
/// </para>
/// </summary>
public class TerminarApoyoCarteraCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioDestinosCartera directorioDestinos,
    ICatalogoIncorporacionCartera catalogo,
    IPropuestaApoyoCarteraRepository repositorio,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera,
    INotificacionUsuarioRepository notificaciones,
    ITenantsQueryContext tenants,
    IUnitOfWork unitOfWork,
    ILogger<TerminarApoyoCarteraCommandHandler> logger)
    : IRequestHandler<DesasignarmeDeApoyoCommand, Result>,
      IRequestHandler<RetirarApoyoConcedidoCommand, Result>,
      IRequestHandler<RevocarApoyoCarteraCommand, Result>
{
    private enum Via
    {
        Desasignarme,
        RetirarLoConcedido,
        Revocar,
    }

    /// <summary>Quién actúa, desde qué Operador CAE y con qué rol en él (lo resuelve <c>ResolverActorAsync</c>).</summary>
    private sealed record Actor(Guid UsuarioId, Guid OperadorTenantId, string Rol);

    public Task<Result> Handle(DesasignarmeDeApoyoCommand request, CancellationToken cancellationToken) =>
        TerminarAsync(request.PropuestaId, Via.Desasignarme, cancellationToken);

    public Task<Result> Handle(RetirarApoyoConcedidoCommand request, CancellationToken cancellationToken) =>
        TerminarAsync(request.PropuestaId, Via.RetirarLoConcedido, cancellationToken);

    public Task<Result> Handle(RevocarApoyoCarteraCommand request, CancellationToken cancellationToken) =>
        TerminarAsync(request.PropuestaId, Via.Revocar, cancellationToken);

    private async Task<Result> TerminarAsync(Guid propuestaId, Via via, CancellationToken cancellationToken)
    {
        var actor = await ResolverActorAsync(via, cancellationToken);
        if (actor is null)
            return Result.Fallo(ErroresPropuestaApoyo.SinPermiso);

        // El apoyo que de verdad se cerró, para avisar después de confirmar. Si la cartera ya
        // estaba cerrada no hay nada nuevo que contar.
        PropuestaApoyoCartera? terminada = null;

        Result resultado;
        try
        {
            using (AmbitoTenantExplicito.Establecer(actor.OperadorTenantId))
            {
                resultado = await transaccion.EjecutarAsync(async ct =>
                {
                    terminada = null;

                    var propuesta = await repositorio.ObtenerPorIdAsync(propuestaId, actor.OperadorTenantId, ct);
                    if (propuesta is null)
                        return Result.Fallo(ErroresPropuestaApoyo.ApoyoNoEncontrado);

                    // Antes de mirar el estado: a quien no es parte del apoyo no se le dice si
                    // sigue vivo.
                    switch (via)
                    {
                        case Via.Desasignarme when propuesta.DestinatarioUsuarioId != actor.UsuarioId:
                            return Result.Fallo(ErroresPropuestaApoyo.ApoyoNoEncontrado);
                        case Via.RetirarLoConcedido when propuesta.ProponenteUsuarioId != actor.UsuarioId:
                            return Result.Fallo(ErroresPropuestaApoyo.SoloRetirasLoQueConcediste);
                    }

                    if (propuesta.Estado != EstadoPropuestaApoyoCartera.Aceptada)
                        return Result.Fallo(ErroresPropuestaApoyo.ApoyoNoEncontrado);

                    await bloqueoCartera.BloquearExclusivoAsync(propuesta.DestinatarioUsuarioId, ct);

                    // El rol y la cuenta se vuelven a leer ya dentro de la transacción: si la
                    // cuenta se desactivó o perdió el rol desde la primera lectura, no escribe.
                    // OWASP ASVS 4.0.3 V4.1.3 y V4.2.1.
                    if (await ResolverActorAsync(via, ct) != actor)
                        return Result.Fallo(ErroresPropuestaApoyo.SinPermiso);

                    // La jerarquía de hoy, no la de cuando se propuso.
                    if (via == Via.Revocar
                        && !await AutoridadSobreCarteraDeGestorCae.PuedeRevocarApoyoAsync(
                            actor.Rol, actor.UsuarioId, propuesta.DestinatarioUsuarioId, propuesta.ProponenteUsuarioId,
                            directorioDestinos, ct))
                        return Result.Fallo(ErroresPropuestaApoyo.ApoyoFueraDeTuEquipo);

                    // La cartera se cierra en el Tenant propietario: la política RLS de las
                    // carteras solo deja escribir sobre el propietario contextual. El Guid sale
                    // de la propuesta recién cargada del propio Operador CAE, no de la petición.
                    using (AmbitoTenantExplicito.Establecer(propuesta.PropietarioTenantId))
                    {
                        var retirada = await catalogo.RetirarCarteraDeApoyoAsync(
                            propuesta, actor.UsuarioId, exigirProponentePrincipal: via == Via.RetirarLoConcedido, ct);
                        switch (retirada)
                        {
                            case ResultadoRetiradaApoyo.YaNoEsDeApoyo:
                                return Result.Fallo(via == Via.Desasignarme
                                    ? ErroresPropuestaApoyo.EresElPrincipal
                                    : ErroresPropuestaApoyo.ApoyoEsAhoraPrincipal);
                            case ResultadoRetiradaApoyo.ProponenteYaNoEsPrincipal:
                                return Result.Fallo(ErroresPropuestaApoyo.YaNoEresElPrincipal);
                        }

                        // Pierde quien llega cuando la cartera cambió a la vez: otro la cerró, o
                        // una designación le puso (o le quitó a quien la concedió) la marca.
                        if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                            return Result.Fallo(ErroresPropuestaApoyo.CambioMientrasDecidias);

                        if (retirada == ResultadoRetiradaApoyo.Retirada)
                            terminada = propuesta;
                    }

                    return Result.Exito();
                }, cancellationToken);
            }
        }
        catch (DbUpdateException ex)
        {
            // La transacción ya se deshizo y el contexto quedó vacío (ITransaccionDeComando).
            // Se registra siempre: un fallo determinista llegaría aquí con el mismo aspecto.
            logger.LogWarning(ex,
                "El apoyo de cartera de la propuesta {PropuestaId} no se pudo terminar: la base de datos rechazó la escritura.",
                propuestaId);
            return Result.Fallo(ErroresPropuestaApoyo.CambioMientrasDecidias);
        }

        if (resultado.EsExitoso && terminada is { } hecha)
            await AvisoDeApoyoDeCartera.AvisarTerminadoAsync(
                hecha, actor.UsuarioId,
                new AvisoDeApoyoDeCartera.Dependencias(
                    directorioDestinos, directorioUsuarios, tenants, notificaciones, unitOfWork, catalogo, logger),
                cancellationToken);

        return resultado;
    }

    /// <summary>
    /// Desasignarse y retirar lo concedido son de quien gestiona CAE (Gestor CAE o Coordinador
    /// CAE activo: ser parte del apoyo no basta si la cuenta se degradó o se desactivó).
    /// Revocar es de quien tiene autoridad sobre carteras.
    /// </summary>
    private async Task<Actor?> ResolverActorAsync(Via via, CancellationToken cancellationToken)
    {
        if (via == Via.Revocar)
        {
            var autoridad = await AutoridadSobreCarteraDeGestorCae.ResolverActorAsync(
                currentUserService, directorioUsuarios, cancellationToken);
            return autoridad.EsFallido
                ? null
                : new Actor(autoridad.Valor.ActorUsuarioId, autoridad.Valor.OperadorTenantId, autoridad.Valor.Rol);
        }

        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        return contexto.EsFallido || contexto.Valor.Rol is not { } rol
            ? null
            : new Actor(contexto.Valor.UsuarioId, contexto.Valor.OperadorTenantId, rol);
    }
}
