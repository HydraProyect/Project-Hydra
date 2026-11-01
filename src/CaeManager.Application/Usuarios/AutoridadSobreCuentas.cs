using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios;

/// <summary>
/// Quién puede administrar las cuentas de Identity del Tenant propietario, y las
/// reglas que los Commands de <c>Usuarios/Commands</c> comparten (P1-I2). Antes
/// vivían en <c>Usuarios.razor.cs</c> y <c>Roles.razor.cs</c>: la autorización era
/// de la página, y un segundo camino hasta <c>UserManager</c> no la heredaba.
///
/// <para>
/// Los nombres de rol van en literales porque Application no referencia
/// Infrastructure.Identity (mismo motivo que <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>);
/// <c>RolesDeCuentasParidadTests</c> vigila que no diverjan de <c>Roles.Todos</c>.
/// </para>
///
/// <para>
/// El rol que se evalúa es el <b>efectivo</b> (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>).
/// Dentro de un Tenant beneficiario delegado por Asignación de Operación es el
/// perfil de origen de la persona del Operador CAE externo, con todos sus
/// poderes, también los de Propiedad, y sin Encargo de administración (decisión
/// Chris 2026-10-20, que deroga «la operación no concede roles de Propiedad» y,
/// en el rol, la D-8 del 2026-10-08): su Administrador administra las cuentas
/// del Tenant beneficiario como las de su propio Tenant.
/// </para>
///
/// <para>
/// Las dos reglas que comparan el Tenant de origen de quien actúa con el Tenant
/// activo —no conceder Administrador o Dirección CAE
/// (<see cref="RolesReservadosAlTenantDeOrigen"/>) y no actuar sobre una cuenta que
/// ya tiene uno de esos roles (<see cref="CuentasConRolDePropiedad"/>)— dejan pasar a
/// quien opera el Tenant por Asignación de Operación
/// (<see cref="IEncargoDeAdministracionActual.OperaPorAsignacionDeOperacionAsync"/>) o
/// por un Encargo de administración pleno
/// (<see cref="IEncargoDeAdministracionActual.EncargoPlenoAsync"/>, decisión del
/// 2026-10-15; deuda: ya no lo señala nadie). Sin señal fallan cerradas: la vía
/// heredada (acceso de soporte) y la Sesión Privilegiada no la dan.
/// </para>
///
/// <para>
/// Encima de esas dos reglas de Tenant va una de rol (decisión del 2026-10-30): el rol
/// Administrador solo lo concede un Administrador
/// (<see cref="VerificarConcesionDeAdministradorAsync"/>), y por la misma razón solo un
/// Administrador emite o corrige el enlace de activación de una cuenta Administrador
/// pendiente (<see cref="VerificarActivacionDeCuentaAdministradorAsync"/>). Dirección CAE
/// gestiona el resto de cuentas. Y nadie deja al Tenant sin Administrador
/// (<see cref="VerificarQueNoEsElUltimoAdministradorAsync"/>).
/// </para>
/// </summary>
public static class AutoridadSobreCuentas
{
    public const string Administrador = "Administrador";

    /// <summary>Los seis roles de negocio que una cuenta puede tener (exactamente uno).</summary>
    public static readonly IReadOnlyList<string> RolesExistentes =
        [Administrador, "DireccionCae", "CoordinadorCae", "GestorCae", "Consulta", "Cliente"];

    /// <summary>Dan de alta, editan, activan y eliminan cuentas (la página /usuarios).</summary>
    public static readonly IReadOnlyList<string> RolesQueGestionanCuentas = [Administrador, "DireccionCae"];

    public const string RolGestorCae = "GestorCae";

    public const string RolCliente = "Cliente";

    public static readonly Error SinAutoridad = Error.Crear(
        "Usuarios.SinAutoridad", "Tu rol no permite administrar las cuentas de esta organización.");

    public static readonly Error NoEncontrado = Error.Crear("Usuarios.NoEncontrado", "No encontramos este usuario.");

