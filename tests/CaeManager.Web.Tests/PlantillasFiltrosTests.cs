using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaPlantillas = CaeManager.Web.Features.Plantillas.Pages.Plantillas;

namespace CaeManager.Web.Tests;

/// <summary>
/// Buscador, pastillas (Ámbito, Formato, Alcance), franja de estado con recuentos y vacío con «Limpiar filtros» de /plantillas
/// (lienzo «Documentos · listado sin pestañas», v34; D-53). Los filtros viajan en la URL, así que los casos los fijan ahí; el
/// camino del buscador y de la franja se prueba además accionando sus componentes.
/// </summary>
public class PlantillasFiltrosTests : BunitContext
{
    private static readonly Guid Centro = Guid.NewGuid();
    private static readonly Guid Cliente = Guid.NewGuid();
    private static readonly Guid Trabajador1 = Guid.NewGuid();
    private static readonly Guid Trabajador2 = Guid.NewGuid();

    public PlantillasFiltrosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<PlantillaDocumentoListaDto> Plantillas { get; } = [];
        public List<DocumentoGeneradoListaDto> Generados { get; } = [];
        public List<ObtenerDocumentosGeneradosQuery> ConsultasGenerados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)Responder(request));

        private object Responder(object request)
        {
            switch (request)
            {
                case ObtenerPlantillasDocumentoQuery:
                    return (IReadOnlyList<PlantillaDocumentoListaDto>)Plantillas.ToList();
                case ObtenerCentrosParaSelectorQuery:
                    return (IReadOnlyList<CentroSelectorDto>)[new CentroSelectorDto(Centro, "Planta Norte", "Cliente", "Empresa")];
                case ObtenerClientesParaSelectorQuery:
                    return (IReadOnlyList<ClienteSelectorDto>)[new ClienteSelectorDto(Cliente, "Acerías del Sur")];
                case ObtenerTrabajadoresParaSelectorQuery:
                    return (IReadOnlyList<TrabajadorSelectorDto>)[
                        new TrabajadorSelectorDto(Trabajador1, "Ana Pérez", null, null),
                        new TrabajadorSelectorDto(Trabajador2, "Luis Gómez", null, null)];
                case ObtenerTotalDocumentosGeneradosConAvisosQuery:
                    return Generados.Count(g => g.Estado == EstadoDocumentoGenerado.GeneradoConAvisos);
                case ObtenerDocumentosGeneradosQuery q:
                    ConsultasGenerados.Add(q);
                    return (IReadOnlyList<DocumentoGeneradoListaDto>)Generados
                        .Where(g => q.PlantillaDocumentoId is null || g.PlantillaDocumentoId == q.PlantillaDocumentoId)
                        .Where(g => q.TrabajadorId is null || g.TrabajadorId == q.TrabajadorId)
                        .ToList();
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
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

    private static PlantillaDocumentoListaDto Plantilla(
        string nombre,
        EstadoConfiguracionPlantilla estado = EstadoConfiguracionPlantilla.Confirmada,
        AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador,
        FormatoOrigenPlantilla formato = FormatoOrigenPlantilla.PdfConCampos,
        int version = 1,
        Guid? centroId = null,
        Guid? clienteId = null) =>
        new(Guid.NewGuid(), nombre, null, ambito, formato, EstadoPlantillaDocumento.Activa,
            estado == EstadoConfiguracionPlantilla.Confirmada ? Guid.NewGuid() : null, Guid.NewGuid(), estado,
            version, centroId, clienteId);

    private static DocumentoGeneradoListaDto Generado(
        PlantillaDocumentoListaDto plantilla, Guid trabajador, string nombre, EstadoDocumentoGenerado estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), plantilla.Id, plantilla.Nombre, trabajador, nombre, null, "Montajes Roca",
            new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), estado);

    private MediatorFalso Catalogo() => new()
    {
        Plantillas =
        {
            Plantilla("Entrega de EPI", EstadoConfiguracionPlantilla.Confirmada, version: 3),
            Plantilla("Anexo II", EstadoConfiguracionPlantilla.Borrador, formato: FormatoOrigenPlantilla.PdfVisual),
            Plantilla("Ficha de vehículo", EstadoConfiguracionPlantilla.PendienteRevision, AmbitoAplicacion.Vehiculo, centroId: Centro),
            Plantilla("Autorización de acceso", EstadoConfiguracionPlantilla.Confirmada, AmbitoAplicacion.Empresa, clienteId: Cliente)
        }
    };

    private IRenderedComponent<PaginaPlantillas> Renderizar(MediatorFalso mediador, string url = "plantillas")
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<PaginaPlantillas>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static List<string> Nombres(IRenderedComponent<PaginaPlantillas> cut) =>
        cut.FindAll("tbody td.nombre-plantilla .celda-principal").Select(td => td.TextContent.Trim()).ToList();

    private static Dictionary<string, string> Recuentos(IRenderedComponent<PaginaPlantillas> cut) =>
        cut.FindAll(".franja-estado-boton").ToDictionary(
            b => b.GetAttribute("data-estado") ?? "todos",
            b => b.QuerySelector(".franja-estado-recuento")?.TextContent.Trim() ?? string.Empty);

    // ------------------------------------------------------------------ Catálogo

    [Fact]
    public void Sin_filtros_el_catalogo_muestra_todas_y_el_segmentado_abre_en_Catalogo()
    {
        var cut = Renderizar(Catalogo());

        Nombres(cut).Should().HaveCount(4);
        cut.FindAll(".segmentado-opcion").Single(o => o.GetAttribute("aria-pressed") == "true").TextContent.Trim()
            .Should().StartWith("Catálogo");
        cut.FindAll("[role='tab']").Should().BeEmpty("el catálogo y los generados son un control segmentado, no pestañas");
    }

    [Fact]
    public void La_segunda_linea_del_nombre_dice_el_alcance_y_la_version()
    {
        var cut = Renderizar(Catalogo());

        var segundas = cut.FindAll("tbody td.nombre-plantilla .celda-secundaria").Select(s => s.TextContent.Trim()).ToList();
        segundas.Should().Contain("Toda la organización · v3");
        segundas.Should().Contain(s => s.StartsWith("Planta Norte"));
        segundas.Should().Contain(s => s.StartsWith("Acerías del Sur"));
    }

    [Fact]
    public void La_busqueda_filtra_por_nombre_sin_distinguir_mayusculas_ni_tildes()
    {
        var cut = Renderizar(Catalogo(), "plantillas?q=autorizacion");

        Nombres(cut).Should().Equal(["Autorización de acceso"]);
    }

    [Fact]
    public async Task El_buscador_de_la_barra_filtra_y_lo_deja_en_la_url()
    {
        var cut = Renderizar(Catalogo());

        await cut.InvokeAsync(() => cut.FindComponent<BarraFiltros>().Instance.BusquedaChanged.InvokeAsync("anexo"));

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal(["Anexo II"]));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=anexo");
    }

    [Fact]
    public void Ambito_formato_y_alcance_filtran()
    {
        Nombres(Renderizar(Catalogo(), "plantillas?ambito=Vehiculo")).Should().Equal(["Ficha de vehículo"]);
    }

    [Fact]
    public void Formato_filtra_por_como_se_rellena()
    {
        Nombres(Renderizar(Catalogo(), "plantillas?formato=PdfVisual")).Should().Equal(["Anexo II"]);
    }

    [Fact]
    public void Alcance_filtra_por_toda_la_organizacion_por_centro_y_por_cliente()
    {
        Nombres(Renderizar(Catalogo(), "plantillas?alcance=org")).Should().BeEquivalentTo(["Entrega de EPI", "Anexo II"]);
    }

    [Theory]
    [InlineData("ambito=99")]
    [InlineData("ambito=Inventado")]
    [InlineData("formato=x")]
    [InlineData("formato=99")]
    [InlineData("alcance=centro:zz")]
    [InlineData("alcance=cliente:no-guid")]
    [InlineData("alcance=inventado")]
    [InlineData("estado=Inventado")]
    [InlineData("estado=Generado")]
    [InlineData("plantilla=no-guid")]
    [InlineData("trabajador=no-guid")]
    public void Un_valor_inventado_en_la_url_no_filtra_por_nada(string consulta)
    {
        var cut = Renderizar(Catalogo(), $"plantillas?{consulta}");

        Nombres(cut).Should().HaveCount(4, "un valor que no es de la pantalla se descarta en vez de dejar la lista vacia");
    }

    [Fact]
    public void Un_estado_mezclado_con_uno_inventado_conserva_solo_el_valido()
    {
        Nombres(Renderizar(Catalogo(), "plantillas?estado=Inventado,Borrador")).Should().Equal(["Anexo II"]);
    }

    [Fact]
    public void Los_filtros_de_Generados_en_la_url_del_Catalogo_se_descartan()
    {
        var cut = Renderizar(Catalogo(), $"plantillas?plantilla={Guid.NewGuid()}&trabajador={Trabajador1}");

        Nombres(cut).Should().HaveCount(4);
        cut.Markup.Should().NotContain("Limpiar filtros", "no queda ningun filtro puesto que limpiar");
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("U")]
    public void Alcance_por_cliente_acepta_el_Guid_en_mayusculas_y_sin_guiones(string formato)
    {
        var id = formato == "U" ? Cliente.ToString().ToUpperInvariant() : Cliente.ToString(formato);

        Nombres(Renderizar(Catalogo(), $"plantillas?alcance=cliente:{id}")).Should().Equal(["Autorización de acceso"]);
    }

    [Fact]
    public void Alcance_por_centro_deja_solo_las_plantillas_de_ese_centro()
    {
        Nombres(Renderizar(Catalogo(), $"plantillas?alcance=centro:{Centro}")).Should().Equal(["Ficha de vehículo"]);
    }

    [Fact]
    public void La_franja_de_estado_cuenta_con_los_demas_filtros_y_sin_el_suyo()
    {
        var cut = Renderizar(Catalogo(), "plantillas?estado=Borrador");

        Nombres(cut).Should().Equal(["Anexo II"], "el estado filtra la lista");
        var recuentos = Recuentos(cut);
        recuentos["todos"].Should().Be("4", "la franja cuenta el catálogo sin su propio filtro");
        recuentos["Confirmada"].Should().Be("2");
        recuentos["Borrador"].Should().Be("1");
        recuentos["PendienteRevision"].Should().Be("1");
    }

    [Fact]
    public void Los_recuentos_de_la_franja_respetan_los_otros_filtros()
    {
        var cut = Renderizar(Catalogo(), "plantillas?ambito=Trabajador");

        var recuentos = Recuentos(cut);
        recuentos["todos"].Should().Be("2");
        recuentos["Confirmada"].Should().Be("1");
        recuentos["Borrador"].Should().Be("1");
    }

    [Fact]
    public async Task Pulsar_un_estado_de_la_franja_filtra_la_lista()
    {
        var cut = Renderizar(Catalogo());

        await cut.InvokeAsync(() => cut.FindComponent<FranjaEstado>().Instance.ValorChanged.InvokeAsync("Confirmada"));

        cut.WaitForAssertion(() => Nombres(cut).Should().BeEquivalentTo(["Entrega de EPI", "Autorización de acceso"]));
    }

    [Fact]
    public async Task Sin_coincidencias_el_vacio_dice_que_el_catalogo_no_esta_vacio_y_Limpiar_filtros_lo_devuelve()
    {
        var cut = Renderizar(Catalogo(), "plantillas?q=zzz");

        cut.Find(".estado-vacio").TextContent.Should().Contain("Ninguna plantilla coincide");
        cut.Markup.Should().NotContain("Todavía no hay plantillas");

        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Limpiar filtros").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(4));
    }

    // ------------------------------------------------------------------ Generados

    private MediatorFalso CatalogoConGenerados()
    {
        var mediador = Catalogo();
        var epi = mediador.Plantillas[0];
        var anexo = mediador.Plantillas[1];
        mediador.Generados.Add(Generado(epi, Trabajador1, "Ana Pérez", EstadoDocumentoGenerado.Generado));
        mediador.Generados.Add(Generado(epi, Trabajador2, "Luis Gómez", EstadoDocumentoGenerado.GeneradoConAvisos));
        mediador.Generados.Add(Generado(anexo, Trabajador1, "Ana Pérez", EstadoDocumentoGenerado.GeneradoConAvisos));
        return mediador;
    }

    private static List<string> FilasGenerados(IRenderedComponent<PaginaPlantillas> cut) =>
        cut.FindAll("tbody tr").Select(f => string.Join(" | ", f.QuerySelectorAll("td").Take(2).Select(t => t.TextContent.Trim()))).ToList();

    [Fact]
    public void Generados_trae_su_buscador_su_franja_con_recuentos_y_sus_pastillas()
    {
        var cut = Renderizar(CatalogoConGenerados(), "plantillas?Pestana=generados");

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().HaveCount(3));
        cut.FindAll(".filtro-pastilla-lista").Should().HaveCount(2, "Plantilla y Trabajador");
        cut.Markup.Should().Contain("Filtrar por estado del documento generado");
        cut.WaitForAssertion(() =>
        {
            var recuentos = Recuentos(cut);
            recuentos["todos"].Should().Be("3");
            recuentos["Generado"].Should().Be("1");
            recuentos["GeneradoConAvisos"].Should().Be("2");
        });
    }

    [Fact]
    public void Generados_filtra_por_estado_y_por_busqueda_en_memoria()
    {
        var cut = Renderizar(CatalogoConGenerados(), "plantillas?Pestana=generados&estado=GeneradoConAvisos&q=anexo");

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().Equal(["Anexo II | Ana Pérez"]));
    }

    /// <summary>Sola, sin buscador que ya excluya filas: la franja de estado es la que filtra (mutación: quitarla).</summary>
    [Fact]
    public void Generados_filtra_solo_por_estado_en_la_franja()
    {
        var cut = Renderizar(CatalogoConGenerados(), "plantillas?Pestana=generados&estado=GeneradoConAvisos");

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().Equal(["Entrega de EPI | Luis Gómez", "Anexo II | Ana Pérez"]));
    }

    [Fact]
    public void Generados_con_el_estado_limpio_deja_solo_los_generados_sin_avisos()
    {
        var cut = Renderizar(CatalogoConGenerados(), "plantillas?Pestana=generados&estado=Generado");

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().Equal(["Entrega de EPI | Ana Pérez"]));
    }

    [Fact]
    public void Generados_filtra_por_plantilla_en_el_servidor()
    {
        var mediador = CatalogoConGenerados();
        var anexo = mediador.Plantillas[1];

        var cut = Renderizar(mediador, $"plantillas?Pestana=generados&plantilla={anexo.Id}");

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().Equal(["Anexo II | Ana Pérez"]));
        mediador.ConsultasGenerados.Should().Contain(q => q.PlantillaDocumentoId == anexo.Id);
    }

    [Fact]
    public void Generados_filtra_por_trabajador_en_el_servidor()
    {
        var mediador = CatalogoConGenerados();

        var cut = Renderizar(mediador, $"plantillas?Pestana=generados&trabajador={Trabajador2}");

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().Equal(["Entrega de EPI | Luis Gómez"]));
    }

    [Fact]
    public async Task Generados_sin_coincidencias_ofrece_Limpiar_filtros()
    {
        var cut = Renderizar(CatalogoConGenerados(), "plantillas?Pestana=generados&q=zzz");

        cut.WaitForAssertion(() => cut.Find(".estado-vacio").TextContent.Should().Contain("Ningún documento generado con este filtro"));
        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Limpiar filtros").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => FilasGenerados(cut).Should().HaveCount(3));
    }
}
