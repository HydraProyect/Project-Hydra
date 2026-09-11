using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hub de Configuración (<c>/configuracion/{entrada?}</c>) contra su mockup
/// Gen 2 («Configuracion TALVEG.dc.html»).
///
/// <para>
/// <b>Qué observa.</b> El marco del hub, no las páginas que embebe: una sola
/// cabecera con un solo h1; la navegación agrupada tal como la declara el
/// catálogo (catorce entradas, cuatro listas nombradas por su grupo, sin
/// Delegaciones ni Estado comercial); qué entrada lleva
/// <c>aria-current="page"</c> y que se mueve con la ruta; el nombre accesible
/// de cada entrada y de las migas (lo que queda fuera de <c>aria-hidden</c>);
/// que el hub pone título solo a los paneles que no traen el suyo; y el
/// destino del enlace de salida («plataforma») por ruta y por query.
/// </para>
///
/// <para>
/// <b>Qué NO observa.</b> El aspecto (bUnit no evalúa CSS), la política
/// <c>[Authorize(Roles = Administrador)]</c> del hub ni la de las páginas
/// embebidas (bUnit no la aplica), ni el comportamiento interno de esas
/// páginas, que tiene sus propios tests Gen 2.
/// </para>
/// </summary>
public class ConfiguracionHubGen2Tests : BunitContext
{
    public ConfiguracionHubGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly IReadOnlyList<ClienteSelectorDto> ClientesEmpresariales =
    [
        new(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001"), "Refrielectric S.L."),
        new(Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002"), "Montajes Ebro S.A.")
    ];

    /// <summary>
    /// Catálogo esperado, en orden: grupo, nombre, descripción, destino. La
    /// descripción de «ia» es la corregida, no la del mockup («Qué se extrae y
    /// con qué umbral»): no existe ningún umbral.
    /// </summary>
    private static readonly (string Grupo, string Nombre, string Descripcion, string Ruta)[] CatalogoEsperado =
    [
        ("Acceso e identidad", "Usuarios", "Cuentas y carteras asignadas", "/configuracion/usuarios"),
        ("Acceso e identidad", "Roles", "Permisos por perfil", "/configuracion/roles"),
        ("Plataforma y conexiones", "Claves API", "Acceso programático", "/configuracion/api"),
        ("Plataforma y conexiones", "Conexiones de integración", "M365, portales, webhooks", "/configuracion/integraciones"),
        ("Plataforma y conexiones", "Importar datos", "Cuadro de Control CAE (Excel)", "/configuracion/importar"),
        ("Plataforma y conexiones", "Administración de plataforma", "Inicialización e identidad raíz", "/configuracion/plataforma"),
        ("Catálogos y datos", "Tipos de documento", "Catálogo y vigencias", "/configuracion/tipos"),
        ("Catálogos y datos", "Lectura IA por Cliente empresarial", "Restricción por tipo de documento", "/configuracion/ia"),
        ("Catálogos y datos", "Macros de respuesta", "Plantillas de comunicación", "/configuracion/macros"),
        ("Catálogos y datos", "Parámetros del sistema", "Umbrales del semáforo", "/configuracion/params"),
        ("Catálogos y datos", "Retención de datos", "Plazos de borrado", "/configuracion/retencion"),
        ("Auditoría", "Auditoría", "Quién hizo qué y cuándo", "/configuracion/auditoria"),
        ("Auditoría", "Auditoría IA", "Lecturas y decisiones automáticas", "/configuracion/auditoria-ia"),
        ("Auditoría", "Automatizaciones", "Trabajos del sistema", "/configuracion/automatizaciones")
    ];

    // ---------------------------------------------------------------- dobles

    /// <summary>
    /// Responde al selector de Lectura IA; cualquier otra lectura (la de
    /// Parámetros del sistema) se queda pendiente a propósito: el panel sigue
    /// cargando y lo que se ve del contenido es solo lo que pone el hub.
    /// </summary>
    private sealed class MediadorDelHub : IMediator
    {
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return request is ObtenerClientesParaSelectorQuery
                ? Task.FromResult((TResponse)(object)ClientesEmpresariales.ToList())
                : new TaskCompletionSource<TResponse>().Task;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException("El hub no envía comandos en estos tests.");

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

    // ---------------------------------------------------------------- arnés

    private MediadorDelHub Registrar()
    {
        var mediador = new MediadorDelHub();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<SeleccionarClienteLecturaIa>>(_ => NullLogger<SeleccionarClienteLecturaIa>.Instance);
        return mediador;
    }

    private IRenderedComponent<Configuracion> RenderizarHub(string? entrada)
    {
        Registrar();
        return Render<Configuracion>(p => p.Add(x => x.EntradaRuta, entrada));
    }

    /// <summary>
    /// Texto que llega a la tecnología de apoyo: el del nodo sin los subárboles
    /// marcados <c>aria-hidden="true"</c>, con los espacios normalizados.
    /// </summary>
    private static string TextoAccesible(INode nodo)
    {
        static string Recorrer(INode n) => n switch
        {
            IElement e when e.GetAttribute("aria-hidden") == "true" => " ",
            IElement e => string.Concat(e.ChildNodes.Select(Recorrer)),
            _ => " " + n.TextContent + " "
        };

        return Regex.Replace(Recorrer(nodo), @"\s+", " ").Trim();
    }

    private static IElement Navegacion(IRenderedComponent<Configuracion> cut) =>
        cut.Find("nav[aria-label='Secciones de configuración']");

    private static IElement Contenido(IRenderedComponent<Configuracion> cut) =>
        cut.Find(".contenido-configuracion");

    // ---------------------------------------------------------------- cabecera

    [Fact]
    public void La_cabecera_es_la_primitiva_con_Sistema_encima_y_un_solo_h1()
    {
        var cut = RenderizarHub("plataforma");

        var cabecera = cut.FindComponents<CabeceraPagina>().First().Instance;
        cabecera.Kicker.Should().Be("Sistema");
        cabecera.Titulo.Should().Be("Configuración");
        cabecera.Integrada.Should().BeFalse("la cabecera del hub es la de la página: h1");

        cut.FindAll("h1").Select(h => h.TextContent.Trim()).Should().Equal(["Configuración"]);
    }

    // ---------------------------------------------------------------- navegación

    [Fact]
    public void La_navegacion_agrupa_las_catorce_entradas_en_listas_nombradas_por_su_grupo()
    {
        var cut = RenderizarHub("plataforma");

        var leidas = new List<(string Grupo, string Nombre, string Descripcion, string Ruta)>();
        var listas = Navegacion(cut).QuerySelectorAll("ul");
        listas.Should().HaveCount(4, "un grupo del mockup, una lista");

        foreach (var lista in listas)
        {
            var idTitulo = lista.GetAttribute("aria-labelledby");
            idTitulo.Should().NotBeNullOrEmpty("sin nombre, el lector anuncia «lista» sin decir de qué grupo");
            var titulo = cut.Find($"#{idTitulo}");
            titulo.Closest("nav")?.GetAttribute("aria-label").Should().Be("Secciones de configuración",
                "el título que la nombra vive en la propia navegación");

            foreach (var enlace in lista.QuerySelectorAll("li > a"))
            {
                leidas.Add((titulo.TextContent.Trim(),
                    enlace.QuerySelector(".nombre-entrada-subnav")!.TextContent.Trim(),
                    enlace.QuerySelector(".descripcion-entrada-subnav")!.TextContent.Trim(),
                    enlace.GetAttribute("href")!));
            }
        }

        leidas.Should().Equal(CatalogoEsperado);
        leidas.Select(e => e.Nombre).Should().NotContain(["Delegaciones", "Estado comercial"],
            "su autoridad es de capacidad AdminPlataforma y viven en el grupo Plataforma del menú lateral");
        leidas.Select(e => e.Descripcion).Should().NotContain(d => d.Contains("umbral", StringComparison.OrdinalIgnoreCase)
            && !d.StartsWith("Umbrales del semáforo", StringComparison.Ordinal),
            "la Lectura IA no tiene umbral: la descripción corregida no puede volver al texto del mockup");
    }

    [Fact]
    public void Cada_entrada_es_un_enlace_que_el_teclado_alcanza_y_su_nombre_no_lee_la_abreviatura()
    {
        var cut = RenderizarHub("plataforma");

        var enlaces = Navegacion(cut).QuerySelectorAll("a");
        enlaces.Should().HaveCount(14);

        foreach (var enlace in enlaces)
        {
            enlace.HasAttribute("href").Should().BeTrue("un enlace sin href no entra en el orden de tabulación");
            enlace.GetAttribute("tabindex").Should().BeNull("nadie saca una entrada del orden de tabulación");
            enlace.GetAttribute("role").Should().BeNull("sigue siendo un enlace: Intro abre, no una pestaña falsa");

            var nombre = enlace.QuerySelector(".nombre-entrada-subnav")!.TextContent.Trim();
            var abreviatura = enlace.QuerySelector(".icono-entrada-subnav")!.TextContent.Trim();
            TextoAccesible(enlace).Should().StartWith(nombre,
                $"«{abreviatura}» es decoración: el nombre accesible empieza por la entrada, no por la abreviatura");
        }
    }

    [Fact]
    public async Task Solo_la_entrada_vigente_lleva_aria_current_y_se_mueve_al_cambiar_la_ruta()
    {
        var cut = RenderizarHub("params");

        var actuales = Navegacion(cut).QuerySelectorAll("[aria-current]");
        actuales.Should().ContainSingle();
        actuales[0].GetAttribute("aria-current").Should().Be("page");
        actuales[0].GetAttribute("href").Should().Be("/configuracion/params");

        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.EntradaRuta, "ia")));

        var tras = Navegacion(cut).QuerySelectorAll("[aria-current]");
        tras.Should().ContainSingle();
        tras[0].GetAttribute("href").Should().Be("/configuracion/ia");

        // El panel de Parámetros se retira y entra la página de Lectura IA.
        cut.WaitForAssertion(() =>
            Contenido(cut).QuerySelectorAll("a.item-seleccion-cliente").Should().HaveCount(2));
        cut.FindComponents<Features.Configuracion.Components.ParametrosSistemaPanel>().Should().BeEmpty();
        Contenido(cut).QuerySelectorAll("h2").Select(h => h.TextContent.Trim())
            .Should().Equal(["Lectura IA por Cliente empresarial"]);
    }

