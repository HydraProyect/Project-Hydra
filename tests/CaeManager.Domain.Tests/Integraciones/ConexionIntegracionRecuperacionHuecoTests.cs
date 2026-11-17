using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

/// <summary>Issue #2124: estado de la recuperación del hueco de correo en la conexión de Microsoft 365.</summary>
public class ConexionIntegracionRecuperacionHuecoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ConexionIntegracion ConMarca(DateTime marcaUtc)
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.RegistrarNotificacionDescartada(marcaUtc + ConexionIntegracion.RetrasoMaximoNotificacion);
        conexion.HuecoPendienteDesdeUtc.Should().Be(marcaUtc, "control: la marca de partida");
        return conexion;
    }

    [Fact]
    public void Iniciar_sin_marca_lanza()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");

        var iniciar = () => conexion.IniciarRecuperacionHueco(Ahora);

        iniciar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Iniciar_empieza_en_la_marca_y_retomar_devuelve_el_cursor()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));

        conexion.IniciarRecuperacionHueco(Ahora).Should().Be(Ahora.AddHours(-3));
        conexion.RecuperacionHuecoCubreDesdeUtc.Should().Be(Ahora.AddHours(-3));
        conexion.RecuperacionHuecoTruncada.Should().BeFalse();

        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: true);

        conexion.IniciarRecuperacionHueco(Ahora.AddMinutes(15)).Should().Be(Ahora.AddHours(-1), "retoma desde el cursor, no desde la marca");
        conexion.RecuperacionHuecoCubreDesdeUtc.Should().Be(Ahora.AddHours(-3));
    }

    [Fact]
    public void Una_marca_mas_antigua_que_el_tope_se_recupera_truncada_y_no_se_limpia()
    {
        var conexion = ConMarca(Ahora.AddDays(-10));

        conexion.IniciarRecuperacionHueco(Ahora).Should().Be(Ahora - ConexionIntegracion.AntiguedadMaximaRecuperacion);
        conexion.RecuperacionHuecoTruncada.Should().BeTrue();
        conexion.AvanzarRecuperacionHueco(null, hayMas: false);

        conexion.CompletarRecuperacionHueco().Should().BeFalse();
        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddDays(-10), "el correo anterior al tope no se ha leído");
        conexion.RecuperacionHuecoEnCurso.Should().BeTrue("queda terminada sin volver a listar");
    }

    /// <summary>
    /// Issue #2263: el indicador y un desempate que ya no vale (sin el formato recuperación|segundo|Id, como el que dejaría
    /// una fila tocada por la ranura anterior del despliegue azul/verde) se ponen por reflexión para aislar el veto del
    /// recorrido real por Id (<see cref="ConexionIntegracion.AvanzarRecuperacionHuecoEnSegundo"/>).
    /// </summary>
    private static void SimularRecorridoPorId(ConexionIntegracion conexion, string? desempate = "desempate")
    {
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.RecuperacionHuecoRecorridaPorId))!
            .SetValue(conexion, true);
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.RecuperacionHuecoCursorDesempate))!
            .SetValue(conexion, desempate);
    }

    [Fact]
    public void Una_recuperacion_recorrida_por_id_termina_sin_limpiar_la_marca()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        SimularRecorridoPorId(conexion, desempate: null);
        conexion.AvanzarRecuperacionHueco(null, hayMas: false);

        conexion.CompletarRecuperacionHueco().Should().BeFalse("el recorrido por Id está sin verificar contra Graph real");

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3), "el aviso de posible correo no recuperado se queda");
        conexion.RecuperacionHuecoEnCurso.Should().BeTrue("queda terminada sin volver a listar");
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeTrue();
    }

    [Fact]
    public void Sin_recorrido_por_id_la_misma_recuperacion_limpia_la_marca()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(null, hayMas: false);

        conexion.RecuperacionHuecoRecorridaPorId.Should().BeFalse("ningún segundo se recorrió por Id");
        conexion.CompletarRecuperacionHueco().Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    [Fact]
    public void Retomar_conserva_el_indicador_y_avanzar_por_fecha_borra_el_desempate()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        SimularRecorridoPorId(conexion);

        conexion.IniciarRecuperacionHueco(Ahora.AddMinutes(15)).Should().Be(Ahora.AddHours(-3), "retoma la misma recuperación");
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeTrue("retomar no borra lo que recorrió la otra ranura");
        conexion.RecuperacionHuecoCursorDesempate.Should().Be("desempate");

        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: true);

        conexion.RecuperacionHuecoCursorDesempate.Should().BeNull("avanzar por fecha no arrastra el desempate");
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_atasco_que_reinicia_el_segundo_borra_el_desempate_y_el_que_no_lo_conserva(bool reiniciarSegundo)
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        var segundo = ConexionIntegracion.SegundoDe(Ahora.AddHours(-2));
        conexion.AvanzarRecuperacionHueco(segundo, hayMas: true);
        conexion.AvanzarRecuperacionHuecoEnSegundo("AAMkAG-id-03", hayMas: true);

        conexion.RegistrarAtascoRecuperacionHueco(reiniciarSegundo).Should().BeTrue();

        conexion.RecuperacionHuecoAtascadaEnUtc.Should().Be(segundo);
        conexion.RecuperacionHuecoCursorUtc.Should().Be(segundo);
        conexion.RecuperacionHuecoCursorId.Should().Be(reiniciarSegundo ? null : "AAMkAG-id-03");
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeTrue("lo recorrido por Id ya se encoló: el veto se queda");
        conexion.RegistrarAtascoRecuperacionHueco(reiniciarSegundo).Should().BeFalse("ya detenida y sin desempate que borrar");
    }

    [Fact]
    public void Anular_la_recuperacion_borra_el_indicador_y_el_desempate()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        SimularRecorridoPorId(conexion);

        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2));

        conexion.RecuperacionHuecoRecorridaPorId.Should().BeFalse();
        conexion.RecuperacionHuecoCursorDesempate.Should().BeNull();
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeFalse("la recuperación nueva empieza sin recorrido por Id");
    }

    [Fact]
    public void El_cursor_solo_avanza_y_una_pagina_con_mas_no_completa_el_listado()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);

        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: true);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-2), hayMas: true);
        conexion.AvanzarRecuperacionHueco(null, hayMas: true);

        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-1));
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
    }

    [Fact]
    public void Una_pagina_sin_mas_completa_el_listado_y_despues_no_se_avanza()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);

        conexion.AvanzarRecuperacionHueco(null, hayMas: false);

        conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue("una página vacía sin más detrás también termina el listado");
        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-3));
        var avanzarTrasCompletar = () => conexion.AvanzarRecuperacionHueco(Ahora, hayMas: true);
        avanzarTrasCompletar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_atasco_registra_el_segundo_del_cursor_una_vez_y_avanzar_o_anular_lo_borra()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1).AddTicks(1234567), hayMas: true);

        conexion.RegistrarAtascoRecuperacionHueco().Should().BeTrue();
        conexion.RecuperacionHuecoAtascadaEnUtc.Should().Be(Ahora.AddHours(-1), "el segundo, sin la fracción");
        conexion.RegistrarAtascoRecuperacionHueco().Should().BeFalse("ya estaba detenida en ese segundo: nada que guardar");
        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-1).AddTicks(1234567), "el atasco no mueve el cursor");
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();

        conexion.AvanzarRecuperacionHueco(Ahora.AddMinutes(-30), hayMas: true);
        conexion.RecuperacionHuecoAtascadaEnUtc.Should().BeNull("una página que avanza la levanta");

        conexion.RegistrarAtascoRecuperacionHueco();
        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2));
        conexion.RecuperacionHuecoAtascadaEnUtc.Should().BeNull("anular la recuperación la levanta");
        var sinRecuperacion = () => conexion.RegistrarAtascoRecuperacionHueco();
        sinRecuperacion.Should().Throw<InvalidOperationException>();
    }

    /// <summary>Issue #2263: el cursor compuesto (recepción, Id) recorre un segundo por Id y sale al siguiente.</summary>
    [Fact]
    public void El_recorrido_por_id_de_un_segundo_guarda_el_desempate_y_al_terminar_pasa_al_segundo_siguiente()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1).AddTicks(1234567), hayMas: true);
        conexion.RegistrarAtascoRecuperacionHueco();

        conexion.AvanzarRecuperacionHuecoEnSegundo("id-03", hayMas: true);

        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-1), "al principio de su segundo: releer de más se deduplica");
        conexion.RecuperacionHuecoCursorId.Should().Be("id-03");
        conexion.RecuperacionHuecoAtascadaEnUtc.Should().BeNull("avanzar por Id levanta la detención");
        var retroceder = () => conexion.AvanzarRecuperacionHuecoEnSegundo("id-02", hayMas: true);
        retroceder.Should().Throw<InvalidOperationException>("el desempate solo avanza");
        var porFecha = () => conexion.AvanzarRecuperacionHueco(Ahora, hayMas: true);
        porFecha.Should().Throw<InvalidOperationException>("recorriendo un segundo por Id no se avanza por fecha");

        conexion.AvanzarRecuperacionHuecoEnSegundo("id-07", hayMas: true);
        conexion.RecuperacionHuecoCursorId.Should().Be("id-07");
        conexion.IniciarRecuperacionHueco(Ahora.AddMinutes(15)).Should().Be(Ahora.AddHours(-1), "retomar conserva el cursor compuesto");
        conexion.RecuperacionHuecoCursorId.Should().Be("id-07");

        conexion.AvanzarRecuperacionHuecoEnSegundo(null, hayMas: false);

        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-1).AddSeconds(1));
        conexion.RecuperacionHuecoCursorId.Should().BeNull();
        conexion.RecuperacionHuecoCursorDesempate.Should().BeNull();
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse("lo posterior a ese segundo aún no se ha pedido");

        conexion.AvanzarRecuperacionHueco(null, hayMas: false);
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeTrue();
        conexion.CompletarRecuperacionHueco().Should().BeFalse("el recorrido por Id está sin verificar contra Graph real");
        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
    }

    [Fact]
    public void Anular_la_recuperacion_borra_el_desempate()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHuecoEnSegundo("id-03", hayMas: true);

        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2));

        conexion.RecuperacionHuecoCursorDesempate.Should().BeNull();
        conexion.RecuperacionHuecoRecorridaPorId.Should().BeFalse();
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.RecuperacionHuecoCursorId.Should().BeNull("la recuperación nueva empieza con el cursor solo por fecha");
    }

    /// <summary>
    /// La ranura anterior del despliegue azul/verde no conoce el desempate: puede mover el cursor o rehacer la
    /// recuperación sin borrarlo. Un desempate de otro segundo o de otra recuperación no vale: saltaría correo.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Un_desempate_de_otro_segundo_o_de_otra_recuperacion_no_vale(bool otroSegundo)
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHuecoEnSegundo("id-03", hayMas: true);
        conexion.RecuperacionHuecoCursorId.Should().Be("id-03", "control");
        var desempate = conexion.RecuperacionHuecoCursorDesempate;

        if (otroSegundo)
        {
            typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.RecuperacionHuecoCursorUtc))!
                .SetValue(conexion, Ahora.AddHours(-2));
        }
        else
        {
            conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2));
            conexion.IniciarRecuperacionHueco(Ahora);
            typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.RecuperacionHuecoCursorDesempate))!
                .SetValue(conexion, desempate);
            conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-3), "control: el mismo segundo que el desempate");
        }

        conexion.RecuperacionHuecoCursorId.Should().BeNull();
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: true);
        conexion.RecuperacionHuecoCursorDesempate.Should().BeNull("avanzar por fecha no arrastra un desempate que ya no vale");
    }

    [Fact]
    public void El_tope_de_antiguedad_se_trunca_al_segundo()
    {
        var conexion = ConMarca(Ahora.AddDays(-10));
        var ahoraConFraccion = Ahora.AddTicks(9_876_543);

        conexion.IniciarRecuperacionHueco(ahoraConFraccion).Should().Be(Ahora - ConexionIntegracion.AntiguedadMaximaRecuperacion,
            "la base guarda microsegundos: una cobertura con ticks no volvería igual al releerla");
    }

    [Fact]
    public void Cada_recuperacion_tiene_su_generacion()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.RecuperacionHuecoId.Should().BeNull();

        conexion.IniciarRecuperacionHueco(Ahora);
        var primera = conexion.RecuperacionHuecoId;
        primera.Should().NotBeNull();
        conexion.IniciarRecuperacionHueco(Ahora.AddMinutes(15));
        conexion.RecuperacionHuecoId.Should().Be(primera, "retomar la misma recuperación no cambia su generación");

        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2)).Should().BeTrue();
        conexion.RecuperacionHuecoId.Should().BeNull("anulada");

        conexion.IniciarRecuperacionHueco(Ahora).Should().Be(Ahora.AddHours(-3), "control: misma cobertura y mismo cursor que la anulada");
        conexion.RecuperacionHuecoId.Should().NotBeNull();
        (conexion.RecuperacionHuecoId == primera).Should().BeFalse("una recuperación nueva tiene otra generación");

        conexion.AvanzarRecuperacionHueco(null, hayMas: false);
        conexion.CompletarRecuperacionHueco().Should().BeTrue();
        conexion.RecuperacionHuecoId.Should().BeNull("completada");
    }

    [Fact]
    public void Avanzar_sin_recuperacion_lanza()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));

        var avanzar = () => conexion.AvanzarRecuperacionHueco(Ahora, hayMas: false);

        avanzar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Completar_sin_listado_completo_lanza_y_no_limpia()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: true);

        var completar = () => conexion.CompletarRecuperacionHueco();

        completar.Should().Throw<InvalidOperationException>();
        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
    }

    [Fact]
    public void Completar_con_listado_completo_limpia_la_marca_y_la_recuperacion()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: false);

        conexion.CompletarRecuperacionHueco().Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
        conexion.RecuperacionHuecoCursorUtc.Should().BeNull();
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
    }

    [Fact]
    public void Una_perdida_que_no_mueve_la_marca_anula_igualmente_la_recuperacion()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), hayMas: false);

        // Recepción posterior: min(marca, recepción − 4 h) es la marca de antes, pero hay que guardar la anulación.
        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
        conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
        conexion.RecuperacionHuecoCursorUtc.Should().BeNull();
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        var completar = () => conexion.CompletarRecuperacionHueco();
        completar.Should().Throw<InvalidOperationException>("la limpieza de una recuperación anulada no se puede guardar");
    }

    [Fact]
    public void Una_perdida_observada_en_la_suscripcion_anula_la_recuperacion()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);

        conexion.RegistrarHuecoAlObservarSuscripcion(Ahora.AddHours(-1), Ahora).Should().BeTrue();

        conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
    }

    [Fact]
    public void Sin_recuperacion_en_curso_una_perdida_que_no_mueve_la_marca_no_tiene_nada_que_guardar()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));

        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2)).Should().BeFalse();
    }

    [Fact]
    public void Deshabilitar_y_Rehabilitar_anulan_la_recuperacion()
    {
        var deshabilitada = ConMarca(Ahora.AddHours(-3));
        deshabilitada.IniciarRecuperacionHueco(Ahora);
        deshabilitada.Deshabilitar();
        deshabilitada.RecuperacionHuecoEnCurso.Should().BeFalse();

        var rehabilitada = ConMarca(Ahora.AddHours(-3));
        rehabilitada.IniciarRecuperacionHueco(Ahora);
        rehabilitada.Rehabilitar();
        rehabilitada.RecuperacionHuecoEnCurso.Should().BeFalse();
    }

    [Fact]
    public void Un_evento_de_recuperacion_descartado_retrocede_la_marca_a_lo_que_cubria()
    {
        var conexion = ConMarca(Ahora.AddHours(-1));

        // El evento se recibió ahora, pero traía correo de hace dos días.
        conexion.RegistrarNotificacionDescartada(Ahora, cubreDesdeUtc: Ahora.AddDays(-2)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddDays(-2));
    }

    [Fact]
    public void Una_cobertura_posterior_a_la_recepcion_menos_el_retraso_no_adelanta_nada()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");

        conexion.RegistrarNotificacionDescartada(Ahora, cubreDesdeUtc: Ahora.AddHours(-1)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora - ConexionIntegracion.RetrasoMaximoNotificacion);
    }

    [Fact]
    public void Crear_recuperacion_lleva_la_cobertura_y_se_reconoce()
    {
        var evento = EventoWebhook.CrearRecuperacion(Guid.NewGuid(), "{}", Ahora.AddDays(-1));

        evento.CubreDesdeUtc.Should().Be(Ahora.AddDays(-1));
        evento.EsRecuperacionHueco.Should().BeTrue();
        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
    }
}
