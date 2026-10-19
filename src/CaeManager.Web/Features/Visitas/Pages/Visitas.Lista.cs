using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Application.Visitas;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components;
using CaeManager.Domain.Documentos;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Visitas.Pages;

public partial class Visitas
{
    private static string DetallePreparacion(TrabajadorDocumentacionDto trabajador) =>
        string.Join(" · ", trabajador.Documentacion.Documentos.Where(d => !CaeManager.Web.Features.Documentos.EstadoDocumentoUi.EsCorrecto(d.Estado)).Select(d => d.TipoDocumentoNombre));

    private static CaeManager.Domain.Documentos.EstadoDocumento EstadoDocumentalDe(VisitaListaDto visita) =>
        visita.PeorEstadoDocumental ?? (visita.DocumentacionCompleta ? CaeManager.Domain.Documentos.EstadoDocumento.Vigente : CaeManager.Domain.Documentos.EstadoDocumento.Faltante);
    private string? _agruparPor;
    private string? _ordenColumna;
    private bool _ordenDescendente;
    private bool _listaIniciada;
    private string VistaActual => System.Text.Json.JsonSerializer.Serialize(new
    {
        _busqueda,
        _soloActivas,
        _soloUrgentes,
        _filtroNotificado,
        _orden,
        _estadoFiltro,
        _clienteFiltro,
        _empresaFiltro,
        _ventanaFiltro,
        _agruparPor
    });
    private string _clienteFiltro = string.Empty;
    private string _empresaFiltro = string.Empty;
    private string _ventanaFiltro = string.Empty;
    private IReadOnlyList<CentroSelectorDto> _opcionesDeCentros = [];
    private IReadOnlyList<OpcionEstado> OpcionesCliente => _opcionesDeCentros.Select(c => c.ClienteRazonSocial).Distinct().Order().Select(n => new OpcionEstado(n, n)).ToList();
    private IReadOnlyList<OpcionEstado> OpcionesEmpresa => _opcionesDeCentros.Select(c => c.EmpresaRazonSocial).Distinct().Order().Select(n => new OpcionEstado(n, n)).ToList();
    private IReadOnlyList<OpcionEstado> OpcionesVentana => [new("0", Textos["VentanaHoy"]), new("7", Textos["VentanaSieteDias"]), new("14", Textos["VentanaCatorceDias"])];

    [SupplyParameterFromQuery(Name = "cliente")] public string? ClienteInicial { get; set; }
    [SupplyParameterFromQuery(Name = "empresa")] public string? EmpresaInicial { get; set; }
    [SupplyParameterFromQuery(Name = "ventana")] public string? VentanaInicial { get; set; }

    private async Task CambiarFiltroDisenoAsync(string clave, string valor)
    {
        if (clave == "cliente") _clienteFiltro = valor;
        if (clave == "empresa") _empresaFiltro = valor;
        if (clave == "ventana") _ventanaFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl(clave, valor);
        await RecargarAsync();
    }

    private static string NormalizarVentana(string? valor) => SeleccionFiltroVisitas.Escribir(
        SeleccionFiltroVisitas.Leer(valor).Where(v => v is "0" or "7" or "14"));

    private Task QuitarValorFiltroAsync(string clave, string valor)
    {
        var seleccion = clave switch { "cliente" => _clienteFiltro, "empresa" => _empresaFiltro, _ => _ventanaFiltro };
        return CambiarFiltroDisenoAsync(clave, SeleccionFiltroVisitas.Escribir(SeleccionFiltroVisitas.Leer(seleccion).Where(v => v != valor)));
    }

    private async Task CargarOpcionesDisenoAsync()
    {
        try { _opcionesDeCentros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery()); }
        catch (Exception) { _opcionesDeCentros = []; }
    }
    private readonly HashSet<Guid> _expandidas = [];
    private readonly HashSet<Guid> _preparacionCargando = [];
    private readonly HashSet<Guid> _preparacionErrores = [];
    private readonly Dictionary<Guid, DocumentacionVisitaDto> _preparaciones = [];
    private readonly Dictionary<Guid, long> _versionesPreparacion = [];
    private long _versionPreparacion;
    private bool _expandiendoTodas;
    private bool TodasExpandidas => _elementosPagina.Count > 0 && _elementosPagina.All(v => _expandidas.Contains(v.Id));
    private IReadOnlyList<OpcionAgrupar> OpcionesAgrupar => [new("cliente", Textos["FiltroCliente"])];

    [SupplyParameterFromQuery(Name = "agrupar")]
    public string? AgruparInicial { get; set; }

    private IEnumerable<IGrouping<Guid, VisitaListaDto>> GruposDeVisitas =>
        _elementosPagina.GroupBy(v => _agruparPor == "cliente" ? EmpresaTitularDe(v) : Guid.Empty);

    private async Task CambiarAgrupacionAsync(string? valor)
    {
        _agruparPor = valor == "cliente" ? valor : null;
        NavigationManager.ActualizarFiltroEnUrl("agrupar", _agruparPor);
        await RecargarAsync();
    }

    private Task OrdenarFechasAsync() => OrdenarColumnaAsync(nameof(VisitaListaDto.FechaInicio));

    private async Task OrdenarColumnaAsync(string columna)
    {
        _ordenDescendente = _ordenColumna == columna && !_ordenDescendente;
        _ordenColumna = columna;
        _orden = string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("orden", null);
        await RecargarAsync();
    }

    private string AriaOrden(string columna) => _ordenColumna == columna
        ? (_ordenDescendente ? "descending" : "ascending") : "none";

    private static string ReferenciaVisita(Guid id) => $"VIS-{id.ToString("N")[..8].ToUpperInvariant()}";

    private static string AnchoPreparacion(VisitaListaDto visita) =>
        $"width: {new FraccionCumplimiento(visita.TrabajadoresListos ?? 0, visita.TotalTrabajadores).Porcentaje ?? 0}%";
    private async Task AlternarPreparacionAsync(VisitaListaDto visita)
    {
        if (!_expandidas.Add(visita.Id)) { _expandidas.Remove(visita.Id); return; }
        if (!_preparaciones.ContainsKey(visita.Id)) await CargarPreparacionAsync(visita.Id);
    }

    private async Task ExpandirTodoAsync()
    {
        if (TodasExpandidas) { _expandidas.Clear(); return; }
        _expandiendoTodas = true;
        var carga = _cargaLista;
        try
        {
            foreach (var visita in _elementosPagina.ToList())
            {
                if (carga != _cargaLista) return;
                _expandidas.Add(visita.Id);
                if (!_preparaciones.ContainsKey(visita.Id)) await CargarPreparacionAsync(visita.Id);
            }
        }
        finally { _expandiendoTodas = false; }
    }

    private async Task CargarPreparacionAsync(Guid id)
    {
        if (!_preparacionCargando.Add(id)) return;
        _preparacionErrores.Remove(id);
        var carga = _cargaLista;
        var version = ++_versionPreparacion;
        _versionesPreparacion[id] = version;
        try
        {
            var preparacion = await Mediator.Send(new ObtenerDocumentacionVisitaQuery(id));
            if (carga != _cargaLista || _versionesPreparacion.GetValueOrDefault(id) != version) return;
            if (preparacion is null) _preparacionErrores.Add(id);
            else _preparaciones[id] = preparacion;
        }
        catch (Exception)
        {
            if (carga == _cargaLista && _versionesPreparacion.GetValueOrDefault(id) == version) _preparacionErrores.Add(id);
        }
        finally
        {
            if (_versionesPreparacion.GetValueOrDefault(id) == version) _preparacionCargando.Remove(id);
        }
    }
}
