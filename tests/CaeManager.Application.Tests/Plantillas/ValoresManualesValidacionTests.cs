using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Auditoría de seguridad del módulo (2026-08-30): ValoresManuales es texto
/// libre que el cliente controla y que se serializa sin más cotejo en
/// DocumentoGenerado.DatosUtilizadosJson / LoteGeneracionDocumento.ContextoJson
/// — sin límite, un valor gigante o un número arbitrario de ellos infla ese
/// JSON sin ningún control.
/// </summary>
public class ValoresManualesValidacionTests
{
    [Fact]
    public void GenerarDocumentoIndividual_rechaza_un_valor_manual_demasiado_largo()
    {
        var validador = new GenerarDocumentoIndividualCommandValidator();
        var comando = new GenerarDocumentoIndividualCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            ValoresManuales: new Dictionary<Guid, string>
            {
                [Guid.NewGuid()] = new string('a', GenerarDocumentoIndividualCommandValidator.LongitudMaximaValorManual + 1),
            });

        var resultado = validador.Validate(comando);

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GenerarDocumentoIndividual_rechaza_demasiados_valores_manuales()
    {
        var validador = new GenerarDocumentoIndividualCommandValidator();
        var valores = Enumerable.Range(0, GenerarDocumentoIndividualCommandValidator.MaximoValoresManuales + 1)
            .ToDictionary(_ => Guid.NewGuid(), _ => "x");
        var comando = new GenerarDocumentoIndividualCommand(Guid.NewGuid(), Guid.NewGuid(), ValoresManuales: valores);

        var resultado = validador.Validate(comando);

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GenerarDocumentoIndividual_acepta_valores_manuales_dentro_del_limite()
    {
        var validador = new GenerarDocumentoIndividualCommandValidator();
        var comando = new GenerarDocumentoIndividualCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            ValoresManuales: new Dictionary<Guid, string> { [Guid.NewGuid()] = "Sin incidencias" });

        var resultado = validador.Validate(comando);

        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IniciarLoteGeneracionDocumentos_rechaza_un_valor_manual_demasiado_largo()
    {
        var validador = new IniciarLoteGeneracionDocumentosCommandValidator();
        var comando = new IniciarLoteGeneracionDocumentosCommand(
            Guid.NewGuid(), [Guid.NewGuid()],
            ValoresManuales: new Dictionary<Guid, string>
            {
                [Guid.NewGuid()] = new string('a', IniciarLoteGeneracionDocumentosCommandValidator.LongitudMaximaValorManual + 1),
            });

        var resultado = validador.Validate(comando);

        resultado.IsValid.Should().BeFalse();
    }

    /// <summary>ADR-010 § 2.6: sin límite arquitectónico arbitrario sobre el tamaño del lote — un lote grande sigue siendo válido.</summary>
    [Fact]
    public void IniciarLoteGeneracionDocumentos_no_limita_el_numero_de_trabajadores()
    {
        var validador = new IniciarLoteGeneracionDocumentosCommandValidator();
        var comando = new IniciarLoteGeneracionDocumentosCommand(
            Guid.NewGuid(), Enumerable.Range(0, 5000).Select(_ => Guid.NewGuid()).ToList());

        var resultado = validador.Validate(comando);

        resultado.IsValid.Should().BeTrue();
    }
}
