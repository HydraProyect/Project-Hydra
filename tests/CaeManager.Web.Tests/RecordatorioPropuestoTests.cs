using Bunit;
using CaeManager.Application.Comunicaciones.Commands.AprobarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Commands.DescartarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Commands.RestaurarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El recordatorio propuesto a los dos días laborables de espera de respuesta externa (maqueta Flujos 13, «Recordatorio
/// propuesto»): una fila «Propuesto» con «Revisar» que abre el drawer con Para, Motivo, Asunto y el texto editable; nada sale
/// hasta «Aprobar y enviar», el error se ve dentro del drawer y «Descartar» se puede deshacer desde el aviso.
/// </summary>
public class RecordatorioPropuestoTests : BunitContext
{
    private readonly MediatorDeRecordatorio _mediator = new();
    private readonly Guid _conversacionId = Guid.NewGuid();
    private ToastService Toasts => Services.GetRequiredService<ToastService>();

    public RecordatorioPropuestoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging().AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
    }

    private IRenderedComponent<RecordatorioPropuesto> Renderizar(Action? alEnviar = null, string? destinatario = "cae@mnorte.example") =>
        Render<RecordatorioPropuesto>(p => p
            .Add(c => c.ConversacionId, _conversacionId)
            .Add(c => c.Asunto, "Documentación de octubre")
            .Add(c => c.Destinatario, destinatario)
            .Add(c => c.Estado, EstadoConversacion.EsperandoRespuestaExterna)
            .Add(c => c.OnEnviado, () => alEnviar?.Invoke()));

    private RecordatorioPropuestoDto Proponer()
    {
        _mediator.Propuesta = new RecordatorioPropuestoDto(
            Guid.NewGuid(), new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc));
        return _mediator.Propuesta;
    }

    private static void Revisar(IRenderedComponent<RecordatorioPropuesto> componente) =>
        componente.FindAll("button").Single(b => b.TextContent.Trim() == "Revisar").Click();

    private static void Pulsar(IRenderedComponent<RecordatorioPropuesto> componente, string texto) =>
        componente.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == texto).Click();

    [Fact]
    public void Sin_propuesta_no_pinta_nada()
    {
        var componente = Renderizar();

        componente.FindAll(".recordatorio-propuesto").Should().BeEmpty();
        componente.FindAll(".drawer-pie").Should().BeEmpty("sin propuesta no hay drawer que abrir");
        _mediator.Enviados.Should().BeEmpty("sin propuesta no se aprueba ni se descarta nada");
    }

    [Fact]
    public void Con_propuesta_pinta_la_fila_Propuesto_y_no_envia_nada_hasta_aprobar()
    {
        Proponer();

        var componente = Renderizar();

        var fila = componente.Find(".recordatorio-propuesto");
        fila.TextContent.Should().Contain("Recordatorio · Documentación de octubre").And.Contain("Propuesto")
            .And.Contain("A cae@mnorte.example").And.Contain("16/10/2026");
        componente.FindAll(".drawer-pie").Should().BeEmpty("el drawer se abre con «Revisar»");
        _mediator.Enviados.Should().BeEmpty("nada se envía solo");
    }

    [Fact]
    public void Revisar_abre_el_drawer_con_Para_Motivo_Asunto_el_borrador_y_el_aviso()
    {
        Proponer();
        var componente = Renderizar();

        Revisar(componente);

        componente.Markup.Should().Contain("Propuesto por TALVEG el 20/10/2026 07:00", "hora peninsular, no la del servidor");
        componente.Markup.Should().Contain("cae@mnorte.example").And.Contain("Sin respuesta desde el 16/10/2026")
            .And.Contain("RE: Documentación de octubre");
        componente.Find("textarea").TextContent.Should().Contain("Documentación de octubre");
        componente.Find(".recordatorio-propuesto-aviso").TextContent.Should().Be("Al aprobar se envía. No se puede deshacer.");
        componente.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).Should()
            .Equal(["Descartar", "Cancelar", "Aprobar y enviar"], "maqueta: Descartar a la izquierda, lejos de la primaria");
        _mediator.Enviados.Should().BeEmpty();
    }

    [Fact]
    public void Aprobar_envia_el_texto_editado_cierra_y_avisa_con_el_destinatario()
    {
        var propuesta = Proponer();
        var avisada = false;
        var componente = Renderizar(() => avisada = true);
        Revisar(componente);

        componente.Find("textarea").Input("Hola, ¿pudisteis revisarlo?");
        Pulsar(componente, "Aprobar y enviar");

        var aprobacion = _mediator.Enviados.OfType<AprobarRecordatorioConversacionCommand>().Should().ContainSingle().Subject;
        aprobacion.ConversacionId.Should().Be(_conversacionId);
        aprobacion.PropuestaId.Should().Be(propuesta.PropuestaId);
        aprobacion.CuerpoHtml.Should().Be("Hola, ¿pudisteis revisarlo?");
        avisada.Should().BeTrue("la página recarga el hilo con el recordatorio ya enviado");
        componente.FindAll(".recordatorio-propuesto").Should().BeEmpty();
        componente.FindAll(".drawer-pie").Should().BeEmpty();
        Toasts.Mensajes.Should().ContainSingle(t => t.Mensaje == "Recordatorio enviado a cae@mnorte.example." && t.TextoAccion == null,
            "lo enviado no tiene Deshacer");
    }

    [Fact]
    public void Si_el_envio_falla_el_error_se_ve_en_el_drawer_y_la_propuesta_sigue()
    {
        Proponer();
        _mediator.ResultadoAprobar = Result.Fallo(Error.Crear("Conversacion.ConexionNoDisponible", "El buzón no está disponible."));
        var componente = Renderizar();
        Revisar(componente);

        Pulsar(componente, "Aprobar y enviar");

        componente.Markup.Should().Contain("No se ha enviado. El recordatorio sigue propuesto.").And.Contain("El buzón no está disponible.");
        componente.FindAll(".drawer-pie").Should().NotBeEmpty("sigue abierto para reintentar");
        componente.FindAll(".recordatorio-propuesto").Should().NotBeEmpty();
    }

    [Fact]
    public void Sin_texto_no_se_puede_aprobar_y_dice_por_que()
    {
        Proponer();
        var componente = Renderizar();
        Revisar(componente);

        componente.Find("textarea").Input("   ");

        var aprobar = componente.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Aprobar y enviar");
        aprobar.HasAttribute("disabled").Should().BeTrue();
        aprobar.GetAttribute("title").Should().Be("Escribe el texto del recordatorio.");
    }

    [Fact]
    public void Descartar_no_envia_nada_y_Deshacer_lo_devuelve()
    {
        var propuesta = Proponer();
        var componente = Renderizar();
        Revisar(componente);

        Pulsar(componente, "Descartar");

        _mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<DescartarRecordatorioConversacionCommand>()
            .Which.PropuestaId.Should().Be(propuesta.PropuestaId);
        componente.FindAll(".recordatorio-propuesto").Should().BeEmpty();
        var aviso = Toasts.Mensajes.Should().ContainSingle(t => t.Mensaje == "Recordatorio descartado.").Subject;
        aviso.TextoAccion.Should().Be("Deshacer");

        componente.InvokeAsync(() => aviso.OnAccion!()).GetAwaiter().GetResult();

        _mediator.Enviados.OfType<RestaurarRecordatorioConversacionCommand>().Should().ContainSingle()
            .Which.Should().Be(new RestaurarRecordatorioConversacionCommand(_conversacionId, propuesta.PropuestaId));
        componente.WaitForAssertion(() => componente.FindAll(".recordatorio-propuesto").Should().NotBeEmpty(
            "Deshacer la devuelve a propuesta"));
    }

    private sealed class MediatorDeRecordatorio : IMediator
    {
        public RecordatorioPropuestoDto? Propuesta { get; set; }
        public Result ResultadoAprobar { get; set; } = Result.Exito();
        public List<object> Enviados { get; } = [];
        private RecordatorioPropuestoDto? _resuelta;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerRecordatorioPropuestoConversacionQuery)
                return Task.FromResult((TResponse)(object?)Propuesta!);

            Enviados.Add(request);
            var resultado = request is AprobarRecordatorioConversacionCommand ? ResultadoAprobar : Result.Exito();
            if (resultado.EsExitoso && request is AprobarRecordatorioConversacionCommand or DescartarRecordatorioConversacionCommand)
            {
                _resuelta = Propuesta;
                Propuesta = null;
            }
            if (request is RestaurarRecordatorioConversacionCommand)
                Propuesta = _resuelta;
            return Task.FromResult((TResponse)(object)resultado);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
