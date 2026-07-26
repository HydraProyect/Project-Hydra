using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class RevisionIaDocumentoTests
{
    [Fact]
    public void Crea_una_revision_pendiente_con_los_datos_detectados()
    {
        var documentoId = Guid.NewGuid();
        var fechaEmision = new DateOnly(2026, 1, 10);

        var revision = RevisionIaDocumento.Crear(
            documentoId, confianzaGeneral: 62, tipoDetectado: "Reconocimiento médico",
            fechaEmisionDetectada: fechaEmision, fechaVencimientoDetectada: null, tieneFirmaDetectada: false,
            motivo: "Confianza baja (62%); no se detectó firma");

        revision.DocumentoId.Should().Be(documentoId);
        revision.ConfianzaGeneral.Should().Be(62);
        revision.TipoDetectado.Should().Be("Reconocimiento médico");
        revision.FechaEmisionDetectada.Should().Be(fechaEmision);
        revision.TieneFirmaDetectada.Should().BeFalse();
        revision.Resuelta.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rechaza_una_confianza_fuera_de_rango(int confianza)
    {
        var accion = () => RevisionIaDocumento.Crear(
            Guid.NewGuid(), confianza, null, null, null, null, "Confianza baja");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_motivo_vacio()
    {
        var accion = () => RevisionIaDocumento.Crear(Guid.NewGuid(), 50, null, null, null, null, motivo: "   ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolver_marca_la_revision_como_hecha()
    {
        var revision = RevisionIaDocumento.Crear(Guid.NewGuid(), 50, null, null, null, null, "Confianza baja (50%)");

        revision.Resolver();

        revision.Resuelta.Should().BeTrue();
    }
}