    [Fact]
    public void Las_migas_nombran_grupo_y_entrada_vigentes_y_callan_las_flechas()
    {
        var cut = RenderizarHub("ia");

        var migas = cut.Find(".migas-configuracion");
        TextoAccesible(migas).Should().Be("Configuración Catálogos y datos Lectura IA por Cliente empresarial");
        migas.TextContent.Should().Contain("→", "las flechas del mockup se ven");
    }

    // ---------------------------------------------------------------- contenido

    [Fact]
    public void Parametros_recibe_del_hub_su_titulo_y_una_entradilla_que_no_promete_una_pasada_nocturna()
    {
        var cut = RenderizarHub("params");

        var contenido = Contenido(cut);
        contenido.QuerySelectorAll("h2").Select(h => h.TextContent.Trim())
            .Should().Equal(["Parámetros del sistema"], "el panel no trae cabecera: sin la del hub, el contenido no tendría título");

        var entradilla = contenido.QuerySelector(".cabecera-pagina-descripcion")!.TextContent;
        entradilla.Should().NotContainEquivalentOf("nocturna", "el estado documental se calcula en vivo, no hay pasada nocturna");
        entradilla.Should().NotContainEquivalentOf("recalcula");
        entradilla.Should().Contain("jornada").And.Contain("IA",
            "el panel también contiene jornada, medición de tiempo y presupuesto de IA");

        cut.FindAll("h1").Should().ContainSingle();
    }

