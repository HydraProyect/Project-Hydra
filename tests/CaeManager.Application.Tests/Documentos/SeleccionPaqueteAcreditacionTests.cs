using CaeManager.Application.Documentos.PaqueteAcreditacion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class SeleccionPaqueteAcreditacionTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 1);
    private static readonly Guid Titular = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TipoAptitud = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid TipoFormacion = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid TipoReciclaje = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

    private static int _n;

    private static DocumentoCandidatoPaquete Vence(Guid tipo, DateOnly emision, int diasHastaVencer, Guid? titular = null, string? archivo = "blob", DateTime? creado = null) =>
        new(Guid.Parse($"cccccccc-0000-0000-0000-{++_n:000000000000}"), titular ?? Titular, tipo, EstadoVigenciaDocumento.VenceEnFecha,
            Hoy.AddDays(diasHastaVencer), emision, creado ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(_n), archivo);

    private static DocumentoCandidatoPaquete SinVigencia(Guid tipo, DateOnly emision, EstadoVigenciaDocumento estado = EstadoVigenciaDocumento.NoCaduca, Guid? titular = null) =>
        new(Guid.Parse($"cccccccc-0000-0000-0000-{++_n:000000000000}"), titular ?? Titular, tipo, estado, null, emision,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(_n), "blob");

    private static List<Guid> Incluidos(IEnumerable<DecisionPaquete> d) => d.Where(x => x.Incluido).Select(x => x.Documento.Id).ToList();

    [Fact]
    public void Un_documento_vigente_entra()
    {
        var d = Vence(TipoAptitud, new DateOnly(2026, 3, 1), 100);
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([d], Hoy)).Should().Equal(d.Id);
    }

    [Fact]
    public void Un_vencido_nunca_entra_y_se_anota_con_su_motivo()
    {
        var d = Vence(TipoAptitud, new DateOnly(2025, 1, 1), -1);
        var r = SeleccionPaqueteAcreditacion.Seleccionar([d], Hoy);
        r.Should().ContainSingle().Which.Should().Match<DecisionPaquete>(x => !x.Incluido && x.Motivo == MotivoExclusionPaquete.Vencido);
    }

    [Fact]
    public void Prevalece_la_emision_mas_reciente_aunque_otra_tenga_mas_vigencia()
    {
        var antigua = Vence(TipoAptitud, new DateOnly(2026, 1, 1), 360);
        var nueva = Vence(TipoAptitud, new DateOnly(2026, 9, 28), 4);
        var r = SeleccionPaqueteAcreditacion.Seleccionar([antigua, nueva], Hoy);
        Incluidos(r).Should().Equal(nueva.Id);
        r.Single(x => x.Documento.Id == antigua.Id).Motivo.Should().Be(MotivoExclusionPaquete.NoEsLaVersionMasReciente);
    }

    [Fact]
    public void Un_vencido_mas_reciente_no_desplaza_al_vigente_mas_antiguo()
    {
        var vigente = Vence(TipoAptitud, new DateOnly(2026, 1, 1), 100);
        var vencidoReciente = Vence(TipoAptitud, new DateOnly(2026, 5, 1), -2);
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([vigente, vencidoReciente], Hoy)).Should().Equal(vigente.Id);
    }

    [Fact]
    public void Formacion_sin_vigencia_de_2012_mas_reciclaje_vigente_entran_los_dos()
    {
        var formacion = SinVigencia(TipoFormacion, new DateOnly(2012, 5, 5));
        var reciclaje = Vence(TipoReciclaje, new DateOnly(2026, 6, 1), 400);
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([formacion, reciclaje], Hoy)).Should().BeEquivalentTo([formacion.Id, reciclaje.Id]);
    }

    [Fact]
    public void Formacion_sin_vigencia_y_version_mas_nueva_del_mismo_tipo_solo_la_nueva()
    {
        var vieja = SinVigencia(TipoFormacion, new DateOnly(2012, 5, 5));
        var nueva = SinVigencia(TipoFormacion, new DateOnly(2025, 5, 5));
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([vieja, nueva], Hoy)).Should().Equal(nueva.Id);
    }

    [Fact]
    public void Sin_confirmar_entra_y_va_marcado_y_no_caduca_entra_sin_marca()
    {
        var sinConfirmar = SinVigencia(TipoAptitud, new DateOnly(2026, 2, 1), EstadoVigenciaDocumento.SinConfirmar);
        var noCaduca = SinVigencia(TipoFormacion, new DateOnly(2026, 2, 1));
        var r = SeleccionPaqueteAcreditacion.Seleccionar([sinConfirmar, noCaduca], Hoy);
        r.Single(x => x.Documento.Id == sinConfirmar.Id).Should().Match<DecisionPaquete>(x => x.Incluido && x.VigenciaSinConfirmar);
        r.Single(x => x.Documento.Id == noCaduca.Id).Should().Match<DecisionPaquete>(x => x.Incluido && !x.VigenciaSinConfirmar);
    }

    [Fact]
    public void Un_sin_confirmar_mas_reciente_gana_a_un_vigente_mas_antiguo()
    {
        var confirmado = Vence(TipoAptitud, new DateOnly(2026, 1, 1), 300);
        var sinConfirmar = SinVigencia(TipoAptitud, new DateOnly(2026, 8, 1), EstadoVigenciaDocumento.SinConfirmar);
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([confirmado, sinConfirmar], Hoy)).Should().Equal(sinConfirmar.Id);
    }

    [Fact]
    public void Con_la_misma_emision_entran_todos_con_ordinal_por_creacion_y_sin_desempate_por_vencimiento()
    {
        var emision = new DateOnly(2026, 4, 4);
        var segundoCreado = Vence(TipoAptitud, emision, 400, creado: new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc));
        var primeroCreado = Vence(TipoAptitud, emision, 10, creado: new DateTime(2026, 4, 4, 8, 0, 0, DateTimeKind.Utc));
        var r = SeleccionPaqueteAcreditacion.Seleccionar([segundoCreado, primeroCreado], Hoy);
        r.Single(x => x.Documento.Id == primeroCreado.Id).Ordinal.Should().Be(1);
        r.Single(x => x.Documento.Id == segundoCreado.Id).Ordinal.Should().Be(2);
    }

    [Fact]
    public void Sin_archivo_se_excluye_antes_de_elegir_y_no_esconde_a_otro_con_archivo()
    {
        var conArchivo = Vence(TipoAptitud, new DateOnly(2026, 1, 1), 100);
        var sinArchivo = Vence(TipoAptitud, new DateOnly(2026, 6, 1), 100, archivo: null);
        var r = SeleccionPaqueteAcreditacion.Seleccionar([conArchivo, sinArchivo], Hoy);
        Incluidos(r).Should().Equal(conArchivo.Id);
        r.Single(x => x.Documento.Id == sinArchivo.Id).Motivo.Should().Be(MotivoExclusionPaquete.SinArchivo);
    }

    [Fact]
    public void La_seleccion_es_por_titular_y_por_tipo()
    {
        var otro = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var a = Vence(TipoAptitud, new DateOnly(2026, 1, 1), 100);
        var b = Vence(TipoAptitud, new DateOnly(2025, 6, 1), 100, titular: otro); // más antigua que a: agrupar sin titular la descartaría
        var c = Vence(TipoFormacion, new DateOnly(2026, 1, 1), 100);
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([a, b, c], Hoy)).Should().BeEquivalentTo([a.Id, b.Id, c.Id]);
    }

    [Fact]
    public void Vence_hoy_aun_es_vigente_y_ayer_ya_no()
    {
        var hoyVence = Vence(TipoAptitud, new DateOnly(2025, 10, 1), 0);
        var ayer = Vence(TipoFormacion, new DateOnly(2025, 9, 30), -1);
        Incluidos(SeleccionPaqueteAcreditacion.Seleccionar([hoyVence, ayer], Hoy)).Should().Equal(hoyVence.Id);
    }
}
