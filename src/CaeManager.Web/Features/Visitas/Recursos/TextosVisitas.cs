namespace CaeManager.Web.Features.Visitas.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosVisitas&gt;</c>: los textos de la
/// pantalla de Visitas (lista, formulario, detalle con comprobación previa,
/// diálogos y toasts). <c>TextosVisitas.resx</c> es el neutral (español) y
/// <c>TextosVisitas.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora el catalán es copia idéntica del neutral: la traducción es un
/// incremento propio. Los mensajes de <c>Result.Error</c> que llegan de
/// Application no pasan por aquí: se muestran tal cual. Tampoco los textos
/// de <c>NivelUrgenciaVisitaUi</c> y <c>AntelacionVisitaUi</c>: son estáticos
/// y los comparten Dashboard y DashboardEjecutivo.
/// </para>
/// </summary>
public sealed class TextosVisitas;
