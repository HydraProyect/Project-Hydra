using CaeManager.Application.Subcontratas;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// El estado documental de una Subcontrata en el listado es el peor de sus incidencias. De él salen la
/// pastilla de la fila, el botón de la franja en que se cuenta y el filtro de estado: se fija aquí,
/// aparte de los tests de pantalla, que lo usan para construir sus propios datos.
/// </summary>
public class RecuentosSubcontrataDtoTests
{
    private static IncidenciaSubcontrataDto Incidencia(EstadoDocumento estado) => new("Documento", estado, null, null, null);

    [Fact]
    public void Sin_incidencias_el_estado_es_vigente()
    {
        new RecuentosSubcontrataDto([], []).PeorEstado.Should().Be(EstadoDocumento.Vigente);
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    public void Un_documento_vencido_o_ausente_manda_sobre_todo_lo_demas(EstadoDocumento vencida)
    {
        var recuentos = new RecuentosSubcontrataDto(
            [Incidencia(vencida)], [Incidencia(EstadoDocumento.Urgente), Incidencia(EstadoDocumento.Proximo)])
        {
            SinConfirmar = [Incidencia(EstadoDocumento.SinConfirmar)]
        };

        recuentos.PeorEstado.Should().Be(EstadoDocumento.Vencido, "lo ausente cuenta como vencido en el listado");
    }

    [Fact]
    public void Entre_las_proximas_urgente_manda_sobre_proximo()
    {
        new RecuentosSubcontrataDto([], [Incidencia(EstadoDocumento.Proximo), Incidencia(EstadoDocumento.Urgente)])
            .PeorEstado.Should().Be(EstadoDocumento.Urgente);
        new RecuentosSubcontrataDto([], [Incidencia(EstadoDocumento.Proximo)])
            .PeorEstado.Should().Be(EstadoDocumento.Proximo);
    }

    [Fact]
    public void Sin_confirmar_solo_decide_cuando_no_hay_nada_peor()
    {
        new RecuentosSubcontrataDto([], []) { SinConfirmar = [Incidencia(EstadoDocumento.SinConfirmar)] }
            .PeorEstado.Should().Be(EstadoDocumento.SinConfirmar);
        new RecuentosSubcontrataDto([], [Incidencia(EstadoDocumento.Proximo)]) { SinConfirmar = [Incidencia(EstadoDocumento.SinConfirmar)] }
            .PeorEstado.Should().Be(EstadoDocumento.Proximo);
    }
}
