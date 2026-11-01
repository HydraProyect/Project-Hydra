using CaeManager.Application.Common;
using CaeManager.Infrastructure.Conversion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// La espera por el semáforo de conversión tiene tope (M-20). Se ocupa el semáforo
/// compartido a mano, así que no lanza LibreOffice: observa solo que la cola no es
/// ilimitada y que el rechazo es el fallo controlado. NO prueba la conversión real
/// ni el timeout del proceso (eso vive en las pruebas de integración).
/// </summary>
public class EsperaMaximaConversionWordTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private LibreOfficeConversorWordPdfService CrearServicio(int esperaMaximaSegundos) => new(
        Options.Create(new LibreOfficeConversorWordPdfServiceOptions
        {
            RutaTemporales = _raiz,
            RutaEjecutable = Path.Combine(_raiz, "ejecutable-inexistente"),
            EsperaMaximaSegundos = esperaMaximaSegundos
        }), NullLogger<LibreOfficeConversorWordPdfService>.Instance);

    [Fact]
    public async Task Con_el_servicio_ocupado_falla_con_el_aviso_controlado_tras_la_espera_maxima()
    {
        var servicio = CrearServicio(esperaMaximaSegundos: 1);
        await LibreOfficeConversorWordPdfService.Semaforo.WaitAsync();
        try
        {
            // El token de seguridad hace fallar de forma distinta (cancelación) si el
            // tope desapareciera, en vez de colgar la ejecución de tests.
            using var seguridad = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var accion = () => servicio.ConvertirAPdfAsync([1, 2, 3], seguridad.Token);

            var fallo = await accion.Should().ThrowAsync<ConversionWordPdfOcupadaException>();
            fallo.Which.Message.Should().Be(ConversionWordPdfOcupadaException.MensajeUsuario);
            Directory.GetDirectories(_raiz).Should().BeEmpty("el directorio de trabajo se borra aunque no llegue a convertir");
        }
        finally
        {
            LibreOfficeConversorWordPdfService.Semaforo.Release();
        }
    }

    [Fact]
    public async Task El_rechazo_por_ocupado_no_libera_el_semaforo_que_tiene_otra_conversion()
    {
        var servicio = CrearServicio(esperaMaximaSegundos: 1);
        await LibreOfficeConversorWordPdfService.Semaforo.WaitAsync();
        try
        {
            using var seguridad = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await FluentActions.Awaiting(() => servicio.ConvertirAPdfAsync([1], seguridad.Token))
                .Should().ThrowAsync<ConversionWordPdfOcupadaException>();

            LibreOfficeConversorWordPdfService.Semaforo.CurrentCount.Should().Be(0);
        }
        finally
        {
            LibreOfficeConversorWordPdfService.Semaforo.Release();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }
}
