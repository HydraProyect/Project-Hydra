namespace CaeManager.Web.Features.Usuarios.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosUsuarios&gt;</c>: los textos de la
/// pantalla de Usuarios. <c>TextosUsuarios.resx</c> es el neutral (español) y
/// <c>TextosUsuarios.ca-ES.resx</c> el catalán, con las mismas claves
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Por ahora solo lleva los textos del restablecimiento de la verificación en
/// dos pasos (P0-8); el resto de la pantalla sigue congelado en
/// <c>TextosSinLocalizarCongeladosTests</c>. Los mensajes de <c>Result.Error</c>
/// que llegan de Application no pasan por aquí: se muestran tal cual.
/// </para>
/// </summary>
public sealed class TextosUsuarios;