    [Fact]
    public void Una_pagina_integrable_trae_su_propio_h2_y_el_hub_no_le_pone_otro_encima()
    {
        var cut = RenderizarHub("ia");

        cut.WaitForAssertion(() =>
            Contenido(cut).QuerySelectorAll("a.item-seleccion-cliente").Should().HaveCount(2));

        Contenido(cut).QuerySelectorAll("h2").Select(h => h.TextContent.Trim())
            .Should().Equal(["Lectura IA por Cliente empresarial"]);
        cut.FindAll("h1").Select(h => h.TextContent.Trim()).Should().Equal(["Configuración"]);
    }

    [Fact]
    public void Una_entrada_desconocida_cae_en_Parametros_como_hasta_ahora()
    {
        var cut = RenderizarHub("no-existe");

        Navegacion(cut).QuerySelector("[aria-current=page]")!.GetAttribute("href").Should().Be("/configuracion/params");
        Contenido(cut).QuerySelector("h2")!.TextContent.Trim().Should().Be("Parámetros del sistema");
    }

    [Fact]
    public void El_parametro_entry_por_query_sigue_eligiendo_la_entrada()
    {
        Registrar();
        Services.GetRequiredService<NavigationManager>().NavigateTo("configuracion?entry=ia");

        var cut = Render<Configuracion>();

        Navegacion(cut).QuerySelector("[aria-current=page]")!.GetAttribute("href").Should().Be("/configuracion/ia");
    }

    [Fact]
    public void El_enlace_de_salida_por_query_tambien_redirige_a_su_ruta_literal()
    {
        Registrar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("configuracion?entry=plataforma");

        Render<Configuracion>();

        navegacion.Uri.Should().EndWith("/configuracion/plataforma");
    }

    [Fact]
    public void El_enlace_de_salida_dice_que_tiene_pantalla_propia_y_ofrece_ir_en_vez_de_prometerla()
    {
        var cut = RenderizarHub("plataforma");

        var contenido = Contenido(cut);
        contenido.TextContent.Should().NotContainEquivalentOf("pendiente de especificación",
            "/configuracion/plataforma existe: decir que está pendiente es falso");
        contenido.QuerySelector("a[href='/configuracion/plataforma']")!.TextContent.Trim()
            .Should().Be("Ir a Administración de plataforma");
    }
}
