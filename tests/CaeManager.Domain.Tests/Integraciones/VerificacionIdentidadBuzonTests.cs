using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

/// <summary>
/// "Verificada" es una frescura de la identidad del buzón, no un estado: separa lo que la ingesta y los envíos
/// pueden hacer (<see cref="ConexionIntegracion.OperativaVerificada"/>) de ConError, que queda para lo definitivo.
/// </summary>
public class VerificacionIdentidadBuzonTests
{
    private static readonly DateTime Ahora = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static ConexionIntegracion ConIdentidad()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-cae");
        return conexion;
    }

    [Fact]
    public void Una_conexion_nueva_no_esta_verificada()
    {
        var conexion = ConIdentidad();

        conexion.IdentidadVerificadaUtc.Should().BeNull();
        conexion.OperativaVerificada(Ahora).Should().BeFalse();
    }

    [Fact]
    public void Verificada_dentro_de_la_ventana_es_operativa_y_fuera_no()
    {
        var conexion = ConIdentidad();
        conexion.MarcarIdentidadVerificada(Ahora);

        conexion.OperativaVerificada(Ahora + ConexionIntegracion.VentanaVerificacionIdentidad - TimeSpan.FromMinutes(1)).Should().BeTrue();
        conexion.OperativaVerificada(Ahora + ConexionIntegracion.VentanaVerificacionIdentidad).Should().BeFalse();
    }

    [Fact]
    public void La_ventana_es_de_26_horas()
    {
        ConexionIntegracion.VentanaVerificacionIdentidad.Should().Be(TimeSpan.FromHours(26));
    }

    /// <summary>Una fecha adelantada no extiende la vigencia: con el reloj desajustado, no cuenta.</summary>
    [Fact]
    public void Una_verificacion_con_fecha_futura_no_cuenta()
    {
        var conexion = ConIdentidad();
        conexion.MarcarIdentidadVerificada(Ahora.AddHours(1));

        conexion.IdentidadVerificadaVigente(Ahora).Should().BeFalse();
        conexion.OperativaVerificada(Ahora).Should().BeFalse();
    }

    [Fact]
    public void Una_conexion_en_ConError_no_es_operativa_aunque_este_verificada()
    {
        var conexion = ConIdentidad();
        conexion.MarcarIdentidadVerificada(Ahora);
        conexion.MarcarConError("motivo");

        conexion.IdentidadVerificadaVigente(Ahora).Should().BeTrue();
        conexion.OperativaVerificada(Ahora).Should().BeFalse();
    }

    [Fact]
    public void Una_linea_de_WhatsApp_no_necesita_verificar_identidad()
    {
        var linea = new ConexionIntegracion("linea@operador.test", "Línea", proveedor: ProveedorIntegracion.WhatsApp);

        linea.OperativaVerificada(Ahora).Should().BeTrue();
    }

    [Fact]
    public void No_se_puede_verificar_sin_identidad()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");

        var accion = () => conexion.MarcarIdentidadVerificada(Ahora);

        accion.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// La espera no gasta intentos: con el backoff de RegistrarFallo los cinco se agotarían en unos dos minutos.
    /// </summary>
    [Fact]
    public void Esperar_verificacion_no_gasta_intentos_y_vuelve_a_mirar_en_15_minutos()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");
        var ahora = evento.FechaRecepcionUtc.AddMinutes(1);

        for (var i = 0; i < EventoWebhook.MaximoIntentos * 3; i++)
            evento.EsperarVerificacion("en espera", ahora);

        evento.Intentos.Should().Be(0);
        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.SiguienteIntentoEnUtc.Should().Be(ahora + EventoWebhook.IntervaloEsperaVerificacion);
        EventoWebhook.IntervaloEsperaVerificacion.Should().Be(TimeSpan.FromMinutes(15));
        evento.ErrorProcesado.Should().Be("en espera");
    }

    [Fact]
    public void Pasado_el_tope_de_espera_queda_retenido_nunca_descartado()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");

        evento.EsperarVerificacion("en espera", evento.FechaRecepcionUtc + EventoWebhook.TopeEsperaVerificacion - TimeSpan.FromMinutes(1));
        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);

        evento.EsperarVerificacion("en espera", evento.FechaRecepcionUtc + EventoWebhook.TopeEsperaVerificacion);
        evento.Estado.Should().Be(EstadoEventoWebhook.Retenido);
        evento.SiguienteIntentoEnUtc.Should().BeNull();
        evento.Intentos.Should().Be(0);
        EventoWebhook.TopeEsperaVerificacion.Should().Be(TimeSpan.FromHours(72));
    }

    [Fact]
    public void Liberar_devuelve_un_retenido_a_la_cola_ya_y_no_toca_otros_estados()
    {
        var retenido = new EventoWebhook(Guid.NewGuid(), "{}");
        retenido.EsperarVerificacion("en espera", retenido.FechaRecepcionUtc + EventoWebhook.TopeEsperaVerificacion);
        var completado = new EventoWebhook(Guid.NewGuid(), "{}");
        completado.MarcarEnProceso();
        completado.MarcarProcesado();

        retenido.Liberar();
        completado.Liberar();

        retenido.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        retenido.SiguienteIntentoEnUtc.Should().BeNull();
        completado.Estado.Should().Be(EstadoEventoWebhook.Completado);
    }

    [Fact]
    public void Esperar_verificacion_exige_un_motivo()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");

        var accion = () => evento.EsperarVerificacion(" ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }
}
