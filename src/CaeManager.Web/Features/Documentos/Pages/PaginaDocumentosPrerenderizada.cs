using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;

namespace CaeManager.Web.Features.Documentos.Pages;

/// <summary>
/// La página de filas del listado de Documentos tal como viaja del prerenderizado al circuito
/// (<see cref="Components.EstadoPrerenderizado{TValor}"/>). No viaja el <see cref="ResultadoPaginado{T}"/> tal cual
/// porque su JSON pierde lo que la página pinta: <see cref="DocumentoListaDto.DestinosEnCentros"/> lleva
/// <c>[JsonIgnore]</c> (no es contrato de la API pública), así que va aparte, fila a fila.
/// </summary>
public sealed record PaginaDocumentosPrerenderizada(
    IReadOnlyList<DocumentoListaDto> Elementos,
    IReadOnlyList<IReadOnlyList<DocumentoEnCentroListaDto>?> DestinosEnCentros,
    int TotalElementos,
    int Pagina,
    int TamanoPagina,
    IReadOnlyDictionary<string, int>? RecuentosPorEstado,
    int? TotalSinFiltroDeEstado)
{
    public static PaginaDocumentosPrerenderizada Desde(ResultadoPaginado<DocumentoListaDto> resultado) =>
        new(resultado.Elementos,
            resultado.Elementos.Select(d => d.DestinosEnCentros).ToList(),
            resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina,
            resultado.RecuentosPorEstado, resultado.TotalSinFiltroDeEstado);

    public ResultadoPaginado<DocumentoListaDto> AResultado() =>
        new(Elementos.Select((d, i) => d with { DestinosEnCentros = DestinosEnCentros[i] }).ToList(),
            TotalElementos, Pagina, TamanoPagina)
        {
            RecuentosPorEstado = RecuentosPorEstado,
            TotalSinFiltroDeEstado = TotalSinFiltroDeEstado,
        };
}
