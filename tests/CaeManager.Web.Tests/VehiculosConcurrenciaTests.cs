using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculo;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Carreras del proveedor de la rejilla y guardas de reentrada de
/// <c>Vehiculos.razor.cs</c> (borrado individual y en lote). Las esperas
/// retenidas se liberan desde InvokeAsync para observar su continuación.
/// </summary>
public class VehiculosConcurrenciaTests : BunitContext
{
    public VehiculosConcurrenciaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Los textos de Vehículos salen de IStringLocalizer<TextosVehiculos>.
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediadorFalso : IMediator
    {
        /// <summary>
        /// Resultados fijados por término de búsqueda (cadena vacía = sin
        /// filtro): una respuesta retenida devuelve siempre lo que se fijó
        /// para SU búsqueda, sin importar cuánto tarde en resolverse ni qué
        /// otra búsqueda se haya disparado mientras tanto. El total va
        /// separado de los elementos (como en la paginación real) para poder
        /// distinguir, en la aserción, un total que QuickGrid no protege por
        /// su cuenta —a diferencia de las filas, que sí— del total vigente.
        /// </summary>
        public Dictionary<string, (List<VehiculoListaDto> Elementos, int Total)> ResultadosPorBusqueda { get; } = [];
        public Result Baja { get; set; } = Result.Exito();
        public Result<ResultadoEliminacionLoteDto> BajaLote { get; set; } = Result.Exito(new ResultadoEliminacionLoteDto(1, []));
        public Func<object, Task?>? Retener { get; set; }
        public List<object> Enviadas { get; } = [];

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (Retener?.Invoke(request) is { } espera) await espera;

            // El switch tiene ramas de tipos distintos, asi que se unifica en
            // object? y se convierte una sola vez: sin esto, CS0029 por rama.
            object? valor = request switch
            {
                ObtenerEmpresasParaSelectorQuery => (object)Array.Empty<EmpresaSelectorDto>(),
                ObtenerSubcontratasParaSelectorQuery => Array.Empty<SubcontrataSelectorDto>(),
                ObtenerVehiculosQuery q => Paginado(q),
                EliminarVehiculoCommand => Baja,
                EliminarVehiculosCommand => BajaLote,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (T)valor!;
        }

        private ResultadoPaginado<VehiculoListaDto> Paginado(ObtenerVehiculosQuery q)
        {
            var (elementos, total) = ResultadosPorBusqueda.GetValueOrDefault(q.Busqueda ?? string.Empty, ([], 0));
            return new ResultadoPaginado<VehiculoListaDto>(elementos, total, q.Pagina, q.TamanoPagina);
        }

        public Task Send<T>(T request, CancellationToken ct = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> r, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object n, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<T>(T n, CancellationToken ct = default) where T : INotification => Task.CompletedTask;
    }

    private (IRenderedComponent<Vehiculos> Cut, MediadorFalso Mediador) Renderizar(params VehiculoListaDto[] vehiculos)
    {
        var m = new MediadorFalso();
        m.ResultadosPorBusqueda[string.Empty] = (vehiculos.ToList(), vehiculos.Length);
        Services.AddScoped<IMediator>(_ => m);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo("vehiculos");
        var cut = Render<Vehiculos>();
        cut.WaitForState(() => cut.FindAll(".menu-acciones-disparador").Count > 0);
        return (cut, m);
    }

    private static async Task PulsarEnElMenuDeLaFila(IRenderedComponent<Vehiculos> cut, int fila, string item)
    {
        await cut.FindAll(".menu-acciones-disparador")[fila].ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    private static IElement Boton(IRenderedComponent<Vehiculos> cut, string texto) =>
        cut.FindAll("button").Where(x => x.TextContent.Trim() == texto).Should().ContainSingle().Subject;

    private static VehiculoListaDto Vehiculo(string nombre) =>
        new(Guid.NewGuid(), nombre, "Transit", "1234-ABC", "Montajes Ebro S.L.");

    private static DialogoConfirmacion DialogoEliminar(IRenderedComponent<Vehiculos> cut) =>
        cut.FindComponents<DialogoConfirmacion>().Single(x => x.Instance.Titulo.StartsWith("¿Eliminar el vehículo")).Instance;

    private static DialogoConfirmacion DialogoEliminarLote(IRenderedComponent<Vehiculos> cut) =>
        cut.FindComponents<DialogoConfirmacion>().Single(x => x.Instance.Titulo.Contains("vehículo(s)?")).Instance;

    [Fact]
    public async Task Dos_invocaciones_del_OnConfirmar_del_dialogo_individual_mandan_un_solo_borrado()
    {
        var espera = new TaskCompletionSource();
        var (cut, m) = Renderizar(Vehiculo("Furgoneta de obra"));
        m.Retener = x => x is EliminarVehiculoCommand ? espera.Task : null;

        await PulsarEnElMenuDeLaFila(cut, 0, "Eliminar");
        var dialogo = DialogoEliminar(cut);

        var primero = cut.InvokeAsync(() => dialogo.OnConfirmar.InvokeAsync());
        var segundo = cut.InvokeAsync(() => dialogo.OnConfirmar.InvokeAsync());

        m.Enviadas.OfType<EliminarVehiculoCommand>().Should().ContainSingle(
            "la guarda de la página también cubre llamadores que no son el botón del diálogo");
        await cut.InvokeAsync(espera.SetResult); await primero; await segundo;
        m.Enviadas.OfType<EliminarVehiculoCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Dos_invocaciones_del_OnConfirmar_del_dialogo_de_lote_mandan_un_solo_borrado()
    {
        var espera = new TaskCompletionSource();
        var (cut, m) = Renderizar(Vehiculo("Furgoneta de obra"), Vehiculo("Camión grúa"));
        m.Retener = x => x is EliminarVehiculosCommand ? espera.Task : null;

        // Activa "Selección múltiple" (los checkboxes de fila solo se pintan
        // con esto activo) y marca la primera fila.
        await Boton(cut, "Selección múltiple").ClickAsync(new MouseEventArgs());
        cut.FindAll("input[type=checkbox]")[1].Change(true);
        await Boton(cut, "Eliminar seleccionados").ClickAsync(new MouseEventArgs());

        var dialogo = DialogoEliminarLote(cut);

        var primero = cut.InvokeAsync(() => dialogo.OnConfirmar.InvokeAsync());
        var segundo = cut.InvokeAsync(() => dialogo.OnConfirmar.InvokeAsync());

        m.Enviadas.OfType<EliminarVehiculosCommand>().Should().ContainSingle(
            "la guarda de la página también cubre llamadores que no son el botón del diálogo");
        await cut.InvokeAsync(espera.SetResult); await primero; await segundo;
        m.Enviadas.OfType<EliminarVehiculosCommand>().Should().ContainSingle();
    }

    /// <summary>
    /// QuickGrid ya protege sus propias filas frente a un provider superado
    /// (ver el comentario de <c>ProveerElementosAsync</c>): una aserción sobre
    /// el texto de las filas pasaría con o sin la guarda de generación de la
    /// página. Lo que SOLO protege esa guarda es el estado que la página
    /// escribe aparte —aquí, el total del paginador, deliberadamente distinto
    /// del recuento de filas de cada búsqueda— así que la aserción se hace
    /// sobre <c>.paginador-texto</c>, no sobre el marcado de la rejilla.
    /// </summary>
    [Fact]
    public async Task La_respuesta_de_una_busqueda_ya_abandonada_no_pisa_el_total_de_la_busqueda_vigente()
    {
        var espera = new TaskCompletionSource();
        var (cut, m) = Renderizar(Vehiculo("Furgoneta de obra"), Vehiculo("Camión grúa"));
        m.ResultadosPorBusqueda["furgo"] = ([Vehiculo("Furgoneta de obra")], 99);
        m.ResultadosPorBusqueda["camion"] = ([Vehiculo("Camión grúa")], 1);
        m.Retener = x => x is ObtenerVehiculosQuery q && q.Busqueda == "furgo" ? espera.Task : null;

        var caja = cut.FindComponents<CampoTexto>().First(c => c.Instance.Placeholder?.StartsWith("Buscar por nombre") == true);
        var primeraBusqueda = cut.InvokeAsync(() => caja.Instance.ValorChanged.InvokeAsync("furgo"));
        m.Enviadas.OfType<ObtenerVehiculosQuery>().Should().Contain(q => q.Busqueda == "furgo", "la primera búsqueda quedó retenida");

        await cut.InvokeAsync(() => caja.Instance.ValorChanged.InvokeAsync("camion"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("1 vehículo(s)");

        await cut.InvokeAsync(espera.SetResult);
        await primeraBusqueda;

        cut.Find(".paginador-texto").TextContent.Should().Contain("1 vehículo(s)", "el total vigente es el de 'camion'")
            .And.NotContain("99 vehículo(s)", "la respuesta tardía de 'furgo' ya no es la carga vigente");
    }

    /// <summary>
    /// El Workspace no es modal: con la ficha del vehículo abierta, la baja se
    /// confirma desde la fila que queda detrás, y la lista es quien la retira.
    /// </summary>
    [Fact]
    public async Task Eliminar_el_vehiculo_cuya_ficha_esta_abierta_retira_la_ficha()
    {
        var furgoneta = Vehiculo("Furgoneta de obra");
        var (cut, m) = Renderizar(furgoneta);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Vehiculo, furgoneta.Id, "Furgoneta de obra", "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await PulsarEnElMenuDeLaFila(cut, 0, "Eliminar");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EliminarVehiculoCommand>().Should().ContainSingle("la baja se ejecutó");
        workspace.EstaAbierto.Should().BeFalse("una ficha abierta de un vehículo ya dado de baja no puede seguir editable");
    }

    /// <summary>
    /// Baja en lote: se retira la ficha si su vehículo iba en el lote y cayó
    /// alguno; no se toca si era de otro vehículo ni si el lote no eliminó nada.
    /// </summary>
    [Theory]
    [InlineData(true, 1, false)]  // iba en el lote, cayó: se retira
    [InlineData(false, 1, true)]  // no iba en el lote: se queda
    [InlineData(true, 0, true)]   // iba en el lote pero no cayó nada: se queda
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_solo_si_su_vehiculo_iba_y_cayo_alguno(
        bool ibaEnElLote, int eliminados, bool seQuedaAbierta)
    {
        var elegido = Vehiculo("Furgoneta de obra");
        var otro = Vehiculo("Camión grúa");
        var (cut, m) = Renderizar(elegido, otro);
        m.BajaLote = Result.Exito(new ResultadoEliminacionLoteDto(eliminados, []));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var abierto = ibaEnElLote ? elegido : otro;
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Vehiculo, abierto.Id, "Ficha abierta", "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await Boton(cut, "Selección múltiple").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label^='Seleccionar el vehículo Furgoneta de obra']").ChangeAsync(new ChangeEventArgs { Value = true });
        await Boton(cut, "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EliminarVehiculosCommand>().Single().Ids.Should().Equal([elegido.Id],
            "el caso solo vale si el lote pidió ese vehículo y ninguno más");
        workspace.EstaAbierto.Should().Be(seQuedaAbierta);
    }
}
