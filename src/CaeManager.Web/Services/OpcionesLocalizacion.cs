namespace CaeManager.Web.Services;

/// <summary>
/// Interruptor único del catalán (<c>Localizacion:CatalanHabilitado</c>),
/// apagado por defecto en todos los entornos: decisión de producto del
/// 2026-09-26 — mientras la traducción no esté terminada, una funcionalidad a
/// medias se ve peor que una que no existe.
///
/// <para>
/// Apagado: <c>SelectorIdioma</c> no se pinta, la resolución de cultura
/// solo admite es-ES (una cookie <c>ca-ES</c> cae a es-ES, ver
/// <see cref="CulturaUsuarioCookie.ConfigurarLocalizacion"/>) y
/// <c>POST /cuenta/idioma</c> rechaza <c>ca-ES</c>. Ni la preferencia guardada
/// (<c>ApplicationUser.Idioma</c>) ni la cookie se borran: al encenderlo, cada
/// cuenta recupera el idioma que tenía. Los recursos ca-ES y la infraestructura
/// de localización siguen intactos.
/// </para>
/// </summary>
public sealed class OpcionesLocalizacion
{
    public const string SeccionConfiguracion = "Localizacion";

    public bool CatalanHabilitado { get; set; }
}
