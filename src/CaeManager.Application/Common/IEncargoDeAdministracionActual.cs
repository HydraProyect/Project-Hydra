namespace CaeManager.Application.Common;

/// <summary>
/// La señal «este usuario actúa ahora mismo por Encargo de administración»
/// (decisión D-8, 2026-10-08): el Id del encargo que está subiendo el techo de
/// su rol efectivo en el Tenant propietario que opera, o <c>null</c>.
///
/// <para>
/// La calcula <b>la misma función</b> que el rol efectivo
/// (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>): no puede
/// decir «por encargo» de alguien cuyo rol no se elevó, ni callarlo de alguien
/// cuyo rol sí. Es una interfaz aparte, y no un miembro más de
/// <see cref="ICurrentUserService"/>, solo para no obligar a sus decenas de
/// dobles de prueba a implementarla; la implementa la misma clase y el
/// contenedor la resuelve a la misma instancia. Un
/// <see cref="ICurrentUserService"/> registrado junto a <c>AddApplication()</c>
/// que no la implemente no degrada a «sin encargo»: el contenedor falla al
/// resolverla.
/// </para>
///
/// <para>
/// <see cref="EncargoQueElevaAsync"/> no autoriza: <b>restringe</b>.
/// <c>ExclusionesDelEncargoBehavior</c> niega las áreas excluidas, la
/// auditoría marca la vía, y la revalidación del circuito corta un claim que
/// ya no vale.
/// </para>
///
/// <para>
/// <see cref="EncargoPlenoAsync"/> sí abre (decisión de Chris, 2026-10-15): con
/// un encargo pleno —Tenant propietario sin Administrador propio, ni activo ni pendiente de activar— quien
/// administra por encargo es Administrador pleno, sin las exclusiones del
/// 2026-10-08. Lo consumen, y solo para levantar esas exclusiones, las
/// exclusiones por área, la reserva de cuentas con rol de Propiedad, la
/// renuncia al propio encargo y la activación del tratamiento con IA. Nunca
/// concede acceso a un Tenant: sin cartera no hay rol, haya o no encargo.
/// </para>
/// </summary>
public interface IEncargoDeAdministracionActual
{
    /// <summary>
    /// El encargo que eleva el rol efectivo en el contexto actual, resuelto en
    /// fresco contra la base, o <c>null</c> si el rol no está elevado.
    /// </summary>
    Task<Guid?> EncargoQueElevaAsync();

    /// <summary>
    /// El encargo que elevaba en la <b>última resolución</b> del rol efectivo
    /// de este ámbito para la operación seleccionada, sin consultar la base.
    /// Para la auditoría, que corre en cada guardado y no puede permitirse una
    /// consulta: todo Command pasa antes por <c>AutorizacionEscrituraBehavior</c>,
    /// que acaba de resolver el rol. <c>null</c> si no se elevó, si aún no se ha
    /// resuelto en este ámbito, o si la operación no es la de esa resolución.
    /// </summary>
    Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId);

    /// <summary>
    /// El encargo que eleva el rol efectivo <b>y es pleno</b> (el Tenant
    /// propietario no tiene Administrador propio, ni activo ni pendiente de activar), resuelto en fresco
    /// contra la base; <c>null</c> si no se elevó o si el encargo no es pleno.
    /// </summary>
    Task<Guid?> EncargoPlenoAsync();

    /// <summary>
    /// Como <see cref="EncargoDeLaUltimaResolucion"/>, pero solo si aquella
    /// resolución dio el encargo por pleno. Para el middleware que deja el claim.
    /// </summary>
    bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId);
}

/// <summary>
/// Sin sesión de usuario no hay encargo que eleve: es lo que resuelve el
/// contenedor cuando no hay ningún <see cref="ICurrentUserService"/> registrado.
/// Nunca es la implementación de la aplicación web: ahí la señal la da
/// <c>CurrentUserService</c>, y si lo que hay registrado no la da el contenedor
/// falla en vez de caer aquí (lo fija <c>EncargoDeAdministracionEnElContenedorTests</c>).
/// </summary>
public sealed class SinEncargoDeAdministracion : IEncargoDeAdministracionActual
{
    public static readonly SinEncargoDeAdministracion Instancia = new();

    public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult<Guid?>(null);

    public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => null;

    public Task<Guid?> EncargoPlenoAsync() => Task.FromResult<Guid?>(null);

    public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => false;
}
