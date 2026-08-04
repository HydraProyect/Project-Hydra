using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class ConversacionWhatsAppTests
{
    private static ConversacionCorreo CrearConversacionWhatsApp() =>
        ConversacionCorreo.CrearWhatsApp("+34686543364", Guid.NewGuid(), clienteId: null, ejecutivoId: Guid.NewGuid());

    [Fact]
    public void CrearWhatsApp_configura_canal_telefono_conexion_y_ejecutivo()
    {
        var conexionId = Guid.NewGuid();
        var ejecutivoId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();

        var conversacion = ConversacionCorreo.CrearWhatsApp("+34686543364", conexionId, clienteId, ejecutivoId);

        conversacion.Canal.Should().Be(CanalConversacion.WhatsApp);
        conversacion.TelefonoContacto.Should().Be("+34686543364");
        conversacion.ConexionIntegracionId.Should().Be(conexionId);
        conversacion.EjecutivoAsignadoId.Should().Be(ejecutivoId);
        conversacion.ClienteId.Should().Be(clienteId);
        conversacion.Estado.Should().Be(EstadoConversacion.Abierta);
        conversacion.HiloExternoId.Should().BeNull();
        conversacion.Asunto.Should().Contain("+34686543364");
    }

    [Fact]
    public void CrearWhatsApp_rechaza_telefono_vacio()
    {
        var accion = () => ConversacionCorreo.CrearWhatsApp(" ", Guid.NewGuid(), null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CrearWhatsApp_rechaza_conexion_vacia()
    {
        var accion = () => ConversacionCorreo.CrearWhatsApp("+34686543364", Guid.Empty, null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_constructor_de_correo_deja_el_canal_en_Correo()
    {
        var conversacion = new ConversacionCorreo("Consulta");

        conversacion.Canal.Should().Be(CanalConversacion.Correo);
        conversacion.VentanaServicioAbierta(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Un_mensaje_entrante_actualiza_la_fecha_de_ultimo_entrante()
    {
        var conversacion = CrearConversacionWhatsApp();
        var fecha = new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc);

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, "+34686543364", "Hola", fecha, "wamid.1");

        conversacion.FechaUltimoMensajeEntranteUtc.Should().Be(fecha);
    }

    [Fact]
    public void Un_mensaje_saliente_no_toca_la_fecha_de_ultimo_entrante()
    {
        var conversacion = CrearConversacionWhatsApp();

        conversacion.AgregarMensaje(DireccionMensaje.Saliente, "+34600000000", "Respuesta");

        conversacion.FechaUltimoMensajeEntranteUtc.Should().BeNull();
    }

    [Fact]
    public void Un_entrante_desordenado_mas_antiguo_no_retrocede_la_fecha_de_ultimo_entrante()
    {
        var conversacion = CrearConversacionWhatsApp();
        var reciente = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var antiguo = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, "+34686543364", "Segundo", reciente, "wamid.2");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, "+34686543364", "Primero", antiguo, "wamid.1");

        conversacion.FechaUltimoMensajeEntranteUtc.Should().Be(reciente);
    }

    [Fact]
    public void La_ventana_esta_abierta_dentro_de_las_24_horas_del_ultimo_entrante()
    {
        var conversacion = CrearConversacionWhatsApp();
        var entrada = new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, "+34686543364", "Hola", entrada, "wamid.1");

        conversacion.VentanaServicioAbierta(entrada.AddHours(23).AddMinutes(59)).Should().BeTrue();
    }

    [Fact]
    public void La_ventana_esta_cerrada_pasadas_24_horas_del_ultimo_entrante()
    {
        var conversacion = CrearConversacionWhatsApp();
        var entrada = new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, "+34686543364", "Hola", entrada, "wamid.1");

        conversacion.VentanaServicioAbierta(entrada.AddHours(24)).Should().BeFalse();
    }

    [Fact]
    public void La_ventana_esta_cerrada_sin_ningun_mensaje_entrante()
    {
        var conversacion = CrearConversacionWhatsApp();

        conversacion.VentanaServicioAbierta(DateTime.UtcNow).Should().BeFalse();
    }
}
