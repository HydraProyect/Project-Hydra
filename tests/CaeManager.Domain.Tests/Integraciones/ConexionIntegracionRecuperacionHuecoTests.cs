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
