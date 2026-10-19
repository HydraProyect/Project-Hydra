using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;

/// <summary>Entidades asociadas con documentos operativos visibles, sin materializar todo el catálogo.</summary>
public record ObtenerEntidadesAsociadasDeDocumentosQuery(
    string? Busqueda, AmbitoAplicacion? Ambito = null, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<EntidadAsociadaDocumentoDto>>;

public record EntidadAsociadaDocumentoDto(Guid Id, AmbitoAplicacion Ambito, string Nombre);
