using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
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
        public List<CancellationToken> Tokens { get; } = [];

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add(cancellationToken);
            if (Retener?.Invoke(request) is { } espera) await espera;

            // El switch tiene ramas de tipos distintos, asi que se unifica en
            // object? y se convierte una sola vez: sin esto, CS0029 por rama.
            object? valor = request switch
            {
                ObtenerClientesAutorizadosQuery => (object)Array.Empty<ClienteAutorizadoDto>(),
                ObtenerEmpresasParaSelectorQuery => Array.Empty<EmpresaSelectorDto>(),
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
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo("vehiculos");
        var cut = Render<Vehiculos>();
        cut.WaitForState(() => cut.FindAll("tbody .menu-acciones-disparador").Count > 0);
        return (cut, m);
    }

    private static async Task PulsarEnElMenuDeLaFila(IRenderedComponent<Vehiculos> cut, int fila, string item)
    {
        await cut.FindAll("tbody .menu-acciones-disparador")[fila].ClickAsync(new MouseEventArgs());
        await cut.FindAll("tbody .menu-acciones-item").Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
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
    public void Consulta_ve_la_lista_sin_seleccion_multiple_porque_el_lote_solo_elimina()
    {
        // La selección solo alimenta «Eliminar seleccionados» (EliminarVehiculosCommand, ICommand
        // que AutorizacionEscrituraBehavior deniega a Consulta): sin eso, no se ofrece el modo.
        this.ConRolDeEscritura(Roles.Consulta);
        var (cut, _) = Renderizar(Vehiculo("Furgoneta de obra"));

        cut.Markup.Should().Contain("Furgoneta de obra", "la lista es lectura: la fila se ve");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Selección múltiple");
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().ContainSingle();
        cut.FindAll(".cabecera-pagina button[aria-label='Selección múltiple']").Should().BeEmpty();
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
    }

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
        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
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

        var caja = cut.FindComponents<CampoTexto>().First(c => c.Instance.Placeholder?.StartsWith("Filtrar esta pantalla") == true);
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

        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label^='Seleccionar el vehículo Furgoneta de obra']").ChangeAsync(new ChangeEventArgs { Value = true });
        await Boton(cut, "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EliminarVehiculosCommand>().Single().Ids.Should().Equal([elegido.Id],
            "el caso solo vale si el lote pidió ese vehículo y ninguno más");
        workspace.EstaAbierto.Should().Be(seQuedaAbierta);
    }

    // Insertar dentro de VehiculosConcurrenciaTests. Añadir using AngleSharp.Dom y CaeManager.Domain.Documentos si faltan.
    public static IEnumerable<object[]> CamposOrdenVehiculoFase1()
    {
        foreach (var campo in new[] { "Nombre", "Modelo" })
            foreach (var misma in new[] { false, true })
                foreach (var descendente in new[] { false, true })
                    yield return [campo, misma, descendente];
    }

    [Theory]
    [MemberData(nameof(CamposOrdenVehiculoFase1))]
    public async Task Opciones_nativas_envian_el_campo_real_y_el_sentido_del_vehiculo(
        string campo, bool mismaColumna, bool descendente)
    {
        var (cut, m) = Renderizar(Vehiculo("Furgoneta contractual"));
        var previo = campo == "Nombre" ? "Modelo" : "Nombre";
        await ElegirOrdenVehiculoFase1(cut, previo);
        await FijarSentidoVehiculoFase1(cut, m, mismaColumna ? "Vehículo" : "Matrícula", descendente);
        UltimoOrdenVehiculoFase1(m).OrdenarPor.Should().Be(mismaColumna ? previo : "NumeroPlaca");
        UltimoOrdenVehiculoFase1(m).Descendente.Should().Be(descendente);
        var antes = m.Enviadas.OfType<ObtenerVehiculosQuery>().Count();
        await ElegirOrdenVehiculoFase1(cut, campo);
        cut.WaitForAssertion(() =>
        {
            m.Enviadas.OfType<ObtenerVehiculosQuery>().Should().HaveCount(antes + 1);
            UltimoOrdenVehiculoFase1(m).OrdenarPor.Should().Be(campo);
            UltimoOrdenVehiculoFase1(m).Descendente.Should().Be(mismaColumna && descendente);
        });
    }

    [Fact]
    public async Task Nombre_y_modelo_en_una_celda_conservan_matricula_tinte_y_foco()
    {
        var vencido = Vehiculo("Furgoneta roja") with { Modelo = "Transit contractual", EstadoDocumental = EstadoDocumento.Vencido };
        var urgente = Vehiculo("Camión amarillo") with { EstadoDocumental = EstadoDocumento.Urgente };
        var neutro = Vehiculo("Turismo neutro") with { EstadoDocumental = EstadoDocumento.Vigente };
        var (cut, m) = Renderizar(vencido, urgente, neutro);
        m.Enviadas.OfType<ObtenerVehiculosQuery>().Should().NotBeEmpty();
        var filas = cut.FindAll("tbody tr:has(button.enlace-nombre-fila)");
        filas.Should().HaveCount(3);
        cut.Find(".cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("3");
        filas[0].QuerySelectorAll("td").Single(td => td.TextContent.Contains(vencido.Nombre))
            .TextContent.Should().Contain(vencido.Modelo);
        filas[0].TextContent.Should().Contain(vencido.NumeroPlaca);
        filas[0].ClassList.Should().Contain("fila-tintada-peligro");
        filas[1].ClassList.Should().Contain("fila-tintada-aviso");
        filas[2].ClassList.Should().NotContain("fila-tintada-peligro").And.NotContain("fila-tintada-aviso");
        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.OnAtajo.InvokeAsync("j"));
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)")[0].ClassList.Should().Contain("fila-enfocada").And.Contain("fila-tintada-peligro");
        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.OnAtajo.InvokeAsync("j"));
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)")[0].ClassList.Should().NotContain("fila-enfocada").And.Contain("fila-tintada-peligro");
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)")[1].ClassList.Should().Contain("fila-enfocada").And.Contain("fila-tintada-aviso");
    }

    [Fact]
    public async Task Retirar_vehiculos_durante_orden_pendiente_no_reconsulta()
    {
        var (cut, m) = Renderizar(Vehiculo("Furgoneta contractual"));
        var retenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Retener = x => x is ObtenerVehiculosQuery { OrdenarPor: "Modelo" } ? retenida.Task : null;
        Task? cambiar = null;
        var huboError = false;
        var antes = 0;
        try
        {
            cambiar = ElegirOrdenVehiculoFase1(cut, "Modelo");
            cut.WaitForAssertion(() => UltimoOrdenVehiculoFase1(m).OrdenarPor.Should().Be("Modelo"));
            antes = m.Enviadas.OfType<ObtenerVehiculosQuery>().Count();
            var token = m.Tokens[m.Enviadas.FindLastIndex(x => x is ObtenerVehiculosQuery)];
            token.CanBeCanceled.Should().BeTrue();
            token.IsCancellationRequested.Should().BeFalse();
            await DisposeComponentsAsync();
            token.IsCancellationRequested.Should().BeTrue();
        }
        catch
        {
            huboError = true;
            throw;
        }
        finally
        {
            retenida.TrySetResult();
            if (cambiar is not null)
            {
                try { await cambiar; }
                catch when (huboError) { /* Conserva el error original si la liberación también falla. */ }
            }
        }
        m.Enviadas.OfType<ObtenerVehiculosQuery>().Should().HaveCount(antes);
        // La copia integrada registra Tokens junto a Enviadas; no atribuye cobertura a HideColumnOptionsAsync.
    }

    private static ObtenerVehiculosQuery UltimoOrdenVehiculoFase1(MediadorFalso m) =>
        m.Enviadas.OfType<ObtenerVehiculosQuery>().Last();

    private static IElement CabeceraVehiculoFase1(IRenderedComponent<Vehiculos> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim().StartsWith(titulo, StringComparison.Ordinal));

    private static async Task FijarSentidoVehiculoFase1(
        IRenderedComponent<Vehiculos> cut, MediadorFalso m, string titulo, bool descendente)
    {
        await CabeceraVehiculoFase1(cut, titulo).QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        if (UltimoOrdenVehiculoFase1(m).Descendente != descendente)
            await CabeceraVehiculoFase1(cut, titulo).QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => UltimoOrdenVehiculoFase1(m).Descendente.Should().Be(descendente));
    }

    private static async Task ElegirOrdenVehiculoFase1(IRenderedComponent<Vehiculos> cut, string campo)
    {
        await CabeceraVehiculoFase1(cut, "Vehículo").QuerySelector("button.col-options-button")!.ClickAsync(new MouseEventArgs());
        await cut.Find(".col-options select").ChangeAsync(new ChangeEventArgs { Value = campo });
    }

}
