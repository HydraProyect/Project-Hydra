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
    public async Task Sin_identificacion_fiscal_muestra_error_y_no_envia(bool clienteEmpresarial)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var mediador = new MediadorVigilado();
        Services.AddSingleton<IMediator>(mediador);
        Services.AddLocalization();
        IRenderedComponent<IComponent> cut = clienteEmpresarial
            ? Render<FormularioRapidoCliente>(p => p.Add(x => x.Visible, true).Add(x => x.NombreInicial, "Empresa de prueba"))
            : Render<FormularioRapidoEmpresa>(p => p.Add(x => x.Visible, true).Add(x => x.NombreInicial, "Empresa de prueba"));
        var fiscal = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Placeholder == "CIF, DNI o NIE");
        await Renderer.Dispatcher.InvokeAsync(() => fiscal.Instance.ValorChanged.InvokeAsync(""));
        await cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Crear", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        cut.Find("input[aria-required='true']").Should().NotBeNull();
        cut.FindAll(".campo-mensaje-error").Should().Contain(e => e.TextContent == "La identificación fiscal es obligatoria.");
        mediador.Envios.Should().Be(0);
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
