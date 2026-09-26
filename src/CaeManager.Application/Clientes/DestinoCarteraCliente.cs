using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Clientes;

/// <summary>
/// Lo que Application necesita saber de la cuenta que va a recibir la cartera de
/// un Cliente empresarial, leído en el momento de escribir. Es un puerto por el
/// mismo motivo que <see cref="IDirectorioUsuariosService"/>: <c>ApplicationUser</c>
/// vive en Infrastructure.Identity. <b>No decide nada</b>: devuelve hechos, y
/// quién puede recibir la cartera lo decide <see cref="ReglaDestinoCarteraCliente"/>.
/// </summary>
public interface IDirectorioDestinosCartera
{
    /// <summary>
    /// La cuenta tal y como se ve desde el Tenant activo, o <c>null</c> si no es
    /// alcanzable desde él: ni pertenece al Tenant activo ni es un Operador CAE
    /// externo con una Asignación de Operador Delegado vigente sobre él, o no
    /// hay Tenant resuelto (fallo cerrado).
    /// </summary>
    Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <param name="Activa">La cuenta no está desactivada.</param>
/// <param name="RolEfectivo">
/// El rol con el que la cuenta operaría en el Tenant activo: el de Identity si es
/// propia, el de su Asignación de Operador Delegado si es de un Operador CAE
/// externo. <c>null</c> si no hay exactamente uno (fallo cerrado).
/// </param>
/// <param name="CoordinadorUsuarioId">El Coordinador CAE al que reporta, si lo tiene.</param>
/// <param name="EsOperadorDelegado">Pertenece al Tenant de un Operador CAE externo, no al Tenant activo.</param>
public record DestinoCartera(bool Activa, string? RolEfectivo, Guid? CoordinadorUsuarioId, bool EsOperadorDelegado);

/// <summary>
/// Quién puede recibir la cartera de un Cliente empresarial (FS-25, revisión Codex
/// de la PR #931). Antes solo lo comprobaba la pantalla de Usuarios: un Command
/// enviado con otro Guid dejaba el Cliente empresarial en manos de una cuenta
/// desactivada, de un rol sin cartera (una cartera de Cliente empresarial no le
/// da nada a un Coordinador CAE ni a Consulta, ver <c>AlcanceDatosService</c>) o
/// de un Gestor CAE fuera de la supervisión de quien reasigna.
///
/// <list type="number">
/// <item>alcanzable desde el Tenant activo (propia o delegada vigente);</item>
/// <item>activa;</item>
/// <item>con rol efectivo Gestor CAE, también cuando llega por un Operador CAE
/// externo delegado;</item>
/// <item>si quien reasigna es Coordinador CAE, un Gestor CAE que le reporta —la
/// misma jerarquía que acota su lectura (decisión D-001)—.</item>
/// </list>
/// Quitar el Gestor CAE (destino <c>null</c>) no pasa por aquí.
/// </summary>
public static class ReglaDestinoCarteraCliente
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolGestorCae = "GestorCae";
    private const string RolCoordinadorCae = "CoordinadorCae";

    public static readonly Error NoAlcanzable = Error.Crear(
        "Cliente.DestinoNoAlcanzable", "No encontramos a ese Gestor CAE en esta organización.");

    public static readonly Error Inactivo = Error.Crear(
        "Cliente.DestinoInactivo", "Ese Gestor CAE tiene la cuenta desactivada: elige otro.");

    public static readonly Error NoEsGestorCae = Error.Crear(
        "Cliente.DestinoNoEsGestorCae", "La cartera de un cliente solo puede pasar a un Gestor CAE.");

    public static readonly Error FueraDeAlcance = Error.Crear(
        "Cliente.DestinoFueraDeAlcance", "Ese Gestor CAE no está a tu cargo: solo puedes pasar la cartera a los tuyos.");

    public static async Task<Result> ValidarAsync(
        Guid destinoUsuarioId, IDirectorioDestinosCartera directorio, ICurrentUserService currentUserService,
        CancellationToken cancellationToken)
    {
        var destino = await directorio.ObtenerAsync(destinoUsuarioId, cancellationToken);
        if (destino is null) return Result.Fallo(NoAlcanzable);
        if (!destino.Activa) return Result.Fallo(Inactivo);
        if (destino.RolEfectivo != RolGestorCae) return Result.Fallo(NoEsGestorCae);

        if (await currentUserService.ObtenerRolEfectivoAsync() == RolCoordinadorCae
            && (destino.CoordinadorUsuarioId is null
                || destino.CoordinadorUsuarioId != await currentUserService.ObtenerUsuarioActualIdAsync()))
            return Result.Fallo(FueraDeAlcance);

        return Result.Exito();
    }
}
