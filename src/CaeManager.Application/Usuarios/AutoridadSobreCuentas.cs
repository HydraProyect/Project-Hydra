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
/// El rol que se evalúa es el <b>efectivo</b> (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>):
/// dentro de un Workspace operativo derivado es el de la Asignación de Cartera,
/// que nunca es Administrador ni Dirección CAE (la operación no concede roles
/// de propiedad), así que un Operador CAE externo no administra las cuentas del
/// Tenant propietario.
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

    public static readonly Error CuentaInexistente = Error.Crear(
        "Usuarios.CuentaInexistente", "Esta cuenta ya no existe. Recargamos la lista.");

    public static async Task<bool> PuedeGestionarCuentasAsync(ICurrentUserService currentUserService) =>
        await currentUserService.ObtenerRolEfectivoAsync() is { } rol && RolesQueGestionanCuentas.Contains(rol);

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
