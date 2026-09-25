using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.GenerarCodigosRecuperacion;

/// <summary>
/// Genera los códigos de recuperación de la verificación en dos pasos de la
/// <b>propia</b> cuenta y los devuelve para mostrarlos una sola vez (P0-8 del plan
/// de madurez 09-24, hallazgo FS-01). Sirve para la primera vez, justo después de
/// activar la 2FA, y para regenerarlos: los anteriores dejan de valer.
///
/// <para>
/// Autoservicio (<see cref="IComandoDeAutoservicio"/>): la cuenta sale de
/// <see cref="ICurrentUserService"/>, nunca del request, y lo que escribe solo lo usa
/// su dueño. Por eso lo puede ejecutar también un rol de solo lectura. Una Sesión
/// Privilegiada de plataforma no llega aquí (la corta
/// <c>AutorizacionEscrituraBehavior</c>), y además el handler exige que el actor real
/// sea la propia cuenta: quien simula a otro usuario nunca obtiene un segundo factor
/// permanente de esa persona.
/// </para>
/// </summary>
public record GenerarCodigosRecuperacionCommand : ICommand<IReadOnlyList<string>>, IComandoDeAutoservicio;

/// <summary>
/// Cuántos códigos se generan. Diez: la salida mínima que fija FS-01
/// (AUDITORIA-UX-FLUJOS-SIN-SALIDA-2026-09-24) y la cifra de la plantilla de
/// ASP.NET Core Identity.
/// </summary>
public static class CodigosRecuperacion
{
    public const int Cantidad = 10;
}

public class GenerarCodigosRecuperacionCommandHandler(
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    ISegundoFactorDeCuentas segundoFactor)
    : IRequestHandler<GenerarCodigosRecuperacionCommand, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(
        GenerarCodigosRecuperacionCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<IReadOnlyList<string>>(
                Error.Crear("SegundoFactor.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.UsuarioSimuladoId is not null || actor.ActorRealUsuarioId != usuarioId)
            return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                "SegundoFactor.SoloLaPropiaCuenta",
                "Los códigos de recuperación solo los puede generar la persona titular de la cuenta."));

        var estado = await segundoFactor.ObtenerEstadoAsync(usuarioId.Value, cancellationToken);
        if (estado is null || !estado.Activo)
            return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                "SegundoFactor.NoActivo",
                "Activa primero la verificación en dos pasos: los códigos de recuperación la sustituyen cuando no tienes el móvil."));

        return await segundoFactor.GenerarCodigosRecuperacionAsync(
            usuarioId.Value, CodigosRecuperacion.Cantidad, cancellationToken);
    }
}
