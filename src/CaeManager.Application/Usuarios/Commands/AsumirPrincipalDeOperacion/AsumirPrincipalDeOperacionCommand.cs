using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;

/// <summary>
/// «Asumir» (ADR-011 § 2.7, enmienda 2026-10-08, punto 4): quien lo ejecuta recibe en el acto,
/// sin aprobación de nadie, la cartera principal de una Asignación de Operación externa de su
/// Operador CAE que <b>no tiene principal vivo</b>. Si ya tiene cartera viva bajo esa operación
/// se marca; si no, se le emite una del Tenant entero. Las carteras de apoyo no se tocan.
///
/// <para>
/// <b>Autoridad</b>, leída en Identity sobre el Tenant de origen, nunca en el claim: Coordinador
/// CAE, Dirección CAE o Administrador del Operador CAE. Un Gestor CAE no puede. La operación
/// sale de las que su Operador CAE puede poner en una cartera
/// (<see cref="ICatalogoIncorporacionCartera.ObtenerAsignablesAsync"/>): la de otro Operador CAE
/// «no existe», sin revelar si existe.
/// </para>
///
/// <para>
/// <b>No sirve para quitarle el principal a otro</b>: con principal vivo falla; para eso está
/// <c>DesignarGestorCaePrincipalCommand</c>.
/// </para>
///
/// <para>
/// <b>Rol de la cartera</b>: siempre Coordinador CAE, aunque quien asume sea Dirección CAE o
/// Administrador en su organización. No es parámetro: lo fija la escritura
/// (<see cref="ICatalogoIncorporacionCartera.RelevarPrincipalAsync"/>). La Operación nunca
/// concede roles de Propiedad.
/// </para>
///
/// <para>
/// <b>Concurrencia</b>: no hay candado por operación. Dos «Asumir» a la vez, o uno contra el
/// escalado automático, los separa el índice único de principal: quien pierde falla al guardar
/// dentro de su transacción (<see cref="ITransaccionDeComando"/>) y no deja nada escrito. El
/// candado compartido de cartera sobre quien asume espera a una desactivación en curso de su
/// cuenta (<see cref="IBloqueoCarteraUsuario"/>).
/// </para>
/// </summary>
public record AsumirPrincipalDeOperacionCommand(Guid AsignacionOperacionId) : ICommand;

public class AsumirPrincipalDeOperacionCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera)
    : IRequestHandler<AsumirPrincipalDeOperacionCommand, Result>
{
    public static readonly Error SinAutoridad = Error.Crear(
        "AsumirPrincipal.SinAutoridad", "Tu rol no permite asumir una empresa que está sin principal.");

    public static readonly Error OperacionNoEncontrada = Error.Crear(
        "AsumirPrincipal.OperacionNoEncontrada",
        "No encontramos esa empresa entre las que gestiona tu organización. Recarga la lista y revisa.");

    public static readonly Error YaTienePrincipal = Error.Crear(
        "AsumirPrincipal.YaTienePrincipal",
        "Esa empresa ya tiene principal. No se ha cambiado nada; recarga la lista y revisa.");

    /// <summary>
    /// La escritura no encontró cartera suya que marcar ni pudo emitir una: ya tiene en la
    /// operación un acceso de otro tipo, la operación conserva un principal sin cerrar (caducado
    /// y aún no cerrado por el proceso de expiración) o la autorización del Operador CAE cayó.
    /// </summary>
    public static readonly Error NoSePudoAsignar = Error.Crear(
        "AsumirPrincipal.NoSePudoAsignar",
        "No se te ha podido asignar esta empresa: ya tienes en ella un acceso de otro tipo o su situación acaba de cambiar. No se ha cambiado nada; recarga la lista y, si sigue ahí, pide que revisen tu acceso.");

    public static readonly Error CambioMientrasDecidias = Error.Crear(
        "AsumirPrincipal.CambioMientrasDecidias",
        "Otra persona recibió esta empresa mientras decidías. No se ha cambiado nada; recarga la lista y revisa.");

    public async Task<Result> Handle(AsumirPrincipalDeOperacionCommand request, CancellationToken cancellationToken)
    {
        // Fuera de la transacción solo para fallar pronto; todo se vuelve a decidir dentro.
        var previa = await ResolverAsync(request, cancellationToken);
        if (previa.EsFallido)
            return Result.Fallo(previa.Error);

        try
        {
            return await transaccion.EjecutarAsync(async ct =>
            {
                await bloqueoCartera.BloquearCompartidoAsync([previa.Valor.ActorUsuarioId], ct);

                var vigente = await ResolverAsync(request, ct);
                if (vigente.EsFallido)
                    return Result.Fallo(vigente.Error);
                var decision = vigente.Valor;

                // El Guid del Tenant propietario sale de la operación ya leída y acotada al
                // Operador CAE; de la petición solo sale el identificador de la operación.
                using (AmbitoTenantExplicito.Establecer(decision.PropietarioTenantId))
                {
                    var asumida = await catalogo.RelevarPrincipalAsync(
                        decision.PropietarioTenantId, decision.OperadorTenantId, request.AsignacionOperacionId,
                        decision.ActorUsuarioId, ct);
                    if (asumida == ResultadoRelevoPrincipal.SinRelevo)
                        return Result.Fallo(NoSePudoAsignar);

                    if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                        return Result.Fallo(CambioMientrasDecidias);
                }

                return Result.Exito();
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // La transacción ya se deshizo y el contexto quedó vacío (ITransaccionDeComando).
            return Result.Fallo(CambioMientrasDecidias);
        }
    }

    private sealed record Decision(Guid ActorUsuarioId, Guid OperadorTenantId, Guid PropietarioTenantId);

    /// <summary>Autoridad del actor, pertenencia de la operación a su Operador CAE y ausencia de principal, leídas ahora.</summary>
    private async Task<Result<Decision>> ResolverAsync(
        AsumirPrincipalDeOperacionCommand request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return Result.Fallo<Decision>(SinAutoridad);

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            // Falla cerrado sin rol de negocio en la sesión (sesión privilegiada de plataforma,
            // delegación retirada): Soporte TALVEG nunca es Operador CAE ni Gestor CAE.
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return Result.Fallo<Decision>(SinAutoridad);

            if (!await PerfilQueAsume.LoTieneAsync(actorId.Value, origen.Value, directorioUsuarios, cancellationToken))
                return Result.Fallo<Decision>(SinAutoridad);

            var operacion = (await catalogo.ObtenerAsignablesAsync(origen.Value, cancellationToken))
                .FirstOrDefault(o => o.AsignacionOperacionId == request.AsignacionOperacionId);
            if (operacion is null)
                return Result.Fallo<Decision>(OperacionNoEncontrada);

            var carteras = await catalogo.ObtenerCarterasVivasAsync(origen.Value, operacion.PropietarioTenantId, cancellationToken);
            if (carteras.Any(c => c.AsignacionOperacionId == request.AsignacionOperacionId && c.EsPrincipal))
                return Result.Fallo<Decision>(YaTienePrincipal);

            return Result.Exito(new Decision(actorId.Value, origen.Value, operacion.PropietarioTenantId));
        }
    }
}

/// <summary>
/// Si una cuenta es hoy Coordinador CAE, Dirección CAE o Administrador activo de un Operador
/// CAE: los perfiles que ven la alerta «sin principal» y pueden asumir. Una sola definición
/// para la lectura y para la escritura. Se llama con el Tenant del Operador CAE como Tenant activo.
/// </summary>
public static class PerfilQueAsume
{
    public static async Task<bool> LoTieneAsync(
        Guid usuarioId, Guid operadorTenantId, IDirectorioUsuariosService directorioUsuarios,
        CancellationToken cancellationToken)
    {
        foreach (var nivel in EscaladoDePrincipalDeCartera.Niveles)
        {
            if (await directorioUsuarios.EsCuentaActivaConRolAsync(usuarioId, operadorTenantId, nivel, cancellationToken))
                return true;
        }

        return false;
    }
}
