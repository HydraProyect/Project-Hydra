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
    [InlineData(MotivosRevisionIa.CertificadoEnNegativo)]
    public void Cada_alerta_se_reconoce_tambien_entre_otros_motivos(string alerta)
    {
        Revision(alerta).TieneAlertaDeterminista.Should().BeTrue();
        Revision($"Confianza baja (62%); {alerta}; No hay evidencia de firma en el documento").TieneAlertaDeterminista.Should().BeTrue();
    }

    /// <summary>
    /// Los textos tal como quedan en la columna, escritos a mano y no con las constantes: una revisión pendiente lleva el
    /// texto de cuando se creó, de modo que retocar una constante la sacaría del catálogo sin que cambie ningún test que
    /// use la misma constante a los dos lados.
    /// </summary>
    private static readonly string[] Literales =
    [
        "No se pudo contrastar la lectura automática con el texto del documento",
        "El documento contiene texto dirigido al sistema de lectura automática (posible manipulación)",
        "El documento menciona «no apto» o una restricción: lo tiene que decidir una persona",
        "La fecha de emisión que dice haber leído la IA no aparece escrita en el documento",
        "La firma digital no cuadra con el contenido: el documento fue alterado después de firmarse.",
        "El certificado está emitido EN NEGATIVO — declara que la empresa no está al corriente.",
        "El CIF del documento (B99999999) no coincide con el del propietario (B12345674).",
        "Lectura parcial: 26 página(s) sin leer; un «no apto», una restricción o un dato que figure solo en ellas no se habrá detectado",
    ];

    public static TheoryData<string> LiteralesPersistidos() => new(Literales);

    [Theory]
    [MemberData(nameof(LiteralesPersistidos))]
    public void Una_revision_con_el_literal_persistido_tiene_alerta(string literal) =>
        Revision(literal).TieneAlertaDeterminista.Should().BeTrue();

    [Fact]
    public void Lo_que_se_escribe_hoy_es_exactamente_el_literal_persistido()
    {
        Literales.Should().Contain(MotivosRevisionIa.CifNoCoincideConElPropietario("B99999999", "B12345674"));
        Literales.Should().Contain(MotivosRevisionIa.LecturaParcial(26));
    }

    [Fact]
    public void Cada_alerta_del_catalogo_tiene_su_literal_persistido()
    {
        Literales.Should().HaveSameCount(MotivosRevisionIa.Alertas);
        MotivosRevisionIa.Alertas.Should().OnlyContain(alerta => Literales.Count(l => l.Contains(alerta, StringComparison.Ordinal)) == 1);
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
    [InlineData("La fecha de emisión introducida no coincide con la del documento.")]
    [InlineData("El documento no trae un CIF legible con el que cotejar la identidad del propietario.")]
    [InlineData("El propietario del documento no tiene CIF registrado en la plataforma.")]
    [InlineData("La firma es íntegra pero su emisor no es una CA reconocida de la Administración.")]
    [InlineData("El documento no está firmado digitalmente.")]
    public void Un_motivo_que_no_es_alerta_no_marca_la_revision(string motivo) =>
        Revision(motivo).TieneAlertaDeterminista.Should().BeFalse();

    private static RevisionIaDocumento Revision(string motivo) =>
        RevisionIaDocumento.Crear(Guid.NewGuid(), 99, "Formación", new DateOnly(2026, 2, 10), null, true, motivo);
}