    public static readonly Error RolDesconocido = Error.Crear("Usuarios.RolDesconocido", "Ese rol no existe.");

    public static readonly Error ClienteRequerido = Error.Crear(
        "Usuarios.ClienteRequerido",
        "Busca y confirma la identificación fiscal de la empresa a vincular antes de guardar.");

    /// <summary>
    /// Lo devuelve el puerto cuando la cuenta dejó de estar pendiente entre la
    /// comprobación del Command y la escritura.
    /// </summary>
    public static readonly Error YaNoPendiente = Error.Crear(
        "Usuarios.NoPendiente", "Esta cuenta ya no está pendiente de activación.");

    public static readonly Error CuentaInexistente = Error.Crear(
        "Usuarios.CuentaInexistente", "Esta cuenta ya no existe. Recargamos la lista.");

    public static readonly Error SoloUnAdministradorConcedeAdministrador = Error.Crear(
        "Usuarios.SoloUnAdministradorConcedeAdministrador",
        "Solo un Administrador de la organización puede conceder el rol Administrador.");

    public static async Task<bool> PuedeGestionarCuentasAsync(ICurrentUserService currentUserService) =>
        await currentUserService.ObtenerRolEfectivoAsync() is { } rol && RolesQueGestionanCuentas.Contains(rol);

    /// <summary>
    /// Decisión del propietario, 2026-10-30 (cierra el hallazgo alto de la revisión de
    /// madurez del 2026-10-08): el rol Administrador del Tenant solo lo concede un
    /// Administrador —por su rol efectivo— o un Actor de Plataforma TALVEG por su propio
    /// camino (el alta de un Tenant, que siembra al primer Administrador; no /usuarios).
    /// Dirección CAE, Coordinador CAE y Gestor CAE nunca lo conceden, ni a otra cuenta ni
    /// a sí mismos: conceder Propiedad no es una capacidad del plano Operación.
    ///
    /// <para>
    /// «Un Administrador» incluye, por decisión del propietario del mismo día (segunda
    /// entrada), al Administrador del Operador CAE externo que opera el Tenant beneficiario
    /// por una Asignación de Operación vigente: su rol efectivo allí es su perfil de origen
    /// (decisión del 2026-10-20) y concede Administrador en ese Tenant. La Dirección CAE,
    /// el Coordinador CAE y el Gestor CAE de ese Operador CAE externo, no. Lo fijan
    /// <c>GestionCuentasCommandsTests</c> («Operador_CAE_externo_…»).
    /// </para>
    ///
    /// <para>
    /// Es una regla de rol, no de Tenant: se compone con
    /// <see cref="RolesReservadosAlTenantDeOrigen"/>, que decide en qué Context Workspace
    /// puede concederse. Solo se evalúa cuando el guardado concede el rol: conservar el
    /// Administrador que la cuenta ya tenía no es concederlo.
    /// </para>
    /// </summary>
    public static async Task<Result> VerificarConcesionDeAdministradorAsync(
        string? rolConcedido, ICurrentUserService currentUserService) =>
        rolConcedido == Administrador && await currentUserService.ObtenerRolEfectivoAsync() != Administrador
            ? Result.Fallo(SoloUnAdministradorConcedeAdministrador)
            : Result.Exito();

    public static readonly Error UltimoAdministrador = Error.Crear(
        "Usuarios.UltimoAdministrador",
        "Es el único Administrador de la organización, y la organización no puede quedarse sin ninguno. " +
        "Antes tiene que haber otro Administrador.");

