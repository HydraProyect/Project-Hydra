using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Inbound.InformacionDelCentro.Pages;

/// <summary>
/// Información del Centro (tarjeta 15): lista de documentos por Centro con el acuse de las Empresas contratistas y su panel de
/// detalle. Todo se calcula en memoria sobre <see cref="IInformacionDelCentro"/>; mientras el servicio no esté disponible (B-29)
/// la pantalla lo dice y no pinta datos.
/// </summary>
public partial class InformacionDelCentroPagina
{
    private const string ConPendientes = "Pendientes";
    private const string AlDia = "AlDia";

    [Inject] private IInformacionDelCentro Servicio { get; set; } = default!;

    private IReadOnlyList<DocumentoInformacionCentro>? _todos;
    private IReadOnlyList<DocumentoInformacionCentro> _visibles = [];
    private IReadOnlyList<OpcionFranjaEstado> _opcionesAcuse =
    [
        new("Con acuses pendientes", TonoBadge.Advertencia, ConPendientes),
        new("Al día", TonoBadge.Exito, AlDia),
    ];
    private readonly IReadOnlyList<OpcionEstado> _opcionesModalidad =
        [new(nameof(ModalidadInformacion.Firmado), "Firmado"), new(nameof(ModalidadInformacion.Leido), "Leído")];
    private bool _cargando = true;
    private bool _errorCarga;
    private string _busqueda = string.Empty;
    private string _centro = string.Empty;
    private string _modalidad = string.Empty;
    private string? _acuse;
    private Guid? _seleccionId;

    private bool HayDatos => !_cargando && !_errorCarga && _todos is not null;

    private DocumentoInformacionCentro? Seleccionado =>
        _visibles.FirstOrDefault(d => d.Id == _seleccionId) ?? _visibles.FirstOrDefault();

    private IReadOnlyList<OpcionEstado> OpcionesCentro =>
        (_todos ?? []).Select(d => d.Centro).Distinct().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new OpcionEstado(c, c)).ToList();

    private IReadOnlyDictionary<string, int> Recuentos
    {
        get
        {
            var baseSinAcuse = (_todos ?? []).Where(CumpleBusqueda).Where(CumpleCentro).Where(CumpleModalidad).ToList();
            return new Dictionary<string, int>
            {
                [ConPendientes] = baseSinAcuse.Count(d => d.Pendiente),
                [AlDia] = baseSinAcuse.Count(d => !d.Pendiente),
            };
        }
    }

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        try
        {
            _todos = await Servicio.ListarAsync(null);
            Recalcular();
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

    // Con acuses pendientes primero (maqueta), después el orden del servicio.
    private void Recalcular()
    {
        var acuses = SeleccionEstados.Separar(_acuse);
        _visibles = (_todos ?? [])
            .Where(CumpleBusqueda).Where(CumpleCentro).Where(CumpleModalidad)
            .Where(d => acuses.Count == 0 || acuses.Contains(d.Pendiente ? ConPendientes : AlDia))
            .Select((d, i) => (d, i))
            .OrderByDescending(x => x.d.Pendiente).ThenBy(x => x.i)
            .Select(x => x.d)
            .ToList();
    }

    private bool CumpleBusqueda(DocumentoInformacionCentro d) =>
        string.IsNullOrWhiteSpace(_busqueda)
        || d.Nombre.Contains(_busqueda.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || d.Centro.Contains(_busqueda.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private bool CumpleCentro(DocumentoInformacionCentro d) => _centro.Length == 0 || d.Centro == _centro;

    private bool CumpleModalidad(DocumentoInformacionCentro d) => _modalidad.Length == 0 || d.Modalidad.ToString() == _modalidad;

    private Task BuscarAsync(string texto)
    {
        _busqueda = texto ?? string.Empty;
        Recalcular();
        return Task.CompletedTask;
    }

    private void Cambiar(Action cambio)
    {
        cambio();
        Recalcular();
    }

    private Task LimpiarAsync()
    {
        _busqueda = string.Empty;
        _centro = _modalidad = string.Empty;
        _acuse = null;
        Recalcular();
        return Task.CompletedTask;
    }

    private void Elegir(DocumentoInformacionCentro documento) => _seleccionId = documento.Id;

    private static string TextoAcuse(DocumentoInformacionCentro d) =>
        d.Pendiente
            ? $"{d.EmpresasAlDia} de {d.EmpresasTotal} {(d.Modalidad == ModalidadInformacion.Firmado ? "firmaron" : "leyeron")}"
            : "Todas al día";
}
