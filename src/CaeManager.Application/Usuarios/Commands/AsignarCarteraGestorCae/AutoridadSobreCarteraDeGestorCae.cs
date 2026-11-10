using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;

/// <summary>
/// El Operador CAE desde el que se opera, quién opera y la cuenta de Gestor CAE sobre la que se opera.
/// </summary>
public record ContextoCarteraDeGestorCae(Guid OperadorTenantId, Guid ActorUsuarioId, Guid GestorUsuarioId, bool GestorActivo);

/// <summary>
/// Quién puede asignar o retirar Tenants beneficiarios enteros de la cartera de un Gestor CAE
/// que ya existe, y sobre quién. Una sola regla para el Command y para la Query que alimenta
/// la pantalla.
///
/// <list type="bullet">
/// <item><b>Quién</b>: cuenta activa del Operador CAE externo (su Tenant de origen) con rol
/// Administrador o Dirección CAE —los que ya asignan cartera al dar de alta, ver
/// <c>CrearUsuarioCommand</c>— o Coordinador CAE. Decide el rol efectivo de la sesión en el
/// Tenant de origen e Identity confirma que la cuenta sigue activa con ese rol
/// (<see cref="RolDeAutoridadAsync"/>): la sesión de una cuenta desactivada no sigue
/// escribiendo, y una sesión limitada a Consulta no actúa con el rol de la cuenta en Identity.</item>
/// <item><b>Sobre quién</b>: un Gestor CAE del propio Operador CAE (nunca de un Operador CAE
/// externo delegado). Un Coordinador CAE solo sobre su equipo: los Gestores CAE que le
/// reportan (<c>CoordinadorUsuarioId</c>, decisión D-001, la misma jerarquía que acota su
/// lectura y que <see cref="ReglaDestinoCarteraCliente"/> aplica al reasignar). Administrador
/// y Dirección CAE, sobre cualquiera del Operador CAE.</item>
/// </list>
///
/// La Operación nunca concede roles de Propiedad: esta regla solo mueve carteras de rol
/// Gestor CAE y no toca el rol de nadie. Tampoco amplía <c>AlcanceDatosService</c>.
/// </summary>
public static class AutoridadSobreCarteraDeGestorCae
{
    private const string Administrador = "Administrador";
    private const string DireccionCae = "DireccionCae";
    private const string CoordinadorCae = "CoordinadorCae";
    private const string GestorCae = "GestorCae";

    private static readonly string[] RolesAutorizados = [Administrador, DireccionCae, CoordinadorCae];

    public static readonly Error SinAutoridad = Error.Crear(
        "Cartera.SinAutoridad", "Tu rol no permite asignar empresas a la cartera de un Gestor CAE.");

    public static readonly Error GestorNoAlcanzable = Error.Crear(
        "Cartera.GestorNoAlcanzable", "No encontramos a ese Gestor CAE en tu organización.");

    public static readonly Error GestorFueraDeTuEquipo = Error.Crear(
        "Cartera.GestorFueraDeTuEquipo", "Ese Gestor CAE no está en tu equipo: solo puedes cambiar la cartera de los tuyos.");

    public static readonly Error CuentaNoEsGestorCae = Error.Crear(
        "Cartera.CuentaNoEsGestorCae", "Solo un Gestor CAE tiene empresas en su cartera.");

    /// <summary>
    /// Quién puede <b>revocar un apoyo</b> (ADR-011 § 2.7, enmienda 2026-10-08, punto 6; D-4):
    /// los mismos tres roles, leídos igual. Es la única ampliación de la regla de arriba, y
    /// solo vale para la Asignación de Cartera de apoyo que nació de una propuesta de apoyo:
    /// un Coordinador CAE la revoca si le reporta <b>el Gestor CAE de apoyo o quien propuso el
    /// apoyo</b>, aunque el otro no sea de su equipo. Sin relación con ninguno de los dos, no.
    /// Administrador y Dirección CAE, sobre cualquier apoyo del Operador CAE.
    ///
    /// <para>
    /// La jerarquía se lee de las cuentas en el momento de revocar
    /// (<see cref="IDirectorioDestinosCartera"/>), no lo que valía al proponer: quien propuso
    /// pudo cambiar de equipo desde entonces. Se llama con el Tenant de origen del Operador
    /// CAE como Tenant activo, que es desde donde se ven esas cuentas.
    /// </para>
    /// </summary>
    public static async Task<bool> PuedeRevocarApoyoAsync(
        string rolActor, Guid actorUsuarioId, Guid apoyoUsuarioId, Guid proponenteUsuarioId,
        IDirectorioDestinosCartera directorioDestinos, CancellationToken cancellationToken)
    {
        if (rolActor is Administrador or DireccionCae)
            return true;
        if (rolActor != CoordinadorCae)
            return false;

        return await LeReportaAsync(apoyoUsuarioId) || await LeReportaAsync(proponenteUsuarioId);

        async Task<bool> LeReportaAsync(Guid usuarioId)
        {
            var cuenta = await directorioDestinos.ObtenerAsync(usuarioId, cancellationToken);
            return cuenta is { EsOperadorDelegado: false } && cuenta.CoordinadorUsuarioId == actorUsuarioId;
        }
    }