    /// <summary>
    /// Decisión del propietario, 2026-10-30: un Tenant no se queda sin Administrador. Se
    /// rechaza degradar, desactivar o eliminar la cuenta si es Administrador y el Tenant no
    /// tiene otro (<see cref="IAdministradoresDelTenant.EsElUltimoAdministradorAsync"/>; una
    /// cuenta pendiente de activación cuenta, en los dos lados: el último Administrador
    /// aún sin activar tampoco se elimina). Vale para cualquiera, también para el propio
    /// Administrador sobre su cuenta. La otra mitad de la decisión es la vía de Plataforma
    /// para reponer un Administrador.
    ///
    /// <para>
    /// Se llama dentro de la transacción que escribe, en todo cambio que pueda retirar a la
    /// cuenta, sea cual sea el rol que el Command le leyó al empezar: pudo ascender a
    /// Administrador después. Toma antes el candado de los Administradores del Tenant, para que
    /// dos retiradas simultáneas no puedan verse la una a la otra como «el otro Administrador»,
    /// y la lectura con el candado es la que decide. Sin Tenant activo no hay a quién preguntar
    /// y falla cerrada.
    /// </para>
    /// </summary>
    public static async Task<Result> VerificarQueNoEsElUltimoAdministradorAsync(
        Guid usuarioId, Guid? tenantActivo, IAdministradoresDelTenant administradores,
        CancellationToken cancellationToken)
    {
        if (tenantActivo is not { } tenantId)
            return Result.Fallo(UltimoAdministrador);

        await administradores.BloquearAsync(tenantId, cancellationToken);
        return await administradores.EsElUltimoAdministradorAsync(tenantId, usuarioId, cancellationToken)
            ? Result.Fallo(UltimoAdministrador)
            : Result.Exito();
    }

    public static readonly Error SoloUnAdministradorActivaAdministrador = Error.Crear(
        "Usuarios.SoloUnAdministradorActivaAdministrador",
        "Solo un Administrador de la organización puede enviar o corregir el enlace de activación de una cuenta Administrador.");

    /// <summary>
    /// Corolario de <see cref="VerificarConcesionDeAdministradorAsync"/> para una cuenta
    /// Administrador pendiente de activación: emitir su enlace de activación —que vuelve a
    /// quien lo pide— o corregirle el correo equivale a quedarse con la cuenta, es decir, a
    /// conceder Administrador a quien la active. Solo lo hace un Administrador (revisión
    /// puente del incremento del 2026-10-30).
    ///
    /// <para>
    /// Una cuenta pendiente <b>sin rol</b> cuenta igual: es lo que deja el alta de un
    /// Administrador cuando la asignación del rol falla después de crear la cuenta, y un
    /// Administrador la completará asignándole el rol. Si Dirección CAE pudiera reenviarle
    /// o corregirle el enlace antes, se quedaría con la cuenta que luego recibe Administrador.
    /// </para>
    /// </summary>
    public static async Task<Result> VerificarActivacionDeCuentaAdministradorAsync(
        IReadOnlyCollection<string> rolesDestino, ICurrentUserService currentUserService) =>
        (rolesDestino.Count == 0 || rolesDestino.Contains(Administrador))
        && await currentUserService.ObtenerRolEfectivoAsync() != Administrador
            ? Result.Fallo(SoloUnAdministradorActivaAdministrador)
            : Result.Exito();

    /// <summary>
    /// DEC-36 (REC-099): solo otro Administrador concede o revoca el permiso de
    /// rastro de acceso a documentos sensibles. Un Administrador que se edita a sí
    /// mismo no puede cambiar el suyo en ninguna dirección: el permiso existe para
    /// que nadie se audite a sí mismo sin que otro lo sepa, y la autoconcesión —o
    /// la autorrevocación silenciosa, que borra el rastro de quién lo tenía— rompe
    /// esa separación de funciones.
    ///
    /// <para>
    /// Solo se compara mientras <paramref name="rolNuevo"/> sigue siendo
    /// Administrador: si el rol cambia, el permiso se retira como consecuencia de
    /// dejar de serlo, no como una revocación decidida por nadie; bloquear eso
    /// impediría a un Administrador cambiar su propio rol.
    /// </para>
    /// </summary>
    public static bool EsAutogestionDelPermisoSensible(
        Guid idEditado, Guid? idActor, string rolNuevo, bool valorNuevo, bool valorActual) =>
        idActor is not null
        && idEditado == idActor.Value
        && rolNuevo == Administrador
        && valorNuevo != valorActual;
}
