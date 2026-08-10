using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

public class CalculadoraAntelacionVisitaTests
{
    private const int HorasAviso = 48;
    private const int HorasCriticas = 24;
    private static readonly TimeOnly InicioJornada = new(8, 0);
    private static readonly TimeOnly FinJornada = new(18, 0);

    private static ResultadoAntelacion Calcular(DateTime entrada, DateTime solicitud, DateTime expedienteCompleto) =>
        CalculadoraAntelacionVisita.Calcular(entrada, solicitud, expedienteCompleto, HorasAviso, HorasCriticas, InicioJornada, FinJornada);

    /// <summary>
    /// El caso que motiva toda la matriz: el cliente avisa el lunes a las 09:00 de una
    /// entrada el jueves a las 08:00 (71h de margen aparente) pero no manda el último
    /// documento hasta el miércoles a las 17:00. El gestor tuvo 15h, no 71.
    /// </summary>
    [Fact]
    public void Separa_la_antelacion_nominal_de_la_efectiva_cuando_la_documentacion_llega_la_vispera()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 12, 17, 0, 0, DateTimeKind.Utc));

        resultado.NominalHoras.Should().Be(71m);
        resultado.EfectivaHoras.Should().Be(15m);
        resultado.BloqueadoPorClienteHoras.Should().Be(56m);
        resultado.Tramo.Should().Be(TramoAntelacion.Expres);
        resultado.Atribucion.Should().Be(AtribucionUrgencia.DocumentacionTardiaCliente);
    }

    [Fact]
    public void Clasifica_como_Estandar_y_sin_urgencia_cuando_todo_llego_con_margen()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 6, 9, 0, 0, DateTimeKind.Utc));

        resultado.Tramo.Should().Be(TramoAntelacion.Estandar);
        resultado.Atribucion.Should().Be(AtribucionUrgencia.SinUrgencia);
    }

    [Fact]
    public void Clasifica_como_Urgente_cuando_el_margen_efectivo_cae_entre_las_horas_criticas_y_las_de_aviso()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 11, 21, 0, 0, DateTimeKind.Utc));

        resultado.EfectivaHoras.Should().Be(36m);
        resultado.Tramo.Should().Be(TramoAntelacion.Urgente);
        resultado.Atribucion.Should().Be(AtribucionUrgencia.DocumentacionTardiaCliente);
    }

    [Fact]
    public void Imputa_al_aviso_tardio_cuando_es_la_propia_solicitud_la_que_llega_dentro_de_la_ventana()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 12, 9, 30, 0, DateTimeKind.Utc));

        resultado.NominalHoras.Should().Be(24m);
        resultado.Atribucion.Should().Be(AtribucionUrgencia.SolicitudTardiaCliente);
    }

    /// <summary>
    /// Documentación tardía solo tiene sentido como atribución si el aviso llegó con
    /// margen sobrado: si el propio aviso ya venía dentro de la ventana, la culpa es
    /// del aviso, no de los papeles.
    /// </summary>
    [Fact]
    public void No_imputa_a_la_documentacion_cuando_el_aviso_ya_venia_justo()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 11, 15, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc));

        resultado.NominalHoras.Should().Be(42m);
        resultado.Atribucion.Should().Be(AtribucionUrgencia.SolicitudTardiaCliente);
    }

    [Fact]
    public void Una_entrada_fuera_de_la_jornada_es_expres_aunque_hubiera_margen_de_sobra()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 6, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc));

        resultado.Tramo.Should().Be(TramoAntelacion.Expres);

        // La atribución mira el margen, no la hora: nadie hizo nada mal, la entrada
        // simplemente cae fuera de horario.
        resultado.Atribucion.Should().Be(AtribucionUrgencia.SinUrgencia);
    }

    [Fact]
    public void Nunca_emite_RetrasoGestor_porque_no_hay_dato_del_cierre_de_la_gestion()
    {
        var resultado = Calcular(
            entrada: new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            solicitud: new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
            expedienteCompleto: new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc));

        resultado.Atribucion.Should().NotBe(AtribucionUrgencia.RetrasoGestor);
    }
}

public class VisitaFechaHoraEntradaTests
{
    private static readonly DateOnly Jueves = new(2026, 8, 13);

    [Fact]
    public void Usa_la_hora_estimada_cuando_el_cliente_la_indico()
    {
        var visita = new Visita(Guid.NewGuid(), Jueves, Jueves, notas: null, OrigenVisita.Correo, new TimeOnly(6, 30));

        visita.ObtenerFechaHoraEntrada(new TimeOnly(8, 0))
            .Should().Be(new DateTime(2026, 8, 13, 6, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Cae_en_la_apertura_de_la_jornada_cuando_la_visita_no_trae_hora()
    {
        var visita = new Visita(Guid.NewGuid(), Jueves, Jueves, notas: null);

        visita.ObtenerFechaHoraEntrada(new TimeOnly(8, 0))
            .Should().Be(new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void El_sello_del_expediente_es_de_un_solo_sentido()
    {
        var visita = new Visita(Guid.NewGuid(), Jueves, Jueves, notas: null, OrigenVisita.Correo);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc));

        var primera = new ResultadoAntelacion(71m, 15m, 56m, TramoAntelacion.Expres, AtribucionUrgencia.DocumentacionTardiaCliente);
        var segunda = new ResultadoAntelacion(1m, 1m, 0m, TramoAntelacion.Estandar, AtribucionUrgencia.SinUrgencia);

        visita.MarcarExpedienteCompleto(new DateTime(2026, 8, 12, 17, 0, 0, DateTimeKind.Utc), primera).Should().BeTrue();
        visita.MarcarExpedienteCompleto(new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc), segunda).Should().BeFalse();

        visita.AntelacionEfectivaHoras.Should().Be(15m);
        visita.Atribucion.Should().Be(AtribucionUrgencia.DocumentacionTardiaCliente);
    }

    [Fact]
    public void No_sella_una_visita_sin_solicitud_de_origen_porque_no_hay_nada_que_medir()
    {
        var visita = new Visita(Guid.NewGuid(), Jueves, Jueves, notas: null);

        visita.MarcarExpedienteCompleto(DateTime.UtcNow, new ResultadoAntelacion(1m, 1m, 0m, TramoAntelacion.Estandar, AtribucionUrgencia.SinUrgencia))
            .Should().BeFalse();
        visita.FechaHoraExpedienteCompletoUtc.Should().BeNull();
    }

    [Fact]
    public void El_origen_de_la_solicitud_no_se_reasigna()
    {
        var visita = new Visita(Guid.NewGuid(), Jueves, Jueves, notas: null, OrigenVisita.Correo);
        var primeraConversacion = Guid.NewGuid();

        visita.RegistrarOrigenSolicitud(primeraConversacion, new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc));
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc));

        visita.ConversacionOrigenId.Should().Be(primeraConversacion);
        visita.FechaHoraSolicitudUtc.Should().Be(new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc));
    }
}
