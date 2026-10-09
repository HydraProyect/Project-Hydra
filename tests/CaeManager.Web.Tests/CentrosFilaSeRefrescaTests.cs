using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila de /centros se refresca en sitio tras guardar en la vista rápida (panel del Context
/// Workspace, que vive en MainLayout y guarda sin pasar por la página). El panel avisa por
/// <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve a pedir SOLO esa
/// fila, con el filtro por id de la consulta de lista. Aquí no se monta el panel: el aviso se
/// emite a mano sobre el servicio que la página tiene inyectado. Que el panel lo emite al guardar
/// lo prueba <c>CentroWorkspacePanelLapizTests</c> (parte «AvisoDeGuardado»).
/// </summary>
public class CentrosFilaSeRefrescaTests : BunitContext
{
    private static readonly Guid Titular = Guid.NewGuid();

    public CentrosFilaSeRefrescaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        ComponentFactories.AddStub<AcordeonAsignacionesCentro>();
        Services.AddLocalization();
    }

    /// <summary>Doble con almacén mutable: filtra por búsqueda e id y pagina, como el handler.</summary>
    private sealed class MediatorFalso : IMediator
    {
        public List<CentroListaDto> Almacen { get; } = [];
        public List<object> Enviadas { get; } = [];
        public bool FallarConsultaPorId { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerCentrosQuery { CentroId: not null } && FallarConsultaPorId)
                return Task.FromException<TResponse>(new InvalidOperationException("Fallo simulado de la relectura."));

            return Task.FromResult((TResponse)(request switch
            {
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                ObtenerProximaVisitaPorCentroQuery =>
                    (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[new ClienteAutorizadoDto(Guid.NewGuid(), "Propia", EsOrigen: true)],
                ObtenerCentrosQuery q => Pagina(q),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
        }

        private ResultadoPaginado<CentroListaDto> Pagina(ObtenerCentrosQuery q)
        {
            var filas = Almacen
                .Where(c => q.CentroId is null || c.Id == q.CentroId)
                .Where(c => string.IsNullOrWhiteSpace(q.Busqueda) || c.Nombre.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new ResultadoPaginado<CentroListaDto>(
                filas.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), filas.Count, q.Pagina, q.TamanoPagina);
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Todos del mismo titular: un solo grupo, y el orden de las filas es el de la página.</summary>
    private static CentroListaDto Centro(string nombre) => new(
        Guid.NewGuid(), nombre, "C-001", Titular, "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", EstadoCentro.Vigente,
        CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);

    private IRenderedComponent<Centros> Renderizar(MediatorFalso mediador, string ruta = "centros")
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo(ruta);

        return Render<Centros>().AbrirGruposDeCentros();
    }

    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<Centros> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    private static List<string> NombresDeLasFilas(IRenderedComponent<Centros> cut) =>
        cut.FindAll(".tarjeta-fila-acordeon .nombre-abre-vista-rapida").Select(b => b.TextContent.Trim()).ToList();

    private static List<bool> FilasEnfocadas(IRenderedComponent<Centros> cut) =>
        cut.FindAll(".tarjeta-fila-acordeon").Select(f => f.ClassList.Contains("fila-enfocada")).ToList();

    private static int ConsultasDeLista(MediatorFalso mediador) => mediador.Enviadas.OfType<ObtenerCentrosQuery>().Count();

    private static int ConsultasDePagina(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Count(q => q.CentroId is null);

    /// <summary>Cambia lo guardado en el doble, como haría el comando de edición del panel.</summary>
    private static void Renombrar(MediatorFalso mediador, Guid id, string nombre)
    {
        var indice = mediador.Almacen.FindIndex(c => c.Id == id);
        mediador.Almacen[indice] = mediador.Almacen[indice] with { Nombre = nombre };
    }

    private static MediatorFalso ConVeinticinco()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Centro($"Centro {i:00}"));
        return mediador;
    }

    private static async Task IrALaPaginaDosAsync(IRenderedComponent<Centros> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.AbrirGruposDeCentros();
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(5).And.StartWith("Centro 21"));
    }

    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var norte = Centro("Almacén Norte");
        var mediador = new MediatorFalso { Almacen = { norte, Centro("Almacén Sur") } };
        var cut = Renderizar(mediador);
        NombresDeLasFilas(cut).Should().Equal(["Almacén Norte", "Almacén Sur"], "punto de partida");
        var (listaAntes, paginaAntes) = (ConsultasDeLista(mediador), ConsultasDePagina(mediador));

        Renombrar(mediador, norte.Id, "Zona Franca");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, norte.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Zona Franca", "Almacén Sur"],
            "la fila enseña el dato nuevo y no cambia de sitio"));
        var ultima = mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last();
        ultima.CentroId.Should().Be(norte.Id, "se pide solo esa fila");
        ultima.Busqueda.Should().BeNull();
        ConsultasDeLista(mediador).Should().Be(listaAntes + 1, "la consulta por id y ninguna de página detrás");
        ConsultasDePagina(mediador).Should().Be(paginaAntes, "ninguna consulta de página nueva");
    }

    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_la_fila_enfocada_y_el_acordeon_desplegado()
    {
        var mediador = ConVeinticinco();
        var editado = mediador.Almacen.Single(c => c.Nombre == "Centro 23").Id;
        var cut = Renderizar(mediador);
        await IrALaPaginaDosAsync(cut);
        await cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro 22']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("input[aria-label='Seleccionar el centro Centro 23']").ChangeAsync(new ChangeEventArgs { Value = true });
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        FilasEnfocadas(cut).Should().Equal([true, false, false, false, false], "punto de partida: la j enfocó la primera fila");
        await cut.FindAll(".tarjeta-fila-acordeon")[2].QuerySelector("button.boton-expandir-fila")!.ClickAsync(new MouseEventArgs());
        cut.FindComponents<Stub<AcordeonAsignacionesCentro>>().Should().ContainSingle("punto de partida: el acordeón de la tercera fila está desplegado");
        var (listaAntes, paginaAntes) = (ConsultasDeLista(mediador), ConsultasDePagina(mediador));

        Renombrar(mediador, editado, "Renombrado 23");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, editado);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(
            "Centro 21", "Centro 22", "Renombrado 23", "Centro 24", "Centro 25"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 centro(s)");
        cut.Find("input[aria-label='Seleccionar el centro Centro 22']").HasAttribute("checked").Should().BeTrue(
            "la selección de otra fila sigue marcada");
        cut.Find("input[aria-label='Seleccionar el centro Renombrado 23']").HasAttribute("checked").Should().BeTrue(
            "la fila sustituida sigue seleccionada");
        cut.FindAll("input[aria-label^='Seleccionar el centro']").Count(c => c.HasAttribute("checked")).Should().Be(2);
        FilasEnfocadas(cut).Should().Equal(true, false, false, false, false);
        cut.FindComponents<Stub<AcordeonAsignacionesCentro>>().Should().ContainSingle("el acordeón sigue desplegado");
        ConsultasDeLista(mediador).Should().Be(listaAntes + 1, "una carga de página habría limpiado selección, foco y acordeones");
        ConsultasDePagina(mediador).Should().Be(paginaAntes);
    }

    /// <summary>
    /// La búsqueda activa es «Norte» y el guardado cambia el nombre: la fila ya no casa con el
    /// filtro, pero permanece hasta la siguiente carga, y el filtro no se toca.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_el_cambio_la_saque_del_filtro_activo()
    {
        var norte = Centro("Almacén Norte");
        var mediador = new MediatorFalso { Almacen = { norte, Centro("Almacén Sur") } };
        var cut = Renderizar(mediador, "centros?q=Norte");
        NombresDeLasFilas(cut).Should().Equal(["Almacén Norte"], "punto de partida: el filtro deja una fila");

        Renombrar(mediador, norte.Id, "Zona Franca");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, norte.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Zona Franca"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Norte");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last(q => q.CentroId is null).Busqueda.Should().Be("Norte",
            "la última consulta de página sigue siendo la del filtro");
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, un Centro que no existe y uno que está en la página 1 mientras se mira la 2. El
    /// aviso se atiende en línea dentro del despachador y el doble responde en síncrono, así que
    /// al volver de cada uno ya no queda nada pendiente; el control positivo del final demuestra
    /// que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var mediador = ConVeinticinco();
        var dePagina1 = mediador.Almacen.Single(c => c.Nombre == "Centro 01").Id;
        var dePagina2 = mediador.Almacen.Single(c => c.Nombre == "Centro 21").Id;
        var cut = Renderizar(mediador);
        await IrALaPaginaDosAsync(cut);
        var enviadasAntes = mediador.Enviadas.Count;
        Renombrar(mediador, dePagina1, "Renombrado 01");
        Renombrar(mediador, dePagina2, "Renombrado 21");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        NombresDeLasFilas(cut).Should().StartWith("Centro 21", "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, dePagina2);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().StartWith("Renombrado 21"));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }

    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_no_se_ensena_ningun_error_y_la_fila_se_queda()
    {
        var norte = Centro("Almacén Norte");
        var mediador = new MediatorFalso { Almacen = { norte, Centro("Almacén Sur") } };
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);
        mediador.FallarConsultaPorId = true;

        Renombrar(mediador, norte.Id, "Zona Franca");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, norte.Id);

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "control positivo: la relectura se intentó");
        NombresDeLasFilas(cut).Should().Equal(["Almacén Norte", "Almacén Sur"], "la fila conserva el dato anterior");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el guardado ya es firme: no hay error que enseñar");
        cut.Markup.Should().NotContain("Fallo simulado");

        // La página sigue viva: el siguiente aviso, ya sin fallo, sí sustituye la fila.
        mediador.FallarConsultaPorId = false;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Centro, norte.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Zona Franca", "Almacén Sur"));
    }
}
