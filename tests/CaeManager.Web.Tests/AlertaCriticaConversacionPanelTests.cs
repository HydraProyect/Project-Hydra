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
/// crea la Incidencia y confirma la alerta, y el Deshacer del toast elimina la Incidencia y devuelve la alerta a Pendiente.
/// </summary>
public class AlertaCriticaConversacionPanelTests : BunitContext
{
    private static readonly Guid ConversacionId = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid IncidenciaId = Guid.NewGuid();
    private static readonly Guid AlertaId = Guid.NewGuid();

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
        public AlertaCriticaDto Alerta { get; set; } = new(AlertaId, "Trabajador sin curso de altura en cubierta", EstadoAlertaCritica.Pendiente, true, true, 91);
        public bool ResolverFalla { get; set; }
        public bool ResolverLanza { get; set; }
        public bool EliminarFalla { get; set; }

        /// <summary>Otra alerta llegó Pendiente a la conversación después de derivar: el Deshacer no cambia nada.</summary>
        public bool OtraPendiente { get; set; }

        /// <summary>Otra persona resolvió la alerta entre tanto: Resolver falla y la alerta ya no está pendiente.</summary>
        public bool OtraPersonaResolvio { get; set; }
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
                EliminarIncidenciaCommand => EliminarFalla ? Result.Fallo(Error.Crear("Incidencia.NoEncontrada", "No encontramos esta incidencia.")) : Result.Exito(),
                ResolverAlertaCriticaCommand resolver => Resolver(resolver),
                DeshacerDerivacionAlertaCriticaCommand => Deshacer(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private Result Resolver(ResolverAlertaCriticaCommand comando)
        {
            if (ResolverLanza) throw new InvalidOperationException("Fallo de base de datos simulado.");
            if (OtraPersonaResolvio)
            {
                Alerta = Alerta with { Estado = EstadoAlertaCritica.Descartada, PuedeResolver = false };
                return Result.Fallo(Error.Crear("AlertaCritica.NoPendiente", "La alerta ya está resuelta."));
            }

            if (ResolverFalla) return Result.Fallo(Error.Crear("AlertaCritica.NoPendiente", "La alerta ya está resuelta."));
            Alerta = Alerta with { Estado = comando.ConfirmarYDerivar ? EstadoAlertaCritica.ConfirmadaYDerivada : EstadoAlertaCritica.Descartada, PuedeResolver = false };
            return Result.Exito();
        }

        private Result Deshacer()
        {
            if (OtraPendiente) return Result.Fallo(Error.Crear("AlertaCritica.OtraPendiente", "Hay otra alerta pendiente en esta conversación."));
            Alerta = Alerta with { Estado = EstadoAlertaCritica.Pendiente, PuedeResolver = true };
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
        var resolver = _mediator.Enviados.OfType<ResolverAlertaCriticaCommand>().Should().ContainSingle().Subject;
        resolver.ConfirmarYDerivar.Should().BeTrue();
        resolver.AlertaId.Should().Be(AlertaId);
        resolver.IncidenciaId.Should().Be(IncidenciaId);
        var toast = _toasts.Mensajes.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Be("Incidencia registrada.");
        toast.TextoAccion.Should().Be("Deshacer");
    }

    [Fact]
    public async Task Deshacer_elimina_la_incidencia_registrada_y_devuelve_la_alerta_a_Pendiente()
    {
        var cut = Renderizar();
        ElegirCentroYRegistrar(cut);
        cut.WaitForAssertion(() => _toasts.Mensajes.Should().ContainSingle());

        await cut.InvokeAsync(() => _toasts.Mensajes[0].OnAccion!());

        // Una sola orden: el servidor elimina la Incidencia y devuelve la alerta a Pendiente en la misma transacción.
        var deshacer = _mediator.Enviados.OfType<DeshacerDerivacionAlertaCriticaCommand>().Should().ContainSingle().Subject;
        deshacer.Should().Be(new DeshacerDerivacionAlertaCriticaCommand(ConversacionId, AlertaId, IncidenciaId));
        _mediator.Enviados.OfType<EliminarIncidenciaCommand>().Should().BeEmpty();
        _mediator.Alerta.Estado.Should().Be(EstadoAlertaCritica.Pendiente);
        _toasts.Mensajes.Should().Contain(m => m.Mensaje == "Incidencia eliminada. La alerta vuelve a estar pendiente.");
        // La alerta vuelve a Pendiente y el modal reaparece para decidir de nuevo.
        cut.WaitForAssertion(() => cut.Find("h2").TextContent.Trim().Should().Be("Alerta crítica"));
    }

    [Fact]
    public async Task Si_llego_otra_alerta_pendiente_el_deshacer_lo_dice_y_no_reabre_el_modal()
    {
        _mediator.OtraPendiente = true;
        var cut = Renderizar();
        ElegirCentroYRegistrar(cut);
        cut.WaitForAssertion(() => _toasts.Mensajes.Should().ContainSingle());

        await cut.InvokeAsync(() => _toasts.Mensajes[0].OnAccion!());

        _toasts.Mensajes.Should().Contain(m => m.Mensaje.StartsWith("No se pudo deshacer: ya hay otra alerta pendiente"));
        _mediator.Alerta.Estado.Should().Be(EstadoAlertaCritica.ConfirmadaYDerivada);
        cut.FindAll("h2").Should().BeEmpty();
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
    public void Si_confirmar_la_alerta_lanza_una_excepcion_tambien_se_retira_la_incidencia()
    {
        _mediator.ResolverLanza = true;
        var cut = Renderizar();

        ElegirCentroYRegistrar(cut);

        cut.WaitForAssertion(() => _mediator.Enviados.OfType<EliminarIncidenciaCommand>().Should().ContainSingle());
        cut.Markup.Should().Contain("No se pudo resolver la alerta");
        _toasts.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Si_la_incidencia_no_se_puede_retirar_el_aviso_lo_dice()
    {
        _mediator.ResolverFalla = true;
        _mediator.EliminarFalla = true;
        var cut = Renderizar();

        ElegirCentroYRegistrar(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("no se pudo retirar").And.Contain("Revísala en Incidencias"));
    }

    [Fact]
    public void Si_otra_persona_ya_resolvio_la_alerta_el_error_sale_en_un_toast_porque_el_modal_desaparece()
    {
        _mediator.OtraPersonaResolvio = true;
        var cut = Renderizar();

        ElegirCentroYRegistrar(cut);

        cut.WaitForAssertion(() => cut.FindAll("h2").Should().BeEmpty());
        _toasts.Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Error);
        _toasts.Mensajes[0].Mensaje.Should().Contain("No se pudo resolver la alerta");
    }

    [Fact]
    public void La_descripcion_no_pasa_de_2000_caracteres_ni_parte_un_par_sustituto()
    {
        const string prefijo = "Alerta crítica detectada en la conversación «Obra Centro Norte»: ";
        // El primer sustituto del emoji cae justo en el carácter 2000: cortar ahí dejaría un sustituto huérfano.
        _mediator.Alerta = _mediator.Alerta with { Texto = new string('a', Incidencia.LongitudMaximaDescripcion - 1 - prefijo.Length) + "😀zzz" };
        var cut = Renderizar();

        ElegirCentroYRegistrar(cut);

        cut.WaitForAssertion(() => _mediator.Enviados.OfType<CrearIncidenciaCommand>().Should().ContainSingle());
        var descripcion = _mediator.Enviados.OfType<CrearIncidenciaCommand>().Single().Descripcion;
        descripcion.Length.Should().BeLessThanOrEqualTo(Incidencia.LongitudMaximaDescripcion);
        char.IsHighSurrogate(descripcion[^1]).Should().BeFalse();
    }

    [Fact]
    public void Refrescarse_el_asunto_no_desmonta_el_modal_ni_borra_el_Centro_elegido()
    {
        var cut = Renderizar();
        cut.FindAll("select")[0].Change(CentroId.ToString());

        cut.Render(p => p
            .Add(c => c.ConversacionId, ConversacionId)
            .Add(c => c.Asunto, "Obra Centro Norte (2)"));

        cut.FindAll("select")[0].GetAttribute("value").Should().Be(CentroId.ToString());
        _mediator.Enviados.OfType<ObtenerAlertaCriticaQuery>().Should().ContainSingle();
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
