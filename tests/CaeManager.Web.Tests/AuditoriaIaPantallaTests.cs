using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de Auditoría IA (Gen 2): cabecera integrable, consulta enviada con
/// sus parámetros, carga vigente frente a respuestas fuera de orden, nombre
/// legible del proveedor, columnas numéricas y el panel de detalle por fila.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> la consulta que llega al mediador —con sus
/// parámetros—, la URL resultante y el marcado. El doble del mediador APLICA
/// el filtro de proveedor de la consulta que recibe: uno que lo ignorase
/// dejaría en verde una pantalla que no lo envía.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> que el servidor filtre de verdad, qué se escribe
/// en <c>AuditoriaExtraccionIa</c> ni quién puede leerlo — eso vive en
/// Application/Infrastructure y en el <c>[Authorize]</c>, que esta suite no
/// ejercita. Tampoco que <c>/documentos?documentoId=</c> abra el documento:
/// solo que el enlace se construye con el DocumentoId del registro.
/// </para>
/// </summary>
public class AuditoriaIaPantallaTests : BunitContext
{
    private sealed class MediatorAuditoriaIa : IMediator
    {
        public List<ObtenerAuditoriaIaQuery> Consultas { get; } = [];
        public List<RegistroAuditoriaIaDto> Filas { get; } = [];

        /// <summary>Total que declara la respuesta; sin valor, el número de filas que casan.</summary>
        public int? TotalDeclarado { get; set; }

        /// <summary>Sustituye la respuesta por defecto — p. ej. por una tarea que el test resuelve cuando quiere.</summary>
        public Func<ObtenerAuditoriaIaQuery, Task<ResultadoPaginado<RegistroAuditoriaIaDto>>>? Responder { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerAuditoriaIaQuery consulta)
                throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");

            Consultas.Add(consulta);
            return Convertir<TResponse>((Responder ?? ResponderAplicandoFiltro)(consulta));
        }

        private Task<ResultadoPaginado<RegistroAuditoriaIaDto>> ResponderAplicandoFiltro(ObtenerAuditoriaIaQuery consulta)
        {
            var casan = Filas.Where(f => consulta.ProveedorCodigo is null || f.ProveedorCodigo == consulta.ProveedorCodigo).ToList();
            return Task.FromResult(new ResultadoPaginado<RegistroAuditoriaIaDto>(
                casan, TotalDeclarado ?? casan.Count, consulta.Pagina, consulta.TamanoPagina));
        }

        private static async Task<TResponse> Convertir<TResponse>(Task<ResultadoPaginado<RegistroAuditoriaIaDto>> tarea) =>
            (TResponse)(object)await tarea;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException("La pantalla es de solo lectura: no envía comandos.");

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private readonly MediatorAuditoriaIa _mediador = new();

    private IRenderedComponent<Features.AuditoriaIa.Pages.AuditoriaIa> Renderizar(string? proveedor = null, bool integrada = false)
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        // BotonCopiar (panel de detalle) importa clipboard.js al pintarse.
        JSInterop.Mode = JSRuntimeMode.Loose;

        // [SupplyParameterFromQuery]: se llega navegando, igual que en el producto.
        Navegacion.NavigateTo(proveedor is null ? "auditoria-ia" : "auditoria-ia?proveedor=" + Uri.EscapeDataString(proveedor));

        return Render<Features.AuditoriaIa.Pages.AuditoriaIa>(parametros => parametros
            .Add(p => p.IntegradaEnConfiguracion, integrada));
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static RegistroAuditoriaIaDto Fila(
        string tipo, string proveedor = "anthropic", long ms = 1200, Guid? documentoId = null,
        DecisionHumanaIa? decision = null, DateTime? fechaDecisionUtc = null, string? hash = null) =>
        new(Guid.NewGuid(), hash ?? new string('a', 64), tipo, proveedor, ms, null, 0.01m, 3, 92,
            Incidencias: null, DateTime.UtcNow, documentoId, decision, UsuarioDecisionId: null, fechaDecisionUtc);

    private static IElement FilaDe(IRenderedComponent<Features.AuditoriaIa.Pages.AuditoriaIa> cut, string tipo) =>
        cut.FindAll("table.tabla-datos tbody tr:not(.fila-detalle-auditoria-ia)")
            .Single(tr => tr.QuerySelectorAll("td")[1].TextContent == tipo);

