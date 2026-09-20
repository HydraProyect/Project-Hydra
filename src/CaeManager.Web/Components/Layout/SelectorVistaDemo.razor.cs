using CaeManager.Application.VistaDemo;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Components.Layout;

public partial class SelectorVistaDemo : ComponentBase
{
    [Inject] private IServiceProvider Servicios { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;

    private bool _disponible;
    private IReadOnlyList<GestorDeVistaDemo> _gestores = [];
    private string _opcionActual = "direccion";
    private string _etiquetaActual = "Dirección";
    private string _returnUrl = "/";
    private AntiforgeryRequestToken? _token;

    protected override async Task OnInitializedAsync()
    {
        // Opcional: sin la lente registrada (otro host, tests) el selector no existe.
        if (Servicios.GetService<IVistaDemoActual>() is not { } vistaDemoActual) return;

        _disponible = await vistaDemoActual.EstaDisponibleAsync();
        if (!_disponible) return;

        _gestores = await vistaDemoActual.ObtenerGestoresElegiblesAsync();

        // Lo que se muestra es la vista EFECTIVA, ya validada, no la petición cruda de la cookie:
        // si la cookie no vale (o el Tenant activo no es de demo), el selector dice «Dirección»,
        // que es lo que de verdad se está viendo.
        var efectiva = await vistaDemoActual.ObtenerEfectivaAsync();
        (_opcionActual, _etiquetaActual) = efectiva switch
        {
            { Vista: Application.VistaDemo.VistaDemo.CoordinadorCae } => ("coordinador", "Coordinador CAE"),
            { Vista: Application.VistaDemo.VistaDemo.GestorCae, GestorUsuarioId: { } id } =>
                ($"gestor:{id:N}", _gestores.FirstOrDefault(g => g.UsuarioId == id)?.Nombre is { } nombre ? $"Gestor CAE: {nombre}" : "Gestor CAE"),
            _ => ("direccion", "Dirección"),
        };

        _returnUrl = new Uri(NavigationManager.Uri).PathAndQuery;
        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
    }
}
