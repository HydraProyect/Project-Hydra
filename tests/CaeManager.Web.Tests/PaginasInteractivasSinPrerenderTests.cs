using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Rendimiento de navegación: toda página (<c>@page</c>) con <c>@rendermode InteractiveServer</c> carga sus datos
/// en el ciclo de vida del componente y no usa <c>PersistentComponentState</c>. Con el prerender, cada navegación
/// mejorada pedía la página por SSR (consultas completas para pintar el HTML) y volvía a cargarlo todo al montar la
/// raíz en el circuito: medido en local, la navegación a /empresas despachaba 28 consultas de MediatR y 108 comandos
/// SQL, con ObtenerEmpresasQuery, la cartera y los operadores repetidos. Sin prerender la carga corre una sola vez,
/// como en Mi trabajo (M-23). El título de la pestaña lo mantiene <c>TituloDePaginaSinPrerender</c> (WCAG 2.4.2):
/// sin prerender, <c>&lt;PageTitle&gt;</c> no llega en el HTML.
///
/// <para>
/// El enumerador recorre por reflexión todos los tipos con <c>[Route]</c> y modo de render <c>InteractiveServer</c>:
/// una página nueva entra sola en el ratchet. Una excepción que ya no existe o ya no se prerenderiza pone el test en
/// rojo, para que las listas no se pudran.
/// </para>
/// </summary>
public class PaginasInteractivasSinPrerenderTests
{
    private static readonly Assembly Web = typeof(CaeManager.Web.Features.Empresas.Pages.Empresas).Assembly;

    /// <summary>Páginas que siguen con prerender, con su motivo. Clave: nombre completo del tipo.</summary>
    private static readonly Dictionary<string, string> ConPrerender = new()
    {
        ["CaeManager.Web.Features.Dashboard.Pages.Inicio"] =
            "la primera impresión (Inicio) está pendiente de una decisión de producto (D1)",
        ["CaeManager.Web.Features.Plataforma.Pages.Plataforma"] =
            "lee AntiforgeryStateProvider.GetAntiforgeryToken(), cuyo token llega persistido desde el prerender; sin prerender podría ser null (riesgo señalado por la medición MJ-03)",
        ["CaeManager.Web.Features.VisionCartera.Pages.VisionCartera"] =
            "lee AntiforgeryStateProvider.GetAntiforgeryToken(), cuyo token llega persistido desde el prerender; sin prerender podría ser null (riesgo señalado por la medición MJ-03)",
    };

    /// <summary>Páginas sin prerender que no montan el componente de título, con su motivo.</summary>
    private static readonly Dictionary<string, string> SinTitulo = new()
    {
        ["CaeManager.Web.Features.Clientes.Pages.ImportarClientes"] = "no tenía PageTitle",
        ["CaeManager.Web.Features.Clientes.Pages.ImportarCombinado"] = "no tenía PageTitle",
        ["CaeManager.Web.Features.Documentos.Pages.ImportarDocumentos"] = "no tenía PageTitle",
    };

    private static IEnumerable<Type> PaginasInteractiveServer() =>
        Web.GetTypes()
            .Where(t => !t.IsAbstract && t.GetCustomAttributes<RouteAttribute>(inherit: false).Any())
            .Where(t => t.GetCustomAttribute<RenderModeAttribute>(inherit: true)?.Mode is InteractiveServerRenderMode)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

    public static TheoryData<string> Paginas()
    {
        var datos = new TheoryData<string>();
        foreach (var pagina in PaginasInteractiveServer())
            datos.Add(pagina.FullName!);
        return datos;
    }

    public static TheoryData<string> PaginasSinPrerender()
    {
        var datos = new TheoryData<string>();
        foreach (var pagina in PaginasInteractiveServer()
                     .Where(t => !ConPrerender.ContainsKey(t.FullName!)))
            datos.Add(pagina.FullName!);
        return datos;
    }

