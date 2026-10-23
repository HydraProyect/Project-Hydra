using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class CajaPlataformaCaeClienteTests
{
    [Fact]
    public async Task Copiar_el_codigo_de_cliente_confirma_la_copia_con_el_texto_de_la_maqueta()
    {
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var mediador = new MediatorFalso();
        var cut = Montar(mediador);
        var boton = cut.FindComponents<BotonCopiar>()
            .Single(b => b.Instance.Valor == mediador.Plataforma!.CodigoCliente);

        await boton.Find("button").ClickAsync(new MouseEventArgs());

        modulo.Invocations["copiarAlPortapapeles"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be(mediador.Plataforma!.CodigoCliente);
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == "Código de cliente copiado al portapapeles" && m.Tono == TonoToast.Exito);
    }
}