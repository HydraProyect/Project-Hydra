using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Usuarios.Commands.DesignarGestorCaePrincipal;

/// <summary>
/// Pasa la marca de principal de una Asignación de Operación a la cartera viva de otra persona
/// del mismo Operador CAE (ADR-011 § 2.7, enmienda 2026-10-08). La marca dice quién responde
/// de ese Tenant ante el Operador CAE; <b>no cambia el ámbito efectivo de nadie</b>: quien la
/// pierde conserva su cartera como cartera de apoyo.
///
/// <para>
/// <b>Autoridad</b>, leída en Identity sobre el Tenant de origen, nunca en el claim:
/// Administrador y Dirección CAE del Operador CAE, sobre cualquier cartera; Coordinador CAE,
/// si es él quien lleva hoy la marca, o si el destino le reporta y el principal actual —si lo
/// hay— también (un Coordinador CAE no le quita la marca al equipo de otro). Un Gestor CAE no
/// puede. La Operación nunca concede roles de Propiedad: aquí no se emite ni se cambia ningún rol.
/// </para>
///
/// <para>
/// <b>Destino</b>: una cuenta activa del propio Operador CAE con cartera vigente, del Tenant
/// entero y de rol Gestor CAE o Coordinador CAE bajo esa operación
/// (<see cref="ICatalogoIncorporacionCartera.ObtenerCarterasVivasAsync"/>).
/// </para>
///
/// <para>
/// <b>Atomicidad y concurrencia</b>: el índice único de principal no es diferible, así que se
/// apaga la marca actual y se guarda, y se enciende la nueva y se guarda, dentro de una
/// transacción (<see cref="ITransaccionDeComando"/>). No hay candado por operación: todo cambio
/// de marca empieza escribiendo la cartera del principal actual, cuya versión serializa a dos
/// que lo intenten a la vez, y el índice único deja pasar un solo principal; quien pierde falla
/// al guardar y no queda nada escrito. El candado compartido de cartera sobre el destino
/// (<see cref="IBloqueoCarteraUsuario"/>) espera a una desactivación en curso de esa cuenta.
/// </para>
/// </summary>
public record DesignarGestorCaePrincipalCommand(Guid AsignacionOperacionId, Guid UsuarioId) : ICommand;

public class DesignarGestorCaePrincipalCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioDestinosCartera directorioDestinos,
    ICatalogoIncorporacionCartera catalogo,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera)
    : IRequestHandler<DesignarGestorCaePrincipalCommand, Result>
{
    private const string Administrador = "Administrador";
    private const string DireccionCae = "DireccionCae";
    private const string CoordinadorCae = "CoordinadorCae";

    private static readonly string[] RolesAutorizados = [Administrador, DireccionCae, CoordinadorCae];

    public static readonly Error SinAutoridad = Error.Crear(
        "Principal.SinAutoridad", "Tu rol no permite designar al Gestor CAE principal de esta empresa.");

    public static readonly Error FueraDeTuEquipo = Error.Crear(
        "Principal.FueraDeTuEquipo",
        "Solo puedes designar principal dentro de tu equipo, o cuando el principal eres tú.");

    public static readonly Error DestinoSinCartera = Error.Crear(
        "Principal.DestinoSinCartera",
        "Esa persona no tiene esta empresa entera en su cartera. Asígnasela primero y vuelve a intentarlo.");

    public static readonly Error DestinoDesactivado = Error.Crear(
        "Principal.DestinoDesactivado", "Esa persona tiene la cuenta desactivada: no puede ser el principal.");

    public static readonly Error CambioMientrasDecidias = Error.Crear(
        "Principal.CambioMientrasDecidias",
        "El principal de esta empresa cambió mientras decidías. No se ha cambiado nada; cierra, vuelve a abrir y revisa.");

    public async Task<Result> Handle(DesignarGestorCaePrincipalCommand request, CancellationToken cancellationToken)
    {
        // Fuera de la transacción solo para fallar pronto; todo se vuelve a decidir dentro.
        var previa = await ResolverAsync(request, cancellationToken);
        if (previa.EsFallido)
            return Result.Fallo(previa.Error);
        if (previa.Valor.YaEsPrincipal)
            return Result.Exito();

        try
        {
            return await transaccion.EjecutarAsync(async ct =>
            {
                await bloqueoCartera.BloquearCompartidoAsync([request.UsuarioId], ct);

                var vigente = await ResolverAsync(request, ct);
                if (vigente.EsFallido)
                    return Result.Fallo(vigente.Error);
                var decision = vigente.Valor;
                if (decision.YaEsPrincipal)
                    return Result.Exito();

                // El Guid del Tenant propietario sale de la cartera del destino, ya leída y
                // acotada al Operador CAE; de la petición solo salen la operación y la persona.
                using (AmbitoTenantExplicito.Establecer(decision.PropietarioTenantId))
                {
                    if (decision.PrincipalActualUsuarioId is { } actual)
                    {
                        if (!await catalogo.ApagarPrincipalAsync(
                                decision.OperadorTenantId, request.AsignacionOperacionId, actual, ct)
                            || !await catalogo.GuardarDetectandoCarreraAsync(ct))
                            return Result.Fallo(CambioMientrasDecidias);
                    }

                    if (!await catalogo.EncenderPrincipalAsync(
                            decision.OperadorTenantId, request.AsignacionOperacionId, request.UsuarioId, ct)
                        || !await catalogo.GuardarDetectandoCarreraAsync(ct))
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

    private sealed record Decision(
        Guid OperadorTenantId, Guid PropietarioTenantId, Guid? PrincipalActualUsuarioId, bool YaEsPrincipal);

    /// <summary>Autoridad del actor, validez del destino y principal actual, leídos ahora.</summary>
    private async Task<Result<Decision>> ResolverAsync(
        DesignarGestorCaePrincipalCommand request, CancellationToken cancellationToken)
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

            string? rolActor = null;
            foreach (var rol in RolesAutorizados)
            {
                if (await directorioUsuarios.EsCuentaActivaConRolAsync(actorId.Value, origen.Value, rol, cancellationToken))
                {
                    rolActor = rol;
                    break;
                }
            }

            if (rolActor is null)
                return Result.Fallo<Decision>(SinAutoridad);

            // Solo las carteras del propio Operador CAE: una operación de otro no devuelve nada
            // y el destino «no tiene cartera», sin revelar si la operación existe.
            var carteras = (await catalogo.ObtenerCarterasVivasAsync(origen.Value, null, cancellationToken))
                .Where(c => c.AsignacionOperacionId == request.AsignacionOperacionId)
                .ToList();
            var destino = carteras.FirstOrDefault(c => c.UsuarioId == request.UsuarioId);
            if (destino is null)
                return Result.Fallo<Decision>(DestinoSinCartera);

            var cuentaDestino = await directorioDestinos.ObtenerAsync(request.UsuarioId, cancellationToken);
            if (cuentaDestino is null || cuentaDestino.EsOperadorDelegado)
                return Result.Fallo<Decision>(DestinoSinCartera);
            if (!cuentaDestino.Activa)
                return Result.Fallo<Decision>(DestinoDesactivado);

            var principalActual = carteras.FirstOrDefault(c => c.EsPrincipal)?.UsuarioId;

            if (rolActor == CoordinadorCae && principalActual != actorId)
            {
                if (!EsDeSuEquipo(request.UsuarioId, cuentaDestino, actorId.Value))
                    return Result.Fallo<Decision>(FueraDeTuEquipo);

                if (principalActual is { } actual && actual != request.UsuarioId)
                {
                    var cuentaActual = await directorioDestinos.ObtenerAsync(actual, cancellationToken);
                    if (cuentaActual is null
                        || !EsDeSuEquipo(actual, cuentaActual, actorId.Value))
                        return Result.Fallo<Decision>(FueraDeTuEquipo);
                }
            }

            return Result.Exito(new Decision(
                origen.Value, destino.PropietarioTenantId, principalActual, principalActual == request.UsuarioId));
        }
    }

    /// <summary>Es el propio Coordinador CAE o alguien que le reporta (<c>CoordinadorUsuarioId</c>).</summary>
    private static bool EsDeSuEquipo(Guid usuarioId, DestinoCartera cuenta, Guid coordinadorUsuarioId) =>
        usuarioId == coordinadorUsuarioId || cuenta.CoordinadorUsuarioId == coordinadorUsuarioId;
}
