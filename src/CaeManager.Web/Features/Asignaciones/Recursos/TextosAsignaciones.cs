namespace CaeManager.Web.Features.Asignaciones.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosAsignaciones&gt;</c>: los textos de
/// la exportación a Excel de asignaciones (nombre de hoja, cabeceras y estado).
/// <c>TextosAsignaciones.resx</c> es el neutral (español) y
/// <c>TextosAsignaciones.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// <c>ColumnaCliente</c> rotula «Cliente» (decisión de rótulo del 2026-10-09; la columna
/// muestra <c>AsignacionListaDto.ClienteNombre</c>); el nombre del DTO sigue siendo deuda terminológica, y
/// renombrarlo es un incremento de terminología, no de localización.
/// </para>
/// </summary>
public sealed class TextosAsignaciones;
