using System.Globalization;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Bandeja.Recursos;
using MediatR;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Bandeja.Pages;

public partial class MiTrabajo : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>
    /// Cultura de las fechas: catalán si la interfaz está en catalán y español en
    /// cualquier otro caso, también con la cultura invariante del runner de CI. Los
    /// patrones salen del recurso (<c>FormatoDiaMes</c>, <c>FormatoFechaLarga</c>).
    /// </summary>
    private static CultureInfo Cultura => CultureInfo.GetCultureInfo(
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ca" ? "ca-ES" : "es-ES");

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;
    [Inject] private ILogger<MiTrabajo> Logger { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;

    /// <summary>
    /// Los filtros de la cola viven también en la URL (UX_PATTERNS: compartir, recargar y volver
    /// desde la pantalla de una acción sin perderlos). Lo que no se reconoce (un valor que no es una
    /// severidad, un Tenant que no es un Guid) se ignora: la URL es entrada del usuario.
    /// </summary>
    [SupplyParameterFromQuery(Name = "severidad")] public string? SeveridadUrl { get; set; }
    [SupplyParameterFromQuery(Name = "empresa")] public string? EmpresaUrl { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? BusquedaUrl { get; set; }
    [SupplyParameterFromQuery(Name = "agrupar")] public string? AgruparUrl { get; set; }
    [SupplyParameterFromQuery(Name = "orden")] public string? OrdenUrl { get; set; }

    /// <summary>
    /// D-15: «Pídesela a tu Coordinador CAE» se lo decía al propio Coordinador CAE. Ahora le dice que su alcance
    /// sale de la cartera de los Gestores CAE de su equipo y le enlaza a Usuarios; no concede ni quita nada. Opcional: sin estado de
    /// autenticación en cascada se queda el texto general.
    /// </summary>
    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private bool _esCoordinadorCae;

    private MiTrabajoVista? _vista;
    private AntiforgeryRequestToken? _token;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _completados;
    private int _totalPorCargar;

    /// <summary>Con la carga en marcha, la vista es parcial: lo que falte de la cartera no está «al día», está sin consultar.</summary>
    private bool ConsultandoCartera => _cargando && _vista is not null;

    private int PorcentajeCargado => _totalPorCargar == 0 ? 0 : _completados * 100 / _totalPorCargar;

    /// <summary>
    /// «Añadir a mi cartera» (contrato Gen2 § 13): solo si la Query de candidatos
    /// devuelve al menos una Empresa. Si falla —<c>SolicitudCartera.SinPermiso</c>
    /// cuando quien mira no es Gestor CAE en su organización— el botón no aparece,
    /// sin mensaje: no es un error de la pantalla, es que la acción no le toca.
    /// </summary>
    private bool _puedeAnadirACartera;
    private bool _dialogoCarteraVisible;

    private SeveridadMiTrabajo? _severidad;
    private Guid? _tenantFiltro;
    private string _busqueda = string.Empty;
    private AgruparMiTrabajo _agrupar = AgruparMiTrabajo.Tenant;
    private OrdenMiTrabajo _orden = OrdenMiTrabajo.Prioridad;
    private readonly HashSet<string> _abiertos = [];
    private readonly HashSet<Guid> _cerrados = [];
    private readonly HashSet<string> _lotesAbiertos = [];
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

    /// <summary>
    /// Filtros que esconden trabajo dentro del ámbito: el chip de severidad y la búsqueda. La
    /// Empresa elegida en el carril no cuenta: acota el ámbito, y su vacío es «al día».
    /// </summary>
    private bool HayFiltrosActivos => _severidad is not null || !string.IsNullOrWhiteSpace(_busqueda);

    private FilaCarteraMiTrabajo? TenantFiltrado => _tenantFiltro is { } id ? _vista?.Cartera.FirstOrDefault(t => t.TenantId == id) : null;

    private FilaMiTrabajo? FilaAbierta => _idAbierto is null ? null : Visibles.FirstOrDefault(f => f.Item.Id == _idAbierto);

    private bool TodoPlegado => _vista is not null && _vista.Cartera.Count > 0 && _vista.Cartera.All(t => _cerrados.Contains(t.TenantId));

    private sealed record ChipMiTrabajo(SeveridadMiTrabajo? Severidad, string Etiqueta, int Cantidad);

    private IReadOnlyList<ChipMiTrabajo> Chips =>
    [
        new(null, TextosMiTrabajo.Texto("ChipTodas"), _vista!.Contar(Filtro, null)),
        new(SeveridadMiTrabajo.Bloqueo, TextosMiTrabajo.Texto("ChipBloqueos"), _vista.Contar(Filtro, SeveridadMiTrabajo.Bloqueo)),
        new(SeveridadMiTrabajo.Actuacion, TextosMiTrabajo.Texto("ChipActuacion"), _vista.Contar(Filtro, SeveridadMiTrabajo.Actuacion)),
        new(SeveridadMiTrabajo.Proximo, TextosMiTrabajo.Texto("SeveridadProximo"), _vista.Contar(Filtro, SeveridadMiTrabajo.Proximo)),
        new(SeveridadMiTrabajo.Seguimiento, TextosMiTrabajo.Texto("SeveridadSeguimiento"), _vista.Contar(Filtro, SeveridadMiTrabajo.Seguimiento)),
    ];

    private string Pie
    {
        get
        {
            var mostrados = Visibles.Count;
            var plegados = Math.Max(0, (_vista?.Alcance(Filtro).Count ?? 0) - mostrados);
            return plegados == 0
                ? MiTrabajoVista.Plural(mostrados, "ElementosUno", "ElementosVarios")
                : $"{MiTrabajoVista.Plural(mostrados, "MostradosUno", "MostradosVarios")} · {MiTrabajoVista.Plural(plegados, "PlegadosUno", "PlegadosVarios")}";
        }
    }

    /// <summary>
    /// Valores de búsqueda que esta página ha escrito en la URL y cuyo eco puede llegar tarde: con la
    /// tecla siguiente ya en el campo, el eco de «ab» no debe pisar «abc». Un valor que no esté aquí
    /// viene de fuera (atrás, adelante, un enlace) y se adopta.
    /// </summary>
    private readonly HashSet<string> _busquedasEscritas = [];

    /// <summary>La URL de Mi trabajo con los filtros vigentes: lo que el destino de una acción ofrece como vuelta.</summary>
    private string UrlMiTrabajo
    {
        get
        {
            var consulta = new List<string>();
            if (_severidad is { } s) consulta.Add($"severidad={Uri.EscapeDataString(s.ToString().ToLowerInvariant())}");
            if (_tenantFiltro is { } t) consulta.Add($"empresa={t}");
            if (!string.IsNullOrWhiteSpace(_busqueda)) consulta.Add($"q={Uri.EscapeDataString(_busqueda)}");
            if (_agrupar != AgruparMiTrabajo.Tenant) consulta.Add($"agrupar={Uri.EscapeDataString(_agrupar.ToString().ToLowerInvariant())}");
            if (_orden != OrdenMiTrabajo.Prioridad) consulta.Add($"orden={Uri.EscapeDataString(_orden.ToString().ToLowerInvariant())}");
            return RetornoMiTrabajo.Ruta + (consulta.Count > 0 ? "?" + string.Join('&', consulta) : "");
        }
    }

    private static TEnum? ParsearEnum<TEnum>(string? valor) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(valor, ignoreCase: true, out var resultado) && Enum.IsDefined(resultado) ? resultado : null;

    /// <summary>
    /// Los valores de la URL tal como se aplicaron por última vez. <c>OnParametersSet</c> también corre por
    /// motivos ajenos a la URL (y entre una escritura y su eco): sin esta guarda, una URL todavía sin el
    /// filtro recién tecleado lo borraba. Solo se adopta lo que la URL ha cambiado de verdad.
    /// </summary>
    private string? _urlAplicada;

    private void AplicarFiltrosDeLaUrl()
    {
        var urlActual = $"{SeveridadUrl}|{EmpresaUrl}|{BusquedaUrl}|{AgruparUrl}|{OrdenUrl}";
        if (urlActual == _urlAplicada) return;
        _urlAplicada = urlActual;

        _severidad = ParsearEnum<SeveridadMiTrabajo>(SeveridadUrl);
        _tenantFiltro = Guid.TryParse(EmpresaUrl, out var tenantId) ? tenantId : null;
        _agrupar = ParsearEnum<AgruparMiTrabajo>(AgruparUrl) ?? AgruparMiTrabajo.Tenant;
        _orden = ParsearEnum<OrdenMiTrabajo>(OrdenUrl) ?? OrdenMiTrabajo.Prioridad;

        var busqueda = BusquedaUrl ?? string.Empty;
        if (busqueda == _busqueda) _busquedasEscritas.Clear();
        else if (!_busquedasEscritas.Contains(busqueda))
        {
            _busqueda = busqueda;
            _busquedasEscritas.Clear();
        }
    }

    private void EscribirFiltrosEnUrl() => Navegacion.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
    {
        ["severidad"] = _severidad?.ToString().ToLowerInvariant(),
        ["empresa"] = _tenantFiltro?.ToString(),
        ["q"] = _busqueda,
        ["agrupar"] = _agrupar == AgruparMiTrabajo.Tenant ? null : _agrupar.ToString().ToLowerInvariant(),
        ["orden"] = _orden == OrdenMiTrabajo.Prioridad ? null : _orden.ToString().ToLowerInvariant(),
    });

    protected override void OnInitialized() => AplicarFiltrosDeLaUrl();

    protected override void OnParametersSet() => AplicarFiltrosDeLaUrl();

    protected override async Task OnInitializedAsync()
    {
        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
        if (EstadoAutenticacion is not null)
            _esCoordinadorCae = (await EstadoAutenticacion).User.IsInRole(Roles.CoordinadorCae);
        // En serie, no en paralelo: las dos Queries comparten el ámbito del circuito.
        await CargarAsync();
        await CargarCandidatosCarteraAsync();
    }

    private async Task CargarCandidatosCarteraAsync()
    {
        if (_desechado) return;
        try
        {
            var resultado = await Mediator.Send(new ObtenerCandidatosIncorporacionCarteraQuery(), _ciclo.Token);
            if (_desechado) return;
            _puedeAnadirACartera = resultado.EsExitoso && resultado.Valor.Count > 0;
        }
        catch (Exception) when (_desechado)
        {
        }
        catch (Exception ex)
        {
            // Un botón secundario no tumba la cola: se esconde y queda registrado.
            Logger.LogWarning(ex, "Mi trabajo no pudo consultar los candidatos de incorporación a cartera.");
            _puedeAnadirACartera = false;
        }
    }

    private void AbrirDialogoCartera() => _dialogoCarteraVisible = true;

    private async Task CargarAsync()
    {
        if (_desechado) return;
        var carga = ++_cargaVigente;
        _cargando = true;
        _errorCarga = false;
        _vista = null;
        _completados = 0;
        _totalPorCargar = 0;
        StateHasChanged();

        try
        {
            // Cada Empresa (Tenant propietario) se pinta en cuanto termina; el recorrido sigue siendo secuencial y cada
            // parte ya llega autorizada. La vista se rehace con lo acumulado, que es siempre un subconjunto coherente.
            var tenants = new List<MiTrabajoTenantDto>();
            var noConsultados = new List<TenantNoConsultadoDto>();
            await foreach (var parte in Mediator.CreateStream(new ObtenerMiTrabajoPorPartesQuery(), _ciclo.Token))
            {
                if (!EsVigente(carga)) return;
                _completados = parte.Completados;
                _totalPorCargar = parte.Total;
                if (parte.Tenant is not null) tenants.Add(parte.Tenant);
                if (parte.NoConsultado is not null) noConsultados.Add(parte.NoConsultado);
                _vista = new MiTrabajoVista(new MiTrabajoAgregadoDto(tenants.ToList(), noConsultados.ToList()));
                StateHasChanged();
            }

            // Un recorrido sin Tenants no emite nada más que la apertura: la vista vacía es la de siempre.
            _vista ??= new MiTrabajoVista(new MiTrabajoAgregadoDto([], []));
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
        EscribirFiltrosEnUrl();
    }

    private void CambiarTenant(Guid? tenantId)
    {
        _tenantFiltro = tenantId;
        _idEnfocado = null;
        EscribirFiltrosEnUrl();
    }

    private void CambiarAgrupar(AgruparMiTrabajo agrupar)
    {
        _agrupar = agrupar;
        EscribirFiltrosEnUrl();
    }

    private void CambiarOrden(OrdenMiTrabajo orden)
    {
        _orden = orden;
        EscribirFiltrosEnUrl();
    }

    private void CambiarBusqueda(ChangeEventArgs e)
    {
        _busqueda = e.Value?.ToString() ?? string.Empty;
        _idEnfocado = null;
        _busquedasEscritas.Add(_busqueda);
        EscribirFiltrosEnUrl();
    }

    private void QuitarFiltros()
    {
        _severidad = null;
        _tenantFiltro = null;
        _busqueda = string.Empty;
        _idEnfocado = null;
        // La URL se limpia en la misma navegación: si no, OnParametersSet repondría los filtros desde ella.
        _busquedasEscritas.Add(string.Empty);
        EscribirFiltrosEnUrl();
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

    /// <summary>Un tramo se enseña entero si se abrió (con clic, o con j/k al entrar en él) o si hay búsqueda, que debe alcanzar cada gestión.</summary>
    private bool LoteAbierto(LoteMiTrabajo lote) => !lote.Colapsable
        || !string.IsNullOrWhiteSpace(_busqueda)
        || _lotesAbiertos.Contains(lote.Clave);

    /// <summary>j/k no pueden dejar el foco en una fila escondida: abren el tramo al entrar.</summary>
    private void AbrirLoteDeLaFilaEnfocada()
    {
        if (_idEnfocado is null) return;
        var lote = Grupos
            .SelectMany(g => MiTrabajoVista.AgruparPorTrabajador(g.Filas))
            .FirstOrDefault(l => l.Colapsable && l.Filas.Any(f => f.Item.Id == _idEnfocado));
        if (lote is not null) _lotesAbiertos.Add(lote.Clave);
    }

    private void AlternarLote(LoteMiTrabajo lote)
    {
        if (!_lotesAbiertos.Remove(lote.Clave))
            _lotesAbiertos.Add(lote.Clave);
    }

    private static string ResumenLote(LoteMiTrabajo lote)
    {
        var titulos = lote.Filas.Select(f => f.Item.Titulo).Distinct().ToList();
        return string.Join(" · ", titulos.Take(3)) + (titulos.Count > 3 ? " …" : "");
    }

    private void AbrirPliegue(string clave) => _abiertos.Add(clave);

    private void VolverAPlegar()
    {
        _abiertos.Clear();
        _cerrados.Clear();
        _lotesAbiertos.Clear();
    }

    private void AbrirDetalle(FilaMiTrabajo fila)
    {
        _idAbierto = fila.Item.Id;
        _idEnfocado = fila.Item.Id;
    }

    /// <summary>Cierra el detalle: sin tarea abierta el panel no se pinta y la cola recupera su ancho.</summary>
    private void CerrarDetalle() => _idAbierto = null;

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

        AbrirLoteDeLaFilaEnfocada();
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

    private static string Empresas(int n) => MiTrabajoVista.Plural(n, "EmpresasUna", "EmpresasVarias");

    private static string EmpresasConsultadas(int n) => MiTrabajoVista.Plural(n, "EmpresasConsultadasUna", "EmpresasConsultadasVarias");

    private static string SubtituloCartera(FilaCarteraMiTrabajo tenant) => tenant.Total == 0
        ? TextosMiTrabajo.Texto(tenant.AlcanceCero ? "SinAsignacionCartera" : "SinTrabajoPendiente")
        : tenant.Bloqueos == 0
            ? TextosMiTrabajo.Texto("SinBloqueos")
            : MiTrabajoVista.Plural(tenant.Bloqueos, "BloqueosUno", "BloqueosVarios");

    private static string Iniciales(string nombre) => string.Concat(nombre
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(p => char.IsLetter(p[0]))
        .Take(2)
        .Select(p => char.ToUpperInvariant(p[0])));
}
