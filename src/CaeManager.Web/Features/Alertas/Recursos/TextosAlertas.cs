namespace CaeManager.Web.Features.Alertas.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosAlertas&gt;</c>: los textos de la
/// pantalla de Alertas (lista de alertas y reclamación agregada).
/// <c>TextosAlertas.resx</c> es el neutral (español) y
/// <c>TextosAlertas.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora el catalán es copia idéntica del neutral: la traducción es un
/// incremento propio. Los mensajes de <c>Result.Error</c> que llegan de
/// Application no pasan por aquí: se muestran tal cual.
/// </para>
/// </summary>
public sealed class TextosAlertas;
