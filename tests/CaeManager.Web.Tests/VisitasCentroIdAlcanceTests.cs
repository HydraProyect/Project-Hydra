using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El <c>centroId</c> que llega por query string a <see cref="Visitas"/> —desde
/// "Programar visita" de Centro 360, o como corrección del Action Center sobre
/// una <c>SugerenciaVisitaCorreo</c>— es una coordenada de contexto, no
/// autoridad: antes de preseleccionarlo o de que sustituya un CentroId ya
/// resuelto con alcance, tiene que estar en <c>_centrosDisponibles</c> (la
/// misma lista, ya acotada, que carga <c>PrepararCrearAsync</c>). Hasta este
/// fix, ambos caminos lo aceptaban sin comprobar nada: un centro ajeno o
/// inexistente en la URL se preseleccionaba igual, y en el camino de la
/// sugerencia además podía pisar el CentroId que <c>ObtenerSugerenciaVisitaCorreoQuery</c>
/// ya había resuelto con alcance.
/// </summary>
public class VisitasCentroIdAlcanceTests : BunitContext
{
    public VisitasCentroIdAlcanceTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorDeAlcance : IMediator
    {
        public IReadOnlyList<CentroSelectorDto> CentrosDisponibles { get; init; } = [];
        public SugerenciaVisitaPrefillDto? Sugerencia { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerCentrosParaSelectorQuery => CentrosDisponibles,
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerSugerenciaVisitaCorreoQuery => Sugerencia,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
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

    /// <summary>
    /// Navega a la ruta con el query string dado y renderiza — los filtros
    /// <c>[SupplyParameterFromQuery]</c> se leen de la URI actual del
    /// <see cref="NavigationManager"/> de prueba, igual que
    /// <c>AltaGuiadaResolucionIdentificadoresTests</c>.
    /// </summary>
    private IRenderedComponent<Visitas> RenderizarConQuery(string query, MediatorDeAlcance mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(string.IsNullOrEmpty(query) ? "visitas" : $"visitas?{query}");

        return Render<Visitas>();
    }

    private static CentroSelectorDto Centro(Guid id) => new(id, "Centro Norte", "Iberojet S.A.", "Instalaciones Arbeko S.L.");

    [Fact]
    public void Un_centroId_ajeno_desde_centro_360_no_preselecciona_nada()
    {
        var centroPropio = Guid.NewGuid();
        var centroIdAjeno = Guid.NewGuid();
        var mediator = new MediatorDeAlcance { CentrosDisponibles = [Centro(centroPropio)] };

        var cut = RenderizarConQuery($"centroId={centroIdAjeno}", mediator);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nueva visita"));
        cut.Find(".drawer-panel select").GetAttribute("value").Should().BeNullOrEmpty(
            "un centroId que no está en el catálogo cargado con alcance no puede preseleccionar nada, ajeno o no");
    }

    [Fact]
    public void Un_centroId_valido_desde_centro_360_si_preselecciona()
    {
        var centroPropio = Guid.NewGuid();
        var mediator = new MediatorDeAlcance { CentrosDisponibles = [Centro(centroPropio)] };

        var cut = RenderizarConQuery($"centroId={centroPropio}", mediator);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nueva visita"));
        cut.Find(".drawer-panel select").GetAttribute("value").Should().Be(centroPropio.ToString(),
            "un centroId que sí está en el catálogo cargado con alcance preselecciona con normalidad — la comprobación no rompe el camino válido");
    }

    [Fact]
    public void Un_override_ajeno_desde_el_action_center_no_sustituye_el_centroId_ya_resuelto_de_la_sugerencia()
    {
        var centroDeLaSugerencia = Guid.NewGuid();
        var centroIdAjeno = Guid.NewGuid();
        var sugerenciaId = Guid.NewGuid();
        var mediator = new MediatorDeAlcance
        {
            CentrosDisponibles = [Centro(centroDeLaSugerencia)],
            Sugerencia = new SugerenciaVisitaPrefillDto(sugerenciaId, centroDeLaSugerencia, null, null, "Visita detectada en correo"),
        };

        var cut = RenderizarConQuery($"sugerenciaId={sugerenciaId}&centroId={centroIdAjeno}", mediator);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nueva visita"));
        cut.Find(".drawer-panel select").GetAttribute("value").Should().Be(centroDeLaSugerencia.ToString(),
            "el CentroId ya resuelto con alcance por ObtenerSugerenciaVisitaCorreoQuery no puede ser sustituido por un override sin validar");
    }

    [Fact]
    public void Un_override_valido_desde_el_action_center_si_sustituye_el_centroId_de_la_sugerencia()
    {
        var centroDeLaSugerencia = Guid.NewGuid();
        var centroCorregido = Guid.NewGuid();
        var sugerenciaId = Guid.NewGuid();
        var mediator = new MediatorDeAlcance
        {
            CentrosDisponibles = [Centro(centroDeLaSugerencia), Centro(centroCorregido)],
            Sugerencia = new SugerenciaVisitaPrefillDto(sugerenciaId, centroDeLaSugerencia, null, null, "Visita detectada en correo"),
        };

        var cut = RenderizarConQuery($"sugerenciaId={sugerenciaId}&centroId={centroCorregido}", mediator);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nueva visita"));
        cut.Find(".drawer-panel select").GetAttribute("value").Should().Be(centroCorregido.ToString(),
            "un override que sí pertenece al catálogo cargado con alcance sigue prevaleciendo sobre la sugerencia — la corrección del Gestor en el Action Center no se pierde");
    }
}