    [Fact]
    public void El_enumerador_encuentra_las_paginas_InteractiveServer()
    {
        // Control positivo: si la reflexión dejara de ver las páginas, el ratchet pasaría en vacío.
        PaginasInteractiveServer().Count().Should().BeGreaterThan(50,
            "CaeManager.Web tiene unas setenta páginas InteractiveServer; con menos de 50 el enumerador está ciego");
    }

    [Theory]
    [MemberData(nameof(Paginas))]
    public void La_pagina_es_InteractiveServer_sin_prerender_o_una_excepcion_con_motivo(string nombreDelTipo)
    {
        var pagina = Web.GetType(nombreDelTipo, throwOnError: true)!;
        var modo = pagina.GetCustomAttribute<RenderModeAttribute>(inherit: true)!.Mode
            .Should().BeOfType<InteractiveServerRenderMode>().Subject;

        if (ConPrerender.TryGetValue(nombreDelTipo, out var motivo))
        {
            modo.Prerender.Should().BeTrue(
                $"{pagina.Name} figura como excepción con prerender ({motivo}) pero ya no se prerenderiza: " +
                "quítala de ConPrerender");
            return;
        }

        modo.Prerender.Should().BeFalse(
            $"{pagina.Name} carga sus datos en el ciclo de vida y, con prerender, los cargaba dos veces en cada navegación: " +
            "usa @rendermode @(new InteractiveServerRenderMode(prerender: false)) y monta TituloDePaginaSinPrerender, " +
            "o añade la página a ConPrerender con su motivo");
    }

    [Theory]
    [MemberData(nameof(PaginasSinPrerender))]
    public void La_pagina_sin_prerender_monta_el_titulo(string nombreDelTipo)
    {
        // Sin prerender, <PageTitle> no llega en el HTML: sin este componente la pestaña queda sin título. Los tests
        // de cada página renderizan con JS suelto y no lo notarían.
        var razor = LeerRazor(nombreDelTipo);

        if (SinTitulo.TryGetValue(nombreDelTipo, out var motivo))
        {
            razor.Should().NotContain("<PageTitle", $"{nombreDelTipo} figura en SinTitulo ({motivo}) pero ya lo tiene: quítala de SinTitulo y monta TituloDePaginaSinPrerender");
            return;
        }

        razor.Should().Contain("<PageTitle>", $"{nombreDelTipo} conserva su <PageTitle>");
        razor.Should().Contain("<TituloDePaginaSinPrerender Titulo=\"@(", $"{nombreDelTipo} no se prerenderiza");
    }

    [Fact]
    public void Las_excepciones_siguen_existiendo()
    {
        var paginas = PaginasInteractiveServer().Select(t => t.FullName!).ToHashSet();

        foreach (var (nombre, motivo) in ConPrerender)
            paginas.Should().Contain(nombre, $"la excepción de prerender «{nombre}» ({motivo}) ya no es una página InteractiveServer: quítala de ConPrerender");

        foreach (var (nombre, motivo) in SinTitulo)
        {
            paginas.Should().Contain(nombre, $"la excepción de título «{nombre}» ({motivo}) ya no es una página InteractiveServer: quítala de SinTitulo");
            ConPrerender.Should().NotContainKey(nombre, "una página con prerender conserva su PageTitle y no necesita estar en SinTitulo");
        }
    }

    private static string LeerRazor(string nombreDelTipo)
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull();

        // CaeManager.Web.Features.X.Pages.Pagina -> src/CaeManager.Web/Features/X/Pages/Pagina.razor
        const string raiz = "CaeManager.Web.";
        nombreDelTipo.Should().StartWith(raiz);
        var ruta = Path.Combine(
            [directorio!.FullName, "src", "CaeManager.Web", .. nombreDelTipo[raiz.Length..].Split('.')]) + ".razor";
        File.Exists(ruta).Should().BeTrue($"el .razor de {nombreDelTipo} debe estar en {ruta}");
        return File.ReadAllText(ruta);
    }
}