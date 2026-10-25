using CaeManager.Domain.Documentos;
using CaeManager.Domain.EvidenciasInbound;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.EvidenciasInbound;

/// <summary>
/// Invariantes de la copia de evidencia validada de Inbound M1 (PR 4): la evidencia de «solo conclusión de aptitud» nunca
/// lleva archivo ni huella y sí la conclusión, el Trabajador y su documento de identidad; cualquier otra lleva el archivo
/// copiado y su SHA-256 y ninguna conclusión. La base repite la regla con CK_EvidenciasInbound_ArchivoSegunConclusion
/// (EvidenciasInboundBajoRuntimeTests).
/// </summary>
public class EvidenciaInboundTests
{
    private static readonly DateOnly Emision = new(2026, 3, 1);
    private static readonly string Huella = new('a', 64);
    private static readonly EvidenciaInbound.Procedencia Procedencia = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly EvidenciaInbound.Sujeto Trabajador = new(Guid.NewGuid(), "Ana Pérez", "12345678Z");

    private static EvidenciaInbound NoMedica(EvidenciaInbound.Sujeto? sujeto = null) =>
        EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), sujeto ?? Trabajador, Emision, Emision.AddYears(1), soloConclusionAptitud: false,
            conclusion: null, new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", Huella), Guid.NewGuid(),
            DateTime.UtcNow);

    private static EvidenciaInbound Medica(ConclusionAptitud conclusion = ConclusionAptitud.Apto) =>
        EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, null, soloConclusionAptitud: true, conclusion, archivo: null,
            Guid.NewGuid(), DateTime.UtcNow);

    [Fact]
    public void Una_evidencia_no_medica_nace_aceptada_con_su_archivo_su_huella_y_su_procedencia()
    {
        var evidencia = NoMedica();

        evidencia.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        evidencia.ArchivoUrl.Should().NotBeNull();
        evidencia.HuellaSha256.Should().Be(Huella);
        evidencia.ConclusionAptitud.Should().BeNull();
        evidencia.SoloConclusionAptitud.Should().BeFalse();
        (evidencia.ContratoAccesoId, evidencia.TenantOrigenId, evidencia.DocumentoOrigenId, evidencia.AutorizacionId)
            .Should().Be((Procedencia.ContratoAccesoId, Procedencia.TenantOrigenId, Procedencia.DocumentoOrigenId, Procedencia.AutorizacionId));
        (evidencia.SujetoNombre, evidencia.SujetoDocumentoIdentidad).Should().Be(("Ana Pérez", "12345678Z"));
    }

    [Theory]
    [InlineData(ConclusionAptitud.Apto)]
    [InlineData(ConclusionAptitud.AptoConRestricciones)]
    [InlineData(ConclusionAptitud.NoApto)]
    public void Una_evidencia_de_solo_conclusion_guarda_la_conclusion_sin_archivo_ni_huella(ConclusionAptitud conclusion)
    {
        var evidencia = Medica(conclusion);

        evidencia.SoloConclusionAptitud.Should().BeTrue();
        evidencia.ConclusionAptitud.Should().Be(conclusion);
        evidencia.ArchivoUrl.Should().BeNull();
        evidencia.HuellaSha256.Should().BeNull();
        evidencia.SujetoDocumentoIdentidad.Should().Be("12345678Z");
    }

    [Fact]
    public void Una_evidencia_de_solo_conclusion_nunca_lleva_archivo()
    {
        var registrar = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, null, soloConclusionAptitud: true, ConclusionAptitud.Apto,
            new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/informe.pdf", Huella), Guid.NewGuid(), DateTime.UtcNow);

        registrar.Should().Throw<InvalidOperationException>().WithMessage("*no lleva archivo*");
    }

    [Fact]
    public void Una_evidencia_de_solo_conclusion_exige_la_conclusion()
    {
        var registrar = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, null, soloConclusionAptitud: true, conclusion: null, archivo: null,
            Guid.NewGuid(), DateTime.UtcNow);

        registrar.Should().Throw<InvalidOperationException>().WithMessage("*conclusión transcrita*");
    }

    [Theory]
    [InlineData(false, "12345678Z")]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    public void Una_evidencia_de_solo_conclusion_es_de_un_Trabajador_con_documento_de_identidad(bool conTrabajador, string? documento)
    {
        var registrar = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), new(conTrabajador ? Guid.NewGuid() : null, "Ana Pérez", documento), Emision, null,
            soloConclusionAptitud: true, ConclusionAptitud.Apto, archivo: null, Guid.NewGuid(), DateTime.UtcNow);

        registrar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_evidencia_no_medica_no_lleva_conclusion_y_si_archivo()
    {
        var conConclusion = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, null, soloConclusionAptitud: false, ConclusionAptitud.Apto,
            new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", Huella), Guid.NewGuid(), DateTime.UtcNow);
        var sinArchivo = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, null, soloConclusionAptitud: false, conclusion: null, archivo: null,
            Guid.NewGuid(), DateTime.UtcNow);

        conConclusion.Should().Throw<InvalidOperationException>().WithMessage("*Solo una evidencia de solo conclusión*");
        sinArchivo.Should().Throw<InvalidOperationException>().WithMessage("*archivo copiado y su huella*");
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void La_huella_es_un_SHA256_hexadecimal_en_minusculas(string huella)
    {
        var registrar = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, null, soloConclusionAptitud: false, conclusion: null,
            new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", huella), Guid.NewGuid(), DateTime.UtcNow);

        registrar.Should().Throw<ArgumentException>().WithMessage("*SHA-256*");
    }

    [Fact]
    public void El_vencimiento_no_puede_ser_anterior_a_la_emision()
    {
        var registrar = () => EvidenciaInbound.Registrar(
            Procedencia, Guid.NewGuid(), Trabajador, Emision, Emision.AddDays(-1), soloConclusionAptitud: false,
            conclusion: null, new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", Huella), Guid.NewGuid(),
            DateTime.UtcNow);

        registrar.Should().Throw<ArgumentException>().WithMessage("*vencimiento*");
    }

    [Fact]
    public void Un_Documento_de_Empresa_tiene_sujeto_sin_Trabajador()
    {
        var evidencia = NoMedica(new(null, "Contratas del Norte SL", "B12345678"));

        evidencia.TrabajadorOrigenId.Should().BeNull();
        evidencia.SujetoNombre.Should().Be("Contratas del Norte SL");
    }

    [Fact]
    public void Rechazar_lleva_a_rechazada_con_causa_motivo_y_autor_y_validar_la_devuelve_a_aceptada()
    {
        var evidencia = NoMedica();
        var revisor = Guid.NewGuid();
        var momento = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

        evidencia.Rechazar(CausaRechazoAcreditacion.DatosErroneos, "El NIE no coincide", revisor, momento);

        evidencia.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        (evidencia.CausaRechazo, evidencia.MotivoRechazo, evidencia.RechazadaPorUsuarioId, evidencia.RechazadaEnUtc)
            .Should().Be((CausaRechazoAcreditacion.DatosErroneos, "El NIE no coincide", revisor, momento));

        var validador = Guid.NewGuid();
        evidencia.Validar(validador, momento.AddHours(1));

        evidencia.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        (evidencia.ValidadaPorUsuarioId, evidencia.ValidadaEnUtc).Should().Be((validador, momento.AddHours(1)));
    }

    [Fact]
    public void No_hay_transiciones_nuevas_rechazar_dos_veces_o_validar_lo_aceptado_se_rechaza()
    {
        var evidencia = NoMedica();

        evidencia.Invoking(e => e.Validar(Guid.NewGuid(), DateTime.UtcNow)).Should().Throw<InvalidOperationException>();
        evidencia.Rechazar(CausaRechazoAcreditacion.Ilegible, "No se lee", Guid.NewGuid(), DateTime.UtcNow);
        evidencia.Invoking(e => e.Rechazar(CausaRechazoAcreditacion.Otro, "Otra vez", Guid.NewGuid(), DateTime.UtcNow))
            .Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechazar_exige_motivo(string motivo)
    {
        var evidencia = NoMedica();

        evidencia.Invoking(e => e.Rechazar(CausaRechazoAcreditacion.Otro, motivo, Guid.NewGuid(), DateTime.UtcNow))
            .Should().Throw<ArgumentException>();
        evidencia.Estado.Should().Be(EstadoAcreditacion.Aceptada);
    }
}
