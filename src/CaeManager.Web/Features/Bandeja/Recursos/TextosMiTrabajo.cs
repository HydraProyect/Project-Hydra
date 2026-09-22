using System.Globalization;
using System.Resources;

namespace CaeManager.Web.Features.Bandeja.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosMiTrabajo&gt;</c>: los textos de
/// Mi trabajo Gen2 (<c>/mi-trabajo</c>). <c>TextosMiTrabajo.resx</c> es el
/// neutral (español) y <c>TextosMiTrabajo.ca-ES.resx</c> el catalán, con las
/// mismas claves (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Los componentes inyectan el localizador. <see cref="Texto"/> y
/// <see cref="Formato"/> son para el código sin inyección
/// (<c>MiTrabajoVista</c>, <c>TipoItemBandejaUi</c>). Leen el mismo recurso
/// incrustado con la cultura de interfaz en curso, que es lo mismo que hace
/// <c>ResourceManagerStringLocalizer</c> por debajo.
/// </para>
///
/// <para>
/// Los patrones de fecha (<c>FormatoDiaMes</c>, <c>FormatoFechaLarga</c>) son
/// la forma correcta de cada cultura, no una traducción. Los nombres de mes de
/// ca-ES ya llevan la preposición («de setembre»), así que el patrón español
/// daría «22 de de setembre».
/// </para>
/// </summary>
public sealed class TextosMiTrabajo
{
    private static readonly ResourceManager Recursos = new(typeof(TextosMiTrabajo));

    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;

    public static string Formato(string clave, params object[] argumentos) =>
        string.Format(CultureInfo.CurrentCulture, Texto(clave), argumentos);
}
