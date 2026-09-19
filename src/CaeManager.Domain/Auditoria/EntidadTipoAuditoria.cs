namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Los valores de <see cref="RegistroAuditoria.EntidadTipo"/> que NO son el
/// nombre simple de una clase de <c>CaeManager.Domain</c>: las entidades de
/// ASP.NET Core Identity que <c>AuditoriaInterceptor</c> audita por excepción
/// explícita, renombradas al castellano igual que el resto de EntidadTipo.
///
/// <para>
/// Viven aquí, y no en el interceptor que las produce, porque son un
/// <b>contrato compartido por cuatro consumidores en tres capas</b> que antes
/// repetían el literal: el propio <c>AuditoriaInterceptor</c> (Infrastructure)
/// que los escribe, <c>TenantSelladoInterceptor</c> (Infrastructure) que
/// decide por ellos de qué Tenant propietario es la fila, el filtro de tipo de
/// entidad de <c>Auditoria.razor.cs</c> (Web) y la resolución del nombre de la
/// cuenta afectada de esa misma pantalla. Añadir un tipo nuevo al primero y
/// olvidarlo en el segundo no da un error de compilación: da un
/// <c>SaveChanges</c> que falla cerrado en el camino sin sesión (login SSO,
/// restablecimiento de contraseña anónimo) — exactamente la clase de agujero
/// por omisión que esta auditoría existe para no tener.
/// </para>
/// </summary>
public static class EntidadTipoAuditoria
{
    /// <summary><c>ApplicationUser</c>: alta, edición, baja y activación de una cuenta.</summary>
    public const string Usuario = "Usuario";

    /// <summary><c>IdentityUserRole&lt;Guid&gt;</c>: concesión y revocación de un rol.</summary>
    public const string RolDeUsuario = "RolDeUsuario";

    /// <summary>
    /// <c>IdentityUserLogin&lt;Guid&gt;</c>: vinculación (o desvinculación) de
    /// un proveedor de login externo — Microsoft Entra ID hoy. No concede
    /// autoridad nueva, pero abre un segundo camino de entrada a una cuenta
    /// que ya existe, y eso es "qué pasó con esta cuenta".
    /// </summary>
    public const string LoginExterno = "LoginExterno";

    /// <summary>
    /// <c>IdentityUserToken&lt;Guid&gt;</c>: el secreto del segundo factor
    /// (TOTP) y los demás tokens por usuario. Cambia qué código acepta el
    /// sistema como segundo factor de esa cuenta; el valor en sí NUNCA entra
    /// en el registro (ver <c>AuditoriaInterceptor.PropiedadesSensiblesPorTipo</c>).
    /// </summary>
    public const string TokenDeUsuario = "TokenDeUsuario";

    /// <summary>
    /// Los cuatro anteriores. En todos ellos
    /// <see cref="RegistroAuditoria.EntidadId"/> es un <c>ApplicationUser.Id</c>
    /// —la cuenta afectada—, no la clave de la fila escrita: las tres tablas
    /// de relación de Identity tienen clave compuesta, y "a quién le pasó
    /// esto" es lo que el registro existe para responder.
    ///
    /// <para>
    /// Lista y no conjunto a propósito: uno de sus consumidores es el
    /// desplegable de tipo de entidad de /auditoria, y un <c>HashSet</c>
    /// dejaría el orden de las opciones a merced de la implementación. Con
    /// cuatro elementos, buscar en una lista no cuesta nada.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> TodosLosDeIdentidad =
        [Usuario, RolDeUsuario, LoginExterno, TokenDeUsuario];
}
