using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.DocumentosIa;

public class TrabajoAnalisisDocumentoTests
{
    private static TrabajoAnalisisDocumento CrearTrabajo() =>
        new(Guid.NewGuid(), Guid.NewGuid(), TipoAnalisisDocumento.VerificacionIa);

    [Fact]
    public void Nace_pendiente_sin_intentos()
    {
        var trabajo = CrearTrabajo();

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente);
        trabajo.Intentos.Should().Be(0);
        trabajo.IniciadoEnUtc.Should().BeNull();
        trabajo.CompletadoEnUtc.Should().BeNull();
    }

    [Fact]
    public void MarcarEnProceso_pasa_a_Procesando_y_registra_cuando_empezo()
    {
        var trabajo = CrearTrabajo();

        trabajo.MarcarEnProceso();

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Procesando);
        trabajo.IniciadoEnUtc.Should().NotBeNull();
    }

    [Fact]
    public void MarcarCompletado_pasa_a_Completado_y_registra_cuando_termino()
    {
        var trabajo = CrearTrabajo();
        trabajo.MarcarEnProceso();

        trabajo.MarcarCompletado();

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Completado);
        trabajo.CompletadoEnUtc.Should().NotBeNull();
    }

    [Fact]
    public void Un_fallo_por_debajo_del_maximo_de_intentos_vuelve_a_Pendiente_para_reintentar()
    {
        var trabajo = CrearTrabajo();
        trabajo.MarcarEnProceso();

        trabajo.RegistrarFallo("Timeout llamando al proveedor de IA.");

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente, "todavía no se alcanzó TrabajoAnalisisDocumento.MaximoIntentos");
        trabajo.Intentos.Should().Be(1);
        trabajo.UltimoError.Should().Be("Timeout llamando al proveedor de IA.");
        trabajo.IniciadoEnUtc.Should().BeNull("vuelve a estar disponible para que el procesador lo reclame de cero");
    }

    [Fact]
    public void Tras_agotar_el_maximo_de_intentos_el_trabajo_queda_definitivamente_fallido()
    {
        var trabajo = CrearTrabajo();

        for (var intento = 1; intento < TrabajoAnalisisDocumento.MaximoIntentos; intento++)
        {
            trabajo.RegistrarFallo($"Fallo número {intento}.");
            trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente);
        }

        trabajo.RegistrarFallo("Fallo definitivo.");

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Fallido);
        trabajo.Intentos.Should().Be(TrabajoAnalisisDocumento.MaximoIntentos);
    }

    [Fact]
    public void Un_error_mas_largo_que_el_maximo_se_trunca_sin_reventar()
    {
        var trabajo = CrearTrabajo();

        trabajo.RegistrarFallo(new string('x', TrabajoAnalisisDocumento.LongitudMaximaError + 500));

        trabajo.UltimoError.Should().HaveLength(TrabajoAnalisisDocumento.LongitudMaximaError);
    }

    [Fact]
    public void RecuperarSiEstancado_no_hace_nada_si_el_trabajo_no_esta_Procesando()
    {
        var trabajo = CrearTrabajo();

        trabajo.RecuperarSiEstancado(TimeSpan.FromMinutes(15), DateTime.UtcNow.AddHours(1));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente);
    }

    [Fact]
    public void RecuperarSiEstancado_no_hace_nada_si_todavia_no_paso_el_umbral()
    {
        var trabajo = CrearTrabajo();
        trabajo.MarcarEnProceso();

        trabajo.RecuperarSiEstancado(TimeSpan.FromMinutes(15), trabajo.IniciadoEnUtc!.Value.AddMinutes(5));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Procesando, "todavía no pasó el umbral de 15 minutos");
    }

    [Fact]
    public void RecuperarSiEstancado_pasado_el_umbral_lo_devuelve_a_Pendiente_como_un_fallo_mas()
    {
        var trabajo = CrearTrabajo();
        trabajo.MarcarEnProceso();

        trabajo.RecuperarSiEstancado(TimeSpan.FromMinutes(15), trabajo.IniciadoEnUtc!.Value.AddMinutes(20));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente);
        trabajo.Intentos.Should().Be(1, "cuenta como un intento fallido más, para no reintentar una caída en bucle infinito");
    }

    [Fact]
    public void RecuperarSiEstancado_repetido_puede_agotar_los_intentos_hasta_Fallido()
    {
        var trabajo = CrearTrabajo();

        for (var vez = 1; vez < TrabajoAnalisisDocumento.MaximoIntentos; vez++)
        {
            trabajo.MarcarEnProceso();
            trabajo.RecuperarSiEstancado(TimeSpan.FromMinutes(15), trabajo.IniciadoEnUtc!.Value.AddMinutes(20));
        }

        trabajo.MarcarEnProceso();
        trabajo.RecuperarSiEstancado(TimeSpan.FromMinutes(15), trabajo.IniciadoEnUtc!.Value.AddMinutes(20));

        trabajo.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Fallido, "caerse a mitad de análisis repetidamente no puede reintentar para siempre");
    }
}
