using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Inbound.EvidenciasInbound;

/// <summary>
/// La causa propia de Inbound (ADR-013 P-6) se guarda en la columna de la causa de acreditación (K-5) hasta B-22. Los
/// dos enums tienen que casar en nombre y ordinal en los dos sentidos: si Outbound añade, quita o renumera un valor, o
/// Inbound lo hace, esto da rojo en vez de guardar en silencio una causa por otra.
/// </summary>
public class CausaRechazoEvidenciaInboundTests
{
    private static IEnumerable<(string Nombre, int Ordinal)> Valores<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Cada_causa_de_Inbound_tiene_la_de_acreditacion_con_el_mismo_nombre_y_ordinal()
    {
        Valores<CausaRechazoEvidenciaInbound>().Should().BeSubsetOf(Valores<CausaRechazoAcreditacion>());
    }

    [Fact]
    public void Cada_causa_de_acreditacion_tiene_la_de_Inbound_con_el_mismo_nombre_y_ordinal()
    {
        Valores<CausaRechazoAcreditacion>().Should().BeSubsetOf(Valores<CausaRechazoEvidenciaInbound>());
    }

    [Theory]
    [InlineData(CausaRechazoEvidenciaInbound.Ilegible, CausaRechazoAcreditacion.Ilegible)]
    [InlineData(CausaRechazoEvidenciaInbound.CaducadoAlPresentar, CausaRechazoAcreditacion.CaducadoAlPresentar)]
    [InlineData(CausaRechazoEvidenciaInbound.Otro, CausaRechazoAcreditacion.Otro)]
    public void Rechazar_con_la_causa_de_Inbound_guarda_su_equivalente(CausaRechazoEvidenciaInbound causa, CausaRechazoAcreditacion guardada)
    {
        var evidencia = Aceptada();

        evidencia.Rechazar(causa, "No se lee.", Guid.NewGuid(), DateTime.UtcNow);

        evidencia.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        evidencia.CausaRechazo.Should().Be(guardada);
    }

    [Fact]
    public void Una_causa_de_Inbound_fuera_del_enum_no_se_guarda()
    {
        var evidencia = Aceptada();

        var accion = () => evidencia.Rechazar((CausaRechazoEvidenciaInbound)99, "No se lee.", Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
        evidencia.Estado.Should().Be(EstadoAcreditacion.Aceptada);
    }

    private static EvidenciaInbound Aceptada()
    {
        var emision = new DateOnly(2026, 3, 1);
        return EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(),
            new EvidenciaInbound.Sujeto(Guid.NewGuid(), "Ana Pérez", "12345678Z"), emision, emision.AddYears(1),
            soloConclusionAptitud: false, conclusion: null,
            new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", new string('a', 64)), Guid.NewGuid(), DateTime.UtcNow);
    }
}
