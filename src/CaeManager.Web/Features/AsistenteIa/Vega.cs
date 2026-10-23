namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>
/// Identidad del asistente IA de TALVEG (marco-talveg.js, 2026-10-21). «Vega» es un nombre
/// propio y no se traduce (como <c>Marca.Nombre</c>), y la tecla del atajo se escribe igual
/// en todos los idiomas; por eso no viven en el .resx.
/// </summary>
public static class Vega
{
    public const string Nombre = "Vega";

    public const string Atajo = "Ctrl I";

    /// <summary>Ruta de la pantalla completa. En ella no hay botón flotante ni atajo para plegar.</summary>
    public const string RutaPantallaCompleta = "/asistente";

    /// <summary>¿Es <paramref name="rutaRelativa"/> (sin barra inicial, como la da NavigationManager.ToBaseRelativePath) la pantalla completa?</summary>
    public static bool EsPantallaCompleta(string rutaRelativa)
    {
        var ruta = rutaRelativa.Split('?', '#')[0].TrimEnd('/');
        return string.Equals(ruta, RutaPantallaCompleta.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }
}
