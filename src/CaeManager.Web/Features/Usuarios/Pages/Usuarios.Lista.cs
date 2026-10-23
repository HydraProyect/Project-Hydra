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
    private readonly HashSet<string> _gruposCompletos = [];
    private const int FilasInicialesGrupo = 5;

    private sealed record FilaUsuarioVista(UsuarioListaDto Usuario, GrupoDeLista? Cabecera, bool Abierto, bool Oculta, int Restantes);
    private sealed record VistaUsuarios(IReadOnlyList<UsuarioListaDto> Filtrados, IReadOnlyList<UsuarioListaDto> Pagina, IReadOnlyList<FilaUsuarioVista> Filas);

    // La instantánea se obtiene una vez por render; ningún cálculo de fila vuelve a filtrar la lista.
    private VistaUsuarios CrearVistaLista()
    {
        var filtrados = UsuariosFiltrados;
        var pagina = filtrados.Skip((_pagina - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();
        var filas = new List<FilaUsuarioVista>();
        if (_agruparPor is null)
        {
            filas.AddRange(pagina.Select(u => new FilaUsuarioVista(u, null, true, false, 0)));
        }
        else
        {
            foreach (var grupo in filtrados.GroupBy(u => u.Rol))
            {
                var elementos = grupo.ToList();
                var abierto = _gruposAbiertos.EstaAbierto(grupo.Key);
                var limite = _gruposCompletos.Contains(grupo.Key) ? elementos.Count : Math.Min(FilasInicialesGrupo, elementos.Count);
                for (var i = 0; i < elementos.Count; i++)
                {
                    var cabecera = i == 0 ? new GrupoDeLista(grupo.Key, Roles.NombreVisible(grupo.Key), elementos.Count, 0) : null;
                    filas.Add(new FilaUsuarioVista(elementos[i], cabecera, abierto, !abierto || i >= limite,
                        abierto && i == limite - 1 ? elementos.Count - limite : 0));
                }
            }
        }
        return new VistaUsuarios(filtrados, pagina, filas);
    }

    private IReadOnlyList<UsuarioListaDto> UsuariosVisibles => CrearVistaLista().Filas.Where(f => !f.Oculta).Select(f => f.Usuario).ToList();
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
    private static bool PerteneceAlEstado(UsuarioListaDto usuario, string estado) => estado switch
    {
        ActivacionActivos => usuario.Activo,
        ActivacionDesactivados => !usuario.Activo,
        "pendientes" => usuario.PendienteActivacion,
        _ => false
    };

    private IEnumerable<UsuarioListaDto> UsuariosSinFiltroEstado => _usuarios.Where(u =>
        (string.IsNullOrWhiteSpace(_busqueda) || Contiene(u.NombreCompleto, _busqueda.Trim()) || Contiene(u.Email, _busqueda.Trim())) &&
        (string.IsNullOrEmpty(_rolFiltro) || u.Rol == _rolFiltro));

    private IReadOnlyDictionary<string, int> RecuentosEstadoLista
    {
        get
        {
            var usuarios = UsuariosSinFiltroEstado.ToList();
            return new[] { ActivacionActivos, ActivacionDesactivados, "pendientes" }
                .ToDictionary(estado => estado, estado => usuarios.Count(u => PerteneceAlEstado(u, estado)));
        }
    }
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
        _gruposCompletos.Clear();
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
