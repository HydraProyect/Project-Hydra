using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class MotivosRevisionIaTests
{
    [Theory]
    [InlineData(MotivosRevisionIa.SinContraste)]
    [InlineData(MotivosRevisionIa.TextoDirigidoAlLector)]
    [InlineData(MotivosRevisionIa.NoAptoORestriccion)]
    [InlineData(MotivosRevisionIa.FechaEmisionNoEscrita)]
    [InlineData(MotivosRevisionIa.FirmaNoCuadraConElContenido)]
    public void Cada_alerta_se_reconoce_tambien_entre_otros_motivos(string alerta)
    {
        Revision(alerta).TieneAlertaDeterminista.Should().BeTrue();
        Revision($"Confianza baja (62%); {alerta}; No hay evidencia de firma en el documento").TieneAlertaDeterminista.Should().BeTrue();
    }

    [Fact]
    public void La_lectura_parcial_se_reconoce_sea_cual_sea_el_numero_de_paginas()
    {
        Revision(MotivosRevisionIa.LecturaParcial(1)).TieneAlertaDeterminista.Should().BeTrue();
        Revision(MotivosRevisionIa.LecturaParcial(450)).TieneAlertaDeterminista.Should().BeTrue();
    }

    [Theory]
    [InlineData("Propuesta lista para confirmación humana")]
    [InlineData("Confianza baja (62%)")]
    [InlineData("El tipo detectado no coincide con el nombre ni los alias del tipo de documento")]
    [InlineData("La fecha de emisión introducida no coincide con la detectada en el documento")]
    [InlineData("No hay evidencia de firma en el documento")]
    public void Un_motivo_que_no_es_alerta_no_marca_la_revision(string motivo) =>
        Revision(motivo).TieneAlertaDeterminista.Should().BeFalse();

    private static RevisionIaDocumento Revision(string motivo) =>
        RevisionIaDocumento.Crear(Guid.NewGuid(), 99, "Formación", new DateOnly(2026, 2, 10), null, true, motivo);
}
