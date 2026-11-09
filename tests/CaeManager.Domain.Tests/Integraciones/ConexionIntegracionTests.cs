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
        conexion.CorreoNoRecuperadoDesdeUtc.Should().BeNull();
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
    public void ConError_sobre_una_verificacion_ya_caducada_fija_la_marca_en_la_caducidad()
    {
        var verificada = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(verificada);

        conexion.MarcarConError("caída", verificada.AddHours(40));

        conexion.HuecoPendienteDesdeUtc.Should().Be(verificada + ConexionIntegracion.VentanaVerificacionIdentidad);
    }

    [Fact]
    public void La_caducidad_de_la_verificacion_fija_la_marca_en_el_momento_exacto_aunque_se_detecte_tarde()
    {
        var verificada = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(verificada);

        conexion.RegistrarCaducidadVerificacion(verificada.AddHours(25)).Should().BeFalse("aún está vigente");
        conexion.RegistrarCaducidadVerificacion(verificada.AddHours(50)).Should().BeTrue();
        conexion.RegistrarCaducidadVerificacion(verificada.AddHours(60)).Should().BeFalse("la marca ya es la más antigua");

        conexion.HuecoPendienteDesdeUtc.Should().Be(verificada + ConexionIntegracion.VentanaVerificacionIdentidad);
    }

    [Fact]
    public void Sin_verificacion_previa_la_caducidad_no_fija_marca()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");

        conexion.RegistrarCaducidadVerificacion(DateTime.UtcNow).Should().BeFalse();

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    [Fact]
    public void Cerrar_el_hueco_recuperado_solo_limpia_la_marca_que_se_leyo()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);

        conexion.CerrarHuecoRecuperado(caida.AddMinutes(1));
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida, "otra caída fijó una marca distinta mientras se leía");

        conexion.CerrarHuecoRecuperado(caida);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.CorreoNoRecuperadoDesdeUtc.Should().BeNull();
    }

    [Fact]
    public void Llegar_al_tope_convierte_la_marca_en_aviso_y_conserva_el_aviso_mas_antiguo()
    {
        var primera = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(primera.AddHours(-1));
        conexion.MarcarConError("caída", primera);
        conexion.CerrarHuecoConCorreoNoRecuperado(primera);
        conexion.Rehabilitar();
        conexion.MarcarIdentidadVerificada(primera.AddDays(3).AddHours(-1));
        conexion.MarcarConError("otra caída", primera.AddDays(3));

        conexion.CerrarHuecoConCorreoNoRecuperado(primera.AddDays(3));

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.CorreoNoRecuperadoDesdeUtc.Should().Be(primera);
    }

    [Fact]
    public void Desconectar_con_el_hueco_pendiente_lo_deja_como_aviso_visible()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);

        conexion.Deshabilitar();

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.CorreoNoRecuperadoDesdeUtc.Should().Be(caida);
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
    public void Una_notificacion_descartada_en_ConError_retrocede_la_marca_a_su_recepcion_y_nunca_la_adelanta()
    {
        var caida = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var conexion = Verificada(caida.AddHours(-1));
        conexion.MarcarConError("caída", caida);

        conexion.RegistrarNotificacionDescartada(caida.AddMinutes(-20)).Should().BeTrue();
        conexion.RegistrarNotificacionDescartada(caida.AddMinutes(30)).Should().BeFalse();

        conexion.HuecoPendienteDesdeUtc.Should().Be(caida.AddMinutes(-20));
    }

    [Fact]
    public void Una_notificacion_descartada_de_una_conexion_deshabilitada_no_fija_marca()
    {
        var conexion = Verificada(DateTime.UtcNow.AddHours(-1));
        conexion.Deshabilitar();

        conexion.RegistrarNotificacionDescartada(DateTime.UtcNow.AddMinutes(-5)).Should().BeFalse();

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
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