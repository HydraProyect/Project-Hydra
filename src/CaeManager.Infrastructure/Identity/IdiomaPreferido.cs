namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Idioma de la interfaz elegido por el usuario. Se persiste como el entero del
/// enum (igual que <see cref="TemaPreferido"/>), no como nombre de cultura: la
/// correspondencia con <c>es-ES</c>/<c>ca-ES</c> vive en un único sitio de Web
/// (<c>CulturaUsuarioCookie</c>), no en la columna. Los valores son explícitos
/// porque la migración fija <c>0</c> como valor por defecto de las cuentas
/// existentes: reordenar los miembros cambiaría el idioma de todas ellas.
/// </summary>
public enum IdiomaPreferido
{
    Espanol = 0,
    Catalan = 1,
}
