using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Vocabulario único de estado (decisión del propietario, 2026-10-08; Listados 2/7): Urgente y Próximo se leen
/// los dos «Por vencer» y Faltante se lee «Pendiente». Los estados de código no cambian: el filtro, el orden y
/// las superficies que colorean por gravedad siguen distinguiendo lo urgente de lo próximo. Este fichero fija
/// las dos mitades: lo que ahora es igual (rótulo y tono de la pastilla) y lo que tiene que seguir siendo
/// distinto (tono y nombre de gravedad).
/// </summary>
public class EstadoDocumentoUiVocabularioTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 9);

    public static TheoryData<EstadoDocumento> EstadosDocumento => [.. Enum.GetValues<EstadoDocumento>()];

    // ------------------------------------------------------ Rótulo y pastilla

    [Fact]
    public void Urgente_y_Proximo_comparten_rotulo_y_tono_de_pastilla()
    {
        EstadoDocumentoUi.Texto(EstadoDocumento.Urgente).Should().Be("Por vencer");
        EstadoDocumentoUi.Texto(EstadoDocumento.Proximo).Should().Be("Por vencer");
        EstadoDocumentoUi.PorVencer.Should().Be("Por vencer");

        EstadoDocumentoUi.Tono(EstadoDocumento.Urgente).Should().Be(TonoBadge.Advertencia,
            "dos colores para un mismo rótulo serían dos pastillas distintas");
        EstadoDocumentoUi.Tono(EstadoDocumento.Proximo).Should().Be(TonoBadge.Advertencia);
    }

    [Fact]
    public void Faltante_se_lee_Pendiente_y_sigue_en_rojo()
    {
        EstadoDocumentoUi.Texto(EstadoDocumento.Faltante).Should().Be("Pendiente");
        EstadoDocumentoUi.Tono(EstadoDocumento.Faltante).Should().Be(TonoBadge.Peligro);
    }

    // -------------------------------------------------------------- Gravedad

    [Fact]
    public void Donde_se_colorea_por_gravedad_lo_urgente_sigue_en_rojo_y_lo_proximo_en_ambar()
    {
        EstadoDocumentoUi.TonoDeSeveridad(EstadoDocumento.Urgente).Should().Be(TonoBadge.Peligro,
            "es lo que separa lo urgente de lo próximo ahora que los dos se rotulan igual");
        EstadoDocumentoUi.TonoDeSeveridad(EstadoDocumento.Proximo).Should().Be(TonoBadge.Advertencia);
    }

    [Theory]
    [MemberData(nameof(EstadosDocumento))]
    public void El_tono_de_gravedad_solo_se_aparta_del_de_la_pastilla_en_Urgente(EstadoDocumento estado)
    {
        if (estado == EstadoDocumento.Urgente)
            EstadoDocumentoUi.TonoDeSeveridad(estado).Should().NotBe(EstadoDocumentoUi.Tono(estado));
        else
            EstadoDocumentoUi.TonoDeSeveridad(estado).Should().Be(EstadoDocumentoUi.Tono(estado));
    }

    [Fact]
    public void El_nombre_de_gravedad_distingue_Urgente_de_Proximo()
    {
        EstadoDocumentoUi.TextoDeSeveridad(EstadoDocumento.Urgente).Should().Be("Por vencer (urgente)",
            "en Alertas son dos grupos seguidos con su recuento: con el rótulo de la pastilla se titularían igual");
        EstadoDocumentoUi.TextoDeSeveridad(EstadoDocumento.Proximo).Should().Be("Por vencer");
    }

    [Theory]
    [MemberData(nameof(EstadosDocumento))]
    public void El_nombre_de_gravedad_solo_se_aparta_del_rotulo_en_Urgente(EstadoDocumento estado)
    {
        if (estado == EstadoDocumento.Urgente)
            EstadoDocumentoUi.TextoDeSeveridad(estado).Should().NotBe(EstadoDocumentoUi.Texto(estado));
        else
            EstadoDocumentoUi.TextoDeSeveridad(estado).Should().Be(EstadoDocumentoUi.Texto(estado));
    }

    // ------------------------------------------- Agregado de un propietario

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    public void En_el_agregado_de_un_propietario_lo_que_esta_bien_se_dice_de_una_sola_forma(EstadoDocumento estado)
    {
        EstadoDocumentoUi.TextoDocumental(estado).Should().Be("Sin incidencias");
        EstadoDocumentoUi.EsCorrecto(estado).Should().BeTrue();
        EstadoDocumentoUi.EsCorrecto((EstadoDocumento?)estado).Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Vencido")]
    [InlineData(EstadoDocumento.Urgente, "Por vencer")]
    [InlineData(EstadoDocumento.Proximo, "Por vencer")]
    [InlineData(EstadoDocumento.Faltante, "Pendiente")]
    [InlineData(EstadoDocumento.SinConfirmar, "Sin confirmar")]
    public void En_el_agregado_lo_que_pide_accion_lleva_el_rotulo_de_su_estado_y_no_es_correcto(EstadoDocumento estado, string rotulo)
    {
        EstadoDocumentoUi.TextoDocumental(estado).Should().Be(rotulo);
        EstadoDocumentoUi.EsCorrecto(estado).Should().BeFalse();
    }

    [Fact]
    public void Sin_documentos_no_es_correcto_es_desconocido()
    {
        EstadoDocumentoUi.TextoDocumental(null).Should().Be("Sin documentos");
        EstadoDocumentoUi.EsCorrecto((EstadoDocumento?)null).Should().BeFalse();
        EstadoDocumentoUi.TonoDocumental(null).Should().Be(TonoBadge.Neutro);
    }

    // ------------------------------------------------- Motivo de un Documento

    [Theory]
    [InlineData(EstadoDocumento.Urgente, 0, "Caduca hoy")]
    [InlineData(EstadoDocumento.Urgente, 1, "Caduca en 1 día")]
    [InlineData(EstadoDocumento.Urgente, 5, "Caduca en 5 días")]
    [InlineData(EstadoDocumento.Proximo, 28, "Caduca en 28 días")]
    [InlineData(EstadoDocumento.Vencido, 0, "Hoy")]
    [InlineData(EstadoDocumento.Vencido, -1, "Hace 1 día")]
    [InlineData(EstadoDocumento.Vencido, -3, "Hace 3 días")]
    [InlineData(EstadoDocumento.EnTolerancia, -12, "Hace 12 días")]
    public void El_motivo_dice_los_dias_que_la_pastilla_no_dice(EstadoDocumento estado, int diasHastaVencer, string esperado)
    {
        EstadoDocumentoUi.MotivoDeDocumento(estado, Hoy.AddDays(diasHastaVencer), Hoy).Should().Be(esperado);
    }

    [Fact]
    public void El_motivo_cuenta_dias_de_calendario_tambien_entre_meses()
    {
        EstadoDocumentoUi.MotivoDeDocumento(EstadoDocumento.Proximo, new DateOnly(2026, 11, 2), new DateOnly(2026, 10, 30))
            .Should().Be("Caduca en 3 días");
    }

    [Fact]
    public void Sin_confirmar_dice_que_falta_la_fecha_la_tenga_o_no()
    {
        EstadoDocumentoUi.MotivoDeDocumento(EstadoDocumento.SinConfirmar, null, Hoy).Should().Be("Falta la fecha de vencimiento");
        EstadoDocumentoUi.MotivoDeDocumento(EstadoDocumento.SinConfirmar, Hoy.AddDays(40), Hoy).Should().Be("Falta la fecha de vencimiento");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    [InlineData(EstadoDocumento.Faltante)]
    public void Lo_que_no_necesita_explicacion_no_lleva_motivo(EstadoDocumento estado)
    {
        EstadoDocumentoUi.MotivoDeDocumento(estado, Hoy.AddDays(200), Hoy).Should().BeNull();
        EstadoDocumentoUi.MotivoDeDocumento(estado, null, Hoy).Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.Proximo)]
    public void Sin_fecha_de_vencimiento_no_hay_dias_que_contar(EstadoDocumento estado)
    {
        EstadoDocumentoUi.MotivoDeDocumento(estado, null, Hoy).Should().BeNull();
    }

    // ------------------------------------------------------ Franjas de estado

    [Fact]
    public void La_franja_de_propietarios_agrupa_como_el_rotulo_de_la_fila()
    {
        EstadoDocumentoUi.FranjaDocumental.Select(o => (o.Texto, string.Join(',', o.Valores))).Should().Equal(
            ("Vencidos", "Vencido"),
            ("Por vencer", "Urgente,Proximo"),
            ("Sin confirmar", "SinConfirmar"),
            ("Sin incidencias", "Vigente,SinCaducidad"));
    }

    /// <summary>
    /// Cada botón filtra exactamente las filas que llevan su rótulo: todo estado que un botón marca se rotula en
    /// la fila (TextoDocumental) igual para todos los valores de ese botón.
    /// </summary>
    [Fact]
    public void Todos_los_estados_de_un_boton_de_la_franja_de_propietarios_se_rotulan_igual_en_la_fila()
    {
        foreach (var opcion in EstadoDocumentoUi.FranjaDocumental)
        {
            opcion.Valores.Select(v => EstadoDocumentoUi.TextoDocumental(Enum.Parse<EstadoDocumento>(v)))
                .Distinct().Should().ContainSingle($"el botón «{opcion.Texto}» agrupa estados que la fila rotula igual");
        }
    }

    [Fact]
    public void La_franja_de_Documentos_separa_Vigentes_de_Sin_caducidad()
    {
        EstadoDocumentoUi.FranjaDeDocumentos.Select(o => (o.Texto, string.Join(',', o.Valores))).Should().Equal(
            ("Vencidos", "Vencido"),
            ("Por vencer", "Urgente,Proximo"),
            ("Sin confirmar", "SinConfirmar"),
            ("Vigentes", "Vigente"),
            ("Sin caducidad", "SinCaducidad"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Urgente", "Urgente")]
    [InlineData("Vencido,Urgente,Proximo", "Vencido,Urgente,Proximo")]
    [InlineData("Vencido,Inventado, Proximo", "Vencido,Proximo")]
    [InlineData("SinDocumentos", "SinDocumentos")]
    [InlineData("Inventado", "")]
    [InlineData("4", "")]
    public void La_seleccion_documental_valida_conserva_lo_conocido_y_descarta_lo_demas(string? seleccion, string esperada)
    {
        EstadoDocumentoUi.SeleccionDocumentalValida(seleccion).Should().Be(esperada);
    }

    // ------------------------------------------------------- Estado de Centro

    [Fact]
    public void En_Centros_Urgente_y_Proximo_tambien_comparten_rotulo_y_tono_y_Faltante_se_lee_Pendiente()
    {
        EstadoCentroUi.Texto(EstadoCentro.Urgente).Should().Be("Por vencer");
        EstadoCentroUi.Texto(EstadoCentro.Proximo).Should().Be("Por vencer");
        EstadoCentroUi.Tono(EstadoCentro.Urgente).Should().Be(TonoBadge.Advertencia);
        EstadoCentroUi.Tono(EstadoCentro.Proximo).Should().Be(TonoBadge.Advertencia);
        EstadoCentroUi.Texto(EstadoCentro.Faltante).Should().Be("Pendiente");
        EstadoCentroUi.Tono(EstadoCentro.Faltante).Should().Be(TonoBadge.Peligro);
    }

    [Fact]
    public void La_franja_de_Centros_tiene_un_boton_por_rotulo_y_cubre_todos_los_estados()
    {
        EstadoCentroUi.Franja.Select(o => (o.Texto, string.Join(',', o.Valores))).Should().Equal(
            ("Bloqueo de la plataforma CAE", "Bloqueado"),
            ("Vencido", "Vencido"),
            ("Pendiente", "Faltante"),
            ("Por vencer", "Urgente,Proximo"),
            ("Vigente", "Vigente"),
            ("No requiere gestión CAE", "SinGestionCae"));
        EstadoCentroUi.Franja.SelectMany(o => o.Valores).Should().BeEquivalentTo(
            Enum.GetNames<EstadoCentro>(), "un estado sin botón no se podría filtrar");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Vencido", "Vencido")]
    [InlineData("Urgente,Proximo", "Urgente,Proximo")]
    [InlineData("Vencido,Inventado,Faltante", "Vencido,Faltante")]
    [InlineData("3", "")]
    public void La_seleccion_valida_de_Centros_conserva_los_estados_del_enum_y_descarta_lo_demas(string? seleccion, string esperada)
    {
        EstadoCentroUi.SeleccionValida(seleccion).Should().Be(esperada);
    }

    [Theory]
    [InlineData(EstadoCentro.Vigente, 100, true)]
    [InlineData(EstadoCentro.Vigente, null, false)]
    [InlineData(EstadoCentro.Proximo, 100, false)]
    [InlineData(EstadoCentro.SinGestionCae, null, false)]
    public void Un_Centro_solo_es_correcto_si_esta_Vigente_con_cumplimiento_medido(EstadoCentro estado, int? cumplimiento, bool esperado)
    {
        EstadoCentroUi.EsCorrecto(estado, cumplimiento).Should().Be(esperado);
    }
}