    private static IElement Boton(IRenderedComponent<Features.AuditoriaIa.Pages.AuditoriaIa> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public void Con_ruta_propia_el_titulo_es_h1_con_la_entradilla_de_solo_lectura()
    {
        var cut = Renderizar();

        cut.Find(".contenedor-pagina header.cabecera-pagina h1").TextContent.Should().Be("Auditoría IA Documental");
        cut.Find(".cabecera-pagina-descripcion").TextContent.Should().Contain("Solo lectura");
    }

    [Fact]
    public void Embebida_en_Configuracion_el_titulo_es_h2_y_no_hay_ningun_h1()
    {
        var cut = Renderizar(integrada: true);

        cut.Find(".contenido-panel-configuracion h2.titulo-panel-configuracion").TextContent.Should().Be("Auditoría IA Documental");
        cut.FindAll("h1").Should().BeEmpty("el hub de Configuración ya pone el h1 de la página");
    }

    [Fact]
    public async Task Cambiar_el_proveedor_consulta_ese_codigo_desde_la_pagina_1_y_lo_lleva_a_la_url()
    {
        _mediador.Filas.AddRange([Fila("Certificado", "anthropic"), Fila("TC2", "gemini")]);
        _mediador.TotalDeclarado = 45;
        var cut = Renderizar();

        await Boton(cut, "Siguiente").ClickAsync(new MouseEventArgs());
        _mediador.Consultas[^1].Pagina.Should().Be(2, "es el punto de partida: el filtro debe devolver a la página 1");

        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "gemini" });

