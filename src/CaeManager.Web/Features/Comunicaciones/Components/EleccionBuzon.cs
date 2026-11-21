namespace CaeManager.Web.Features.Comunicaciones.Components;

/// <summary>
/// Lo que se ha elegido en el desplegable «Buzón» (<see cref="SelectorBuzon"/>, lienzo «Comunicaciones multi-buzón», L-9). Es una
/// coordenada del navegador que solo estrecha la lista: el id de la conexión viaja a la query de conversaciones, que lo suma con AND a
/// la regla de visibilidad del servidor. Nunca abre una conversación que esa regla deje fuera ni da permiso para nada.
/// </summary>
/// <param name="TenantId">La Empresa gestionada (Tenant beneficiario) del buzón; <c>null</c> en «Todos los buzones» y en «Mi buzón personal».</param>
/// <param name="ConexionId">La conexión de buzón elegida; <c>null</c> en «Todos los buzones» y en «Mi buzón personal».</param>
/// <param name="Nombre">El nombre que muestra el botón.</param>
/// <param name="Personal">«Mi buzón personal».</param>
public sealed record EleccionBuzon(Guid? TenantId, Guid? ConexionId, string Nombre, bool Personal = false)
{
    /// <summary>«Todos los buzones»: sin filtro de buzón.</summary>
    public bool EsTodos => ConexionId is null && !Personal;
}

/// <summary>Un buzón compartido del desplegable, con sus Abiertas o su estado «Con error».</summary>
/// <param name="Abiertas"><c>null</c> cuando el servidor no dio su actividad: el desplegable no muestra un cero que no ha contado.</param>
public sealed record BuzonDelDesplegable(Guid ConexionId, string Nombre, string Direccion, int? Abiertas, bool ConError);

/// <summary>
/// Los buzones compartidos de una Empresa gestionada (Tenant beneficiario). Sin <paramref name="Nombre"/>, el grupo se pinta sin
/// cabecera: es el caso de quien no tiene más Empresa gestionada que el Tenant activo.
/// </summary>
public sealed record GrupoBuzonesDelDesplegable(Guid TenantId, string? Nombre, IReadOnlyList<BuzonDelDesplegable> Buzones);
