using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components;

/// <summary>
/// Permite que una pantalla con ruta propia reutilice exactamente el mismo
/// componente dentro del hub de Configuración, sin duplicar lógica ni envolver
/// el contenido con el padding y el título de una página completa.
/// </summary>
public abstract class PaginaIntegrableConfiguracionBase : ComponentBase
{
    [Parameter]
    public bool IntegradaEnConfiguracion { get; set; }

    protected string ClaseContenedorPagina => IntegradaEnConfiguracion
        ? "contenido-panel-configuracion"
        : "contenedor-pagina";

    protected RenderFragment TituloPaginaIntegrable(string titulo) => builder =>
    {
        builder.OpenElement(0, IntegradaEnConfiguracion ? "h2" : "h1");
        builder.AddAttribute(
            1,
            "class",
            IntegradaEnConfiguracion ? "titulo-panel-configuracion" : "titulo-pagina");
        builder.AddContent(2, titulo);
        builder.CloseElement();
    };
}
