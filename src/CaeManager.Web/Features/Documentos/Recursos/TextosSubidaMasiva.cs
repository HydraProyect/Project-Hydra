namespace CaeManager.Web.Features.Documentos.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosSubidaMasiva&gt;</c>: los textos de
/// la pantalla de Subida múltiple (<c>/documentos/subida-masiva</c>).
/// <c>TextosSubidaMasiva.resx</c> es el neutral (español) y
/// <c>TextosSubidaMasiva.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Migración parcial: solo los textos que nombran los límites de la subida
/// (archivos por lote y MB por archivo) salen de aquí, con el valor tomado de
/// las constantes del componente para que el texto no pueda desalinearse del
/// límite que se aplica. El resto de la pantalla sigue contado en el
/// trinquete <c>TextosSinLocalizarCongeladosTests</c>.
/// </para>
/// </summary>
public sealed class TextosSubidaMasiva;
