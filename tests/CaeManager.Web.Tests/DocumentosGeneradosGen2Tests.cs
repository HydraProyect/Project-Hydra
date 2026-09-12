using Bunit;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaDocumentosGenerados = CaeManager.Web.Features.Plantillas.Pages.DocumentosGenerados;

namespace CaeManager.Web.Tests;

/// <summary>
/// Documentos generados (/plantillas/documentos-generados) contra su mockup Gen 2
/// («Documentos Generados TALVEG.dc.html»). El contenido lo pinta
/// <see cref="DocumentosGeneradosPanel"/>, que también vive en la pestaña
/// «Generados» de Plantillas: la cabecera se prueba en la página y el resto en el
/// panel suelto. El vacío por filtro, el badge de avisos y el total de avisos
/// notificado los siguen probando sus tres ficheros propios.
/// </summary>
public class DocumentosGeneradosGen2Tests : BunitContext
{
    private static readonly Guid AnexoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EpiId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Con filas, el panel monta AtajosListaTeclado y TextoFechaCopiable: importan módulos JS y avisan por toast.</summary>
    public DocumentosGeneradosGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<ToastService>();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<DocumentoGeneradoListaDto> Generados { get; } = [];
        public List<PlantillaDocumentoListaDto> Plantillas { get; } = [];
        public bool FallaGenerados { get; set; }
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
            ObtenerPlantillasDocumentoQuery => (IReadOnlyList<PlantillaDocumentoListaDto>)Plantillas.ToList(),
            ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
            ObtenerTotalDocumentosGeneradosConAvisosQuery => Generados.Count(d => d.Estado == EstadoDocumentoGenerado.GeneradoConAvisos),
            ObtenerDocumentosGeneradosQuery q => FallaGenerados
                ? throw new InvalidOperationException("Fallo simulado de los documentos generados.")
                : Filtrar(q),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        public IReadOnlyList<DocumentoGeneradoListaDto> Filtrar(ObtenerDocumentosGeneradosQuery q) => Generados
            .Where(d => q.PlantillaDocumentoId is null || d.PlantillaDocumentoId == q.PlantillaDocumentoId)
            .Where(d => q.TrabajadorId is null || d.TrabajadorId == q.TrabajadorId)
            .ToList();

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

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static DocumentoGeneradoListaDto Generado(
        Guid plantillaId,
        string plantilla,
        string? trabajador = "Juan Pérez",
        EstadoDocumentoGenerado estado = EstadoDocumentoGenerado.Generado) => new(
        DocumentoGeneradoId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(),
        PlantillaDocumentoId: plantillaId, PlantillaNombre: plantilla,
        TrabajadorId: trabajador is null ? null : Guid.NewGuid(), TrabajadorNombreCompleto: trabajador,
        EmpresaId: null, EmpresaRazonSocial: "Refrielectric S.A.",
        GeneradoEnUtc: new DateTime(2026, 9, 5, 8, 12, 0, DateTimeKind.Utc),
        Estado: estado);

    private static PlantillaDocumentoListaDto Plantilla(Guid id, string nombre) =>
        new(id, nombre, null, AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfConCampos,
            EstadoPlantillaDocumento.Activa, Guid.NewGuid(), Guid.NewGuid(), EstadoConfiguracionPlantilla.Confirmada);

    private IRenderedComponent<DocumentosGeneradosPanel> RenderizarPanel(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        var cut = Render<DocumentosGeneradosPanel>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static Task FiltrarPorPlantillaAsync(IRenderedComponent<DocumentosGeneradosPanel> cut, Guid plantillaId) =>
        cut.FindAll(".barra-filtros select")[0].ChangeAsync(new ChangeEventArgs { Value = plantillaId.ToString() });

    private static List<string> PlantillasDeLaTabla(IRenderedComponent<DocumentosGeneradosPanel> cut) =>
        cut.FindAll("tbody td.nombre-generado").Select(td => td.TextContent.Trim()).ToList();

    // ------------------------------------------------------------------ Cabecera

    [Fact]
    public void La_pagina_usa_la_cabecera_Gen_2_con_su_kicker_y_Volver_a_Plantillas()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso());

