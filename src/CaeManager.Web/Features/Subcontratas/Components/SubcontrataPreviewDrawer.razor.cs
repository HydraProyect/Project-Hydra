using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Subcontratas.Components;

public partial class SubcontrataPreviewDrawer : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;

    /// <summary>
    /// La fila de la lista que se está previsualizando. Trae el cumplimiento y
    /// los recuentos ya calculados; lo que la fila no tiene se pide al abrir.
    /// </summary>
    [Parameter] public SubcontrataListaDto? Fila { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<(Guid Id, string Pestana)> OnOperar { get; set; }

    private string _pestanaActiva = "informacion";
    private Guid? _idCargado;

    private SubcontrataDetalleDto? _detalle;
    private bool _cargando;
    private bool _errorCarga;
    private int _totalTrabajadores;
    private IReadOnlyList<string> _nombresPrestaServicioA = [];
    private int _relacionesSinNombreVisible;

    // Reabrir sobre otra fila sin cerrar el panel tiene que recargar desde
    // cero — por eso se compara el Id aquí, no solo Visible. Mismo criterio
    // que VehiculoPreviewDrawer.
    protected override Task OnParametersSetAsync()
    {
        if (Visible && Fila is { } fila && _idCargado != fila.Id)
        {
            _idCargado = fila.Id;
            _pestanaActiva = "informacion";
            return CargarInformacionAsync(fila.Id);
        }

        if (!Visible)
            _idCargado = null;

        return Task.CompletedTask;
    }

    private Task CambiarPestanaAsync(string pestana)
    {
        _pestanaActiva = pestana;
        return Task.CompletedTask;
    }

    private async Task CargarInformacionAsync(Guid subcontrataId)
    {
        _cargando = true;
        _errorCarga = false;
        _detalle = null;
        StateHasChanged();

        try
        {
            _detalle = await Mediator.Send(new ObtenerSubcontrataPorIdQuery(subcontrataId));

            var trabajadores = await Mediator.Send(new ObtenerTrabajadoresQuery(null, SubcontrataId: subcontrataId, TamanoPagina: 1));
            _totalTrabajadores = trabajadores.TotalElementos;

            await ResolverPrestaServicioAAsync();
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Nombres de las contrapartes a las que presta servicio. La relación
    /// devuelve Ids SIN acotar por cartera, y los nombres salen de dos
    /// selectores con contratos distintos:
    /// <list type="bullet">
    /// <item><c>ObtenerClientesParaSelectorQuery</c> SÍ acota por
    /// <c>IAlcanceDatosService</c>: un Cliente empresarial fuera de la
    /// Asignación de Cartera no trae nombre.</item>
    /// <item><c>ObtenerEmpresasParaSelectorQuery</c> NO acota: devuelve todas
    /// las Empresas propias del Tenant propietario. Hueco conocido: hasta que
    /// ese selector aplique el alcance, aquí puede aparecer el nombre de una
    /// Empresa propia fuera de la cartera — igual que ya ocurre en
    /// <c>SubcontrataWorkspacePanel</c>, que usa el mismo selector.</item>
    /// </list>
    /// Un Id que no trae nombre se cuenta como «y N más» en vez de omitirlo
    /// —la lista parecería completa— o de adivinarlo.
    /// </summary>
    private async Task ResolverPrestaServicioAAsync()
    {
        _nombresPrestaServicioA = [];
        _relacionesSinNombreVisible = 0;

        if (_detalle is null) return;

        var ids = _detalle.EmpresaIds.Concat(_detalle.ClienteIds).Distinct().ToList();
        if (ids.Count == 0) return;

        var empresas = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        var clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        var nombrePorId = new Dictionary<Guid, string>();
        foreach (var empresa in empresas) nombrePorId.TryAdd(empresa.Id, empresa.RazonSocial);
        foreach (var cliente in clientes) nombrePorId.TryAdd(cliente.Id, cliente.RazonSocial);

        _nombresPrestaServicioA = ids
            .Where(nombrePorId.ContainsKey)
            .Select(id => nombrePorId[id])
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();
        _relacionesSinNombreVisible = ids.Count - _nombresPrestaServicioA.Count;
    }

    private string TextoPrestaServicioA =>
        (_nombresPrestaServicioA.Count, _relacionesSinNombreVisible) switch
        {
            (0, 0) => "—",
            (0, var sinNombre) => sinNombre == 1 ? "1 empresa fuera de tu cartera" : $"{sinNombre} empresas fuera de tu cartera",
            (_, 0) => string.Join(", ", _nombresPrestaServicioA),
            (_, var sinNombre) => $"{string.Join(", ", _nombresPrestaServicioA)} y {sinNombre} más"
        };

    private string TextoMeta
    {
        get
        {
            var cumplimiento = Fila?.CumplimientoPorcentaje is { } c ? $"{c}% de cumplimiento" : "sin cumplimiento calculable";
            if (_cargando || _detalle is null)
                return cumplimiento;

            var trabajadores = _totalTrabajadores == 1 ? "1 trabajador" : $"{_totalTrabajadores} trabajadores";
            return $"{trabajadores} · {cumplimiento}";
        }
    }

    private Task ReintentarAsync() =>
        Fila is { } fila ? CargarInformacionAsync(fila.Id) : Task.CompletedTask;

    private Task Cerrar() => VisibleChanged.InvokeAsync(false);

    private async Task Operar(string pestana)
    {
        if (Fila is not { } fila) return;
        await VisibleChanged.InvokeAsync(false);
        await OnOperar.InvokeAsync((fila.Id, pestana));
    }
}
