using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
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
/// Cubre el kill switch de P0-4 (docs/business/MATURITY_REVIEW.md) y el
/// Nivel 0 (DEC-33/REC-035, instrucción de tratamiento IA por Tenant
/// propietario): en cualquiera de los dos, esta detección no debe enviar
/// ningún PDF de Trabajador al proveedor de IA.
/// </summary>
public class DetectarCamposDocumentoQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Con_la_opcion_desactivada_no_llama_al_router_de_ia()
    {
        // El router lanza si se invoca — si el gate fallara, este test
        // fallaría con la excepción del router, no con una aserción confusa.
        var routerQueNuncaDebeLlamarse = new RouterQueLanzaSiSeInvoca();
        var handler = new DetectarCamposDocumentoQueryHandler(
            routerQueNuncaDebeLlamarse,
            tiposDocumentoContext: null!,
            trabajadoresContext: null!,
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = false }),
            new InstruccionTratamientoIaFalsa(habilitada: true),
            new TenantActualFalso(TenantId));

        var resultado = await handler.Handle(
            new DetectarCamposDocumentoQuery([1, 2, 3], "reconocimiento-medico.pdf", AmbitoAplicacion.Trabajador),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TipoDocumentoId.Should().BeNull();
        resultado.Valor.TrabajadorId.Should().BeNull();
    }

    [Fact]
    public async Task Sin_instruccion_vigente_no_llama_al_router_de_ia()
    {
        // Mismo control positivo que el test de arriba, ahora sobre el
        // Nivel 0: la opción de despliegue está ACTIVA a propósito, para que
        // solo el gate de instrucción pueda estar bloqueando la llamada.
        var routerQueNuncaDebeLlamarse = new RouterQueLanzaSiSeInvoca();
        var handler = new DetectarCamposDocumentoQueryHandler(
            routerQueNuncaDebeLlamarse,
            tiposDocumentoContext: null!,
            trabajadoresContext: null!,
            Options.Create(new DeteccionPreviaDocumentoOptions { Activa = true }),
            new InstruccionTratamientoIaFalsa(habilitada: false),
            new TenantActualFalso(TenantId));

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
            byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "El gate de DeteccionPreviaDocumentoOptions.Activa=false / Nivel 0 debía impedir esta llamada.");
    }

    private sealed class InstruccionTratamientoIaFalsa(bool habilitada) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(habilitada);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
