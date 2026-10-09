using Bunit;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Defecto L1 del piloto Outbound (2026-10-08). «Pedir» sobre una fila de Mi trabajo abre el lote de
/// reclamación con la trabajadora o el trabajador ya elegido: la opción «en concreto» salía marcada
/// pero el campo, vacío, y la persona solo aparecía al pulsar «Continuar».
///
/// <para>
/// <b>Por qué los tests anteriores no lo veían.</b> Sus mediadores responden de forma síncrona, así
/// que el primer render ya tiene la lista cargada. En la aplicación la lista llega después del primer
/// render; aquí la carga se retiene con un <see cref="TaskCompletionSource"/> para reproducir ese orden.
/// </para>
///
/// <para>
/// El grupo «¿A quién?» se escribe como el de la pantalla de referencia del kit de formularios (alta de
/// Trabajador): un <c>fieldset</c> sin estilos propios se pinta con el recuadro del navegador. Aquí se
/// fija el marcado; que se vea bien lo comprueba el recorrido en navegador, no este test.
/// </para>
/// </summary>
public class SelectorLoteDocumentalPreseleccionTests : BunitContext
{
    private static readonly Guid TrabajadorId = Guid.NewGuid();

    private readonly TaskCompletionSource _cargaDeTrabajadores = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SelectorLoteDocumentalPreseleccionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediatorFalso(_cargaDeTrabajadores.Task));
    }

    private IRenderedComponent<SelectorLoteDocumental> Renderizar(Guid? entidadIdInicial) =>
        Render<SelectorLoteDocumental>(p => p
            .Add(x => x.AmbitosDisponibles, [AmbitoAplicacion.Trabajador])
            .Add(x => x.AmbitoInicial, AmbitoAplicacion.Trabajador)
            .Add(x => x.EntidadIdInicial, entidadIdInicial)
            .Add(x => x.OnConfirmar, EventCallback.Factory.Create<FiltroLoteDocumental>(this, _ => { })));

    private static string? TextoDelCampoTrabajador(IRenderedComponent<SelectorLoteDocumental> cut) =>
        cut.FindAll("label").Single(l => l.TextContent.Trim() == "Trabajador")
            .ParentElement!.QuerySelector("input")!.GetAttribute("value");

    [Fact]
    public void La_persona_preseleccionada_se_ve_en_el_campo_cuando_termina_la_carga()
    {
        var cut = Renderizar(TrabajadorId);
        cut.FindAll(".selector-lote-alcance input[type=radio]")[1].HasAttribute("checked").Should().BeTrue(
            "control: la persona llega preseleccionada y la opción «en concreto» sale marcada");
        (TextoDelCampoTrabajador(cut) ?? string.Empty).Should().BeEmpty(
            "control: el campo ya se ha pintado una vez sin la lista, que es el orden que rompía");

        _cargaDeTrabajadores.SetResult();

        cut.WaitForAssertion(() => TextoDelCampoTrabajador(cut).Should().Contain("Ana Ruiz"));
    }

    [Fact]
    public void El_grupo_A_quien_no_es_un_fieldset_sin_estilo_sino_el_grupo_de_opciones_del_kit()
    {
        _cargaDeTrabajadores.SetResult();
        var cut = Renderizar(entidadIdInicial: null);

        cut.FindAll("fieldset").Should().BeEmpty("un fieldset sin estilos propios se pinta con el recuadro del navegador");

        var grupo = cut.Find(".selector-lote-alcance");
        grupo.ClassList.Should().Contain("campo");
        grupo.GetAttribute("role").Should().Be("radiogroup");
        var etiqueta = cut.Find($"#{grupo.GetAttribute("aria-labelledby")}");
        etiqueta.TextContent.Trim().Should().Be("¿A quién?");
        grupo.QuerySelectorAll(".lista-seleccion-multiple input[type=radio]").Should().HaveCount(2);
    }

    private sealed class MediatorFalso(Task cargaDeTrabajadores) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerTrabajadoresParaSelectorQuery)
                await cargaDeTrabajadores;

            return (TResponse)(object)(request switch
            {
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new(TrabajadorId, "Ana Ruiz", null, null)],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });
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
