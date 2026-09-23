namespace CaeManager.Web.Features.Trabajadores.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosTrabajadores&gt;</c>: los textos
/// de la lista de trabajadores (cabecera, filtros, chips, rejilla, alta,
/// borrado, asignación a centro y filtros guardados), de Trabajador 360, de
/// la vista previa y del panel del Context Workspace.
/// <c>TextosTrabajadores.resx</c> es el neutral (español) y
/// <c>TextosTrabajadores.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora el catalán es copia idéntica del neutral: la traducción es un
/// incremento propio. Los nombres de trabajadores, centros, empresas y tipos
/// de documento salen de datos y no pasan por aquí, igual que los mensajes de
/// <c>Result.Error</c> que llegan de Application: se muestran tal cual.
/// </para>
/// </summary>
public sealed class TextosTrabajadores;
