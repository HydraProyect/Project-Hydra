using CaeManager.Application.Importacion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// La importación decide la vigencia del Documento con la misma regla que el resto de productores
/// (<see cref="CalculadoraEstadoDocumento.ResolverVigencia"/>): solo un Tipo de documento con
/// vencimiento automático calcula <c>emisión + VigenciaMeses</c>; el archivo no trae vencimiento
/// manual, así que en los demás el Documento entra sin vigencia confirmada, nunca con una fecha
/// inventada ni como «no caduca».
/// </summary>
public class EjecutarImportacionVigenciaTests
{
    private static readonly DateOnly Emision = new(2026, 2, 1);

    private static PlanImportacionDto PlanDeUnDocumento() =>
        EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(
                EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, Emision, YaExiste: false)
        ]);

    [Fact]
    public async Task Tipo_con_vencimiento_automatico_entra_con_emision_mas_vigencia()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente()
            .ConTipoDocumentoExistente(aplicaVencimientoAutomatico: true);

        var resultado = await escenario.EjecutarAsync(PlanDeUnDocumento());

        resultado.DocumentosCreados.Should().Be(1);
        var documento = escenario.DocumentoRepositorio.Documentos.Should().ContainSingle().Subject;
        documento.EstadoVigencia.Should().Be(EstadoVigenciaDocumento.VenceEnFecha);
        documento.FechaVencimiento.Should().Be(Emision.AddMonths(12));
    }

    [Fact]
    public async Task Tipo_sin_vencimiento_automatico_entra_sin_vigencia_confirmada_aunque_tenga_vigencia_en_meses()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente()
            .ConTipoDocumentoExistente(aplicaVencimientoAutomatico: false);

        var resultado = await escenario.EjecutarAsync(PlanDeUnDocumento());

        resultado.DocumentosCreados.Should().Be(1);
        var documento = escenario.DocumentoRepositorio.Documentos.Should().ContainSingle().Subject;
        documento.EstadoVigencia.Should().Be(EstadoVigenciaDocumento.SinConfirmar);
        documento.FechaVencimiento.Should().BeNull();
    }
}
