using System.Globalization;
using System.Resources;

namespace CaeManager.Web.Features.BusquedaGlobal.Recursos;

/// <summary>
/// Textos de la paleta global (Ctrl/Cmd+K). <c>TextosBusquedaGlobal.resx</c> es
/// el neutral (español) y <c>TextosBusquedaGlobal.ca-ES.resx</c> el catalán,
/// con las mismas claves.
///
/// <para>
/// Hoy solo tiene la entrada de Mi trabajo Gen2 en «Ir a»: el resto de
/// <see cref="CoberturaDePaleta.DestinosNavegacion"/> sigue escrito a mano,
/// congelado por <c>TextosSinLocalizarCongeladosTests</c>, a la espera de que
/// se migre la paleta entera. <see cref="Texto"/> es estático porque
/// <see cref="CoberturaDePaleta"/> es una tabla estática. Lee el recurso con
/// la cultura de interfaz de cada llamada, no con la del arranque.
/// </para>
/// </summary>
public sealed class TextosBusquedaGlobal
{
    private static readonly ResourceManager Recursos = new(typeof(TextosBusquedaGlobal));

    public static string Texto(string clave) =>
        Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;
}
