using Bunit;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de la pantalla con la reclamación en lote a medias pregunta antes. Eligiendo
/// el filtro, cuenta lo que difiere del de partida (lo preseleccionado desde la cola no); en la
/// revisión, los documentos o contactos desmarcados respecto de cómo se resolvieron.
/// </summary>
public class DrawerReclamacionLoteAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid TrabajadorId = Guid.NewGuid();
    private static readonly Guid ClienteTitularId = Guid.NewGuid();

    public DrawerReclamacionLoteAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediatorFalso(_cargaDeTipos.Task));
        Services.AddScoped<ToastService>();
    }

    /// <summary>La carga de tipos del selector: completa salvo en la prueba que la retiene.</summary>
    private TaskCompletionSource _cargaDeTipos = CargaCompletada();

    private static TaskCompletionSource CargaCompletada()
    {
        var carga = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        carga.SetResult();
        return carga;
    }

    private sealed class MediatorFalso(Task cargaDeTipos) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerTiposDocumentoQuery)
                await cargaDeTipos;
            return Responder(request);
        }

        private static TResponse Responder<TResponse>(IRequest<TResponse> request) =>
            (TResponse)(object)(request switch
            {
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new(TrabajadorId, "Ana Ruiz", null, null)],
                ObtenerLoteReclamacionPorFiltroQuery => (IReadOnlyList<LoteReclamacionAgrupadoDto>)
                [
                    new(ClienteTitularId, "Refrielectric S.A.", AmbitoAplicacion.Trabajador, null,
                        [new(Guid.NewGuid(), TrabajadorId, "Ana Ruiz", Guid.NewGuid(), "Formación PRL", new DateOnly(2026, 1, 31), EstadoDocumento.Vencido)],
                        null,
                        [new DestinatarioAgendaDto(Guid.NewGuid(), "Marta Gil", "marta@example.invalid", ["Formación PRL"])]),
                ],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });

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

    private (IRenderedComponent<DrawerReclamacionLote> Cut, NavigationManager Navegacion) Renderizar(Guid? entidadIdInicial = null)
    {
        var cut = Render<DrawerReclamacionLote>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AmbitosDisponibles, [AmbitoAplicacion.Trabajador])
            .Add(x => x.AmbitoInicial, AmbitoAplicacion.Trabajador)
            .Add(x => x.EntidadIdInicial, entidadIdInicial));
        cut.WaitForAssertion(() => cut.FindAll(".selector-lote-documental input[type=checkbox]").Should().NotBeEmpty());
        return (cut, Services.GetRequiredService<NavigationManager>());
    }

    private static Task ContinuarAsync(IRenderedComponent<DrawerReclamacionLote> cut) =>
        cut.FindAll(".selector-lote-acciones button").Single(b => b.TextContent.Trim() == "Continuar").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Cambiar_el_filtro_y_salir_pregunta()
    {
        var (cut, navegacion) = Renderizar();

        await cut.Find(".selector-lote-documental input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Lo_preseleccionado_desde_la_cola_no_es_un_cambio()
    {
        var (cut, navegacion) = Renderizar(entidadIdInicial: TrabajadorId);
        cut.FindAll(".selector-lote-alcance input[type=radio]")[1].HasAttribute("checked").Should().BeTrue(
            "el test necesita que la trabajadora llegue preseleccionada desde la cola");

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "abrir el lote sin tocar el filtro no deja nada que perder");
    }

    [Fact]
    public async Task Revisar_sin_tocar_nada_no_pregunta_y_desmarcar_un_documento_si()
    {
        var (cut, navegacion) = Renderizar();
        await ContinuarAsync(cut);
        cut.WaitForAssertion(() => cut.FindAll(".tabla-datos input[type=checkbox]").Should().ContainSingle());

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el lote tal como se resolvió no es un cambio");

        await cut.Find(".tabla-datos input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Desmarcar_un_contacto_y_cerrar_con_la_X_pregunta_antes()
    {
        var (cut, _) = Renderizar();
        await ContinuarAsync(cut);
        cut.WaitForAssertion(() => cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().ContainSingle());

        await cut.Find(".reclamacion-destinatarios input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find(".drawer-panel button.drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    /// <summary>
    /// Revisión Codex (lote C): lo elegido mientras cargan los tipos de documento es un cambio;
    /// la instantánea del filtro de partida no puede tomarse después de esa carga.
    /// </summary>
    [Fact]
    public async Task Lo_elegido_mientras_carga_el_filtro_es_un_cambio()
    {
        _cargaDeTipos = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<DrawerReclamacionLote>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AmbitosDisponibles, [AmbitoAplicacion.Trabajador])
            .Add(x => x.AmbitoInicial, AmbitoAplicacion.Trabajador));
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.FindAll(".selector-lote-documental input[type=checkbox]").Should().BeEmpty("el test necesita la carga de tipos en vuelo");

        await cut.FindAll(".selector-lote-alcance input[type=radio]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        _cargaDeTipos.SetResult();
        cut.WaitForAssertion(() => cut.FindAll(".selector-lote-documental input[type=checkbox]").Should().NotBeEmpty());

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    /// <summary>
    /// Revisión Codex (lote C): el aviso del navegador al recargar o cerrar la pestaña se
    /// calcula en el render del drawer; cambiar el filtro dentro del selector tiene que
    /// re-renderizarlo.
    /// </summary>
    [Fact]
    public async Task Cambiar_el_filtro_arma_el_aviso_del_navegador()
    {
        var (cut, _) = Renderizar();
        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeFalse();

        await cut.Find(".selector-lote-documental input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeTrue(
            "recargar o cerrar la pestaña con el filtro cambiado tiene que avisar");
    }
}
