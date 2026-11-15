using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

/// <summary>Issue #2124: estado de la recuperación del hueco de correo en la conexión de Microsoft 365.</summary>
public class ConexionIntegracionRecuperacionHuecoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private const string Continuacion = "https://graph.microsoft.com/v1.0/users/buzon/mailFolders/inbox/messages?$skiptoken=abc";

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

        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);

        conexion.IniciarRecuperacionHueco(Ahora.AddMinutes(15)).Should().Be(Ahora.AddHours(-1), "retoma desde el cursor, no desde la marca");
        conexion.RecuperacionHuecoCubreDesdeUtc.Should().Be(Ahora.AddHours(-3));
    }

    [Fact]
    public void Una_marca_mas_antigua_que_el_tope_se_recupera_truncada_y_no_se_limpia()
    {
        var conexion = ConMarca(Ahora.AddDays(-10));

        conexion.IniciarRecuperacionHueco(Ahora).Should().Be(Ahora - ConexionIntegracion.AntiguedadMaximaRecuperacion);
        conexion.RecuperacionHuecoTruncada.Should().BeTrue();
        conexion.AvanzarRecuperacionHueco(null, siguienteEnlace: null, conexion.RecuperacionHuecoCursorUtc!.Value);

        conexion.CompletarRecuperacionHueco().Should().BeFalse();
        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddDays(-10), "el correo anterior al tope no se ha leído");
        conexion.RecuperacionHuecoEnCurso.Should().BeTrue("queda terminada sin volver a listar");
    }

    [Fact]
    public void El_cursor_solo_avanza()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);

        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-2), Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);
        conexion.AvanzarRecuperacionHueco(null, Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);

        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-1));
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
    }

    [Fact]
    public void La_continuacion_se_guarda_se_olvida_y_se_limpia_al_completar()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);

        conexion.AvanzarRecuperacionHueco(null, Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);
        conexion.RecuperacionHuecoSiguienteEnlace.Should().Be(Continuacion, "una página vacía con continuación no avanza el cursor, pero la continuación sí se guarda");
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();

        conexion.OlvidarContinuacionRecuperacionHueco().Should().BeTrue();
        conexion.OlvidarContinuacionRecuperacionHueco().Should().BeFalse();
        conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull();
        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-3), "olvidarla vuelve a listar desde el cursor");

        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);
        conexion.AvanzarRecuperacionHueco(null, siguienteEnlace: null, conexion.RecuperacionHuecoCursorUtc!.Value);
        conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
        conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull();
        var avanzarTrasCompletar = () => conexion.AvanzarRecuperacionHueco(Ahora, Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);
        avanzarTrasCompletar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Una_perdida_anula_tambien_la_continuacion()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(null, Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);
        conexion.RecuperacionHuecoSiguienteEnlace.Should().Be(Continuacion, "control: hay una continuación guardada");

        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2)).Should().BeTrue();

        conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull();
        conexion.IniciarRecuperacionHueco(Ahora.AddMinutes(15)).Should().Be(Ahora.AddHours(-3));
        conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull("la nueva recuperación empieza sin la continuación de la anulada");
    }

    [Fact]
    public void La_continuacion_solo_se_guarda_mientras_el_cursor_no_avanza_en_la_cadena()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        var inicioCadena = conexion.IniciarRecuperacionHueco(Ahora);

        // Todo lo leído es del mismo segundo que el inicio de la cadena: releer desde el cursor repetiría las páginas.
        conexion.AvanzarRecuperacionHueco(inicioCadena, Continuacion, inicioCadena);
        conexion.RecuperacionHuecoSiguienteEnlace.Should().Be(Continuacion);

        // El cursor avanza: una continuación posicional guardada entre pasadas podría saltar correo, se reanuda por cursor.
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-2), Continuacion, inicioCadena);
        conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull();
        conexion.RecuperacionHuecoCursorUtc.Should().Be(Ahora.AddHours(-2));
        conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse("la página traía continuación");

        // Una página posterior de la misma cadena tampoco la guarda: su posición cuenta lo leído desde el inicio de la cadena.
        conexion.AvanzarRecuperacionHueco(null, Continuacion, inicioCadena);
        conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull();
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

        conexion.AvanzarRecuperacionHueco(null, siguienteEnlace: null, Ahora.AddHours(-3));
        conexion.CompletarRecuperacionHueco().Should().BeTrue();
        conexion.RecuperacionHuecoId.Should().BeNull("completada");
    }

    [Fact]
    public void Avanzar_sin_recuperacion_lanza()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));

        var avanzar = () => conexion.AvanzarRecuperacionHueco(Ahora, siguienteEnlace: null, Ahora.AddHours(-3));

        avanzar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Completar_sin_listado_completo_lanza_y_no_limpia()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), Continuacion, conexion.RecuperacionHuecoCursorUtc!.Value);

        var completar = () => conexion.CompletarRecuperacionHueco();

        completar.Should().Throw<InvalidOperationException>();
        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
    }

    [Fact]
    public void Completar_con_listado_completo_limpia_la_marca_y_la_recuperacion()
    {
        var conexion = ConMarca(Ahora.AddHours(-3));
        conexion.IniciarRecuperacionHueco(Ahora);
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), siguienteEnlace: null, conexion.RecuperacionHuecoCursorUtc!.Value);

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
        conexion.AvanzarRecuperacionHueco(Ahora.AddHours(-1), siguienteEnlace: null, conexion.RecuperacionHuecoCursorUtc!.Value);

        // Recepción posterior: min(marca, recepción − 4 h) es la marca de antes, pero hay que guardar la anulación.
        conexion.RegistrarNotificacionDescartada(Ahora.AddHours(2)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(Ahora.AddHours(-3));
        conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
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
