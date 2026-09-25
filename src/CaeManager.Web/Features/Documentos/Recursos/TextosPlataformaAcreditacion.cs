using System.Globalization;
using System.Resources;

namespace CaeManager.Web.Features.Documentos.Recursos;

/// <summary>
/// Textos del bucle de corrección de la pestaña Plataforma (P0-9b): «Subir versión
/// corregida», «Abrir portal» y el aviso de la plataforma a la que lleva un
/// <c>?acreditacionId=</c>. <c>TextosPlataformaAcreditacion.resx</c> es el neutral
/// (español) y <c>TextosPlataformaAcreditacion.ca-ES.resx</c> el catalán, con las
/// mismas claves (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Se lee con el ayudante estático, igual que <c>TextosVigenciaDocumento</c>: el resto
/// de <c>PlataformaTab</c> todavía no está localizado y este incremento solo añade
/// rótulos nuevos, sin mover los existentes.
/// </para>
/// </summary>
public sealed class TextosPlataformaAcreditacion
{
    private static readonly ResourceManager Recursos = new(typeof(TextosPlataformaAcreditacion));

    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;

    public static string Formato(string clave, params object[] argumentos) =>
        string.Format(CultureInfo.CurrentCulture, Texto(clave), argumentos);
}
