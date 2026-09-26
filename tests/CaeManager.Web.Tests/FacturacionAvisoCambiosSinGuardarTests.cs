using System.Globalization;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Facturacion.Commands.ActualizarTarifaCliente;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using FacturacionPagina = CaeManager.Web.Features.Facturacion.Pages.Facturacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: el alta y la edición de tarifa de Facturación son formularios en la propia
/// página. Con algo escrito, salir pregunta; y como cambiar de pestaña los cierra
/// (CambiarPestana), el cambio de pestaña pregunta con el mismo aviso. El concepto que la
/// pantalla preselecciona no es un cambio, y guardar cierra el formulario sin preguntar.
/// </summary>
public class FacturacionAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid ClienteId = Guid.NewGuid();

    public FacturacionAvisoCambiosSinGuardarTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[new ClienteSelectorDto(ClienteId, "Refrielectric S.L.")],
                ObtenerTarifasClienteQuery => new List<TarifaClienteDto>
                {
                    new(Guid.NewGuid(), ClienteId, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m, "EUR", Guid.NewGuid()),
                },
                ObtenerResumenFacturacionQuery => null,
                ActualizarTarifaClienteCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
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

    private async Task<(IRenderedComponent<FacturacionPagina> Cut, NavigationManager Navegacion)> RenderizarConClienteAsync()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddLocalization();

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("facturacion");
        var cut = Render<FacturacionPagina>();
        await cut.Find("#sel-cliente").ChangeAsync(new ChangeEventArgs { Value = ClienteId.ToString() });
        cut.WaitForAssertion(() => cut.FindAll("table.tabla-facturacion tbody tr").Should().HaveCount(1));
        return (cut, navegacion);
    }

    private static Task PulsarAsync(IRenderedComponent<FacturacionPagina> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    private static Task EditarPrecioAsync(IRenderedComponent<FacturacionPagina> cut, string precio) =>
        cut.Find("input[aria-label='Precio unitario']").ChangeAsync(new ChangeEventArgs { Value = precio });

    [Fact]
    public async Task Salir_con_una_tarifa_nueva_a_medias_pregunta()
    {
        var (cut, navegacion) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "+ Añadir tarifa");
        await cut.Find("#nueva-precio").ChangeAsync(new ChangeEventArgs { Value = "12.5" });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Abrir_el_alta_sin_tocar_nada_no_pregunta()
    {
        var (cut, navegacion) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "+ Añadir tarifa");
        cut.FindComponents<CampoSelect>().Should().Contain(c => c.Instance.Etiqueta == "Concepto",
            "el test necesita el alta abierta con su concepto preseleccionado");

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el concepto preseleccionado por la pantalla no es un cambio de quien edita");
    }

    [Fact]
    public async Task Cambiar_de_pestana_con_la_edicion_a_medias_pregunta_y_descartar_cambia()
    {
        var (cut, navegacion) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "Editar");
        await EditarPrecioAsync(cut, "4.25");

        // No se espera: el cambio de pestaña queda pendiente de la respuesta del aviso.
        _ = cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Resumen mensual").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar"));
        cut.FindAll("input[aria-label='Precio unitario']").Should().NotBeEmpty("mientras pregunta, la edición sigue ahí");

        await cut.PulsarEnElAvisoAsync("Salir y descartar");

        cut.WaitForAssertion(() => cut.FindAll(".filtros-resumen").Should().NotBeEmpty("descartar deja cambiar de pestaña"));
        navegacion.Uri.Should().EndWith("/facturacion", "cambiar de pestaña no navega");
    }

    [Fact]
    public async Task Guardar_la_edicion_cierra_el_formulario_y_salir_ya_no_pregunta()
    {
        var (cut, navegacion) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "Editar");
        await EditarPrecioAsync(cut, "4.25");

        await PulsarAsync(cut, "Guardar");
        cut.WaitForAssertion(() => cut.FindAll("input[aria-label='Precio unitario']").Should().BeEmpty());

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "lo escrito ya está guardado");
    }
}
