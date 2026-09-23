using System.Globalization;
using System.Resources;

namespace CaeManager.Web.Features.Documentos.Recursos;

/// <summary>
/// Textos de <c>DrawerGestionDocumento</c>. <c>TextosDrawerGestionDocumento.resx</c> es el
/// neutral (español) y <c>TextosDrawerGestionDocumento.ca-ES.resx</c> el catalán, con las mismas
/// claves (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Migración parcial: solo el aviso de Trabajador preseleccionado fuera del catálogo con alcance
/// sale de aquí; el resto del drawer sigue contado en el trinquete
/// <c>TextosSinLocalizarCongeladosTests</c>. Se lee con el ayudante estático, sin inyectar el
/// localizador, igual que <c>TextosMiTrabajo</c> para el código sin inyección: el drawer se
/// monta dentro de muchas pantallas y sus tests, y ninguno registra la localización.
/// </para>
/// </summary>
public sealed class TextosDrawerGestionDocumento
{
    private static readonly ResourceManager Recursos = new(typeof(TextosDrawerGestionDocumento));

    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;
}
