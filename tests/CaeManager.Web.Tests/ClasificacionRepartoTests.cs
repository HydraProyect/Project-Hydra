using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Listo», «Revisar» y «Sin destino» no se guardan: se deducen del destino, del tipo y de las fechas que la persona ha escrito.
/// La cabecera, el pie y los botones del reparto de adjuntos cuentan sobre esta misma clasificación, así que cualquier desvío
/// aquí se vería a la vez en las tres.
/// </summary>
public class ClasificacionRepartoTests
{
    private static AdjuntoRepartoDto Adjunto(
        EstadoRepartoAdjunto estado = EstadoRepartoAdjunto.Pendiente, Guid? trabajador = null, Guid? empresa = null, Guid? tipo = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "doc.pdf", "application/pdf", 10, estado, trabajador, empresa, tipo, null, null, null, null);

    private static EdicionAdjunto Fechas(string emision = "", string vencimiento = "", bool noCaduca = false) =>
        new() { FechaEmision = emision, FechaVencimiento = vencimiento, NoCaduca = noCaduca };

    [Fact]
    public void Sin_destino_el_adjunto_pendiente_esta_sin_destino_aunque_tenga_tipo_y_fechas()
    {
        ClasificacionReparto.Situacion(Adjunto(tipo: Guid.NewGuid()), Fechas("2026-01-05"))
            .Should().Be(SituacionAdjunto.SinDestino);
    }

    [Fact]
    public void Con_destino_pero_sin_tipo_o_sin_fecha_de_emision_hay_que_revisarlo()
    {
        var trabajador = Guid.NewGuid();
        ClasificacionReparto.Situacion(Adjunto(trabajador: trabajador), Fechas("2026-01-05")).Should().Be(SituacionAdjunto.PorRevisar);
        ClasificacionReparto.Situacion(Adjunto(trabajador: trabajador, tipo: Guid.NewGuid()), Fechas()).Should().Be(SituacionAdjunto.PorRevisar);
        ClasificacionReparto.Situacion(Adjunto(trabajador: trabajador, tipo: Guid.NewGuid()), Fechas("no es fecha")).Should().Be(SituacionAdjunto.PorRevisar);
    }

    [Fact]
    public void Con_destino_tipo_y_fechas_validas_esta_listo_tambien_para_una_empresa()
    {
        ClasificacionReparto.Situacion(Adjunto(trabajador: Guid.NewGuid(), tipo: Guid.NewGuid()), Fechas("2026-01-05", "2027-01-05"))
            .Should().Be(SituacionAdjunto.Listo);
        ClasificacionReparto.Situacion(Adjunto(empresa: Guid.NewGuid(), tipo: Guid.NewGuid()), Fechas("2026-01-05", noCaduca: true))
            .Should().Be(SituacionAdjunto.Listo);
    }

    [Fact]
    public void Un_vencimiento_ilegible_o_junto_a_No_caduca_no_esta_listo_y_se_avisa()
    {
        var emision = Fechas("2026-01-05", "pronto");
        emision.FechasValidas.Should().BeFalse();
        emision.FechasContradictorias.Should().BeTrue();

        var contradictorias = Fechas("2026-01-05", "2027-01-05", noCaduca: true);
        contradictorias.FechasValidas.Should().BeFalse();
        contradictorias.FechasContradictorias.Should().BeTrue();

        ClasificacionReparto.Situacion(Adjunto(trabajador: Guid.NewGuid(), tipo: Guid.NewGuid()), contradictorias)
            .Should().Be(SituacionAdjunto.PorRevisar);
    }

    [Fact]
    public void Una_fecha_sin_escribir_no_es_una_contradiccion_solo_falta()
    {
        var vacia = Fechas();
        vacia.FechasValidas.Should().BeFalse();
        vacia.FechasContradictorias.Should().BeFalse();
    }

    [Theory]
    [InlineData(EstadoRepartoAdjunto.Asignado, SituacionAdjunto.Asignado)]
    [InlineData(EstadoRepartoAdjunto.Rechazado, SituacionAdjunto.Rechazado)]
    [InlineData(EstadoRepartoAdjunto.Descartado, SituacionAdjunto.Descartado)]
    [InlineData(EstadoRepartoAdjunto.Dividido, SituacionAdjunto.Dividido)]
    public void Lo_ya_resuelto_conserva_su_estado_guardado_con_independencia_del_destino(EstadoRepartoAdjunto estado, SituacionAdjunto esperada)
    {
        ClasificacionReparto.Situacion(Adjunto(estado), Fechas()).Should().Be(esperada);
    }

    [Fact]
    public void El_resumen_cuenta_cada_adjunto_una_sola_vez_y_los_recuentos_cuadran()
    {
        var trabajador = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var filas = new List<FilaReparto>
        {
            new(Adjunto(trabajador: trabajador, tipo: tipo), Fechas("2026-01-05")),
            new(Adjunto(trabajador: trabajador, tipo: tipo), Fechas("2026-01-06")),
            new(Adjunto(trabajador: trabajador), Fechas()),
            new(Adjunto(), Fechas()),
            new(Adjunto(EstadoRepartoAdjunto.Asignado), Fechas()),
            new(Adjunto(EstadoRepartoAdjunto.Rechazado), Fechas())
        };

        var resumen = ClasificacionReparto.Resumir(filas);

        resumen.Should().Be(new ResumenReparto(Listos: 2, PorRevisar: 1, SinDestino: 1, Asignados: 1, Rechazados: 1, Descartados: 0, Divididos: 0));
        resumen.Total.Should().Be(filas.Count);
        resumen.EnCurso.Should().Be(4);
        resumen.ParaRevisarUnoAUno.Should().Be(2);
    }

    [Fact]
    public void La_cola_uno_a_uno_lleva_primero_lo_que_pide_decision_con_destino_despues_lo_que_no_lo_tiene_y_no_incluye_lo_listo()
    {
        var trabajador = Guid.NewGuid();
        var sinDestino = new FilaReparto(Adjunto(), Fechas());
        var revisar = new FilaReparto(Adjunto(trabajador: trabajador), Fechas());
        var listo = new FilaReparto(Adjunto(trabajador: trabajador, tipo: Guid.NewGuid()), Fechas("2026-01-05"));

        ClasificacionReparto.Cola([sinDestino, listo, revisar])
            .Should().Equal(revisar.Adjunto.Id, sinDestino.Adjunto.Id);
    }

    [Fact]
    public void Abierta_desde_la_fila_de_un_listo_la_cola_empieza_por_ese_adjunto()
    {
        var trabajador = Guid.NewGuid();
        var revisar = new FilaReparto(Adjunto(trabajador: trabajador), Fechas());
        var listo = new FilaReparto(Adjunto(trabajador: trabajador, tipo: Guid.NewGuid()), Fechas("2026-01-05"));

        ClasificacionReparto.Cola([revisar, listo], listo.Adjunto.Id)
            .Should().Equal(listo.Adjunto.Id, revisar.Adjunto.Id);
    }
}