        var cut = Render<PaginaDocumentosGenerados>();

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Documentos · Plantillas");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Documentos generados");
        var volver = cabecera.QuerySelector(".acciones-cabecera a")!;
        volver.TextContent.Trim().Should().Be("Volver a Plantillas");
        volver.GetAttribute("href").Should().Be("/plantillas");
        cut.FindAll("h1").Should().ContainSingle("la cabecera antigua no puede convivir con la Gen 2");
    }

    // ------------------------------------------------------------------ Resumen

    [Fact]
    public void Sin_filtro_el_resumen_da_el_total_y_su_desglose_por_estado()
    {
        var mediador = new MediatorFalso();
        mediador.Generados.AddRange([
            Generado(AnexoId, "Anexo II"),
            Generado(AnexoId, "Anexo II", "Nuria Salas", EstadoDocumentoGenerado.GeneradoConAvisos),
            Generado(EpiId, "Registro de EPI", "Iker Mena")]);

        var cut = RenderizarPanel(mediador);

        var resumen = cut.Find(".resumen-generados");
        resumen.TextContent.Trim().Should().Be("3 documentos generados");
        resumen.GetAttribute("title").Should().Be("2 generados sin avisos · 1 generados con avisos");
    }

    [Fact]
    public async Task Con_filtro_el_resumen_no_inventa_el_total_sin_filtrar()
    {
        var mediador = new MediatorFalso();
        mediador.Generados.AddRange([Generado(AnexoId, "Anexo II"), Generado(EpiId, "Registro de EPI")]);
        var cut = RenderizarPanel(mediador);

        await FiltrarPorPlantillaAsync(cut, AnexoId);

        cut.Find(".resumen-generados").TextContent.Trim().Should().Be("1 documento con este filtro",
            "el filtrado es de servidor: el panel no conoce el «de M» que pinta el mockup");
    }

    // ------------------------------------------------------------------ Estados

    [Fact]
    public void Sin_documentos_el_vacio_lleva_al_catalogo_de_plantillas_y_no_hay_resumen_ni_aviso()
    {
        var cut = RenderizarPanel(new MediatorFalso());

        var accion = cut.Find(".estado-vacio a");
        accion.TextContent.Trim().Should().Be("Ir al catálogo de plantillas");
        accion.GetAttribute("href").Should().Be("/plantillas");
        cut.FindAll(".resumen-generados").Should().BeEmpty();
        cut.FindAll(".aviso-generados-con-avisos").Should().BeEmpty();
    }

    [Fact]
    public async Task Si_falla_la_carga_lo_dice_como_error_y_Reintentar_pinta_lo_que_llega()
    {
        var mediador = new MediatorFalso { FallaGenerados = true };
        mediador.Generados.Add(Generado(AnexoId, "Anexo II"));
        var cut = RenderizarPanel(mediador);

        cut.Markup.Should().Contain("No pudimos cargar los documentos generados");
        cut.FindAll(".estado-vacio-icono-acento").Should().ContainSingle("el error se distingue del vacío por su icono de severidad");

        mediador.FallaGenerados = false;
        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => PlantillasDeLaTabla(cut).Should().Equal(["Anexo II"]));
    }

    // ------------------------------------------------------------------ Filas

    [Fact]
    public void Con_filas_se_explica_que_generado_con_avisos_no_es_un_fallo()
    {
        var mediador = new MediatorFalso();
        mediador.Generados.Add(Generado(AnexoId, "Anexo II"));

        var cut = RenderizarPanel(mediador);

        cut.Find(".aviso-generados-con-avisos").TextContent.Should().Contain("«Generado con avisos» no es un fallo: el documento existe.");
    }

    [Fact]
    public void La_lista_va_en_un_marco_propio_con_fecha_copiable_y_enlaces_con_nombre_de_su_fila()
    {
        var mediador = new MediatorFalso();
        var generado = Generado(AnexoId, "Anexo II", "Nuria Salas");
        mediador.Generados.Add(generado);

        var cut = RenderizarPanel(mediador);

        var marco = cut.Find(".marco-lista-generados");
        marco.GetAttribute("role").Should().Be("region");
        marco.GetAttribute("tabindex").Should().Be("0");

        var fila = cut.Find("tbody tr");
        fila.QuerySelector("button.texto-fecha-copiable")!.TextContent.Trim()
            .Should().Be(generado.GeneradoEnUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));

        var enlaces = fila.QuerySelectorAll(".acciones-generado a");
        enlaces.Select(a => a.TextContent.Trim()).Should().Equal(["Ver PDF", "Gestionar"]);
        enlaces[0].GetAttribute("href").Should().Be($"/documentos/{generado.DocumentoId}/archivo");
        enlaces[0].GetAttribute("target").Should().Be("_blank");
        enlaces[0].GetAttribute("aria-label").Should().Be("Ver PDF de Anexo II — Nuria Salas");
        enlaces[1].GetAttribute("href").Should().Be($"/documentos?documentoId={generado.DocumentoId}");
        enlaces[1].GetAttribute("aria-label").Should().Be("Gestionar Anexo II — Nuria Salas");
    }

    [Fact]
    public void El_nombre_de_la_plantilla_solo_enlaza_si_se_conoce_su_version_actual()
    {
        var mediador = new MediatorFalso();
        var anexo = Plantilla(AnexoId, "Anexo II");
        mediador.Plantillas.Add(anexo);
        mediador.Generados.AddRange([Generado(AnexoId, "Anexo II"), Generado(EpiId, "Registro de EPI")]);

        var cut = RenderizarPanel(mediador);

        var celdas = cut.FindAll("tbody td.nombre-generado");
        celdas[0].QuerySelector("a")!.GetAttribute("href").Should().Be($"/plantillas/{anexo.UltimaVersionId}/editar");
        celdas[1].QuerySelector("a").Should().BeNull("sin versión conocida no hay editor al que llevar");
        celdas[1].TextContent.Trim().Should().Be("Registro de EPI");
    }

    // ------------------------------------------------------------------ Atajos

    [Fact]
    public async Task j_y_k_recorren_la_lista_y_Enter_abre_la_ficha_del_documento_enfocado()
    {
        var mediador = new MediatorFalso();
        var anexo = Generado(AnexoId, "Anexo II");
        var epi = Generado(EpiId, "Registro de EPI");
        mediador.Generados.AddRange([anexo, epi]);
        var cut = RenderizarPanel(mediador);
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        string Enfocada() => cut.Find("tr.fila-enfocada td.nombre-generado").TextContent.Trim();

        cut.FindAll("tr.fila-enfocada").Should().BeEmpty("sin atajos no hay fila enfocada");
        var antes = Navegacion.Uri;
        await cut.InvokeAsync(() => atajos.RecibirAtajo("Enter"));
        Navegacion.Uri.Should().Be(antes, "Enter sin fila enfocada no abre nada");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be("Anexo II");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be("Registro de EPI");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Enfocada().Should().Be("Registro de EPI", "j en la última fila se queda en ella");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("k"));
        Enfocada().Should().Be("Anexo II");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("Enter"));
        Navegacion.Uri.Should().EndWith($"/documentos?documentoId={anexo.DocumentoId}");
    }

    // ------------------------------------------------------------- Carreras

    /// <summary>
    /// Se elige el Anexo y, antes de que conteste, el EPI. La respuesta del
    /// Anexo llega la última: no puede pisar la lista del filtro que se ve
    /// elegido. El primer cambio no se espera: su manejador aguarda la
    /// respuesta retenida y el test colgaría; se espera al final.
    /// </summary>
    [Fact]
    public async Task La_respuesta_tardia_de_un_filtro_anterior_no_pisa_la_del_vigente()
    {
        var respuestaAnexo = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso();
        mediador.Generados.AddRange([Generado(AnexoId, "Anexo II"), Generado(EpiId, "Registro de EPI")]);
        var cut = RenderizarPanel(mediador);
        mediador.Retener = p => p is ObtenerDocumentosGeneradosQuery { PlantillaDocumentoId: var id } && id == AnexoId
            ? respuestaAnexo.Task
            : null;

        var cambioAnexo = FiltrarPorPlantillaAsync(cut, AnexoId);
        await FiltrarPorPlantillaAsync(cut, EpiId);
        cut.WaitForAssertion(() => PlantillasDeLaTabla(cut).Should().Equal(["Registro de EPI"]));

        await cut.InvokeAsync(() => respuestaAnexo.SetResult(mediador.Filtrar(new ObtenerDocumentosGeneradosQuery(AnexoId, null))));
        await cambioAnexo;

        PlantillasDeLaTabla(cut).Should().Equal(["Registro de EPI"],
            "la lista es la del filtro vigente, no la de la última respuesta en llegar");
        cut.Markup.Should().NotContain("aria-busy=\"true\"", "la carga superada no deja la lista cargando");
    }

    [Fact]
    public async Task El_fallo_tardio_de_un_filtro_anterior_no_tapa_la_lista_vigente_con_un_error()
    {
        var respuestaAnexo = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso();
        mediador.Generados.AddRange([Generado(AnexoId, "Anexo II"), Generado(EpiId, "Registro de EPI")]);
        var cut = RenderizarPanel(mediador);
        mediador.Retener = p => p is ObtenerDocumentosGeneradosQuery { PlantillaDocumentoId: var id } && id == AnexoId
            ? respuestaAnexo.Task
            : null;

        var cambioAnexo = FiltrarPorPlantillaAsync(cut, AnexoId);
        await FiltrarPorPlantillaAsync(cut, EpiId);
        cut.WaitForAssertion(() => PlantillasDeLaTabla(cut).Should().Equal(["Registro de EPI"]));

        await cut.InvokeAsync(() => respuestaAnexo.SetException(new InvalidOperationException("Fallo tardío simulado.")));
        await cambioAnexo;

        cut.Markup.Should().NotContain("No pudimos cargar los documentos generados");
        PlantillasDeLaTabla(cut).Should().Equal(["Registro de EPI"]);
    }

    /// <summary>
    /// Retirar el panel cancela la consulta en curso, y su respuesta tardía ya
    /// no toca un componente retirado. Que el token quede cancelado demuestra
    /// además que el Dispose se ejecutó: DisposeComponentsAsync lo llama.
    /// </summary>
    [Fact]
    public async Task Retirar_el_panel_cancela_la_carga_en_curso()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso();
        mediador.Retener = p => p is ObtenerDocumentosGeneradosQuery ? respuesta.Task : null;
        Services.AddScoped<IMediator>(_ => mediador);
        Render<DocumentosGeneradosPanel>();

        var token = mediador.Tokens[mediador.Enviadas.FindIndex(p => p is ObtenerDocumentosGeneradosQuery)];
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token del ciclo del panel");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue("retirar el panel cancela la consulta en curso");
        var llegaTarde = () => respuesta.SetResult((IReadOnlyList<DocumentoGeneradoListaDto>)[Generado(AnexoId, "Anexo II")]);
        llegaTarde.Should().NotThrow("la respuesta tardía no repinta un componente retirado");
    }

    /// <summary>Control positivo del de arriba: con el panel vivo, la respuesta retenida sí se pinta.</summary>
    [Fact]
    public async Task Una_carga_retenida_que_llega_con_el_panel_vivo_se_pinta()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso();
        mediador.Retener = p => p is ObtenerDocumentosGeneradosQuery ? respuesta.Task : null;
        Services.AddScoped<IMediator>(_ => mediador);
        var cut = Render<DocumentosGeneradosPanel>();
        cut.Markup.Should().Contain("aria-busy=\"true\"");

        await cut.InvokeAsync(() => respuesta.SetResult((IReadOnlyList<DocumentoGeneradoListaDto>)[Generado(AnexoId, "Anexo II")]));

        cut.WaitForAssertion(() => PlantillasDeLaTabla(cut).Should().Equal(["Anexo II"]));
    }
}
