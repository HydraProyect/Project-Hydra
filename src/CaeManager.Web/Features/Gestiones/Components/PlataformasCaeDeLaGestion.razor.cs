using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Gestiones.Components;

/// <summary>Ver <c>PlataformasCaeDeLaGestion.razor</c>.</summary>
public partial class PlataformasCaeDeLaGestion : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _ciclo = new();
    private IReadOnlyList<CanalGestionResumenDto> _plataformas = [];
    private bool _cargando = true;
    private bool _error;
    private Guid _centroCargado;

    [Inject] private IMediator Mediator { get; set; } = default!;

    /// <summary>El Centro de la Gestión: sus canales de gestión de tipo Plataforma son «dónde hay que hacerla».</summary>
    [Parameter, EditorRequired] public Guid CentroId { get; set; }

    [Parameter] public string? CentroNombre { get; set; }

    public void Dispose()
    {
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>
    /// Se pide una vez por Centro: otra Gestión del mismo Centro no repite la consulta, y una del otro Centro descarta
    /// la respuesta que aún viajaba (la comparación con <see cref="_centroCargado"/> tras el <c>await</c>).
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (CentroId == _centroCargado)
            return;

        var centroId = CentroId;
        _centroCargado = centroId;
        _cargando = true;
        _error = false;
        _plataformas = [];

        try
        {
            var canales = await Mediator.Send(new ObtenerCanalesGestionDeCentroQuery(centroId), _ciclo.Token);
            if (centroId != _centroCargado) return;
            _plataformas = canales.Where(c => c.Tipo == TipoCanalGestion.Plataforma).ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (centroId == _centroCargado) _error = true;
        }
        finally
        {
            if (centroId == _centroCargado) _cargando = false;
        }
    }
}
