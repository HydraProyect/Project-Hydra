using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Alertas.Pages;

public partial class Alertas : ComponentBase
{
    private int _tamanoPagina = 20;

    /// <summary>
    /// Ámbitos que la reclamación agregada de esta página puede ofrecer
    /// (DEC-4 + DEC-7, PLAN-SESIONES-NOCTURNAS-2026-09-02.md). Solo
    /// Trabajador: el ámbito Empresa no tiene camino de envío construido —
    /// N3 confirma que el dispatcher del selector lanza
    /// <see cref="NotSupportedException"/> para Empresa a propósito, y
    /// <c>AmbitosDisponibles</c> es justo el parámetro que decide qué se
    /// ofrece en pantalla. Ofrecerlo aquí sería una promesa navegable sin
    /// capacidad detrás (A-08) — se pasa tal cual a
    /// <c>SelectorLoteDocumental.AmbitosDisponibles</c> en cuanto el
    /// contrato de N3 (PR #408) aterrice en main.
    /// </summary>
    public static readonly IReadOnlyList<AmbitoAplicacion> AmbitosSoportados = [AmbitoAplicacion.Trabajador];

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>
    /// Permite llegar aquí desde el Dashboard con el filtro de Estado ya
    /// aplicado (p. ej. la tarjeta KPI "Documentos vencidos" enlaza a
    /// "/alertas?estado=Vencido").
    /// </summary>
    [SupplyParameterFromQuery] public string? Estado { get; set; }

    private IReadOnlyList<AlertaDto> _alertas = [];
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;

    private IReadOnlyList<AlertaDto> AlertasFiltradas =>
        Enum.TryParse<EstadoDocumento>(_estadoFiltro, out var estado)
            ? _alertas.Where(a => a.Estado == estado).ToList()
            : _alertas;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(AlertasFiltradas.Count / (double)_tamanoPagina));
    private IReadOnlyList<AlertaDto> AlertasDePagina => AlertasFiltradas.Skip((_pagina - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página, no solo
    /// en el primer render, para que la URL sea la fuente de verdad del
    /// filtro (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _estadoFiltro = !string.IsNullOrWhiteSpace(Estado) && Enum.TryParse<EstadoDocumento>(Estado, out _)
            ? Estado
            : string.Empty;
    }

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private Task CambiarEstadoFiltroAsync(string valor)
    {
        _estadoFiltro = valor;
        _pagina = 1;
        NavigationManager.ActualizarFiltroEnUrl(nameof(Estado), valor);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Un documento faltante (P1-15) no tiene DocumentoId — no hay nada que
    /// "gestionar" todavía. Lleva al drawer de creación con el propietario y
    /// el tipo ya elegidos en vez de a un documento inexistente.
    /// </summary>
    private void GestionarAlerta(AlertaDto alerta) => NavigationManager.NavigateTo(
        alerta.DocumentoId is { } documentoId
            ? $"/documentos?documentoId={documentoId}"
            : $"/documentos?trabajadorId={alerta.TrabajadorId}&tipoDocumentoId={alerta.TipoDocumentoId}");

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _alertas = await Mediator.Send(new ObtenerAlertasQuery());
            _pagina = 1;
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }
}
