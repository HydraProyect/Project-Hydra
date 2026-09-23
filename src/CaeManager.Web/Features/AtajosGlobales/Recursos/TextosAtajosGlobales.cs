namespace CaeManager.Web.Features.AtajosGlobales.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosAtajosGlobales&gt;</c>: los textos
/// de la chuleta de atajos de teclado («?»). <c>TextosAtajosGlobales.resx</c>
/// es el neutral (español) y <c>TextosAtajosGlobales.ca-ES.resx</c> el catalán,
/// con las mismas claves (<c>LocalizacionRecursosYRegistroTests</c>,
/// Architecture.Tests).
///
/// <para>
/// Las descripciones de <see cref="CatalogoAtajos"/> se guardan aquí por
/// clave (<see cref="DefinicionAtajo.ClaveDescripcion"/>): el catálogo sigue
/// siendo la fuente única que empareja cada tecla con su texto. Las teclas
/// («g c», «Ctrl/Cmd + K», «Enter») no se traducen, salvo «Clic» y
/// «Alt/Option + clic», que llevan una palabra y no solo nombres de tecla.
/// </para>
/// </summary>
public sealed class TextosAtajosGlobales;
