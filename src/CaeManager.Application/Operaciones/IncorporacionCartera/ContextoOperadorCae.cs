using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Operaciones.IncorporacionCartera;

/// <summary>
/// Quién ejecuta una operación de la solicitud de incorporación a cartera y
/// desde qué Operador CAE: el tenant de origen de su cuenta y su rol
/// <b>dentro de ese tenant</b>.
///
/// <para>
/// La solicitud es un flujo interno del Operador CAE, y el Tenant activo de
/// quien pulsa es imprevisible: un Gestor CAE puede estar dentro del workspace
/// de un Tenant propietario, donde su rol efectivo es el de su cartera allí.
/// Por eso todo se resuelve con <see cref="AmbitoTenantExplicito"/> sobre el
/// tenant de origen —el que la selección de workspace no puede cambiar—, y
/// nunca con el Tenant activo.
/// </para>
///
/// <para>
/// <b>El rol se lee en Identity, no en el claim.</b> Dentro de un Workspace
/// operativo derivado, <c>RolEfectivoDelWorkspaceMiddleware</c> sustituye el
/// claim de rol por el de la Asignación de Cartera en ese Tenant propietario,
/// y el circuito hereda el principal ya sustituido. En el ámbito del tenant de
/// origen, <see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/> ya no
/// devuelve ese claim sustituido sino el rol de sesión en origen conservado
/// antes de la sustitución (decisión P7, 2026-09-23); aun así es un rol de
/// SESIÓN, no de la cuenta: no falla cerrado con una cuenta desactivada. Por
/// eso el rol sale de la cuenta en su tenant de origen
/// (<see cref="IDirectorioUsuariosService.EsCuentaActivaConRolAsync"/>), que la
/// selección de workspace no toca y que además falla cerrado con una cuenta
/// desactivada. <c>ObtenerRolEfectivoAsync</c> solo se consulta para fallar
/// cerrado cuando la sesión no tiene rol de negocio (sesión privilegiada de
/// plataforma, delegación retirada).
/// </para>
///
/// <para>
/// Establecer el ámbito sobre el propio tenant de origen no ensancha nada: es
/// el tenant al que la cuenta ya pertenece. Es la única llamada a
/// <see cref="AmbitoTenantExplicito.Establecer"/> de este flujo con ese Guid,
/// y vive aquí para que el ratchet de llamadas la categorice una sola vez.
/// </para>
/// </summary>
public sealed record ContextoOperadorCae(Guid UsuarioId, Guid OperadorTenantId, string? Rol)
{
    public const string RolGestorCae = "GestorCae";
    public const string RolCoordinadorCae = "CoordinadorCae";

    public bool EsGestorCae => Rol == RolGestorCae;
    public bool EsCoordinadorCae => Rol == RolCoordinadorCae;

    /// <summary>Si participa en el flujo: pide (Gestor CAE) o resuelve (Coordinador CAE).</summary>
    public bool ParticipaEnIncorporacionCartera => EsGestorCae || EsCoordinadorCae;

    /// <summary>
    /// Resuelve usuario, tenant de origen y rol en ese tenant. Falla con
    /// <see cref="ErroresSolicitudCartera.SinPermiso"/> si no hay usuario y con
    /// <see cref="ErroresSolicitudCartera.SinTenantDeOrigen"/> si la cuenta no
    /// declara organización.
    /// </summary>
    public static async Task<Result<ContextoOperadorCae>> ResolverAsync(
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        CancellationToken cancellationToken = default)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ContextoOperadorCae>(ErroresSolicitudCartera.SinPermiso);

        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (origen is null)
            return Result.Fallo<ContextoOperadorCae>(ErroresSolicitudCartera.SinTenantDeOrigen);

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return Result.Exito(new ContextoOperadorCae(usuarioId.Value, origen.Value, null));

            var rol = await directorioUsuarios.EsCuentaActivaConRolAsync(
                          usuarioId.Value, origen.Value, RolCoordinadorCae, cancellationToken)
                ? RolCoordinadorCae
                : await directorioUsuarios.EsCuentaActivaConRolAsync(
                      usuarioId.Value, origen.Value, RolGestorCae, cancellationToken)
                    ? RolGestorCae
                    : null;

            return Result.Exito(new ContextoOperadorCae(usuarioId.Value, origen.Value, rol));
        }
    }

    /// <summary>Abre el ámbito del Operador CAE para el resto del handler.</summary>
    public IDisposable EnOrigen() => AmbitoTenantExplicito.Establecer(OperadorTenantId);
}

/// <summary>
/// Códigos estables: la pantalla los compara literalmente. Cambiar uno es un
/// cambio de contrato con sus consumidores (Mi trabajo Gen2 y la bandeja).
/// </summary>
public static class ErroresSolicitudCartera
{
    public static readonly Error SinPermiso = Error.Crear(
        "SolicitudCartera.SinPermiso", "No tienes permiso para esta acción sobre las incorporaciones a cartera.");

    public static readonly Error SinTenantDeOrigen = Error.Crear(
        "SolicitudCartera.SinTenantDeOrigen", "No pudimos determinar desde qué organización operas.");

    public static readonly Error TenantNoCandidato = Error.Crear(
        "SolicitudCartera.TenantNoCandidato",
        "Esa empresa no está disponible para incorporarla a tu cartera.");

    public static readonly Error YaPendiente = Error.Crear(
        "SolicitudCartera.YaPendiente", "Ya tienes una solicitud pendiente para esa empresa.");

    public static readonly Error MensajeInvalido = Error.Crear(
        "SolicitudCartera.MensajeInvalido",
        $"Escribe un mensaje de hasta {Domain.Operaciones.SolicitudIncorporacionCartera.LongitudMaximaMensaje} caracteres.");

    public static readonly Error NoEncontrada = Error.Crear(
        "SolicitudCartera.NoEncontrada", "No encontramos esa solicitud.");

    public static readonly Error YaResuelta = Error.Crear(
        "SolicitudCartera.YaResuelta", "Otra persona ya resolvió esta solicitud.");

    public static readonly Error PropiaSolicitud = Error.Crear(
        "SolicitudCartera.PropiaSolicitud", "No puedes resolver tu propia solicitud.");

    public static readonly Error NoRevocable = Error.Crear(
        "SolicitudCartera.NoRevocable", "Solo se puede revocar una incorporación aceptada.");

    public static readonly Error SolicitanteNoDisponible = Error.Crear(
        "SolicitudCartera.SolicitanteNoDisponible",
        "La persona que la pidió ya no es Gestor CAE activo; la solicitud se ha anulado.");

    public static readonly Error OperacionNoVigente = Error.Crear(
        "SolicitudCartera.OperacionNoVigente",
        "Tu organización ya no gestiona esa empresa; la solicitud se ha anulado.");

    public static readonly Error YaEnCartera = Error.Crear(
        "SolicitudCartera.YaEnCartera", "Esa persona ya tiene la empresa en su cartera; la solicitud se ha anulado.");
}
