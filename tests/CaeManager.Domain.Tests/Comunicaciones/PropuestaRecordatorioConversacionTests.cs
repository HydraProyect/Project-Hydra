using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>
/// Recordatorio a los dos días laborables de espera de respuesta externa (decisión de producto del 2026-10-17): se propone,
/// nunca se envía solo, y una espera recibe como mucho una propuesta.
/// </summary>
public class PropuestaRecordatorioConversacionTests
{
    private static readonly CalendarioLaboralOperadorCae SinFestivos = new([]);

    // Lunes 19-10-2026, 11:00 en Madrid (CEST, UTC+2): dentro de un tramo laborable, no en la pausa de 10:00 a 10:30.
    private static readonly DateTime LunesOnceMadrid = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

    private static Conversacion EsperandoDesde(DateTime desdeUtc, CanalConversacion canal = CanalConversacion.Correo)
    {
        var conversacion = canal == CanalConversacion.WhatsApp
            ? Conversacion.CrearWhatsApp("+34600000000", Guid.NewGuid(), null, null)
            : new Conversacion("Documentación pendiente de la Empresa");
        conversacion.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, desdeUtc).EsExitoso.Should().BeTrue();
        return conversacion;
    }

    [Fact]
    public void No_se_propone_antes_de_dos_dias_laborables_y_si_justo_al_cumplirse()
    {
        var conversacion = EsperandoDesde(LunesOnceMadrid);
        var miercolesOnceMadrid = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

        PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, miercolesOnceMadrid.AddMinutes(-1), SinFestivos)
            .Should().BeNull("aún no han pasado dos días laborables");

        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, miercolesOnceMadrid, SinFestivos);
        propuesta.Should().NotBeNull();
        propuesta!.ConversacionId.Should().Be(conversacion.Id);
        propuesta.EsperaDesdeUtc.Should().Be(LunesOnceMadrid, "la propuesta pertenece a esa espera concreta");
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente, "se propone; nada se envía solo");
        propuesta.PropuestaEnUtc.Should().Be(miercolesOnceMadrid);
    }

    [Fact]
    public void El_fin_de_semana_y_los_festivos_del_Operador_CAE_no_cuentan()
    {
        // Jueves 22-10-2026, 11:00 en Madrid: el fin de semana salta al lunes 26 (ya CET, UTC+1).
        var juevesOnceMadrid = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
        var lunesOnceMadrid = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
        PropuestaRecordatorioConversacion.VenceEn(juevesOnceMadrid, SinFestivos).Should().Be(lunesOnceMadrid);

        var conFestivoElViernes = new CalendarioLaboralOperadorCae([new DateOnly(2026, 10, 23)]);
        PropuestaRecordatorioConversacion.VenceEn(juevesOnceMadrid, conFestivoElViernes)
            .Should().Be(lunesOnceMadrid.AddDays(1), "el viernes festivo del Operador CAE tampoco es laborable");
    }

    [Theory]
    [InlineData(EstadoConversacion.Abierta)]
    [InlineData(EstadoConversacion.Pendiente)]
    [InlineData(EstadoConversacion.EnEsperaDeTercero)]
    [InlineData(EstadoConversacion.Resuelta)]
    public void Solo_se_propone_en_espera_de_respuesta_externa(EstadoConversacion estado)
    {
        var conversacion = new Conversacion("Hilo en otro estado");
        if (estado != EstadoConversacion.Abierta)
            conversacion.CambiarEstado(estado, LunesOnceMadrid).EsExitoso.Should().BeTrue();

        PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, LunesOnceMadrid.AddDays(10), SinFestivos)
            .Should().BeNull();
    }

    [Fact]
    public void No_se_propone_en_WhatsApp_porque_la_ventana_de_servicio_ya_esta_cerrada()
    {
        var conversacion = EsperandoDesde(LunesOnceMadrid, CanalConversacion.WhatsApp);

        PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, LunesOnceMadrid.AddDays(10), SinFestivos)
            .Should().BeNull();
    }

    [Fact]
    public void Deja_de_estar_vigente_cuando_llega_la_respuesta_o_cambia_el_estado()
    {
        var conversacion = EsperandoDesde(LunesOnceMadrid);
        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, LunesOnceMadrid.AddDays(3), SinFestivos)!;
        propuesta.SigueVigentePara(conversacion).Should().BeTrue();

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@contraparte.es",
            "<p>Ya lo tenéis.</p>", LunesOnceMadrid.AddDays(3).AddMinutes(5));
        conversacion.Estado.Should().Be(EstadoConversacion.Abierta, "la respuesta externa reabre la conversación");
        propuesta.SigueVigentePara(conversacion).Should().BeFalse();

        // Volver a esperar es una espera nueva, con su propio instante: la propuesta vieja no la cubre.
        conversacion.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, LunesOnceMadrid.AddDays(4)).EsExitoso.Should().BeTrue();
        propuesta.SigueVigentePara(conversacion).Should().BeFalse();
    }

    [Fact]
    public void Aprobar_o_descartar_solo_una_vez_y_liberar_devuelve_a_pendiente()
    {
        var conversacion = EsperandoDesde(LunesOnceMadrid);
        var ahora = LunesOnceMadrid.AddDays(3);
        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, ahora, SinFestivos)!;

        propuesta.Aprobar(ahora).EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Aprobada);
        propuesta.ResueltaEnUtc.Should().Be(ahora);
        propuesta.Aprobar(ahora).EsFallido.Should().BeTrue("una propuesta aprobada no se aprueba dos veces");
        propuesta.Descartar(ahora).EsFallido.Should().BeTrue();

        propuesta.LiberarTrasEnvioFallido();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente, "el envío no salió: se puede reintentar");
        propuesta.ResueltaEnUtc.Should().BeNull();

        propuesta.Descartar(ahora).EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada);
        propuesta.LiberarTrasEnvioFallido();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada, "liberar solo deshace una aprobación");
    }

    [Fact]
    public void Restaurar_deshace_solo_un_descarte_y_solo_mientras_dura_la_misma_espera()
    {
        var conversacion = EsperandoDesde(LunesOnceMadrid);
        var ahora = LunesOnceMadrid.AddDays(3);
        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, ahora, SinFestivos)!;

        propuesta.Restaurar(conversacion).Error.Should().Be(PropuestaRecordatorioConversacion.ErrorNoDescartada,
            "una propuesta Pendiente no tiene descarte que deshacer");

        propuesta.Descartar(ahora).EsExitoso.Should().BeTrue();
        propuesta.Restaurar(conversacion).EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente, "Deshacer la devuelve, la misma propuesta");
        propuesta.ResueltaEnUtc.Should().BeNull();

        propuesta.Aprobar(ahora).EsExitoso.Should().BeTrue();
        propuesta.Restaurar(conversacion).Error.Should().Be(PropuestaRecordatorioConversacion.ErrorNoDescartada,
            "una aprobada ya salió: no se restaura");
    }

    [Fact]
    public void Un_descarte_cuya_espera_termino_no_se_restaura()
    {
        var conversacion = EsperandoDesde(LunesOnceMadrid);
        var ahora = LunesOnceMadrid.AddDays(3);
        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, ahora, SinFestivos)!;
        propuesta.Descartar(ahora).EsExitoso.Should().BeTrue();

        conversacion.CambiarEstado(EstadoConversacion.Abierta, ahora).EsExitoso.Should().BeTrue();
        propuesta.Restaurar(conversacion).Error.Should().Be(PropuestaRecordatorioConversacion.ErrorEsperaTerminada);

        conversacion.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, ahora.AddHours(1)).EsExitoso.Should().BeTrue();
        propuesta.Restaurar(conversacion).Error.Should().Be(PropuestaRecordatorioConversacion.ErrorEsperaTerminada,
            "una espera nueva es otra espera: tendrá su propia propuesta");
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada);
    }
}
