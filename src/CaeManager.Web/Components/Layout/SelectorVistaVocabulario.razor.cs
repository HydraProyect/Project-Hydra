using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Components.Layout;

public partial class SelectorVistaVocabulario : ComponentBase
{
    [Inject] private IVistaVocabularioPreviewService VistaPreview { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;

    private PerfilVocabularioTenant? _perfilForzado;
    private string _returnUrl = "/";
    private AntiforgeryRequestToken? _token;

    protected override void OnInitialized()
    {
        _perfilForzado = VistaPreview.PerfilForzado;
        _returnUrl = new Uri(NavigationManager.Uri).PathAndQuery;
        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
    }
}
