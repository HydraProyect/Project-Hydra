using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Delegaciones.Recursos;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Delegaciones.Pages;

public partial class Delegaciones
{
    [Inject] private NavigationManager NavegacionLista { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosDelegaciones> TextosLista { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "q")] public string? BusquedaLista { get; set; }
    [SupplyParameterFromQuery(Name = "estado")] public string? EstadoLista { get; set; }
    [SupplyParameterFromQuery(Name = "tipo")] public string? TipoLista { get; set; }
    [SupplyParameterFromQuery(Name = "agrupar")] public string? AgrupacionLista { get; set; }

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
    private readonly ConexionVistaRecordada _vistaRecordada = new();
    private readonly GruposAbiertos _gruposLista = new();
    private readonly HashSet<Guid> _detallesLista = [];
    private readonly HashSet<Guid> _personasCompletas = [];
    private readonly HashSet<Guid> _seleccionLista = [];
    private bool _seleccionMultipleLista;
    private int _paginaLista = 1;
    private int _tamanoLista = 20;
    private bool Agrupada => AgrupacionLista == "tipo";
    private string? _parametrosListaVistos;
    private void SincronizarParametrosLista()
    {
        EstadoLista = SeleccionEstados.Unir(SeleccionEstados.Separar(EstadoLista).Where(v => EstadosLista.Any(o => o.Valores.Contains(v))));
        if (TipoLista is not ("Soporte" or "OperadorExterno")) TipoLista = null;
        if (AgrupacionLista != "tipo") AgrupacionLista = null;
        var firma = System.Text.Json.JsonSerializer.Serialize(new[] { BusquedaLista, EstadoLista, TipoLista, AgrupacionLista });
        if (firma == _parametrosListaVistos) return;
        _parametrosListaVistos = firma;
        _paginaLista = 1;
        _seleccionLista.Clear();
        _detallesLista.Clear();
        _personasCompletas.Clear();
    }
    private bool HayFiltrosLista => !string.IsNullOrWhiteSpace(BusquedaLista) || !string.IsNullOrEmpty(EstadoLista) || !string.IsNullOrEmpty(TipoLista);
    private string TipoDeLista(DelegacionDto d) => d.EsSoporte ? "Soporte" : "OperadorExterno";
    private string EstadoDeLista(DelegacionDto d) => d.EsSoporte
        ? d.AccesoVigente ? "Abierto" : d.VentanaCaducada ? "Caducada" : "SinAcceso"
        : d.Activa ? "Activa" : "Revocada";
    private string NombreLista(DelegacionDto d) => d.SomosLaConsultora ? d.ClienteNombre : d.ConsultoraNombre;
    private string TextoEstadoLista(DelegacionDto d) => TextosLista[EstadoDeLista(d)];
    private IEnumerable<DelegacionDto> FilasSinEstado => _delegaciones.Where(d =>
        (string.IsNullOrWhiteSpace(BusquedaLista) || (d.ClienteNombre + " " + d.ConsultoraNombre).Contains(BusquedaLista.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(TipoLista) || TipoDeLista(d) == TipoLista));
    private IReadOnlyList<DelegacionDto> FilasFiltradas => FilasSinEstado.Where(d =>
        string.IsNullOrEmpty(EstadoLista) || SeleccionEstados.Separar(EstadoLista).Contains(EstadoDeLista(d))).ToList();
    private IReadOnlyDictionary<string, int> RecuentosLista => FilasSinEstado.GroupBy(EstadoDeLista).ToDictionary(g => g.Key, g => g.Count());
    private IReadOnlyList<OpcionFranjaEstado> EstadosLista => [
        new(TextosLista["Abierto"], TonoBadge.Advertencia, "Abierto"),
        new(TextosLista["Activa"], TonoBadge.Exito, "Activa"),
        new(TextosLista["Caducada"], TonoBadge.Neutro, "Caducada"),
        new(TextosLista["SinAcceso"], TonoBadge.Neutro, "SinAcceso"),
        new(TextosLista["Revocada"], TonoBadge.Neutro, "Revocada")];
    private IReadOnlyList<OpcionEstado> TiposLista => [new("OperadorExterno", TextosLista["OperadorExterno"]), new("Soporte", TextosLista["Soporte"])];
    private IReadOnlyList<OpcionEstado> AgrupacionesLista => [new("tipo", TextosLista["Tipo"])];
    private IEnumerable<FilaDeGrupo<DelegacionDto>> BloquesLista => GruposDeLista.Agrupar(
        Agrupada ? FilasFiltradas : FilasFiltradas.Skip((_paginaLista - 1) * _tamanoLista).Take(_tamanoLista),
        Agrupada, TipoDeLista, (clave, _) => TextosLista[clave], _ => false, _gruposLista);
    private IReadOnlyList<DelegacionDto> FilasVisiblesLista => BloquesLista.Where(f => !f.Oculta).Select(f => f.Fila).ToList();
    private bool TodasSeleccionadasLista => FilasVisiblesLista.Count > 0 && FilasVisiblesLista.All(d => _seleccionLista.Contains(d.Id));
    private void AlternarSeleccionLista(Guid id) { if (!_seleccionLista.Remove(id)) _seleccionLista.Add(id); }
    private void SeleccionarVisiblesLista(bool valor)
    {
        _seleccionLista.Clear();
        if (valor) _seleccionLista.UnionWith(FilasVisiblesLista.Select(d => d.Id));
    }
    private void AlternarGrupoLista(string clave)
    {
        _gruposLista.Alternar(clave);
        _seleccionLista.IntersectWith(FilasVisiblesLista.Select(d => d.Id));
    }
    private void AlternarDetalleLista(Guid id) { if (!_detallesLista.Remove(id)) _detallesLista.Add(id); }
    private void CambiarPaginaLista(int pagina) { _paginaLista = pagina; _seleccionLista.Clear(); }
    private void CambiarTamanoLista(int tamano) { _tamanoLista = tamano; CambiarPaginaLista(1); }
    private Task BuscarListaAsync(string valor) => CambiarFiltroLista("q", valor);
    private Task CambiarEstadoListaAsync(string? valor) => CambiarFiltroLista("estado", valor);
    private Task CambiarTipoListaAsync(string valor) => CambiarFiltroLista("tipo", valor);
    private Task CambiarAgrupacionListaAsync(string valor) => CambiarFiltroLista("agrupar", valor);
    private Task LimpiarFiltrosListaAsync() => AplicarVistaListaAsync(new Dictionary<string, string?> { ["agrupar"] = AgrupacionLista });
    private Task CambiarFiltroLista(string clave, string? valor) => AplicarVistaListaAsync(new Dictionary<string, string?>
    {
        ["q"] = BusquedaLista, ["estado"] = EstadoLista, ["tipo"] = TipoLista, ["agrupar"] = AgrupacionLista, [clave] = valor
    });
    private Task AplicarVistaListaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        BusquedaLista = vista.GetValueOrDefault("q");
        EstadoLista = SeleccionEstados.Unir(SeleccionEstados.Separar(vista.GetValueOrDefault("estado")).Where(v => EstadosLista.Any(o => o.Valores.Contains(v))));
        TipoLista = vista.GetValueOrDefault("tipo") is "Soporte" or "OperadorExterno" ? vista["tipo"] : null;
        AgrupacionLista = vista.GetValueOrDefault("agrupar") == "tipo" ? "tipo" : null;
        _seleccionLista.Clear();
        _detallesLista.Clear();
        _personasCompletas.Clear();
        _paginaLista = 1;
        NavegacionLista.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        { ["q"] = BusquedaLista, ["estado"] = EstadoLista, ["tipo"] = TipoLista, ["agrupar"] = AgrupacionLista });
        StateHasChanged();
        return Task.CompletedTask;
    }
}
