namespace CaeManager.Web.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosComunes&gt;</c>: vocabulario de
/// interfaz compartido por varias pantallas (acciones genéricas, avisos
/// genéricos) y los textos de la preferencia de idioma de la cuenta. Lo
/// propio de una Feature va en el recurso de esa Feature, no aquí.
///
/// <para>
/// <c>TextosComunes.resx</c> es el recurso neutral (español, la cultura por
/// defecto) y <c>TextosComunes.ca-ES.resx</c> el catalán, con las mismas
/// claves (lo exige <c>ParidadClavesRecursosTests</c>, Architecture.Tests).
/// El nombre del recurso incrustado sale de este tipo (convención
/// DependentUpon del SDK), así que su namespace tiene que seguir alineado con
/// la carpeta.
/// </para>
/// </summary>
public sealed class TextosComunes;
