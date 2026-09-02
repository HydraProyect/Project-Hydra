using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Alertas.Pages;

public partial class Alertas : ComponentBase
{
    private int _tamanoPagina = 20;

    /// <summary>
    /// Ámbitos que la reclamación agregada de esta página ofrece en
    /// <c>SelectorLoteDocumental.AmbitosDisponibles</c> (DEC-4: reclamación
    /// agregada por entidad, trabajador <b>o empresa</b>). Trabajador y
    /// Empresa, que son los dos con camino de reclamación completo detrás
    /// (dominio, agenda, lote y envío — DEC-11: primero el camino, después la
    /// superficie).
    ///
    /// Cliente, Vehículo y Proyecto siguen fuera:
    /// <see cref="ObtenerLoteReclamacionPorFiltroQueryHandler"/> lanza
    /// <see cref="NotSupportedException"/> para ellos a propósito, y
    /// ofrecerlos aquí sería una promesa navegable sin capacidad detrás
    /// (A-08). <c>AlertasTests</c> protege que esta lista no se amplíe a
    /// ninguno de los tres antes de que exista ese camino.
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

    private FiltroLoteDocumental? _filtroLote;
    private bool _cargandoLote;
    private bool _errorLote;
    private IReadOnlyList<LoteReclamacionAgrupadoDto> _lotes = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _seleccionPorTitular = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _contactosPorTitular = [];
    private Guid? _enviandoTitularId;
    private bool _altaContactoVisible;
    private Guid _titularAltaContacto;

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

    private Task ConfirmarFiltroLoteAsync(FiltroLoteDocumental filtro)
    {
        _filtroLote = filtro;
        return CargarLoteAsync();
    }

    private void CambiarFiltroLote()
    {
        _filtroLote = null;
        _lotes = [];
        _seleccionPorTitular.Clear();
        _contactosPorTitular.Clear();
    }

    private async Task CargarLoteAsync()
    {
        if (_filtroLote is not { } filtro) return;

        _cargandoLote = true;
        _errorLote = false;
        StateHasChanged();

        try
        {
            _lotes = await Mediator.Send(new ObtenerLoteReclamacionPorFiltroQuery(filtro));
            _seleccionPorTitular.Clear();
            _contactosPorTitular.Clear();
            foreach (var lote in _lotes)
            {
                _seleccionPorTitular[lote.TitularId] = lote.Documentos.Select(d => d.DocumentoId).ToHashSet();
                _contactosPorTitular[lote.TitularId] =
                    (lote.Destinatarios ?? []).Select(d => d.ContactoId).ToHashSet();
            }
        }
        catch (Exception)
        {
            _errorLote = true;
        }
        finally
        {
            _cargandoLote = false;
        }
    }

    private void AlternarSeleccion(Guid titularId, Guid documentoId, bool seleccionado)
    {
        var seleccionados = _seleccionPorTitular[titularId];
        if (seleccionado) seleccionados.Add(documentoId);
        else seleccionados.Remove(documentoId);
    }

    private HashSet<Guid> ContactosMarcados(Guid titularId) =>
        _contactosPorTitular.TryGetValue(titularId, out var marcados) ? marcados : [];

    private void AlternarContacto(Guid titularId, Guid contactoId, bool marcado)
    {
        if (!_contactosPorTitular.TryGetValue(titularId, out var marcados))
            _contactosPorTitular[titularId] = marcados = [];

        if (marcado) marcados.Add(contactoId);
        else marcados.Remove(contactoId);
    }

    private void AbrirAltaContacto(Guid titularId)
    {
        _titularAltaContacto = titularId;
        _altaContactoVisible = true;
    }

    private async Task EnviarLoteAsync(LoteReclamacionAgrupadoDto lote)
    {
        var documentoIds = _seleccionPorTitular[lote.TitularId].ToList();
        if (documentoIds.Count == 0) return;

        var contactoIds = ContactosMarcados(lote.TitularId).ToList();
        if (contactoIds.Count == 0) return;

        _enviandoTitularId = lote.TitularId;
        try
        {
            // Un comando por ámbito: el titular de un lote de Empresa no es un
            // Cliente y su camino de documentos reclamables es otro (ver
            // ObtenerLoteReclamacionPorFiltroQuery.LoteReclamacionAgrupadoDto).
            var resultado = lote.Ambito == AmbitoAplicacion.Empresa
                ? await Mediator.Send(new EnviarReclamacionEmpresaCommand(
                    lote.TitularId, documentoIds, ContactoIdsSeleccionados: contactoIds))
                : await Mediator.Send(new EnviarReclamacionCommand(
                    lote.TitularId, documentoIds, CentroId: null, ContactoIdsSeleccionados: contactoIds));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar($"Reclamación enviada a {lote.TitularNombre}.", TonoToast.Exito);
            await CargarLoteAsync();
        }
        finally
        {
            _enviandoTitularId = null;
        }
    }

    private static string FormatearHaceTiempo(DateTime fechaUtc)
    {
        var dias = (int)(DateTime.UtcNow - fechaUtc).TotalDays;
        return dias switch
        {
            <= 0 => "hoy",
            1 => "hace 1 día",
            _ => $"hace {dias} días"
        };
    }
}
