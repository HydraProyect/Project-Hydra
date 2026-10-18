using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public class ToastEnvioDiferidoTests
{
    [Fact]
    public async Task El_foco_pausa_tambien_el_envio_hasta_agotar_la_ventana_real()
    {
        var servicio = new ToastService(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
        var enviado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        servicio.Mostrar("Aviso pendiente", textoAccion: "Deshacer", onAccion: () => Task.CompletedTask,
            alAgotarTiempo: () => { enviado.SetResult(); return Task.CompletedTask; });
        var id = servicio.Mensajes.Single().Id;
        servicio.FocoEn(id, true);
        await Task.Delay(300);
        enviado.Task.IsCompleted.Should().BeFalse();
        servicio.Mensajes.Should().ContainSingle();
        servicio.FocoEn(id, false);
        await enviado.Task.WaitAsync(TimeSpan.FromSeconds(4));
        servicio.Mensajes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cerrar_o_desplazar_el_toast_cancela_sin_ejecutar_el_envio(bool desplazar)
    {
        var servicio = new ToastService(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        var envios = 0;
        var cancelaciones = 0;
        servicio.Mostrar("Aviso pendiente", textoAccion: "Deshacer", onAccion: () => Task.CompletedTask,
            alAgotarTiempo: () => { envios++; return Task.CompletedTask; }, alCancelar: () => cancelaciones++);
        var id = servicio.Mensajes.Single().Id;
        if (desplazar)
            for (var i = 0; i < ToastService.MaximoVisibles; i++) servicio.Mostrar("Otro aviso");
        else servicio.Descartar(id);
        await Task.Delay(200);
        envios.Should().Be(0);
        cancelaciones.Should().Be(1);
    }
}
