namespace CaeManager.Web.Features.Subcontratas.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosSubcontratas&gt;</c>: los textos
/// de la lista de subcontratas (cabecera, búsqueda, estados vacíos, filas con
/// recuentos, alta y eliminación), del acordeón de trabajadores, de la vista
/// previa, de Subcontrata 360 (información, credenciales, supervisión,
/// centros) y del Excel de exportación, más los rótulos de
/// <c>EstadoSupervisionUi</c>. <c>TextosSubcontratas.resx</c> es el neutral
/// (español) y <c>TextosSubcontratas.ca-ES.resx</c> el catalán, con las mismas
/// claves (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora el catalán es copia idéntica del neutral: la traducción es un
/// incremento propio. Los nombres de subcontratas, centros, trabajadores y
/// tipos de documento salen de datos y no pasan por aquí, igual que los
/// mensajes de <c>Result.Error</c> y de validación que llegan de Application:
/// se muestran tal cual.
/// </para>
/// </summary>
public sealed class TextosSubcontratas;
