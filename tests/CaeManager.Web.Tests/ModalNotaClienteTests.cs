using Bunit;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.Clientes.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ModalNotaClienteTests : BunitContext
{
    private readonly MediadorNota _mediador = new();
    private readonly ClienteDetalleDto _detalle = new(Guid.NewGuid(), "Refrielectric S.A.", "A48220917",
        true, "Llamar por la mañana.", DateTime.UtcNow, null, Guid.NewGuid(), NotaInternaVisible: true);

    public ModalNotaClienteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(_mediador);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public async Task Guarda_la_nota_con_la_identidad_y_version_que_se_leyeron_y_notifica_al_padre()
    {
        var guardados = 0;
        bool? visible = null;
        var cut = Render<ModalNotaCliente>(p => p.Add(x => x.Detalle, _detalle).Add(x => x.Visible, true)
            .Add(x => x.VisibleChanged, v => visible = v).Add(x => x.OnGuardado, () => guardados++));
        cut.Find("textarea").TextContent.Should().Be(_detalle.Notas);
        cut.Markup.Should().Contain("No la ve el Cliente.");
        Guardar(cut).HasAttribute("disabled").Should().BeTrue();

        await cut.Find("textarea").InputAsync(new ChangeEventArgs { Value = "Nueva nota" });
        await Guardar(cut).ClickAsync(new MouseEventArgs());

        _mediador.Enviados.Should().Equal(new EditarClienteCommand(_detalle.Id, _detalle.RazonSocial,
            _detalle.Cif, _detalle.EsCritico, "Nueva nota", _detalle.Version));
        visible.Should().BeFalse();
        guardados.Should().Be(1);
    }

    [Fact]
    public async Task Un_rechazo_conserva_el_borrador_y_muestra_el_motivo_sin_cerrar()
    {
        _mediador.Resultado = Result.Fallo(Error.Crear("Conflicto", "La ficha ha cambiado."));
        var cerrado = false;
        var cut = Render<ModalNotaCliente>(p => p.Add(x => x.Detalle, _detalle).Add(x => x.Visible, true)
            .Add(x => x.VisibleChanged, _ => cerrado = true));

        await cut.Find("textarea").InputAsync(new ChangeEventArgs { Value = "Mi borrador" });
        await Guardar(cut).ClickAsync(new MouseEventArgs());

        cut.Find("[role=alert]").TextContent.Should().Contain("La ficha ha cambiado.");
        cut.FindComponent<CaeManager.Web.Components.DesignSystem.CampoTextarea>().Instance.Valor.Should().Be("Mi borrador");
        cerrado.Should().BeFalse();
    }

    [Fact]
    public async Task Vaciar_la_nota_envia_null()
    {
        var cut = Render<ModalNotaCliente>(p => p.Add(x => x.Detalle, _detalle).Add(x => x.Visible, true));
        await cut.Find("textarea").InputAsync(new ChangeEventArgs { Value = "  " });
        await Guardar(cut).ClickAsync(new MouseEventArgs());
        _mediador.Enviados.Should().ContainSingle().Which.Notas.Should().BeNull();
    }

    private static AngleSharp.Dom.IElement Guardar(IRenderedComponent<ModalNotaCliente> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar");

    private sealed class MediadorNota : IMediator
    {
        public Result Resultado { get; set; } = Result.Exito();
        public List<EditarClienteCommand> Enviados { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add((EditarClienteCommand)(object)request);
            return Task.FromResult((TResponse)(object)Resultado);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
