using System.Globalization;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Bandeja.Pages;

public partial class MiTrabajo : ComponentBase, IDisposable
{
    private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("es-ES");

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;

    private MiTrabajoVista? _vista;
    private AntiforgeryRequestToken? _token;
    private bool _cargando = true;
    private bool _errorCarga;

    private SeveridadMiTrabajo? _severidad;
    private Guid? _tenantFiltro;
    private string _busqueda = string.Empty;
    private AgruparMiTrabajo _agrupar = AgruparMiTrabajo.Tenant;
    private OrdenMiTrabajo _orden = OrdenMiTrabajo.Prioridad;
    private readonly HashSet<string> _abiertos = [];
    private readonly HashSet<Guid> _cerrados = [];
    private string? _idAbierto;
    private string? _idEnfocado;

    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;
    private int _cargaVigente;

    public void Dispose()
    {
        if (_desechado) return;
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    private FiltroMiTrabajo Filtro => new(_severidad, _tenantFiltro, _busqueda, _agrupar, _orden, _abiertos, _cerrados);

    private IReadOnlyList<GrupoMiTrabajo> Grupos => _vista?.Grupos(Filtro) ?? [];

    private IReadOnlyList<FilaMiTrabajo> Visibles => _vista?.Visibles(Filtro) ?? [];

    /// <summary>Vacío porque no queda trabajo en el Tenant elegido, no porque el filtro lo esconda.</summary>
    private bool AlDia => _vista is null || _vista.Ambito(Filtro with { Busqueda = string.Empty }).Count == 0;

    private FilaCarteraMiTrabajo? TenantFiltrado => _tenantFiltro is { } id ? _vista?.Cartera.FirstOrDefault(t => t.TenantId == id) : null;

    private FilaMiTrabajo? FilaAbierta => _idAbierto is null ? null : Visibles.FirstOrDefault(f => f.Item.Id == _idAbierto);

    private bool TodoPlegado => _vista is not null && _vista.Cartera.Count > 0 && _vista.Cartera.All(t => _cerrados.Contains(t.TenantId));

    private sealed record ChipMiTrabajo(SeveridadMiTrabajo? Severidad, string Etiqueta, int Cantidad);

    private IReadOnlyList<ChipMiTrabajo> Chips =>
    [
        new(null, "Todas", _vista!.Contar(Filtro, null)),
        new(SeveridadMiTrabajo.Bloqueo, "Bloqueos", _vista.Contar(Filtro, SeveridadMiTrabajo.Bloqueo)),
        new(SeveridadMiTrabajo.Actuacion, "Actuación", _vista.Contar(Filtro, SeveridadMiTrabajo.Actuacion)),
        new(SeveridadMiTrabajo.Proximo, "Próximo", _vista.Contar(Filtro, SeveridadMiTrabajo.Proximo)),
        new(SeveridadMiTrabajo.Seguimiento, "Seguimiento", _vista.Contar(Filtro, SeveridadMiTrabajo.Seguimiento)),
    ];

    private string Pie
    {
        get
        {
            var mostrados = Visibles.Count;
            var plegados = Math.Max(0, (_vista?.Alcance(Filtro).Count ?? 0) - mostrados);
            var texto = mostrados == 1 ? "1 elemento" : $"{mostrados} elementos";
            return plegados == 0 ? texto : $"{texto} mostrados · {plegados} {(plegados == 1 ? "plegado" : "plegados")}";
        }
    }

    protected override Task OnInitializedAsync()
    {
        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
        return CargarAsync();
    }

    private async Task CargarAsync()
    {
        if (_desechado) return;
        var carga = ++_cargaVigente;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var datos = await Mediator.Send(new ObtenerMiTrabajoAgregadoQuery(), _ciclo.Token);
            if (!EsVigente(carga)) return;
            _vista = new MiTrabajoVista(datos);
        }
        catch (Exception) when (!EsVigente(carga))
        {
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private void CambiarSeveridad(SeveridadMiTrabajo? severidad)
    {
        _severidad = severidad;
        _idEnfocado = null;
    }

    private void CambiarTenant(Guid? tenantId)
    {
        _tenantFiltro = tenantId;
        _idEnfocado = null;
    }

    private void CambiarAgrupar(AgruparMiTrabajo agrupar) => _agrupar = agrupar;

    private void CambiarOrden(OrdenMiTrabajo orden) => _orden = orden;

    private void CambiarBusqueda(ChangeEventArgs e)
    {
        _busqueda = e.Value?.ToString() ?? string.Empty;
        _idEnfocado = null;
    }

    private void QuitarFiltros()
    {
        _severidad = null;
        _tenantFiltro = null;
        _busqueda = string.Empty;
        _idEnfocado = null;
    }

    private void AlternarTenant(Guid tenantId)
    {
        if (!_cerrados.Remove(tenantId))
            _cerrados.Add(tenantId);
    }

    private void AlternarTodas()
    {
        if (TodoPlegado)
            _cerrados.Clear();
        else
            _cerrados.UnionWith(_vista!.Cartera.Select(t => t.TenantId));
    }

    private void AbrirPliegue(string clave) => _abiertos.Add(clave);

    private void VolverAPlegar()
    {
        _abiertos.Clear();
        _cerrados.Clear();
    }

    private void AbrirDetalle(FilaMiTrabajo fila)
    {
        _idAbierto = fila.Item.Id;
        _idEnfocado = fila.Item.Id;
    }

    /// <summary>j/k recorren las filas en orden de pantalla; Enter abre el detalle (mockup: «↵ abrir»). La acción en sí exige el botón, que hace el POST cross-Tenant.</summary>
    private Task ManejarAtajoAsync(string tecla)
    {
        var filas = Visibles;
        if (filas.Count == 0) return Task.CompletedTask;

        var indice = _idEnfocado is null ? -1 : filas.ToList().FindIndex(f => f.Item.Id == _idEnfocado);
        switch (tecla)
        {
            case "j":
                _idEnfocado = filas[Math.Min(indice + 1, filas.Count - 1)].Item.Id;
                break;
            case "k":
                _idEnfocado = filas[Math.Max(indice - 1, 0)].Item.Id;
                break;
            case "Enter" when indice >= 0:
                _idAbierto = _idEnfocado;
                break;
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    private string ClaseFila(FilaMiTrabajo fila) => "mi-trabajo-fila"
        + (fila.Severidad == SeveridadMiTrabajo.Bloqueo ? " mi-trabajo-fila-bloqueo" : "")
        + (fila.Item.Id == _idAbierto ? " mi-trabajo-fila-abierta" : "")
        + (fila.Item.Id == _idEnfocado ? " mi-trabajo-fila-enfocada" : "");

    private string ClaseFilaCartera(Guid? tenantId) => "mi-trabajo-cartera-fila" + (_tenantFiltro == tenantId ? " mi-trabajo-cartera-fila-activa" : "");

    private static string ClasePestana(bool activa) => "mi-trabajo-pestana" + (activa ? " mi-trabajo-pestana-activa" : "");

    private string ContextoFila(FilaMiTrabajo fila)
    {
        var partes = new List<string?> { MiTrabajoVista.Etiqueta(fila.Severidad) };
        if (_agrupar == AgruparMiTrabajo.Severidad) partes.Add(fila.TenantNombre);
        if (!(_agrupar == AgruparMiTrabajo.Tenant && _orden == OrdenMiTrabajo.Cliente)) partes.Add(fila.Item.ClienteNombre);
        return string.Join(" · ", partes.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static TonoBadge TonoSeveridad(SeveridadMiTrabajo severidad) => severidad switch
    {
        SeveridadMiTrabajo.Bloqueo => TonoBadge.Peligro,
        SeveridadMiTrabajo.Actuacion => TonoBadge.Info,
        SeveridadMiTrabajo.Proximo => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    private static string Empresas(int n) => n == 1 ? "1 empresa" : $"{n} empresas";

    private static string SubtituloCartera(FilaCarteraMiTrabajo tenant) => tenant.Total == 0
        ? "Sin trabajo pendiente"
        : tenant.Bloqueos switch { 0 => "Sin bloqueos", 1 => "1 bloqueo", _ => $"{tenant.Bloqueos} bloqueos" };

    private static string Iniciales(string nombre) => string.Concat(nombre
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(p => char.IsLetter(p[0]))
        .Take(2)
        .Select(p => char.ToUpperInvariant(p[0])));
}
