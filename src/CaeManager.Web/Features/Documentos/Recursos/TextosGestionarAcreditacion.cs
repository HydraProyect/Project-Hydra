using System.Globalization;
using System.Resources;

namespace CaeManager.Web.Features.Documentos.Recursos;

/// <summary>
/// Textos del panel «Gestionar acreditación en plataforma» y de las dos líneas «En TALVEG» / «En la plataforma» de las filas
/// de documento vistas desde un Centro. <c>TextosGestionarAcreditacion.resx</c> es el neutral (español) y
/// <c>TextosGestionarAcreditacion.ca-ES.resx</c> el catalán, con las mismas claves.
/// </summary>
public sealed class TextosGestionarAcreditacion
{
    private static readonly ResourceManager Recursos = new(typeof(TextosGestionarAcreditacion));

    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;

    public static string Formato(string clave, params object[] argumentos) =>
        string.Format(CultureInfo.CurrentCulture, Texto(clave), argumentos);
}
