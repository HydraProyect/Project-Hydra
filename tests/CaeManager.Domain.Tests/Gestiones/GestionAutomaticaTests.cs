using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Gestiones;

public class GestionAutomaticaTests
{
    private static readonly DateTime Ahora = new(2026, 11, 7, 9, 12, 0, DateTimeKind.Utc);

    [Fact]
    public void Nace_con_un_paso_por_acreditacion_y_las_repetidas_cuentan_una_vez()
    {
        var documentoId = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var gestion = GestionAutomatica.Nacer(documentoId, OrigenGestionAutomatica.RenovacionDocumento, [a, b, a], null);

        gestion.Should().NotBeNull();
        gestion!.DocumentoId.Should().Be(documentoId);
        gestion.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        gestion.Pasos.Select(p => p.AcreditacionId).Should().BeEquivalentTo([a, b]);
        gestion.Pasos.Should().OnlyContain(p => p.GestionAutomaticaId == gestion.Id);
        gestion.EstaSustituida.Should().BeFalse();
    }

    [Fact]
    public void Sin_acreditaciones_no_nace_ninguna_gestion()
    {
        GestionAutomatica.Nacer(Guid.NewGuid(), OrigenGestionAutomatica.AltaDocumento, [], null).Should().BeNull();
    }

    [Fact]
    public void Rechaza_documento_vacio_y_paso_sin_acreditacion()
    {
        var sinDocumento = () => GestionAutomatica.Nacer(Guid.Empty, OrigenGestionAutomatica.AltaDocumento, [Guid.NewGuid()], null);
        var sinAcreditacion = () => GestionAutomatica.Nacer(Guid.NewGuid(), OrigenGestionAutomatica.AltaDocumento, [Guid.Empty], null);

        sinDocumento.Should().Throw<ArgumentException>();
        sinAcreditacion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sustituir_es_idempotente_y_conserva_el_primer_enlace()
    {
        var gestion = GestionAutomatica.Nacer(Guid.NewGuid(), OrigenGestionAutomatica.AltaDocumento, [Guid.NewGuid()], null)!;
        var nueva = Guid.NewGuid();

        gestion.Sustituir(nueva, Ahora);
        gestion.Sustituir(Guid.NewGuid(), Ahora.AddHours(1));

        gestion.EstaSustituida.Should().BeTrue();
        gestion.SustituidaEnUtc.Should().Be(Ahora);
        gestion.SustituidaPorGestionId.Should().Be(nueva);
    }

    [Fact]
    public void Sustituir_exige_UTC_y_no_admite_sustituirse_por_si_misma()
    {
        var gestion = GestionAutomatica.Nacer(Guid.NewGuid(), OrigenGestionAutomatica.AltaDocumento, [Guid.NewGuid()], null)!;

        var local = () => gestion.Sustituir(null, new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Local));
        var aSiMisma = () => gestion.Sustituir(gestion.Id, Ahora);

        local.Should().Throw<ArgumentException>();
        aSiMisma.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, false)]
    [InlineData(EstadoAcreditacion.Rechazada, false)]
    [InlineData(EstadoAcreditacion.Subida, true)]
    [InlineData(EstadoAcreditacion.Aceptada, true)]
    [InlineData(EstadoAcreditacion.NoRequerida, true)]
    public void Paso_hecho_segun_el_estado_de_su_acreditacion(EstadoAcreditacion estado, bool hecho)
    {
        GestionAutomatica.PasoHecho(estado).Should().Be(hecho);
    }

    [Fact]
    public void Con_algun_paso_por_hacer_esta_en_curso()
    {
        GestionAutomatica.ResolverEstado(false, false, [EstadoAcreditacion.Aceptada, EstadoAcreditacion.PendienteDeSubir])
            .Should().Be(EstadoGestionAutomatica.EnCurso);
    }

    [Fact]
    public void Un_rechazo_la_devuelve_a_en_curso_aunque_lo_demas_este_validado()
    {
        GestionAutomatica.ResolverEstado(false, false, [EstadoAcreditacion.Aceptada, EstadoAcreditacion.Rechazada])
            .Should().Be(EstadoGestionAutomatica.EnCurso);
    }

    [Fact]
    public void Todos_hechos_con_alguno_solo_subido_queda_pendiente_de_validar()
    {
        GestionAutomatica.ResolverEstado(false, false, [EstadoAcreditacion.Aceptada, EstadoAcreditacion.Subida])
            .Should().Be(EstadoGestionAutomatica.PendienteDeValidar);
    }

    [Fact]
    public void Todos_validados_o_no_requeridos_la_cierran()
    {
        GestionAutomatica.ResolverEstado(false, false, [EstadoAcreditacion.Aceptada, EstadoAcreditacion.NoRequerida])
            .Should().Be(EstadoGestionAutomatica.Cerrada);
    }

    [Fact]
    public void Sustituida_manda_sobre_el_estado_de_los_pasos()
    {
        GestionAutomatica.ResolverEstado(true, false, [EstadoAcreditacion.Aceptada])
            .Should().Be(EstadoGestionAutomatica.Sustituida);
    }

    // Hallazgos 1 y 2 de la segunda ronda de Codex sobre la PR #2072: con el Documento en el historial, la Gestión no
    // puede seguir abierta, la marcara o no la renovación (relevo, o estados leídos que cambiaron antes del commit).
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Rechazada)]
    [InlineData(EstadoAcreditacion.Subida)]
    public void Abierta_con_el_documento_ya_sustituido_es_sustituida_aunque_no_se_marcara(EstadoAcreditacion abierto)
    {
        GestionAutomatica.ResolverEstado(false, documentoSustituido: true, [EstadoAcreditacion.Aceptada, abierto])
            .Should().Be(EstadoGestionAutomatica.Sustituida);
        GestionAutomatica.ResolverEstado(false, documentoSustituido: false, [EstadoAcreditacion.Aceptada, abierto])
            .Should().NotBe(EstadoGestionAutomatica.Sustituida, "control: con el Documento operativo sigue abierta");
    }

    [Fact]
    public void Cerrada_con_el_documento_ya_sustituido_sigue_cerrada()
    {
        GestionAutomatica.ResolverEstado(false, documentoSustituido: true, [EstadoAcreditacion.Aceptada, EstadoAcreditacion.NoRequerida])
            .Should().Be(EstadoGestionAutomatica.Cerrada, "solo se sustituye la que sigue abierta (§ 4.1 regla 4)");
    }

    [Fact]
    public void Sin_pasos_visibles_y_con_el_documento_sustituido_no_queda_abierta()
    {
        GestionAutomatica.ResolverEstado(false, documentoSustituido: true, []).Should().Be(EstadoGestionAutomatica.Sustituida);
    }

    [Fact]
    public void Sin_pasos_visibles_no_se_da_por_cerrada()
    {
        GestionAutomatica.ResolverEstado(false, false, []).Should().Be(EstadoGestionAutomatica.EnCurso);
    }
}
