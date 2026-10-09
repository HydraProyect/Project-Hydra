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
/// <c>CrearUsuarioCommand</c>— o Coordinador CAE. Se lee en Identity sobre el Tenant de
/// origen, no en el claim: dentro de un Workspace operativo derivado el claim es el de la
/// cartera, y la sesión de una cuenta desactivada no debe seguir escribiendo.</item>
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
    /// Coordinador CAE—, leído en Identity sobre su Tenant de origen. Falla con
    /// <see cref="SinAutoridad"/> si no tiene ninguno o la sesión no tiene rol de negocio.
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
            // Falla cerrado sin rol de negocio en la sesión, igual que ResolverAsync.
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return Result.Fallo<(Guid, Guid, string)>(SinAutoridad);

            foreach (var rol in RolesAutorizados)
            {
                if (await directorioUsuarios.EsCuentaActivaConRolAsync(actorId.Value, origen.Value, rol, cancellationToken))
                    return Result.Exito((origen.Value, actorId.Value, rol));
            }

            return Result.Fallo<(Guid, Guid, string)>(SinAutoridad);
        }
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
            // Falla cerrado sin rol de negocio en la sesión (sesión privilegiada de plataforma,
            // delegación retirada): Soporte TALVEG nunca es Operador CAE ni Gestor CAE.
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return Result.Fallo<ContextoCarteraDeGestorCae>(SinAutoridad);

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