    /// <summary>
    /// El actor y su rol de autoridad sobre carteras —Administrador, Dirección CAE o
    /// Coordinador CAE—. Falla con <see cref="SinAutoridad"/> si no tiene ninguno o la sesión
    /// no tiene rol de negocio. Cómo se decide el rol, en <see cref="RolDeAutoridadAsync"/>.
    /// </summary>
    public static async Task<Result<(Guid OperadorTenantId, Guid ActorUsuarioId, string Rol)>> ResolverActorAsync(
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return Result.Fallo<(Guid, Guid, string)>(SinAutoridad);

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            var rol = await RolDeAutoridadAsync(
                actorId.Value, origen.Value, RolesAutorizados, currentUserService, directorioUsuarios, cancellationToken);
            return rol is null
                ? Result.Fallo<(Guid, Guid, string)>(SinAutoridad)
                : Result.Exito((origen.Value, actorId.Value, rol));
        }
    }

    /// <summary>
    /// El rol con el que el actor opera sobre las carteras de su Operador CAE, o <c>null</c>.
    /// Se llama con el Tenant de origen como <see cref="AmbitoTenantExplicito"/>: las cuentas, los
    /// equipos y las Asignaciones de Operación son de ese Tenant, sea cual sea el Workspace
    /// operativo derivado que la sesión tenga abierto.
    ///
    /// <para>
    /// Decide el <b>rol efectivo</b> de la sesión en ese Tenant
    /// (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>, decisión P7), con las
    /// restricciones de sesión ya aplicadas: una sesión de login local limitada a Consulta es
    /// Consulta aunque la cuenta sea de Dirección CAE, y una Sesión Privilegiada de plataforma no
    /// tiene rol de negocio (Soporte TALVEG nunca es Operador CAE ni Gestor CAE). Identity solo
    /// <b>confirma</b> que la cuenta sigue activa con ese mismo rol en su Tenant de origen
    /// (<see cref="IDirectorioUsuariosService.EsCuentaActivaConRolAsync"/>): una cuenta
    /// desactivada, o a la que retiraron el rol, no sigue operando con la sesión viva; y un rol
    /// concedido en Identity tras abrir la sesión no autoriza hasta la sesión siguiente. Es el
    /// mismo criterio que <c>ObtenerCapasCalendarioQuery</c> (OWASP ASVS 4.0.3 V4.1.3, V4.2.1).
    /// </para>
    /// </summary>
    internal static async Task<string?> RolDeAutoridadAsync(
        Guid actorUsuarioId,
        Guid origenTenantId,
        IReadOnlyCollection<string> rolesAdmitidos,
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol is null || !rolesAdmitidos.Contains(rol))
            return null;

        return await directorioUsuarios.EsCuentaActivaConRolAsync(actorUsuarioId, origenTenantId, rol, cancellationToken)
            ? rol
            : null;
    }

    public static async Task<Result<ContextoCarteraDeGestorCae>> ResolverAsync(
        Guid gestorUsuarioId,
        ICurrentUserService currentUserService,
        IDirectorioUsuariosService directorioUsuarios,
        IDirectorioDestinosCartera directorioDestinos,
        CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return Result.Fallo<ContextoCarteraDeGestorCae>(SinAutoridad);

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            var rolActor = await RolDeAutoridadAsync(
                actorId.Value, origen.Value, RolesAutorizados, currentUserService, directorioUsuarios, cancellationToken);
            if (rolActor is null)
                return Result.Fallo<ContextoCarteraDeGestorCae>(SinAutoridad);

            var gestor = await directorioDestinos.ObtenerAsync(gestorUsuarioId, cancellationToken);
            if (gestor is null || gestor.EsOperadorDelegado)
                return Result.Fallo<ContextoCarteraDeGestorCae>(GestorNoAlcanzable);
            if (gestor.RolEfectivo != GestorCae)
                return Result.Fallo<ContextoCarteraDeGestorCae>(CuentaNoEsGestorCae);

            if (rolActor == CoordinadorCae && gestor.CoordinadorUsuarioId != actorId)
                return Result.Fallo<ContextoCarteraDeGestorCae>(GestorFueraDeTuEquipo);

            return Result.Exito(new ContextoCarteraDeGestorCae(origen.Value, actorId.Value, gestorUsuarioId, gestor.Activa));
        }
    }
}
