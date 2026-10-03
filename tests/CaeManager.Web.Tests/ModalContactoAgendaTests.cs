using Bunit;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// S12 (lote 3b): el contacto de agenda es un <c>ModalFormulario</c>. Lo que cambia respecto al Modal anterior y debe quedar fijado: «Cancelar» cierra
/// como la X (pregunta si hay datos escritos), el error del servidor sale en el aviso fijo del modal y no en un toast, los errores de campo los
/// pintan los campos y el aviso de un intento anterior se va al corregir.
/// </summary>
public sealed class ModalContactoAgendaTests : BunitContext
{
    private readonly Mediador _mediador = new();
    private bool _visible = true;

    public ModalContactoAgendaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
    }

    private IRenderedComponent<ModalContactoAgenda> Renderizar() =>
        Render<ModalContactoAgenda>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid()));

    private static IReadOnlyList<string> Alertas(IRenderedComponent<ModalContactoAgenda> cut) =>
        cut.FindAll("[role=dialog] [role=alert]").Select(a => a.TextContent.Trim()).ToList();

    private static async Task Escribir(IRenderedComponent<ModalContactoAgenda> cut, string etiqueta, string valor) =>
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = valor });

    private static Task Pulsar(IRenderedComponent<ModalContactoAgenda> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Cancelar_sin_datos_cierra_y_con_datos_pregunta_como_la_X()
    {
        var cut = Renderizar();
        await Pulsar(cut, "Cancelar");
        _visible.Should().BeFalse("sin nada escrito «Cancelar» cierra sin preguntar");

        _visible = true;
        cut = Renderizar();
        await Escribir(cut, "Nombre", "Nuria Salas");
        await Pulsar(cut, "Cancelar");

        _visible.Should().BeTrue("con datos escritos no cierra de golpe");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios");
    }

    [Fact]
    public async Task Nombre_y_email_vacios_los_pintan_los_campos_y_no_se_envia_nada()
    {
        var cut = Renderizar();

        await Pulsar(cut, "Guardar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().BeEmpty();
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.MensajeError.Should().Be("El nombre es obligatorio.");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Email").Instance.MensajeError.Should().NotBeNullOrWhiteSpace();

        await Escribir(cut, "Nombre", "Nuria");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Instance.MensajeError
            .Should().BeNull("el error era del intento anterior");
    }

    [Fact]
    public async Task El_rechazo_del_servidor_sale_en_el_aviso_fijo_no_en_un_toast_y_escribir_lo_quita()
    {
        _mediador.FallaGuardarCon = "Ya existe un contacto con ese email.";
        var cut = Renderizar();
        await Escribir(cut, "Nombre", "Nuria Salas");
        await Escribir(cut, "Email", "nuria@example.test");
        Alertas(cut).Should().BeEmpty("control positivo: antes del intento no hay aviso");

        await Pulsar(cut, "Guardar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().ContainSingle("control positivo: se intentó");
        Alertas(cut).Should().Equal("Ya existe un contacto con ese email.");
        cut.FindAll("[role=dialog] .modal-cuerpo [role=alert]").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el motivo ya no es un toast que desaparece");
        _visible.Should().BeTrue("un rechazo no cierra el modal");

        await Escribir(cut, "Nombre", "Nuria Salas Ortiz");
        _mediador.FallaGuardarCon = null;
        await Pulsar(cut, "Guardar");

        _mediador.Enviadas.OfType<GuardarContactoAgendaCommand>().Should().HaveCount(2);
        _visible.Should().BeFalse("guardado correctamente cierra");
    }

    private sealed class Mediador : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public string? FallaGuardarCon { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object valor = request switch
            {
                GuardarContactoAgendaCommand when FallaGuardarCon is { } motivo => Result.Fallo<Guid>(Error.Crear("contacto.rechazado", motivo)),
                GuardarContactoAgendaCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)valor);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
