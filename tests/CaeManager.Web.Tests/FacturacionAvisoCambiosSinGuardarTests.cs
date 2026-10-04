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
    private static readonly Guid ClienteBId = Guid.NewGuid();

    private readonly MediatorFalso _mediator = new();

    public FacturacionAvisoCambiosSinGuardarTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviados { get; } = [];

        /// <summary>Con dos tarifas, «Editar» existe en dos filas (para sustituir una edición por otra).</summary>
        public bool DosTarifas { get; set; }

        private List<TarifaClienteDto> TarifasDelCliente()
        {
            var tarifas = new List<TarifaClienteDto>
            {
                new(Guid.NewGuid(), ClienteId, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m, "EUR", Guid.NewGuid()),
            };
            if (DosTarifas)
                tarifas.Add(new(Guid.NewGuid(), ClienteId, ConceptoFacturable.AltaCentro, "Alta de centro", 40m, "EUR", Guid.NewGuid()));
            return tarifas;
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object? respuesta = request switch
            {
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[new ClienteSelectorDto(ClienteId, "Refrielectric S.L."), new ClienteSelectorDto(ClienteBId, "Frigoríficos Arcos S.A.")],
                ObtenerTarifasClienteQuery => TarifasDelCliente(),
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
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("facturacion");
        var cut = Render<FacturacionPagina>();
        await cut.ElegirPorValorAsync("sel-cliente", ClienteId.ToString());
        cut.WaitForAssertion(() => cut.FindAll("table.tabla-facturacion tbody tr").Should().HaveCount(_mediator.DosTarifas ? 2 : 1));
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

    // ------------------------------------------------------------------ P1-E2b lote 2: cambiar de Cliente empresarial

    private static bool PreguntaAbierta(IRenderedComponent<FacturacionPagina> cut) =>
        cut.FindAll(".modal-pie button").Any(b => b.TextContent.Trim() == "Salir y descartar");

    private static Task ElegirCliente(IRenderedComponent<FacturacionPagina> cut, Guid clienteId) =>
        cut.ElegirPorValorAsync("sel-cliente", clienteId.ToString());

    private int ConsultasDeTarifasDe(Guid clienteId) =>
        _mediator.Enviados.OfType<ObtenerTarifasClienteQuery>().Count(q => q.ClienteId == clienteId);

    [Fact]
    public async Task Cambiar_de_cliente_con_la_edicion_de_tarifa_a_medias_pregunta_seguir_conserva_y_descartar_cambia()
    {
        var (cut, _) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "Editar");
        await EditarPrecioAsync(cut, "4.25");

        // Sin await: el cambio queda pendiente de la respuesta del aviso; se afirma antes de esperarla.
        var cambio = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente empresarial tira la edición"));
        ConsultasDeTarifasDe(ClienteBId).Should().Be(0, "no se cambia de Cliente empresarial mientras pregunta");

        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await cambio.WaitAsync(TimeSpan.FromSeconds(10));

        cut.Find("input[aria-label='Precio unitario']").GetAttribute("value").Should().Be("4.25", "«Seguir editando» conserva lo escrito");
        ConsultasDeTarifasDe(ClienteBId).Should().Be(0, "y no carga las tarifas del otro Cliente empresarial");

        var segundo = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await segundo.WaitAsync(TimeSpan.FromSeconds(10));

        ConsultasDeTarifasDe(ClienteBId).Should().Be(1, "descartar cambia de Cliente empresarial");
        cut.FindAll("input[aria-label='Precio unitario']").Should().BeEmpty("la edición se descartó");
    }

    [Fact]
    public async Task Cambiar_de_cliente_con_la_tarifa_nueva_a_medias_pregunta_seguir_conserva_y_descartar_cambia()
    {
        var (cut, _) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "+ Añadir tarifa");
        await cut.Find("#nueva-precio").ChangeAsync(new ChangeEventArgs { Value = "12.5" });

        var cambio = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente empresarial tira el alta"));
        ConsultasDeTarifasDe(ClienteBId).Should().Be(0, "no se cambia de Cliente empresarial mientras pregunta");

        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await cambio.WaitAsync(TimeSpan.FromSeconds(10));

        cut.Find("#nueva-precio").GetAttribute("value").Should().Be("12.5", "«Seguir editando» conserva lo escrito");
        ConsultasDeTarifasDe(ClienteBId).Should().Be(0);

        var segundo = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await segundo.WaitAsync(TimeSpan.FromSeconds(10));

        ConsultasDeTarifasDe(ClienteBId).Should().Be(1, "descartar cambia de Cliente empresarial");
        cut.FindAll("#nueva-precio").Should().BeEmpty("el alta se descartó");
    }

    [Fact]
    public async Task Cambiar_de_cliente_sin_nada_escrito_no_pregunta()
    {
        var (cut, _) = await RenderizarConClienteAsync();
        await PulsarAsync(cut, "+ Añadir tarifa");

        // Con tope corto: si el aviso preguntara y nadie contestara, la tarea no termina y esperarla colgaría el test.
        var cambio = ElegirCliente(cut, ClienteBId);
        await Task.WhenAny(cambio, Task.Delay(TimeSpan.FromSeconds(2)));

        PreguntaAbierta(cut).Should().BeFalse("el alta está como se abrió");
        cambio.IsCompleted.Should().BeTrue("sin cambios el cambio de Cliente empresarial no se queda esperando una respuesta");
        await cambio;
        ConsultasDeTarifasDe(ClienteBId).Should().Be(1, "el cambio se hace directamente");
    }

    // ---- «Editar» sobre otra fila sustituye la edición abierta (P1-E2b, M2)

    private static Task EditarLaFilaAsync(IRenderedComponent<FacturacionPagina> cut, int fila) =>
        cut.FindAll("table.tabla-facturacion tbody tr")[fila].QuerySelectorAll("button")
            .First(b => b.TextContent.Trim() == "Editar").ClickAsync(new MouseEventArgs());

    private static bool FilaEnEdicion(IRenderedComponent<FacturacionPagina> cut, int fila) =>
        cut.FindAll("table.tabla-facturacion tbody tr")[fila].QuerySelector("input[aria-label='Precio unitario']") is not null;

    [Fact]
    public async Task Editar_otra_fila_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_edita_la_otra()
    {
        _mediator.DosTarifas = true;
        var (cut, _) = await RenderizarConClienteAsync();
        await EditarLaFilaAsync(cut, 0);
        await EditarPrecioAsync(cut, "4.25");

        // Sin await: «Editar» queda pendiente de la respuesta del aviso; se afirma antes de esperarla.
        var otra = EditarLaFilaAsync(cut, 1);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("«Editar» en otra fila tira la edición a medias"));
        FilaEnEdicion(cut, 0).Should().BeTrue("mientras pregunta, la edición sigue ahí");

        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await otra.WaitAsync(TimeSpan.FromSeconds(10));

        FilaEnEdicion(cut, 0).Should().BeTrue("«Seguir editando» conserva la edición en la primera fila");
        FilaEnEdicion(cut, 1).Should().BeFalse("y no abre la de la otra");
        cut.Find("input[aria-label='Precio unitario']").GetAttribute("value").Should().Be("4.25", "con lo escrito");

        var segunda = EditarLaFilaAsync(cut, 1);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await segunda.WaitAsync(TimeSpan.FromSeconds(10));

        FilaEnEdicion(cut, 1).Should().BeTrue("descartar deja editar la otra fila");
        FilaEnEdicion(cut, 0).Should().BeFalse("y la primera deja de estar en edición");
    }

    [Fact]
    public async Task Editar_otra_fila_con_la_edicion_sin_tocar_no_pregunta()
    {
        _mediator.DosTarifas = true;
        var (cut, _) = await RenderizarConClienteAsync();
        await EditarLaFilaAsync(cut, 0);

        // Con tope corto: si el aviso preguntara y nadie contestara, la tarea no termina y esperarla colgaría el test.
        var otra = EditarLaFilaAsync(cut, 1);
        await Task.WhenAny(otra, Task.Delay(TimeSpan.FromSeconds(2)));

        PreguntaAbierta(cut).Should().BeFalse("la edición está como se abrió");
        otra.IsCompleted.Should().BeTrue("sin cambios el gesto no se queda esperando una respuesta");
        await otra;
        FilaEnEdicion(cut, 1).Should().BeTrue("se edita directamente la otra fila");
    }
}
