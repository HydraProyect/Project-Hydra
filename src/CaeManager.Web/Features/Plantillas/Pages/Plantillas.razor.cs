using CaeManager.Web.Components;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Plantillas.Pages;

public partial class Plantillas : CaeManager.Web.Components.PaginaInteractiva
{
    /// <summary>Deep-link de la vista — mismo mecanismo que Documentos.razor.cs (P1-18, hallazgo de revisión adversarial de Codex): "Documentos generados" perdió su ruta propia al plegarse en vista, así que necesita reflejarse en la URL para no perder recarga/enlace compartido/atrás-adelante.</summary>
    [SupplyParameterFromQuery] public string? Pestana { get; set; }

    private string PestanaInicial => Pestana is "generados" ? "generados" : "catalogo";

    /// <summary>Plantillas del catálogo, sin filtrar: la cifra junto al título. Sin valor hasta que carga.</summary>
    private int? _total;

    private void CambiarPestana(string pestana) =>
        Navigation.ActualizarFiltroEnUrl(nameof(Pestana), pestana == "catalogo" ? null : pestana);

    private void CambiarTotal(int total)
    {
        _total = total;
        StateHasChanged();
    }
}
