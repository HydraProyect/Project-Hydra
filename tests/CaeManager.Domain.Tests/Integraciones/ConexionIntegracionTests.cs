using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class ConexionIntegracionTests
{
    [Fact]
    public void Se_crea_habilitada_con_los_datos_informados()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.BuzonEmail.Should().Be("cae@cliente.com");
        conexion.Nombre.Should().Be("Buzón CAE");
        conexion.ClienteId.Should().BeNull();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
    }

    [Fact]
    public void Rechaza_un_buzon_vacio()
    {
        var accion = () => new ConexionIntegracion(" ", "Buzón CAE");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Por_defecto_no_es_un_buzon_compartido_ni_tiene_directorio()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.EsBuzonCompartido.Should().BeFalse();
        conexion.DirectorioEntraId.Should().BeNull();
    }

    [Fact]
    public void Un_buzon_compartido_es_de_Microsoft365_sin_Gestor_y_guarda_el_directorio_normalizado_y_quien_lo_dio_de_alta()
    {
        var usuarioId = Guid.NewGuid();

        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "piloto@cliente.com", "Buzón piloto", "{AABBCCDD-0000-1111-2222-333344445555}", usuarioId);

        conexion.EsBuzonCompartido.Should().BeTrue();
        conexion.Proveedor.Should().Be(ProveedorIntegracion.Microsoft365);
        conexion.GestorPropietarioId.Should().BeNull();
        conexion.DirectorioEntraId.Should().Be("aabbccdd-0000-1111-2222-333344445555");
        conexion.ConsentidoPorUsuarioId.Should().Be(usuarioId);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void Rechaza_un_buzon_compartido_sin_un_directorio_GUID(string directorio)
    {
        var accion = () => ConexionIntegracion.CrearBuzonCompartido(
            "piloto@cliente.com", "Buzón piloto", directorio, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_buzon_compartido_sin_usuario_que_lo_diera_de_alta()
    {
        var accion = () => ConexionIntegracion.CrearBuzonCompartido(
            "piloto@cliente.com", "Buzón piloto", Guid.NewGuid().ToString(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_nombre_vacio()
    {
        var accion = () => new ConexionIntegracion("cae@cliente.com", " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarConError_cambia_el_estado_y_guarda_el_mensaje()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.MarcarConError("El refresh token ha expirado.");

        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be("El refresh token ha expirado.");
    }

    [Fact]
    public void Rehabilitar_limpia_el_ultimo_error()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.MarcarConError("fallo");

        conexion.Rehabilitar();

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
    }

    [Fact]
    public void Deshabilitar_cambia_el_estado()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.Deshabilitar();

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
    }

    [Fact]
    public void La_identidad_del_buzon_se_fija_una_vez_y_admite_repetir_la_misma()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.FijarIdentidadBuzon("inbox-1");
        conexion.FijarIdentidadBuzon("inbox-1");

        conexion.IdentidadBuzon.Should().Be("inbox-1");
    }

    [Fact]
    public void La_identidad_del_buzon_no_se_cambia_por_la_de_otro_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-1");

        var accion = () => conexion.FijarIdentidadBuzon("inbox-2");

        accion.Should().Throw<InvalidOperationException>();
        conexion.IdentidadBuzon.Should().Be("inbox-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_una_identidad_del_buzon_vacia(string identidad)
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        var accion = () => conexion.FijarIdentidadBuzon(identidad);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_identidad_del_buzon_demasiado_larga()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        var accion = () => conexion.FijarIdentidadBuzon(new string('a', ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon + 1));

        accion.Should().Throw<ArgumentException>();
    }

    private static ConexionIntegracion Verificada(DateTime verificadaUtc)
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(verificadaUtc);
        return conexion;
    }

    [Fact]
    public void Pasar_a_ConError_fija_el_hueco_en_el_momento_de_la_caida()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));

        conexion.MarcarConError("refresh token caducado", caida);

        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    [Fact]
    public void Una_segunda_caida_no_adelanta_la_marca()
    {
        var primera = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(primera.AddHours(-1));
        conexion.MarcarConError("primera caída", primera);
        conexion.Rehabilitar();

        conexion.MarcarConError("segunda caída", primera.AddHours(3));

        conexion.HuecoPendienteDesdeUtc.Should().Be(primera);
    }

    [Fact]
    public void Rehabilitar_no_limpia_la_marca()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);

        conexion.Rehabilitar();
        conexion.MarcarIdentidadVerificada(caida.AddHours(1));

        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    [Fact]
    public void ConError_sobre_una_verificacion_ya_caducada_fija_la_marca_a_la_hora_del_ConError()
    {
        // Mientras la verificación estaba caducada los eventos esperaban sin perderse: el hueco empieza al caer.
        var verificada = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(verificada);

        conexion.MarcarConError("caída", verificada.AddHours(40));

        conexion.HuecoPendienteDesdeUtc.Should().Be(verificada.AddHours(40));
    }

    [Fact]
    public void Una_verificacion_caducada_sin_perdida_no_fija_marca()
    {
        var verificada = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(verificada);

        conexion.OperativaVerificada(verificada.AddHours(50)).Should().BeFalse("sigue bloqueando y mostrándose sin verificar");

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    [Fact]
    public void La_perdida_de_la_suscripcion_marca_en_el_minimo_entre_ahora_y_su_expiracion_y_nunca_adelanta()
    {
        var ahora = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(ahora.AddHours(-1));

        conexion.RegistrarPerdidaSuscripcion(ahora.AddHours(5), ahora).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora, "aún no había expirado: deja de recibir desde ahora");

        conexion.RegistrarPerdidaSuscripcion(ahora.AddHours(-3), ahora.AddHours(1)).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora.AddHours(-3), "ya había expirado: desde su expiración");

        conexion.RegistrarPerdidaSuscripcion(ahora.AddHours(2), ahora.AddHours(4)).Should().BeFalse();
        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora.AddHours(-3));
    }

    [Fact]
    public void La_perdida_de_la_suscripcion_retrocede_la_marca_aunque_la_conexion_ya_este_en_ConError()
    {
        var ahora = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(ahora.AddDays(-2));
        conexion.MarcarConError("el buzón ya no es accesible", ahora);

        conexion.RegistrarPerdidaSuscripcion(ahora.AddHours(-20), ahora.AddHours(1)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora.AddHours(-20));
    }

    [Fact]
    public void Ningun_disparador_adelanta_la_marca()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);
        conexion.Rehabilitar();

        conexion.RegistrarNotificacionDescartada(caida.AddHours(10)).Should().BeFalse();
        conexion.RegistrarPerdidaSuscripcion(caida.AddHours(10), caida.AddHours(11)).Should().BeFalse();
        conexion.MarcarConError("otra caída", caida.AddHours(12));

        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    [Fact]
    public void El_reintento_programado_bloquea_hasta_su_hora_y_verificar_lo_vacia()
    {
        var ahora = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(ahora.AddHours(-21));
        conexion.PuedeReintentarVerificacion(ahora).Should().BeTrue("sin programar");

        conexion.ProgramarReintentoVerificacion(ahora, TimeSpan.FromMinutes(10));

        conexion.ReintentoVerificacionNoAntesDeUtc.Should().Be(ahora + ConexionIntegracion.IntervaloReintentoVerificacion + TimeSpan.FromMinutes(10));
        conexion.PuedeReintentarVerificacion(ahora.AddHours(2)).Should().BeFalse();
        conexion.PuedeReintentarVerificacion(ahora.AddHours(2).AddMinutes(10)).Should().BeTrue();

        conexion.MarcarIdentidadVerificada(ahora.AddMinutes(1));
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().BeNull();
    }

    [Fact]
    public void El_primer_reintento_espera_el_retraso_dado()
    {
        var ahora = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(ahora.AddHours(-21));

        conexion.ProgramarPrimerReintentoVerificacion(ahora, TimeSpan.FromMinutes(47));

        conexion.ReintentoVerificacionNoAntesDeUtc.Should().Be(ahora.AddMinutes(47));
        FluentActions.Invoking(() => conexion.ProgramarPrimerReintentoVerificacion(ahora, TimeSpan.FromMinutes(-1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Aplazar_por_Retry_After_solo_retrasa_la_espera_programada()
    {
        var ahora = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(ahora.AddHours(-21));
        conexion.ProgramarReintentoVerificacion(ahora, TimeSpan.Zero);

        conexion.AplazarReintentoVerificacion(ahora, TimeSpan.FromMinutes(5));
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().Be(ahora.AddHours(2), "una espera más corta no la adelanta");

        conexion.AplazarReintentoVerificacion(ahora, TimeSpan.FromHours(6));
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().Be(ahora.AddHours(6));
    }

    [Fact]
    public void Desconectar_y_volver_a_rehabilitar_conservan_la_marca_tal_cual()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);

        conexion.Deshabilitar();
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida, "desconectar no recupera el correo de antes");

        conexion.Rehabilitar();
        conexion.MarcarIdentidadVerificada(caida.AddDays(1));
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida, "el hueco queda declarado: nada lo limpia en esta versión");
    }

    [Fact]
    public void Una_conexion_deshabilitada_que_pasa_a_ConError_no_fija_marca()
    {
        var conexion = Verificada(DateTime.UtcNow.AddHours(-1));
        conexion.Deshabilitar();

        conexion.MarcarConError("renovación fallida");

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    [Fact]
    public void Una_notificacion_descartada_en_ConError_retrocede_la_marca_a_su_recepcion_menos_cuatro_horas_y_nunca_la_adelanta()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);

        // Graph reintenta la entrega hasta cuatro horas: una notificación recibida a las 10:30 puede ser de un correo
        // llegado a las 6:30, antes de la caída.
        conexion.RegistrarNotificacionDescartada(caida.AddMinutes(30)).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida.AddMinutes(30) - ConexionIntegracion.RetrasoMaximoNotificacion);

        conexion.RegistrarNotificacionDescartada(caida.AddHours(2)).Should().BeFalse("una recepción posterior no adelanta la marca");
        conexion.RegistrarNotificacionDescartada(caida.AddHours(-1)).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(caida.AddHours(-5));
    }

    [Fact]
    public void Una_notificacion_descartada_de_una_conexion_deshabilitada_fija_la_marca()
    {
        // Desconectar con eventos en espera: los que se descartan después se pierden y marcan (disparador a).
        var recepcion = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(recepcion.AddHours(-30));
        conexion.Deshabilitar();

        conexion.RegistrarNotificacionDescartada(recepcion).Should().BeTrue();

        conexion.HuecoPendienteDesdeUtc.Should().Be(recepcion - ConexionIntegracion.RetrasoMaximoNotificacion);
    }

    [Theory]
    [InlineData(EstadoConexionIntegracion.Habilitada, 1, true)]
    [InlineData(EstadoConexionIntegracion.Habilitada, 27, false)]
    [InlineData(EstadoConexionIntegracion.ConError, 1, false)]
    [InlineData(EstadoConexionIntegracion.Deshabilitada, 1, false)]
    public void EsOperativaVerificada_aplica_la_misma_regla_que_la_instancia(
        EstadoConexionIntegracion estado, int horasDesdeVerificacion, bool esperado)
    {
        var ahora = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

        ConexionIntegracion.EsOperativaVerificada(estado, ProveedorIntegracion.Microsoft365, ahora.AddHours(-horasDesdeVerificacion), ahora)
            .Should().Be(esperado);
        ConexionIntegracion.EsOperativaVerificada(estado, ProveedorIntegracion.Microsoft365, null, ahora).Should().BeFalse();
    }
}