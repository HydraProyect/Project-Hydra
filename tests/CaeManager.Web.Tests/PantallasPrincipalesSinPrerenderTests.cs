using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Rendimiento de navegación: Empresas, la ficha Empresa 360, Trabajadores, Centros y Documentos cargan sus datos
/// en el ciclo de vida del componente y no usan <c>PersistentComponentState</c>. Con el prerender de
/// <c>InteractiveServer</c>, cada navegación mejorada pedía la página por SSR (consultas completas para pintar el
/// HTML) y volvía a cargarlo todo al montar la raíz en el circuito: medido en local, la navegación a /empresas
/// despachaba 28 consultas de MediatR y 108 comandos SQL, con ObtenerEmpresasQuery, la cartera y los operadores
/// repetidos. Sin prerender la carga corre una sola vez, como en Mi trabajo (M-23). El título de la pestaña lo
/// mantiene <c>TituloDePaginaSinPrerender</c> (WCAG 2.4.2).
/// </summary>
public class PantallasPrincipalesSinPrerenderTests
{
    public static TheoryData<Type, string> Pantallas => new()
    {
        { typeof(CaeManager.Web.Features.Empresas.Pages.Empresas), "Features/Empresas/Pages/Empresas.razor" },
        { typeof(CaeManager.Web.Features.Empresas.Pages.EmpresaDetalle), "Features/Empresas/Pages/EmpresaDetalle.razor" },
        { typeof(CaeManager.Web.Features.Trabajadores.Pages.Trabajadores), "Features/Trabajadores/Pages/Trabajadores.razor" },
        { typeof(CaeManager.Web.Features.Centros.Pages.Centros), "Features/Centros/Pages/Centros.razor" },
        { typeof(CaeManager.Web.Features.Documentos.Pages.Documentos), "Features/Documentos/Pages/Documentos.razor" },
    };

    [Theory]
    [MemberData(nameof(Pantallas))]
    public void La_pantalla_es_InteractiveServer_sin_prerender(Type pantalla, string ruta)
    {
        var atributo = pantalla.GetCustomAttribute<RenderModeAttribute>(inherit: true);

        atributo.Should().NotBeNull($"{ruta} declara @rendermode");
        var modo = atributo!.Mode.Should().BeOfType<InteractiveServerRenderMode>().Subject;
        modo.Prerender.Should().BeFalse(
            $"{pantalla.Name} carga sus datos en el ciclo de vida y, con prerender, los cargaba dos veces en cada navegación");
    }

    [Theory]
    [MemberData(nameof(Pantallas))]
    public void La_pantalla_monta_el_titulo_sin_prerender(Type pantalla, string ruta)
    {
        // Sin prerender, <PageTitle> no llega en el HTML: sin este componente la pestaña queda sin título. Los tests
        // de cada página renderizan con JS suelto y no lo notarían.
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull();

        var razor = File.ReadAllText(Path.Combine(
            [directorio!.FullName, "src", "CaeManager.Web", .. ruta.Split('/')]));

        razor.Should().Contain("<TituloDePaginaSinPrerender Titulo=\"@(", $"{pantalla.Name} no se prerenderiza");
    }
}
