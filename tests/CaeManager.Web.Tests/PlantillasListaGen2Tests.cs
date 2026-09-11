using Bunit;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaPlantillas = CaeManager.Web.Features.Plantillas.Pages.Plantillas;

namespace CaeManager.Web.Tests;

/// <summary>
/// Catálogo de plantillas (/plantillas) contra su mockup Gen 2 («Plantillas
/// TALVEG.dc.html»). La lista la pinta <see cref="PlantillasTab"/>, que también
/// va embebida en /documentos: por eso se prueban las dos superficies. El badge
/// de avisos de «Generados» lo sigue probando <see cref="PlantillasTabTests"/>.
/// </summary>
public class PlantillasListaGen2Tests : BunitContext
{
    /// <summary>La lista monta AtajosListaTeclado, que importa un módulo JS.</summary>
    public PlantillasListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public List<PlantillaDocumentoListaDto> Almacen { get; } = [];
        public bool FallaCatalogo { get; set; }
        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>Si devuelve una tarea para la petición, esa es la respuesta: permite retenerla.</summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add(cancellationToken);

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            return (TResponse)Responder(request);
        }

        private object Responder(object request) => request switch
        {
            ObtenerPlantillasDocumentoQuery => FallaCatalogo
                ? throw new InvalidOperationException("Fallo simulado del catálogo de plantillas.")
                : (IReadOnlyList<PlantillaDocumentoListaDto>)Almacen.ToList(),
            ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
            ObtenerTotalDocumentosGeneradosConAvisosQuery => 0,
            ObtenerDocumentosGeneradosQuery => (IReadOnlyList<DocumentoGeneradoListaDto>)[],
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

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

    private void Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<PaginaPlantillas> RenderizarPagina(MediatorFalso mediador, string url = "plantillas")
    {
        Registrar(mediador);
        Navegacion.NavigateTo(url);

        var cut = Render<PaginaPlantillas>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static PlantillaDocumentoListaDto Plantilla(
        string nombre,
        EstadoConfiguracionPlantilla estado = EstadoConfiguracionPlantilla.Confirmada,
        AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador,
        FormatoOrigenPlantilla formato = FormatoOrigenPlantilla.PdfConCampos) =>
        new(Guid.NewGuid(), nombre, null, ambito, formato, EstadoPlantillaDocumento.Activa,
            estado == EstadoConfiguracionPlantilla.Confirmada ? Guid.NewGuid() : null, Guid.NewGuid(), estado);

    private static List<string> TextosDeLosBotones(IRenderedComponent<PaginaPlantillas> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();

    // ------------------------------------------------------------------ Cabecera

    [Fact]
    public void La_cabecera_es_la_Gen_2_con_su_kicker_su_entradilla_y_la_accion_de_alta()
    {
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { Plantilla("Anexo II — Información de riesgos") } });

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Ciclo documental");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Plantillas");
        cabecera.QuerySelector(".cabecera-pagina-descripcion")!.TextContent.Trim().Should().Be(PlantillasTab.TextoDescripcion);
        cabecera.QuerySelector(".acciones-cabecera button")!.TextContent.Trim().Should().Be("+ Nueva plantilla");
    }

    /// <summary>La pestaña no repite su cabecera bajo la de la página: habría dos «+ Nueva plantilla».</summary>
    [Fact]
    public void En_la_pagina_hay_una_sola_accion_de_alta_y_una_sola_entradilla()
    {
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { Plantilla("Anexo II — Información de riesgos") } });

        TextosDeLosBotones(cut).Count(t => t == "+ Nueva plantilla").Should().Be(1);
        cut.Markup.Split(PlantillasTab.TextoDescripcion).Length.Should().Be(2, "la entradilla aparece una sola vez");
    }

    [Fact]
    public async Task Nueva_plantilla_de_la_cabecera_lleva_al_alta()
    {
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { Plantilla("Anexo II — Información de riesgos") } });

        await cut.Find("header.cabecera-pagina .acciones-cabecera button").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().EndWith("/plantillas/nueva");
    }

    /// <summary>En /documentos la cabecera es la de Documentos: la pestaña conserva su entradilla y su alta.</summary>
    [Fact]
    public void Embebida_en_documentos_la_pestana_conserva_su_entradilla_y_su_alta()
    {
        Registrar(new MediatorFalso { Almacen = { Plantilla("Anexo II — Información de riesgos") } });

        var cut = Render<PlantillasTab>();

        cut.WaitForAssertion(() => cut.Find("div.cabecera-pagina .texto-descriptivo").TextContent.Trim().Should().Be(PlantillasTab.TextoDescripcion));
        cut.Find("div.cabecera-pagina button").TextContent.Trim().Should().Be("+ Nueva plantilla");
        cut.FindAll("header.cabecera-pagina").Should().BeEmpty("la cabecera Gen 2 es de la página /plantillas, no de la pestaña");
    }

    // ------------------------------------------------------------------- Lista

    [Fact]
    public void La_lista_va_en_un_marco_propio_con_sus_columnas_y_rotulos_legibles()
    {
        var cut = RenderizarPagina(new MediatorFalso
        {
            Almacen =
            {
                Plantilla("Anexo II — Información de riesgos"),
                Plantilla("Ficha técnica del vehículo", ambito: AmbitoAplicacion.Vehiculo, formato: FormatoOrigenPlantilla.PdfVisual)
            }
        });

        var marco = cut.Find(".marco-lista-plantillas");
        marco.GetAttribute("role").Should().Be("region", "el marco se desplaza en horizontal: tiene que ser una región");
        marco.GetAttribute("aria-label").Should().Be("Catálogo de plantillas");
        marco.GetAttribute("tabindex").Should().Be("0", "sin foco, con teclado no se puede desplazar");

        var tabla = cut.Find(".marco-lista-plantillas > table.tabla-datos");
        tabla.QuerySelectorAll("th").Select(th => th.TextContent.Trim()).Should().Equal(["Nombre", "Ámbito", "Formato", "Estado", ""]);

        var filas = tabla.QuerySelectorAll("tbody tr");
        filas.Select(f => f.QuerySelector("td.nombre-plantilla")!.TextContent.Trim())
            .Should().Equal(["Anexo II — Información de riesgos", "Ficha técnica del vehículo"]);
        filas[1].QuerySelectorAll("td").Take(3).Select(td => td.TextContent.Trim())
            .Should().Equal(["Ficha técnica del vehículo", "Vehículo", "Posición manual"]);
        filas[0].QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("Campos AcroForm");
    }

    /// <summary>Antes una versión pendiente de revisión se pintaba con el nombre del enum: «PendienteRevision».</summary>
    [Fact]
    public void El_estado_se_dice_en_castellano_y_no_con_el_nombre_del_enum()
    {
        var cut = RenderizarPagina(new MediatorFalso
        {
            Almacen =
            {
                Plantilla("Acta de coordinación", EstadoConfiguracionPlantilla.Confirmada),
                Plantilla("Autorización de trabajos especiales", EstadoConfiguracionPlantilla.Borrador),
                Plantilla("Nombramiento de recurso preventivo", EstadoConfiguracionPlantilla.PendienteRevision)
            }
        });

        cut.FindAll("tbody .badge").Select(b => b.TextContent.Trim())
            .Should().Equal(["Confirmada", "Borrador", "Pendiente de revisión"]);
    }

    [Fact]
    public void Una_confirmada_se_ve_y_admite_nueva_version_y_una_sin_confirmar_solo_se_configura()
    {
        var cut = RenderizarPagina(new MediatorFalso
        {
            Almacen =
            {
                Plantilla("Anexo II", EstadoConfiguracionPlantilla.Confirmada),
                Plantilla("Autorización", EstadoConfiguracionPlantilla.Borrador)
            }
        });

        var filas = cut.FindAll("tbody tr");
        filas[0].QuerySelectorAll(".acciones-plantilla button").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal(["Ver Anexo II", "Nueva versión de Anexo II"]);
        filas[1].QuerySelectorAll(".acciones-plantilla button").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal(["Configurar Autorización"]);
        filas[0].QuerySelectorAll(".acciones-plantilla button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Ver", "Nueva versión"], "el nombre accesible contiene el texto visible (WCAG 2.5.3)");
    }

    [Fact]
    public async Task Ver_abre_el_editor_en_la_ultima_version()
    {
        var anexo = Plantilla("Anexo II");
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { anexo } });

        await cut.Find("tbody .acciones-plantilla button").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().EndWith($"/plantillas/{anexo.UltimaVersionId}/editar");
    }

    [Fact]
    public async Task Nueva_version_abre_el_dialogo_de_subida_de_esa_plantilla()
    {
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { Plantilla("Acta de coordinación"), Plantilla("Anexo II") } });
        cut.Markup.Should().NotContain("Subir nueva versión —", "punto de partida: el diálogo está cerrado");

        await cut.FindAll("tbody tr")[1].QuerySelectorAll(".acciones-plantilla button")[1].ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Subir nueva versión — Anexo II");
    }

    // ------------------------------------------------------ Estados de la carga

    [Fact]
    public void Sin_plantillas_el_vacio_ofrece_crear_la_primera()
    {
        var cut = RenderizarPagina(new MediatorFalso());

        cut.Find(".estado-vacio").TextContent.Should().Contain("Todavía no hay plantillas");
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public async Task Si_falla_la_carga_lo_dice_y_Reintentar_pinta_lo_que_llega()
    {
        var mediador = new MediatorFalso { FallaCatalogo = true, Almacen = { Plantilla("Anexo II") } };
        var cut = RenderizarPagina(mediador);
        cut.Find(".estado-vacio").TextContent.Should().Contain("No pudimos cargar las plantillas");

        mediador.FallaCatalogo = false;
        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll("tbody td.nombre-plantilla").Select(td => td.TextContent.Trim()).Should().Equal(["Anexo II"]));
        cut.Markup.Should().NotContain("No pudimos cargar las plantillas");
    }

    // ------------------------------------------------------------------ Atajos

    [Fact]
    public async Task j_y_k_recorren_el_catalogo_y_Enter_abre_la_plantilla_enfocada()
    {
        var anexo = Plantilla("Anexo II");
        var acta = Plantilla("Acta de coordinación");
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { anexo, acta } });
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        string Enfocada() => cut.Find("tr.fila-enfocada td.nombre-plantilla").TextContent.Trim();

        cut.FindAll("tr.fila-enfocada").Should().BeEmpty("sin atajos no hay fila enfocada");
        var antes = Navegacion.Uri;
        await cut.InvokeAsync(() => atajos.RecibirAtajo("Enter"));
        Navegacion.Uri.Should().Be(antes, "Enter sin fila enfocada no abre nada");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be("Anexo II");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be("Acta de coordinación");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be("Acta de coordinación", "j en la última fila se queda en ella");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("k"));
        Enfocada().Should().Be("Anexo II");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("Enter"));
        Navegacion.Uri.Should().EndWith($"/plantillas/{anexo.UltimaVersionId}/editar");
    }

    // ----------------------------------------------------- Sub-pestaña en la URL

    [Fact]
    public void Con_Pestana_generados_en_la_url_la_pagina_abre_en_Generados()
    {
        var cut = RenderizarPagina(new MediatorFalso(), "plantillas?Pestana=generados");

        cut.FindAll("[role='tab']").Single(t => t.GetAttribute("aria-selected") == "true").TextContent.Trim()
            .Should().StartWith("Generados");
    }

    [Fact]
    public async Task Cambiar_a_Generados_lo_refleja_en_la_url()
    {
        var cut = RenderizarPagina(new MediatorFalso { Almacen = { Plantilla("Anexo II") } });

        await cut.FindAll("[role='tab']")[1].ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().Contain("Pestana=generados");
    }

    // ------------------------------------------------------------- Carreras

    /// <summary>
    /// Salir de la página cancela la consulta del catálogo en curso, y su
    /// respuesta tardía ya no toca un componente retirado. Que el token quede
    /// cancelado demuestra además que el Dispose se ejecutó de verdad:
    /// DisposeComponentsAsync lo llama, cut.Dispose() de bUnit no.
    /// </summary>
    [Fact]
    public async Task Salir_de_la_pagina_cancela_la_carga_del_catalogo_en_curso()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Almacen = { Plantilla("Anexo II") } };
        mediador.Retener = p => p is ObtenerPlantillasDocumentoQuery ? respuesta.Task : null;
        Registrar(mediador);
        Navegacion.NavigateTo("plantillas");
        Render<PaginaPlantillas>();

        var token = mediador.Tokens[mediador.Enviadas.FindIndex(p => p is ObtenerPlantillasDocumentoQuery)];
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token del ciclo de la pestaña");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue("salir de la página cancela la consulta en curso");
        var llegaTarde = () => respuesta.SetResult((IReadOnlyList<PlantillaDocumentoListaDto>)mediador.Almacen.ToList());
        llegaTarde.Should().NotThrow("la respuesta tardía no repinta un componente retirado");
    }

    /// <summary>
    /// Mientras la primera carga está en vuelo, la pestaña muestra que carga;
    /// cuando la respuesta llega, pinta sus filas. Control positivo del test
    /// de arriba: la respuesta retenida, si la pestaña sigue viva, sí se pinta.
    /// </summary>
    [Fact]
    public async Task Una_carga_retenida_que_llega_con_la_pestana_viva_se_pinta()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso();
        mediador.Retener = p => p is ObtenerPlantillasDocumentoQuery ? respuesta.Task : null;
        Registrar(mediador);
        Navegacion.NavigateTo("plantillas");
        var cut = Render<PaginaPlantillas>();
        cut.Markup.Should().Contain("aria-busy=\"true\"");

        await cut.InvokeAsync(() => respuesta.SetResult((IReadOnlyList<PlantillaDocumentoListaDto>)[Plantilla("Anexo II")]));

        cut.WaitForAssertion(() => cut.FindAll("tbody td.nombre-plantilla").Select(td => td.TextContent.Trim()).Should().Equal(["Anexo II"]));
    }
}
