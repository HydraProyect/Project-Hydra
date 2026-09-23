namespace CaeManager.Web.Features.TiposDocumento.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosTiposDocumento&gt;</c>: los textos
/// de la pantalla del catálogo de tipos de documento (cabecera, filtros,
/// estados vacíos, tabla con interruptores en línea, formulario, diálogo de
/// confirmación y toasts) y los rótulos de <c>RequisitoDocumentalUi</c>.
/// <c>TextosTiposDocumento.resx</c> es el neutral (español) y
/// <c>TextosTiposDocumento.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora el catalán es copia idéntica del neutral: la traducción es un
/// incremento propio. Los nombres de los tipos de documento salen de datos
/// y no pasan por aquí, igual que los mensajes de <c>Result.Error</c> que
/// llegan de Application: se muestran tal cual.
/// </para>
/// </summary>
public sealed class TextosTiposDocumento;
