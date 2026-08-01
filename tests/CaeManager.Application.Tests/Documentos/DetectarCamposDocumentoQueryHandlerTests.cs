using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Cubre el kill switch de P0-4 (docs/business/MATURITY_REVIEW.md): mientras
/// no exista DPA de subencargado para datos de salud, esta detección no debe
/// enviar ningún PDF de Trabajador al proveedor de IA.
/// </summary>
public class DetectarCamposDocumentoQueryHandlerTests
{
    [Fact]
    public async Task Con_la_opcion_desactivada_no_llama_al_router_de_ia()
    {
        // El router lanza si se invoca — si el gate fallara, este test
        // fallaría con la excepción del router, no con una aserción confusa.
        var routerQueNuncaDebeLlamarse = new RouterQueLanzaSiSeInvoca();
        var handler = new DetectarCamposDocumentoQueryHandler(
            routerQueNuncaDebeLlamarse,
            dbContext: null!,
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }));

        var resultado = await handler.Handle(
            new DetectarCamposDocumentoQuery([1, 2, 3], "reconocimiento-medico.pdf", AmbitoAplicacion.Trabajador),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TipoDocumentoId.Should().BeNull();
        resultado.Valor.TrabajadorId.Should().BeNull();
    }

    private sealed class RouterQueLanzaSiSeInvoca : IDocumentAIRouterService
    {
        public Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
            byte[] contenido, string nombreArchivo, string tipoEsperado, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "El gate de DeteccionPreviaDocumentoOptions.Activa=false debía impedir esta llamada.");
    }
}
