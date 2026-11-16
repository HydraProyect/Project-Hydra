namespace CaeManager.Web.Features.Cumplimiento.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosAceptacionContractual&gt;</c>: los rótulos de
/// <c>AceptacionContractualGate</c> y de la página que muestra el texto de una versión
/// (<c>DocumentoContractual</c>), #2009. <c>TextosAceptacionContractual.resx</c> es el neutral
/// (español) y <c>TextosAceptacionContractual.ca-ES.resx</c> el catalán, con las mismas claves.
///
/// <para>
/// <b>No son texto contractual.</b> Lo que se acepta es el texto de cada versión del catálogo
/// (<c>ICatalogoDocumentosContractuales</c>), con su título, y eso lo fija Chris con el abogado. Estos
/// rótulos son neutros e interfaz. <c>CasillaDocumento</c> y <c>CasillaFacultades</c> son, sin embargo,
/// la fórmula con la que se acepta y se declaran facultades: su redacción, también en catalán, la revisa
/// el abogado cuando se publiquen los textos, y cambiarla es una decisión legal, no de estilo.
/// <c>{0}</c> es el título del documento y <c>{1}</c> su versión.
/// </para>
/// </summary>
public sealed class TextosAceptacionContractual;
