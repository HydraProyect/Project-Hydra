namespace CaeManager.Web.Features.Comunicaciones.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosEstadosConversacion&gt;</c>: el nombre de cada <c>EstadoConversacion</c> en la bandeja
/// (chip de la fila y de la cabecera, filtro y selector «Cambiar estado…»). La clave de cada estado es el nombre del valor del enum
/// (<c>EsperandoAlCliente</c>…), así que un estado nuevo exige su texto aquí y en el catalán
/// (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests, y <c>EstadoConversacionPresentacionTests</c> en Web.Tests).
/// <c>TextosEstadosConversacion.resx</c> es el neutral (español) y <c>TextosEstadosConversacion.ca-ES.resx</c> el catalán.
/// </summary>
public sealed class TextosEstadosConversacion;
