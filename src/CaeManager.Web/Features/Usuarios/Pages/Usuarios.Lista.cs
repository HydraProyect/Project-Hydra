using CaeManager.Web.Components;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Usuarios.Pages;

public partial class Usuarios
{
    private const string PantallaDeFiltrosGuardados = CaeManager.Application.Configuracion.Commands.GuardarFiltro.PantallasConFiltrosGuardados.Usuarios;
    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
    private readonly ConexionVistaRecordada _vistaRecordada = new();
    private readonly GruposAbiertos _gruposAbiertos = new();
    private string? _agruparPor;
    private bool _seleccionMultiple;
    private readonly HashSet<Guid> _seleccionados = [];
    private readonly HashSet<string> _gruposCompletos = [];
    private const int FilasInicialesGrupo = 5;
    private void AlternarSeleccionMultiple(bool activa) { _seleccionMultiple = activa; _seleccionados.Clear(); }
    private void Seleccionar(Guid id, bool valor) { if (valor && UsuariosVisibles.Any(u => u.Id == id)) _seleccionados.Add(id); else _seleccionados.Remove(id); }
    private void SeleccionarVisibles(bool valor) { _seleccionados.Clear(); if (valor) _seleccionados.UnionWith(UsuariosVisibles.Select(u => u.Id)); }
    private IEnumerable<UsuarioListaDto> FilasDelRol(string rol) => UsuariosFiltrados.Where(u => u.Rol == rol);
    private bool OcultaPorLimite(UsuarioListaDto u) => _agruparPor is not null && !_gruposCompletos.Contains(u.Rol) && !FilasDelRol(u.Rol).Take(FilasInicialesGrupo).Any(f => f.Id == u.Id);
    private int RestantesTras(UsuarioListaDto u) => _agruparPor is not null && !_gruposCompletos.Contains(u.Rol) && FilasDelRol(u.Rol).Take(FilasInicialesGrupo).LastOrDefault()?.Id == u.Id ? Math.Max(0, FilasDelRol(u.Rol).Count() - FilasInicialesGrupo) : 0;
    private void VerRestantes(string rol) => _gruposCompletos.Add(rol);

    [SupplyParameterFromQuery(Name = "q")] public string? BusquedaInicial { get; set; }
    [SupplyParameterFromQuery(Name = "rol")] public string? RolInicial { get; set; }
    [SupplyParameterFromQuery(Name = "estado")] public string? EstadoInicial { get; set; }
    [SupplyParameterFromQuery(Name = "agrupar")] public string? AgrupacionInicial { get; set; }

    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda) || _rolFiltro.Length > 0 || _activacionFiltro.Length > 0;
    private IReadOnlyList<OpcionAgrupar> OpcionesAgrupar => [new("rol", TextosUsuarios["ListaRol"].Value)];
    private IReadOnlyList<OpcionFranjaEstado> OpcionesEstadoLista =>
    [
        new(TextosUsuarios["ListaDesactivados"].Value, TonoBadge.Neutro, ActivacionDesactivados),
        new(TextosUsuarios["ListaPendientes"].Value, TonoBadge.Advertencia, "pendientes"),
        new(TextosUsuarios["ListaActivos"].Value, TonoBadge.Exito, ActivacionActivos)
    ];
    private static string EstadoLista(UsuarioListaDto u) => u.PendienteActivacion ? "pendientes" : u.Activo ? ActivacionActivos : ActivacionDesactivados;
    private IEnumerable<UsuarioListaDto> UsuariosSinFiltroEstado => _usuarios.Where(u =>
        (string.IsNullOrWhiteSpace(_busqueda) || Contiene(u.NombreCompleto, _busqueda.Trim()) || Contiene(u.Email, _busqueda.Trim())) &&
        (string.IsNullOrEmpty(_rolFiltro) || u.Rol == _rolFiltro));
    private IReadOnlyDictionary<string, int> RecuentosEstadoLista => UsuariosSinFiltroEstado.GroupBy(EstadoLista).ToDictionary(g => g.Key, g => g.Count());
    private IEnumerable<FilaDeGrupo<UsuarioListaDto>> FilasAgrupadas => GruposDeLista.Agrupar(
        _agruparPor is null ? UsuariosDePagina : UsuariosFiltrados, _agruparPor is not null,
        u => u.Rol, (clave, _) => Roles.NombreVisible(clave), _ => false, _gruposAbiertos);
    private IReadOnlyList<UsuarioListaDto> UsuariosVisibles => FilasAgrupadas.Where(f => !f.Oculta && !OcultaPorLimite(f.Fila)).Select(f => f.Fila).ToList();

    protected override void OnParametersSet()
    {
        var busqueda = BusquedaInicial ?? string.Empty;
        var rol = OpcionesRol.Any(o => o.Valor == RolInicial) ? RolInicial! : string.Empty;
        var estado = NormalizarEstado(EstadoInicial);
        var agrupar = AgrupacionInicial == "rol" ? "rol" : null;
        if (_busqueda == busqueda && _rolFiltro == rol && _activacionFiltro == estado && _agruparPor == agrupar) return;
        _busqueda = busqueda; _rolFiltro = rol; _activacionFiltro = estado; _agruparPor = agrupar;
        ReiniciarVistaLista();
    }

    private static string NormalizarEstado(string? valor) => string.Join(',',
        (valor ?? string.Empty).Split(',').Where(v => v is ActivacionActivos or ActivacionDesactivados or "pendientes").Distinct());

    private void ReiniciarVistaLista()
    {
        _pagina = 1;
        _idEnfocado = null;
        _gruposAbiertos.PlegarTodos();
        _gruposCompletos.Clear(); _seleccionados.Clear();
    }
    private Task ActualizarVistaListaAsync()
    {
        ReiniciarVistaLista();
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        { ["q"] = _busqueda, ["rol"] = _rolFiltro, ["estado"] = _activacionFiltro, ["agrupar"] = _agruparPor });
        return Task.CompletedTask;
    }
    private Task CambiarEstadoListaAsync(string? valor)
    {
        _activacionFiltro = NormalizarEstado(valor);
        return ActualizarVistaListaAsync();
    }
    private Task CambiarAgrupacionAsync(string? valor)
    {
        _agruparPor = valor == "rol" ? valor : null;
        return ActualizarVistaListaAsync();
    }
    private void AlternarGrupo(string clave)
    {
        _gruposAbiertos.Alternar(clave);
        _seleccionados.IntersectWith(UsuariosVisibles.Select(u => u.Id));
        _idEnfocado = null;
    }
    private Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        _busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        var rol = vista.GetValueOrDefault("rol");
        _rolFiltro = OpcionesRol.Any(o => o.Valor == rol) ? rol! : string.Empty;
        _activacionFiltro = NormalizarEstado(vista.GetValueOrDefault("estado"));
        _agruparPor = vista.GetValueOrDefault("agrupar") == "rol" ? "rol" : null;
        return ActualizarVistaListaAsync();
    }
}
