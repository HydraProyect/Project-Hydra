using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Web.Api.V1;

public static class DocumentosApiEndpoints
{
    public static IEndpointRouteBuilder MapDocumentosApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // pagina/tamanoPagina llevan valor por defecto: sin él, Minimal API los
        // trata como parámetros de query obligatorios (verificado con curl).
        endpoints.MapGet("/documentos", async (
            Guid? trabajadorId, AmbitoAplicacion? ambito, string? busqueda, EstadoDocumento? estado, Guid? propietarioId,
            int pagina = 1, int tamanoPagina = 20, IMediator mediator = default!, ClaimsPrincipal usuario = default!,
            CancellationToken cancellationToken = default) =>
        {
            var resultado = await mediator.Send(
                new ObtenerDocumentosQuery(
                    trabajadorId, ambito, busqueda, estado, ApiV1.Pagina(pagina), ApiV1.TamanoPagina(tamanoPagina), propietarioId,
                    ExcluirCategoriaEspecialSalud: false),
                cancellationToken);

            return Results.Ok(new ResultadoPaginado<DocumentoApiListaDto>(
                resultado.Elementos.Select(DocumentoApiListaDto.DesdeInterno).ToList(),
                resultado.TotalElementos, resultado.Pagina, resultado.TamanoPagina));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.DocumentosLeer);

        endpoints.MapGet("/documentos/{id:guid}", async (Guid id, ClaimsPrincipal usuario, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var documento = await mediator.Send(new ObtenerDocumentoPorIdQuery(id, !ApiV1.EsClaveDeSoporte(usuario)), cancellationToken);
            return documento is null ? Results.NotFound() : Results.Ok(DocumentoApiDetalleDto.DesdeInterno(documento));
        }).RequiereAlcanceIntegracion(AlcancesIntegracion.DocumentosLeer);

        return endpoints;
    }
}
