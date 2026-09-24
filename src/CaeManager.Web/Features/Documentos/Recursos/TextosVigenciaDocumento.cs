using System.Globalization;
using System.Resources;

namespace CaeManager.Web.Features.Documentos.Recursos;

/// <summary>
/// Textos de la vigencia del Documento que comparten varias pantallas: la casilla
/// «No caduca», el estado «Sin confirmar» y el error de marcar fecha y «No caduca» a la
/// vez. <c>TextosVigenciaDocumento.resx</c> es el neutral (español) y
/// <c>TextosVigenciaDocumento.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Se lee con el ayudante estático, sin inyectar el localizador, igual que
/// <c>TextosDrawerGestionDocumento</c>: lo usan componentes que se montan dentro de muchas
/// pantallas y sus tests, y código estático como <c>EstadoDocumentoUi</c>.
/// </para>
/// </summary>
public sealed class TextosVigenciaDocumento
{
    private static readonly ResourceManager Recursos = new(typeof(TextosVigenciaDocumento));

    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;
}
