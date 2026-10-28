using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using CaeManager.Web.Features.Clientes.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class IdentificacionFiscalFormulariosRapidosTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identificacion_fiscal_invalida_muestra_error_y_no_envia(bool clienteEmpresarial)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var mediador = new MediadorVigilado();
        Services.AddSingleton<IMediator>(mediador);
        Services.AddLocalization();
        IRenderedComponent<IComponent> cut = clienteEmpresarial
            ? Render<FormularioRapidoCliente>(p => p.Add(x => x.Visible, true).Add(x => x.NombreInicial, "Empresa de prueba"))
            : Render<FormularioRapidoEmpresa>(p => p.Add(x => x.Visible, true).Add(x => x.NombreInicial, "Empresa de prueba"));
        var fiscal = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Placeholder == "CIF, DNI o NIE");
        await Renderer.Dispatcher.InvokeAsync(() => fiscal.Instance.ValorChanged.InvokeAsync("invalido"));
        await cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Crear", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        cut.Find("input[aria-required='true']").Should().NotBeNull();
        cut.FindAll(".campo-mensaje-error").Should().Contain(e => e.TextContent.Contains("no es válida"));
        mediador.Envios.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campos_obligatorios_vacios_deshabilitan_guardado_y_cierre_con_cambios_pregunta(bool clienteEmpresarial)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var mediador = new MediadorVigilado();
        Services.AddSingleton<IMediator>(mediador);
        Services.AddLocalization();
        var visible = true;
        IRenderedComponent<IComponent> cut = clienteEmpresarial
            ? Render<FormularioRapidoCliente>(p => p.Add(x => x.Visible, true).Add(x => x.VisibleChanged, v => visible = v))
            : Render<FormularioRapidoEmpresa>(p => p.Add(x => x.Visible, true).Add(x => x.VisibleChanged, v => visible = v));

        var guardar = cut.Find(".modal-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue();
        guardar.GetAttribute("title").Should().NotBeNullOrWhiteSpace();
        var razonSocial = cut.FindComponents<CampoTexto>().First();
        await Renderer.Dispatcher.InvokeAsync(() => razonSocial.Instance.ValorChanged.InvokeAsync("Empresa de prueba"));
        cut.Find(".modal-pie .boton-primario").HasAttribute("disabled").Should().BeTrue();
        await cut.Find(".modal-cerrar").ClickAsync(new MouseEventArgs());

        visible.Should().BeTrue();
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        mediador.Envios.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guardar_se_habilita_solo_con_razon_social_e_identificacion_fiscal(bool clienteEmpresarial)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IMediator>(new MediadorVigilado());
        Services.AddLocalization();
        IRenderedComponent<IComponent> cut = clienteEmpresarial
            ? Render<FormularioRapidoCliente>(p => p.Add(x => x.Visible, true))
            : Render<FormularioRapidoEmpresa>(p => p.Add(x => x.Visible, true));
        var campos = cut.FindComponents<CampoTexto>();

        await Renderer.Dispatcher.InvokeAsync(() => campos[1].Instance.ValorChanged.InvokeAsync("B12345674"));
        var guardar = cut.Find(".modal-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue();
        guardar.GetAttribute("title").Should().Be("Completa los campos obligatorios.");

        await Renderer.Dispatcher.InvokeAsync(() => campos[0].Instance.ValorChanged.InvokeAsync("Empresa de prueba"));
        cut.Find(".modal-pie .boton-primario").HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_error_de_identificacion_fiscal_desaparece_al_corregir_el_campo(bool clienteEmpresarial)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IMediator>(new MediadorVigilado());
        Services.AddLocalization();
        IRenderedComponent<IComponent> cut = clienteEmpresarial
            ? Render<FormularioRapidoCliente>(p => p.Add(x => x.Visible, true).Add(x => x.NombreInicial, "Empresa de prueba"))
            : Render<FormularioRapidoEmpresa>(p => p.Add(x => x.Visible, true).Add(x => x.NombreInicial, "Empresa de prueba"));
        var fiscal = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Placeholder == "CIF, DNI o NIE");
        await Renderer.Dispatcher.InvokeAsync(() => fiscal.Instance.ValorChanged.InvokeAsync("invalido"));
        await cut.Find(".modal-pie .boton-primario").ClickAsync(new MouseEventArgs());
        cut.FindAll(".campo-mensaje-error").Should().Contain(e => e.TextContent.Contains("no es válida"));

        await Renderer.Dispatcher.InvokeAsync(() => fiscal.Instance.ValorChanged.InvokeAsync("B1234567"));

        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
    }

    private sealed class MediadorVigilado : IMediator
    {
        public int Envios { get; private set; }
        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Envios++;
            throw new InvalidOperationException("El formulario no debe enviar un comando inválido.");
        }
        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
