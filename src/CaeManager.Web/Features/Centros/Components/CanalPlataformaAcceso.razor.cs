using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Components;

/// <summary>Ver <c>CanalPlataformaAcceso.razor</c>: la lógica de copia de credenciales que vivía en <c>CentroDetalle</c>, sin cambios.</summary>
public partial class CanalPlataformaAcceso : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _ciclo = new();

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>El Centro al que pertenece el canal: la consulta de credenciales lo exige y comprueba su alcance de gestión.</summary>
    [Parameter, EditorRequired] public Guid CentroId { get; set; }

    [Parameter, EditorRequired] public CanalGestionResumenDto Canal { get; set; } = default!;

    public void Dispose()
    {
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>
    /// Copia de una credencial del canal (DEC-53/DEC-62): la petición al servidor
    /// solo se hace en el momento del clic explícito, nunca al abrir la página, y
    /// el valor no se pinta — va del servidor al portapapeles. La consulta está
    /// marcada como de secretos y usa el alcance de GESTIÓN del Centro.
    /// </summary>
    private Func<Task<string?>> CopiarCredencial(Guid canalId, bool contrasena) => async () =>
    {
        var centroId = CentroId;
        CredencialCanalGestionDto? credencial;
        try
        {
            credencial = await Mediator.Send(new ObtenerCredencialCanalGestionQuery(centroId, canalId), _ciclo.Token);
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            if (centroId == CentroId)
                SegundoFactorParaCredenciales.IrAConfigurar(NavigationManager);
            return null;
        }
        if (centroId != CentroId) return null;

        var valor = contrasena ? credencial?.Contrasena : credencial?.Usuario;
        if (string.IsNullOrEmpty(valor))
        {
            ToastService.Mostrar(contrasena ? "No hay ninguna contraseña guardada." : "No hay ningún usuario guardado.", TonoToast.Info);
            return null;
        }

        return valor;
    };
}
