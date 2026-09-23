namespace CaeManager.Web.Features.Proyectos.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosProyectos&gt;</c>: los textos de la
/// pantalla de Proyectos (lista por cliente, filtros, panel de detalle con sus
/// pestañas, alta, cierre y eliminación).
/// <c>TextosProyectos.resx</c> es el neutral (español) y
/// <c>TextosProyectos.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora el catalán es copia idéntica del neutral: la traducción es un
/// incremento propio. Los mensajes de <c>Result.Error</c> y de validación que
/// llegan de Application no pasan por aquí: se muestran tal cual.
/// </para>
/// </summary>
public sealed class TextosProyectos;