        _mediador.Consultas[^1].Should().Be(new ObtenerAuditoriaIaQuery("gemini", Pagina: 1, TamanoPagina: 30));
        Navegacion.Uri.Should().Contain("proveedor=gemini");
        cut.FindAll("table.tabla-datos tbody tr").Should().ContainSingle()
            .Which.TextContent.Should().Contain("TC2");
    }

    [Fact]
    public async Task Paginar_pide_la_pagina_siguiente_sin_perder_el_filtro()
    {
        _mediador.Filas.Add(Fila("Certificado", "mistral-ocr"));
        _mediador.TotalDeclarado = 45;
        var cut = Renderizar(proveedor: "mistral-ocr");

        await Boton(cut, "Siguiente").ClickAsync(new MouseEventArgs());

        _mediador.Consultas[^1].Should().Be(new ObtenerAuditoriaIaQuery("mistral-ocr", Pagina: 2, TamanoPagina: 30));
    }

    /// <summary>
    /// La carga inicial (sin filtro) sigue en vuelo cuando se filtra por
    /// «gemini»; la respuesta nueva llega primero y la vieja después. Si la
    /// vieja se pintara, la tabla enseñaría filas de Anthropic bajo un
    /// desplegable que dice «Gemini».
    ///
    /// <para>
    /// Cada respuesta se resuelve DENTRO del dispatcher del renderer: la
    /// continuación de la página corre en línea y, al volver el
    /// <c>InvokeAsync</c>, ya ha escrito (o descartado) su estado — es la
    /// barrera que evita un verde vacío en la aserción de ausencia.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_respuesta_tardia_de_la_carga_anterior_no_pisa_la_del_filtro_vigente()
    {
        var pendientes = new List<TaskCompletionSource<ResultadoPaginado<RegistroAuditoriaIaDto>>>();
        _mediador.Responder = _ =>
        {
            var pendiente = new TaskCompletionSource<ResultadoPaginado<RegistroAuditoriaIaDto>>();
            pendientes.Add(pendiente);
            return pendiente.Task;
        };
        var cut = Renderizar();
        pendientes.Should().HaveCount(1, "la carga inicial queda en vuelo");

        var cambio = cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "gemini" });
        pendientes.Should().HaveCount(2);
        _mediador.Consultas[^1].ProveedorCodigo.Should().Be("gemini");

        await cut.InvokeAsync(() => pendientes[1].SetResult(
            new ResultadoPaginado<RegistroAuditoriaIaDto>([Fila("Seguro RC", "gemini")], 1, 1, 30)));
        await cambio;
        await cut.InvokeAsync(() => pendientes[0].SetResult(
            new ResultadoPaginado<RegistroAuditoriaIaDto>([Fila("Reconocimiento médico", "anthropic")], 1, 1, 30)));

        var filas = cut.FindAll("table.tabla-datos tbody tr");
        filas.Should().ContainSingle("solo cuenta la respuesta de la carga vigente");
        filas[0].TextContent.Should().Contain("Seguro RC").And.NotContain("Reconocimiento médico");
    }

    /// <summary>
    /// Mismo orden, pero la carga vieja FALLA al llegar la última: su error no
    /// es de la pantalla que se está viendo y no puede tapar la tabla vigente.
    /// </summary>
    [Fact]
    public async Task Un_fallo_tardio_de_la_carga_anterior_no_tapa_la_tabla_vigente()
    {
        var pendientes = new List<TaskCompletionSource<ResultadoPaginado<RegistroAuditoriaIaDto>>>();
        _mediador.Responder = _ =>
        {
            var pendiente = new TaskCompletionSource<ResultadoPaginado<RegistroAuditoriaIaDto>>();
            pendientes.Add(pendiente);
            return pendiente.Task;
        };
        var cut = Renderizar();

        var cambio = cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "gemini" });
        await cut.InvokeAsync(() => pendientes[1].SetResult(
            new ResultadoPaginado<RegistroAuditoriaIaDto>([Fila("Seguro RC", "gemini")], 1, 1, 30)));
        await cambio;
        await cut.InvokeAsync(() => pendientes[0].SetException(new InvalidOperationException("respuesta vieja")));

        cut.Markup.Should().NotContain("No pudimos cargar la auditoría de IA");
        cut.FindAll("table.tabla-datos tbody tr").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Seguro RC");
    }

    [Fact]
    public async Task Quitar_el_filtro_limpia_tambien_la_url_y_vuelve_a_consultar_sin_filtro()
    {
        var cut = Renderizar(proveedor: "ninguno");
        Navegacion.Uri.Should().Contain("proveedor=ninguno", "es el punto de partida");

        await Boton(cut, "Quitar el filtro").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().NotContain("proveedor=");
        _mediador.Consultas[^1].Should().Be(new ObtenerAuditoriaIaQuery(null, Pagina: 1, TamanoPagina: 30));
    }

    [Fact]
    public void El_badge_de_proveedor_dice_el_nombre_legible_y_un_codigo_desconocido_se_muestra_tal_cual()
    {
        _mediador.Filas.AddRange([
            Fila("Certificado", "mistral-ocr"),
            Fila("TC2", "ninguno"),
            Fila("Seguro RC", "proveedor-nuevo")]);
        var cut = Renderizar();

        FilaDe(cut, "Certificado").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("Mistral OCR");
        FilaDe(cut, "TC2").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("Sin proveedor (fallo)");
        FilaDe(cut, "Seguro RC").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("proveedor-nuevo",
            "inventarle un nombre a un código que la pantalla no conoce sería peor que enseñarlo");
    }

    [Fact]
    public void Un_proveedor_de_la_url_fuera_del_desplegable_se_ofrece_seleccionado()
    {
        var cut = Renderizar(proveedor: "proveedor-nuevo");

        cut.FindAll("select option[value='proveedor-nuevo']").Should().ContainSingle();
        cut.Find("select").GetAttribute("value").Should().Be("proveedor-nuevo");
        _mediador.Consultas[^1].ProveedorCodigo.Should().Be("proveedor-nuevo");
    }

    [Fact]
    public void Un_proveedor_del_desplegable_no_se_duplica()
    {
        var cut = Renderizar(proveedor: "gemini");

        cut.FindAll("select option[value='gemini']").Should().ContainSingle();
    }

    [Fact]
    public void Las_columnas_numericas_van_a_la_derecha_y_el_tiempo_lleva_separador_de_miles()
    {
        _mediador.Filas.Add(Fila("Evaluación de riesgos", ms: 12040));
        var cut = Renderizar();

        var cabeceras = cut.FindAll("table.tabla-datos thead th");
        cabeceras.Where(th => th.ClassList.Contains("celda-numerica-auditoria-ia")).Select(th => th.TextContent)
            .Should().Equal("Páginas", "Confianza", "Coste OCR", "Coste extracción", "Tiempo (ms)");

        var tiempo = FilaDe(cut, "Evaluación de riesgos").QuerySelectorAll("td")[7];
        tiempo.ClassList.Should().Contain("celda-numerica-auditoria-ia");
        tiempo.TextContent.Should().Be("12.040");
    }

    [Fact]
    public void Sin_documento_la_decision_es_una_raya_explicada_y_con_documento_sin_decision_es_Pendiente()
    {
        _mediador.Filas.AddRange([
            Fila("Triaje", documentoId: null),
            Fila("En revisión", documentoId: Guid.NewGuid(), decision: null)]);
        var cut = Renderizar();

        var triaje = FilaDe(cut, "Triaje").QuerySelectorAll("td")[8];
        triaje.TextContent.Trim().Should().Be("—");
        triaje.QuerySelector(".sin-decision-auditoria-ia")!.GetAttribute("title").Should().Contain("sin Documento enlazado");

        FilaDe(cut, "En revisión").QuerySelectorAll("td")[8].TextContent.Trim().Should().Be("Pendiente");
    }

    [Fact]
    public async Task Ver_abre_el_detalle_con_la_huella_la_fecha_de_decision_y_el_enlace_al_documento()
    {
        var documentoId = Guid.NewGuid();
        var hash = "9f0ef40e1234" + new string('b', 52);
        var decididaUtc = new DateTime(2026, 9, 4, 8, 22, 0, DateTimeKind.Utc);
        _mediador.Filas.Add(Fila("Certificado", documentoId: documentoId,
            decision: DecisionHumanaIa.ConfirmadaManual, fechaDecisionUtc: decididaUtc, hash: hash));
        var cut = Renderizar();
        cut.FindAll(".fila-detalle-auditoria-ia").Should().BeEmpty("el detalle solo se monta al abrirlo");

        await Boton(cut, "Ver").ClickAsync(new MouseEventArgs());

        var detalle = cut.Find(".fila-detalle-auditoria-ia");
        detalle.QuerySelector("td")!.GetAttribute("colspan").Should().Be("11", "ocupa todas las columnas de la tabla");
        detalle.QuerySelector(".detalle-auditoria-ia-codigo")!.TextContent.Should().Be("9f0ef40e1234…");
        detalle.QuerySelector(".detalle-auditoria-ia-codigo")!.GetAttribute("title").Should().Be(hash);
        detalle.QuerySelector("button.boton-copiar").Should().NotBeNull("la huella completa se copia con el botón");
        detalle.TextContent.Should().Contain(decididaUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        detalle.QuerySelector("a")!.GetAttribute("href").Should().Be($"/documentos?documentoId={documentoId}");
        Boton(cut, "Cerrar").GetAttribute("aria-expanded").Should().Be("true");

        await Boton(cut, "Cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".fila-detalle-auditoria-ia").Should().BeEmpty();
        Boton(cut, "Ver").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public async Task Sin_documento_enlazado_el_detalle_no_ofrece_enlace()
    {
        _mediador.Filas.Add(Fila("Triaje", documentoId: null));
        var cut = Renderizar();

        await Boton(cut, "Ver").ClickAsync(new MouseEventArgs());

        var detalle = cut.Find(".fila-detalle-auditoria-ia");
        detalle.QuerySelector("a").Should().BeNull();
        detalle.TextContent.Should().Contain("Sin Documento enlazado.");
    }

    [Fact]
    public async Task Cambiar_de_filtro_cierra_el_detalle_abierto()
    {
        _mediador.Filas.AddRange([Fila("Certificado", "anthropic"), Fila("TC2", "gemini")]);
        var cut = Renderizar();
        await FilaDe(cut, "TC2").QuerySelector("button")!.ClickAsync(new MouseEventArgs());
        cut.FindAll(".fila-detalle-auditoria-ia").Should().ContainSingle("es el punto de partida");

        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "gemini" });

        cut.FindAll("table.tabla-datos tbody tr").Should().ContainSingle("la fila de TC2 vuelve cerrada")
            .Which.TextContent.Should().Contain("TC2");
        cut.FindAll(".fila-detalle-auditoria-ia").Should().BeEmpty();
    }
}
