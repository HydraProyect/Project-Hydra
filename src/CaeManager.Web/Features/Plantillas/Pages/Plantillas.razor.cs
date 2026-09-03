using CaeManager.Web.Components;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Plantillas.Pages;

public partial class Plantillas : ComponentBase
{
    /// <summary>Deep-link de sub-pestaña — mismo mecanismo que Documentos.razor.cs (P1-18, hallazgo de revisión adversarial de Codex): "Documentos generados" perdió su ruta propia al plegarse en pestaña, así que necesita reflejarse en la URL para no perder recarga/enlace compartido/atrás-adelante.</summary>
    [SupplyParameterFromQuery] public string? Pestana { get; set; }

    private string PestanaInicial => Pestana is "generados" ? "generados" : "catalogo";

    private void CambiarPestana(string pestana) =>
        Navigation.ActualizarFiltroEnUrl(nameof(Pestana), pestana == "catalogo" ? null : pestana);
}
