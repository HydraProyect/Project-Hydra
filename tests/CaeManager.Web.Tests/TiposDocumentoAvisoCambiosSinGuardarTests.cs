using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.TiposDocumento.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de /tipos-documento con el drawer de alta o edición de Tipo de documento
/// a medias pregunta antes. Lo que el drawer trae al abrirse (el orden propuesto en el
/// alta, la ficha cargada al editar) no es un cambio, y lo ya guardado no deja nada que
/// perder.
/// </summary>
public class TiposDocumentoAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid TipoId = Guid.NewGuid();

    public TiposDocumentoAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                ObtenerCentrosParaSelectorQuery => (IReadOnlyList<CentroSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)
                [
                    new TipoDocumentoListaDto(TipoId, "Formación PRL", 12, true, 1, AmbitoAplicacion.Trabajador,
                        RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal, null, null, null, null,
                        false, false, false, default, []),
                ],
                ObtenerTipoDocumentoPorIdQuery => new TipoDocumentoDetalleDto(TipoId, "Formación PRL", 12, true, 1,
                    AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal,
                    "Notas", "Descripción", "Criterios", "Servicio de prevención", null, [], ["PRL"], []),
                CrearTipoDocumentoCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
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

    private readonly MediatorFalso _mediador = new();

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<TiposDocumento> Renderizar()
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
        Navegacion.NavigateTo("tipos-documento");
        var cut = Render<TiposDocumento>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Formación PRL"));
        return cut;
    }

    private static async Task AbrirAltaAsync(IRenderedComponent<TiposDocumento> cut)
    {
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo tipo").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
    }

    private static Task EscribirNombreAsync(IRenderedComponent<TiposDocumento> cut, string nombre) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre").Find("input")
            .InputAsync(new ChangeEventArgs { Value = nombre });

    [Fact]
    public async Task Salir_con_el_alta_a_medias_pregunta()
    {
        var cut = Renderizar();
        await AbrirAltaAsync(cut);

        await EscribirNombreAsync(cut, "Carné de carretillero");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task El_orden_que_propone_el_alta_no_es_un_cambio()
    {
        var cut = Renderizar();
        await AbrirAltaAsync(cut);
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "2",
            "el test necesita que el alta llegue con el orden siguiente ya propuesto");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo que propone el alta no es un cambio de quien edita");
    }

    [Fact]
    public async Task La_ficha_cargada_al_editar_no_es_un_cambio()
    {
        var cut = Renderizar();
        await cut.Find("button[aria-label='Editar Formación PRL']").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Formación PRL"));

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la ficha tal como se cargó no es un cambio");
    }

    [Fact]
    public async Task Guardar_el_alta_no_deja_nada_que_perder()
    {
        var cut = Renderizar();
        await AbrirAltaAsync(cut);
        await EscribirNombreAsync(cut, "Carné de carretillero");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
        _mediador.Enviadas.Should().ContainSingle(p => p is CrearTipoDocumentoCommand, "si no se guardó, el test no mide nada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo escrito ya está guardado");
    }
}
