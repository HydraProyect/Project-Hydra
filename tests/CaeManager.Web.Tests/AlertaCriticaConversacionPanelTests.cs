using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.AlertasCriticas;
using CaeManager.Application.Incidencias.Commands.CrearIncidencia;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencia;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Flujo «Alerta crítica» de la maqueta «Flujos 13 Comunicaciones» de punta a punta, sin base de datos ni IA: «Registrar incidencia»
/// crea la Incidencia y confirma la alerta, y el Deshacer del toast elimina la Incidencia.
/// </summary>
public class AlertaCriticaConversacionPanelTests : BunitContext
{
    private static readonly Guid ConversacionId = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid IncidenciaId = Guid.NewGuid();

    private readonly MediatorFalso _mediator = new();
    private readonly ToastService _toasts = new();

    public AlertaCriticaConversacionPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(_mediator);
        Services.AddSingleton(_toasts);
    }

    private sealed class MediatorFalso : IMediator
    {
        public AlertaCriticaDto Alerta { get; set; } = new("Trabajador sin curso de altura en cubierta", EstadoAlertaCritica.Pendiente, true, true, 91);
        public bool ResolverFalla { get; set; }
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object? respuesta = request switch
            {
                ObtenerAlertaCriticaQuery => Alerta,
                ObtenerCentrosParaSelectorQuery => (IReadOnlyList<CentroSelectorDto>)[new CentroSelectorDto(CentroId, "Centro Norte", "Constructora Norte", "Cubiertas Arrieta")],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                CrearIncidenciaCommand => Result.Exito(IncidenciaId),
                EliminarIncidenciaCommand => Result.Exito(),
                ResolverAlertaCriticaCommand resolver => Resolver(resolver),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private Result Resolver(ResolverAlertaCriticaCommand comando)
        {
            if (ResolverFalla) return Result.Fallo(Error.Crear("AlertaCritica.NoPendiente", "La alerta ya está resuelta."));
            Alerta = Alerta with { Estado = comando.ConfirmarYDerivar ? EstadoAlertaCritica.ConfirmadaYDerivada : EstadoAlertaCritica.Descartada, PuedeResolver = false };
            return Result.Exito();
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private IRenderedComponent<AlertaCriticaConversacionPanel> Renderizar() =>
        Render<AlertaCriticaConversacionPanel>(p => p
            .Add(c => c.ConversacionId, ConversacionId)
            .Add(c => c.Asunto, "Obra Centro Norte"));

    private void ElegirCentroYRegistrar(IRenderedComponent<AlertaCriticaConversacionPanel> cut)
    {
        cut.FindAll("select")[0].Change(CentroId.ToString());
        cut.Find(".modal-pie .boton-primario").Click();
    }

    [Fact]
    public void Quien_puede_decidir_encuentra_el_modal_al_entrar()
    {
        var cut = Renderizar();

        cut.Find("h2").TextContent.Trim().Should().Be("Alerta crítica");
        cut.Find(".alerta-critica-modal-opinion").TextContent.Should().Contain("Coincide");
        _mediator.Enviados.OfType<ObtenerCentrosParaSelectorQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Un_Gestor_Cae_ve_el_aviso_pero_no_el_modal()
    {
        _mediator.Alerta = _mediator.Alerta with { PuedeResolver = false };

        var cut = Renderizar();

        cut.FindAll("h2").Should().BeEmpty();
        cut.Markup.Should().Contain("Pendiente de revisión");
        _mediator.Enviados.OfType<ObtenerCentrosParaSelectorQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Registrar_incidencia_crea_la_incidencia_confirma_la_alerta_y_ofrece_deshacer()
    {
        var cut = Renderizar();

        ElegirCentroYRegistrar(cut);

        cut.WaitForAssertion(() => cut.FindAll("h2").Should().BeEmpty());
        var crear = _mediator.Enviados.OfType<CrearIncidenciaCommand>().Should().ContainSingle().Subject;
        crear.CentroId.Should().Be(CentroId);
        crear.Tipo.Should().Be(TipoIncidencia.Incumplimiento);
        crear.Gravedad.Should().Be(GravedadIncidencia.Grave);
        crear.Descripcion.Should().Contain("Obra Centro Norte").And.Contain("sin curso de altura");
        _mediator.Enviados.OfType<ResolverAlertaCriticaCommand>().Should().ContainSingle().Which.ConfirmarYDerivar.Should().BeTrue();
        var toast = _toasts.Mensajes.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Be("Incidencia registrada.");
        toast.TextoAccion.Should().Be("Deshacer");
    }

    [Fact]
    public async Task Deshacer_elimina_la_incidencia_registrada()
    {
        var cut = Renderizar();
        ElegirCentroYRegistrar(cut);
        cut.WaitForAssertion(() => _toasts.Mensajes.Should().ContainSingle());

        await _toasts.Mensajes[0].OnAccion!();

        _mediator.Enviados.OfType<EliminarIncidenciaCommand>().Should().ContainSingle().Which.Id.Should().Be(IncidenciaId);
        _toasts.Mensajes.Should().Contain(m => m.Mensaje.StartsWith("Incidencia eliminada"));
    }

    [Fact]
    public void Si_confirmar_la_alerta_falla_se_retira_la_incidencia_recien_creada_y_el_modal_sigue()
    {
        _mediator.ResolverFalla = true;
        var cut = Renderizar();

        ElegirCentroYRegistrar(cut);

        cut.WaitForAssertion(() => _mediator.Enviados.OfType<EliminarIncidenciaCommand>().Should().ContainSingle());
        cut.Find("h2").TextContent.Trim().Should().Be("Alerta crítica");
        cut.Markup.Should().Contain("No se pudo resolver la alerta");
        _toasts.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void No_es_critica_descarta_la_alerta_sin_crear_incidencia()
    {
        var cut = Renderizar();

        cut.FindAll(".modal-pie button")[0].Click();

        cut.WaitForAssertion(() => cut.FindAll("h2").Should().BeEmpty());
        _mediator.Enviados.OfType<ResolverAlertaCriticaCommand>().Should().ContainSingle().Which.ConfirmarYDerivar.Should().BeFalse();
        _mediator.Enviados.OfType<CrearIncidenciaCommand>().Should().BeEmpty();
    }
}
