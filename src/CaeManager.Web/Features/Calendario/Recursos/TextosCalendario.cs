namespace CaeManager.Web.Features.Calendario.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosCalendario&gt;</c>: los textos de
/// la pantalla de Calendario. <c>TextosCalendario.resx</c> es el neutral
/// (español) y <c>TextosCalendario.ca-ES.resx</c> el catalán, con las mismas
/// claves (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Los patrones de fecha (<c>FormatoDiaMes</c>, <c>FormatoFechaLarga</c>) no
/// son texto que se traduzca más tarde, sino la forma correcta de cada
/// cultura, y por eso el catalán no copia el español: los nombres de mes de
/// ca-ES ya llevan la preposición en genitivo («de setembre», «d’octubre»),
/// y el patrón español daría «22 de de setembre».
/// </para>
/// </summary>
public sealed class TextosCalendario;
