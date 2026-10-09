using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
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
/// La fila de /vehiculos se refresca en sitio tras guardar en la vista rápida (panel del Context
/// Workspace, que vive en MainLayout y guarda sin pasar por la página). El panel avisa por
/// <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve a pedir SOLO esa
/// fila, con el filtro por id de la consulta de lista. Aquí no se monta el panel: el aviso se
/// emite a mano sobre el servicio que la página tiene inyectado.
///
/// <para>
/// El doble del mediador guarda los vehículos y APLICA lo que recibe de la consulta de lista: el
/// filtro por <c>VehiculoId</c>, la búsqueda por nombre, el orden por nombre (con el Id de
/// desempate) y la paginación. No reproduce el resto de filtros ni el alcance de cartera: ningún
/// test de aquí depende de ellos.
/// </para>
/// </summary>
public class VehiculosFilaSeRefrescaTests : BunitContext
{
    /// <summary>QuickGrid y AtajosListaTeclado importan sus módulos JS al montarse.</summary>
    public VehiculosFilaSeRefrescaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediadorFalso : IMediator
    {
        public List<VehiculoListaDto> Almacen { get; } = [];
        public List<object> Enviadas { get; } = [];

        /// <summary>La consulta de una sola fila falla (la de página no).</summary>
        public bool FallarRelectura { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerVehiculosQuery { VehiculoId: not null } && FallarRelectura)
                return Task.FromException<TResponse>(new InvalidOperationException("Fallo simulado de la relectura."));

            return Task.FromResult((TResponse)(request switch
            {
                ObtenerClientesAutorizadosQuery => (object)(IReadOnlyList<ClienteAutorizadoDto>)[],
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasParaSelectorQuery => Array.Empty<EmpresaSelectorDto>(),
                ObtenerSubcontratasParaSelectorQuery => Array.Empty<SubcontrataSelectorDto>(),
                ObtenerVehiculosQuery q => Filtrar(q),
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));
        }

        private ResultadoPaginado<VehiculoListaDto> Filtrar(ObtenerVehiculosQuery q)
        {
            var coincidentes = Almacen
                .Where(v => q.VehiculoId is null || v.Id == q.VehiculoId)
                .Where(v => string.IsNullOrWhiteSpace(q.Busqueda) || v.Nombre.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Nombre, StringComparer.Ordinal).ThenBy(v => v.Id)
                .ToList();
            var pagina = coincidentes.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList();
            return new ResultadoPaginado<VehiculoListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
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

    private static VehiculoListaDto Vehiculo(string nombre) =>
        new(Guid.NewGuid(), nombre, "Transit", "1234-ABC", "Montajes Ebro S.L.");

    private static MediadorFalso ConVeinticincoVehiculos()
    {
        var mediador = new MediadorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Vehiculo($"Vehículo {i:00}"));
        return mediador;
    }

    private IRenderedComponent<Vehiculos> Renderizar(MediadorFalso mediador, string url = "vehiculos")
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<Vehiculos>();
        cut.WaitForState(() => cut.FindAll("tbody a.boton-360-pagina").Count > 0);
        return cut;
    }

    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<Vehiculos> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    /// <summary>Filas con datos (QuickGrid rellena la página con filas vacías).</summary>
    private static IReadOnlyList<IElement> Filas(IRenderedComponent<Vehiculos> cut) =>
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)");

    private static List<string> Nombres(IRenderedComponent<Vehiculos> cut) =>
        Filas(cut).Select(tr => tr.QuerySelector("button.enlace-nombre-fila")!.TextContent.Trim()).ToList();

    private static int ConsultasDeLista(MediadorFalso mediador) => mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Count();

    private static ObtenerVehiculosQuery UltimaConsulta(MediadorFalso mediador) => mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Last();

    /// <summary>Cambia lo guardado en el doble, como haría el comando de edición del panel.</summary>
    private static void Renombrar(MediadorFalso mediador, Guid id, string nombre)
    {
        var indice = mediador.Almacen.FindIndex(v => v.Id == id);
        mediador.Almacen[indice] = mediador.Almacen[indice] with { Nombre = nombre };
    }

    private static async Task IrALaPagina2Async(IRenderedComponent<Vehiculos> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(5).And.StartWith("Vehículo 21"));
    }

    /// <summary>
    /// El nombre nuevo mandaría la fila al final con el orden por nombre: que siga la primera
    /// es lo que distingue «sustituir en sitio» de «recargar la página».
    /// </summary>
    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var camion = Vehiculo("Camión grúa");
        var mediador = new MediadorFalso { Almacen = { camion, Vehiculo("Furgoneta de obra") } };
        var cut = Renderizar(mediador);
        Nombres(cut).Should().Equal(["Camión grúa", "Furgoneta de obra"], "punto de partida");
        var consultasAntes = ConsultasDeLista(mediador);

        Renombrar(mediador, camion.Id, "Volquete");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, camion.Id);

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal(["Volquete", "Furgoneta de obra"],
            "la fila enseña el dato nuevo y no cambia de sitio"));
        UltimaConsulta(mediador).VehiculoId.Should().Be(camion.Id, "se pide solo esa fila");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        UltimaConsulta(mediador).ConRecuentosPorEstado.Should().BeTrue(
            "misma pregunta de estado que la carga de página: sin ella, quien no tiene documentos pasaría de «Sin incidencias» a «Sin documentos»");
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "la consulta por id y ninguna de página detrás");
        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Count(q => q.VehiculoId is null).Should().Be(consultasAntes,
            "ninguna consulta de página nueva");
    }

    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_multiple_y_la_fila_enfocada()
    {
        var mediador = ConVeinticincoVehiculos();
        var editado = mediador.Almacen.Single(v => v.Nombre == "Vehículo 23").Id;
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await Filas(cut)[1].QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await Filas(cut)[2].QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.OnAtajo.InvokeAsync("j"));
        Filas(cut)[0].ClassList.Should().Contain("fila-enfocada", "punto de partida: la j enfocó la primera fila");
        var consultasAntes = ConsultasDeLista(mediador);

        Renombrar(mediador, editado, "Renombrado");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, editado);

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal(
            "Vehículo 21", "Vehículo 22", "Renombrado", "Vehículo 24", "Vehículo 25"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 vehículo(s)");
        Filas(cut).Select(tr => tr.QuerySelector("input[type=checkbox]")!.HasAttribute("checked")).Should().Equal(
            [false, true, true, false, false], "la selección sigue marcada, también la de la fila sustituida");
        Filas(cut).Select(tr => tr.ClassList.Contains("fila-enfocada")).Should().Equal(true, false, false, false, false);
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "una carga de página habría limpiado selección y foco");
        UltimaConsulta(mediador).VehiculoId.Should().Be(editado);
    }

    /// <summary>
    /// La búsqueda activa es «Camión» y el guardado cambia el nombre: la fila ya no casa con el
    /// filtro, pero permanece hasta la siguiente carga, y el filtro no se toca.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_el_cambio_la_saque_del_filtro_activo()
    {
        var camion = Vehiculo("Camión grúa");
        var mediador = new MediadorFalso { Almacen = { camion, Vehiculo("Furgoneta de obra") } };
        var cut = Renderizar(mediador, "vehiculos?q=Cami%C3%B3n");
        Nombres(cut).Should().Equal(["Camión grúa"], "punto de partida: el filtro deja una fila");

        Renombrar(mediador, camion.Id, "Volquete");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, camion.Id);

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("Volquete"));
        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Last(q => q.VehiculoId is null).Busqueda.Should().Be("Camión",
            "la última consulta de página sigue siendo la del filtro");
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, un Vehículo que no existe y uno que está en la página 1 mientras se mira la 2.
    /// El aviso se atiende en línea dentro del despachador y el doble responde en síncrono, así
    /// que al volver de cada uno ya no queda nada pendiente; el control positivo del final
    /// demuestra que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var mediador = ConVeinticincoVehiculos();
        var dePagina1 = mediador.Almacen.Single(v => v.Nombre == "Vehículo 01").Id;
        var dePagina2 = mediador.Almacen.Single(v => v.Nombre == "Vehículo 21").Id;
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        var enviadasAntes = mediador.Enviadas.Count;
        Renombrar(mediador, dePagina1, "Renombrado de la página 1");
        Renombrar(mediador, dePagina2, "Renombrado");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        Nombres(cut).Should().StartWith("Vehículo 21", "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, dePagina2);

        cut.WaitForAssertion(() => Nombres(cut).Should().StartWith("Renombrado"));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }

    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_no_se_ensena_ningun_error_y_la_fila_se_queda()
    {
        var camion = Vehiculo("Camión grúa");
        var mediador = new MediadorFalso { Almacen = { camion, Vehiculo("Furgoneta de obra") }, FallarRelectura = true };
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        Renombrar(mediador, camion.Id, "Volquete");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, camion.Id);

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "control positivo: la relectura se intentó");
        Nombres(cut).Should().Equal(["Camión grúa", "Furgoneta de obra"], "la fila conserva el dato anterior");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el guardado ya es firme: no hay error que enseñar");
        cut.Markup.Should().NotContain("Fallo simulado");

        // La página sigue viva: el siguiente aviso, ya sin fallo, sí sustituye la fila.
        mediador.FallarRelectura = false;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, camion.Id);

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("Volquete", "Furgoneta de obra"));
    }
}
