using Bunit;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Subcontratas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Cliente360PaginaTests
{
    private async Task ConfirmarBajaAsync(IRenderedComponent<CaeManager.Web.Features.Clientes.Pages.ClienteDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task La_baja_avisa_y_Deshacer_restaura_el_mismo_Cliente_empresarial_incluso_tras_salir_de_su_ficha()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);
        var cut = Renderizar(id);
        var toasts = Services.GetRequiredService<ToastService>();

        await ConfirmarBajaAsync(cut);

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/clientes");
        var aviso = toasts.Mensajes.Should().ContainSingle().Subject;
        aviso.Mensaje.Should().Be($"{mediador.Detalles[id].RazonSocial} dado de baja");
        aviso.TextoAccion.Should().Be("Deshacer");
        aviso.Tono.Should().Be(TonoToast.Exito);
        mediador.Enviadas.OfType<RestaurarClienteCommand>().Should().BeEmpty();
        cut.Dispose();

        await toasts.EjecutarAccionAsync(aviso.Id);

        mediador.Enviadas.OfType<RestaurarClienteCommand>().Should().ContainSingle(c => c.Id == id);
        toasts.Mensajes.Should().ContainSingle(m => m.Mensaje == "Baja del Cliente deshecha" && m.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task La_baja_rechazada_no_ofrece_Deshacer()
    {
        var (id, mediador) = ClienteBase();
        mediador.ResultadoBaja = Result.Fallo(Error.Crear("Conflicto", "La baja no está permitida."));
        Registrar(mediador);
        var cut = Renderizar(id);

        await ConfirmarBajaAsync(cut);

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain(id.ToString());
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
        cut.Find("[role=alert]").TextContent.Should().Contain("La baja no está permitida.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deshacer_informa_del_rechazo_o_excepcion_sin_afirmar_que_restaura(bool excepcion)
    {
        var (id, mediador) = ClienteBase();
        mediador.ResultadoRestaurar = Result.Fallo(Error.Crear("Autorizacion.SinPermiso", "No puedes restaurar esta ficha."));
        mediador.LanzarAlRestaurar = excepcion;
        Registrar(mediador);
        var cut = Renderizar(id);
        var toasts = Services.GetRequiredService<ToastService>();
        await ConfirmarBajaAsync(cut);
        var aviso = toasts.Mensajes.Single();

        await toasts.EjecutarAccionAsync(aviso.Id);

        var error = toasts.Mensajes.Should().ContainSingle().Subject;
        error.Tono.Should().Be(TonoToast.Error);
        error.Mensaje.Should().Be(excepcion
            ? "No se pudo deshacer la baja. Inténtalo de nuevo."
            : "No puedes restaurar esta ficha.");
        mediador.Enviadas.OfType<RestaurarClienteCommand>().Should().ContainSingle(c => c.Id == id);
    }

    [Fact]
    public void Un_Centro_sin_motivos_muestra_que_todo_esta_validado()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.Add(Centro("Oficina", EstadoCentro.Vigente));
        mediador.Centros.Add(Centro("Taller", EstadoCentro.Vencido, vencidas: 1));
        Registrar(mediador);

        var filas = Renderizar(id).FindAll("li.fila-relacion");

        filas[0].TextContent.Should().Contain("Todo validado en la plataforma");
        filas[1].TextContent.Should().Contain("1 vencido").And.NotContain("Todo validado en la plataforma");
    }

    [Fact]
    public async Task Copiar_el_CIF_desde_la_cabecera_o_la_informacion_confirma_el_valor_copiado()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar(id);
        var botones = cut.FindComponents<BotonCopiar>().Where(c => c.Instance.Valor == mediador.Detalles[id].Cif).ToList();
        botones.Should().HaveCount(2);

        foreach (var boton in botones)
            await boton.Find("button").ClickAsync(new MouseEventArgs());

        modulo.Invocations["copiarAlPortapapeles"].Should().HaveCount(2)
            .And.OnlyContain(i => (string)i.Arguments[0]! == mediador.Detalles[id].Cif);
        Services.GetRequiredService<ToastService>().Mensajes.Should().HaveCount(2)
            .And.OnlyContain(m => m.Mensaje == $"CIF {mediador.Detalles[id].Cif} copiado al portapapeles");
    }

    [Fact]
    public void El_destino_del_Boton360_de_Subcontrata_tiene_una_ruta_de_pagina_registrada()
    {
        var (id, mediador) = ClienteBase();
        var subcontrataId = Guid.NewGuid();
        mediador.Subcontratas.Add(new(subcontrataId, "Empresa de prueba"));
        Registrar(mediador);

        var cut = Renderizar(id, "?pestana=subcontratas");

        cut.Find("a.boton-360").GetAttribute("href").Should().Be($"/subcontratas/{subcontrataId}");
        typeof(SubcontrataDetalle).GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Should().Contain(r => r.Template == "/subcontratas/{SubcontrataId:guid}");
    }
}