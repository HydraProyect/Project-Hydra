using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Inbound.Validacion.Pages;

public partial class DocumentosPorValidar
{
    [Inject] private IServicioValidacionInbound Servicio { get; set; } = default!;
    [Inject] private ColaEnCursoValidacion ColaEnCurso { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;

    private static readonly AgrupacionCola[] Agrupaciones =
        [AgrupacionCola.Requerimiento, AgrupacionCola.EmpresaContratista, AgrupacionCola.Centro, AgrupacionCola.Entidad, AgrupacionCola.SinAgrupar];

    // Filtros guardados: sin backend no se guardan (shortcut: sin backend, cablear cuando entre B-21).

    private readonly HashSet<Guid> _marcados = [];

    private ColaValidacion? _cola;
    private DateTimeOffset _ahora;
    private bool _cargando = true;
    private bool _noDisponible;
    private bool _error;
    private bool _hayBase;

    private string _busqueda = string.Empty;
    private string _req = string.Empty;
    private string _entidad = string.Empty;
    private string _empresa = string.Empty;
    private string _centro = string.Empty;
    private string _lectura = string.Empty;
    private string _tramo = string.Empty;
    private AgrupacionCola _agrupar = AgrupacionCola.Requerimiento;

    private bool _confirmarLote;
    private bool _enviandoLote;
    private LoteRegistrado? _loteHecho;

    private bool HayDatos => !_cargando && !_error && !_noDisponible && _hayBase;

    private FiltrosCola Filtros => new(
        _busqueda, _req,
        Enum.TryParse<EntidadValidable>(_entidad, out var entidad) ? entidad : null,
        _empresa, _centro,
        Enum.TryParse<ResultadoLectura>(_lectura, out var lectura) ? lectura : null,
        Enum.TryParse<TramoAntiguedad>(_tramo, out var tramo) ? tramo : null);

    private bool HayFiltrosActivos => Filtros.Hay;

    // La base de los recuentos de antigüedad es la cola sin el filtro de tramo, como en la maqueta.
    private IReadOnlyList<DocumentoPorValidar> Base =>
        _cola is null ? [] : [.. _cola.Documentos.Where(d => LogicaValidacionInbound.Pasa(d, Filtros, _ahora, conTramo: false))];

    private IReadOnlyList<DocumentoPorValidar> Visibles =>
        _cola is null ? [] : [.. _cola.Documentos.Where(d => LogicaValidacionInbound.Pasa(d, Filtros, _ahora))];

    private IReadOnlyList<(string Titulo, IReadOnlyList<DocumentoPorValidar> Documentos)> Grupos =>
        LogicaValidacionInbound.Agrupar(Visibles, _agrupar);

    private string TextoEmpezar => HayFiltrosActivos && HayDatos ? $"Empezar a validar ({Visibles.Count})" : "Empezar a validar";

    private IReadOnlyList<OpcionEstado> OpcionesRequerimiento => Distintas(d => d.Requerimiento);
    private IReadOnlyList<OpcionEstado> OpcionesEmpresa => Distintas(d => d.EmpresaContratista);

    private IReadOnlyList<OpcionEstado> OpcionesCentro =>
        _cola is null ? [] : [.. _cola.Documentos.SelectMany(d => d.Centros).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Select(c => new OpcionEstado(c, c))];

    private IReadOnlyList<OpcionEstado> OpcionesEntidad =>
        [.. Enum.GetValues<EntidadValidable>().Select(e => new OpcionEstado(e.ToString(), LogicaValidacionInbound.TextoPlural(e)))];

    private IReadOnlyList<OpcionEstado> OpcionesLectura =>
        [.. Enum.GetValues<ResultadoLectura>().Select(r => new OpcionEstado(r.ToString(), LogicaValidacionInbound.TextoLecturaGeneral(r)))];

    private static readonly IReadOnlyList<OpcionFranjaEstado> OpcionesTramo =
    [
        new(LogicaValidacionInbound.Texto(TramoAntiguedad.MenosDe24h), TonoBadge.Exito, nameof(TramoAntiguedad.MenosDe24h)),
        new(LogicaValidacionInbound.Texto(TramoAntiguedad.UnoATresDias), TonoBadge.Advertencia, nameof(TramoAntiguedad.UnoATresDias)),
        new(LogicaValidacionInbound.Texto(TramoAntiguedad.MasDeTresDias), TonoBadge.Peligro, nameof(TramoAntiguedad.MasDeTresDias))
    ];

    private IReadOnlyDictionary<string, int> RecuentosTramo
    {
        get
        {
            var baseSinTramo = Base;
            return Enum.GetValues<TramoAntiguedad>().ToDictionary(t => t.ToString(), t => baseSinTramo.Count(d => LogicaValidacionInbound.Tramo(d, _ahora) == t));
        }
    }

    private IReadOnlyList<OpcionEstado> Distintas(Func<DocumentoPorValidar, string> selector) =>
        _cola is null ? [] : [.. _cola.Documentos.Select(selector).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Select(x => new OpcionEstado(x, x))];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        try
        {
            var respuesta = await Servicio.ObtenerColaAsync();
            _ahora = TimeProvider.System.GetUtcNow();
            _noDisponible = !respuesta.Disponible;
            // Una cola rechazada por el servidor (Mensaje) no es una cola vacía: se dice como error, no como «no te queda nada».
            _error = respuesta.Disponible && respuesta.Valor is null;
            _cola = respuesta.Valor;
            _hayBase = _cola is { Documentos.Count: > 0 };
            PodarMarcas();
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private Task BuscarAsync(string texto)
    {
        _busqueda = texto;
        PodarMarcas();
        return Task.CompletedTask;
    }

    private void Cambiar(Action cambio)
    {
        cambio();
        PodarMarcas();
    }

    private void LimpiarFiltros()
    {
        _busqueda = _req = _entidad = _empresa = _centro = _lectura = _tramo = string.Empty;
        PodarMarcas();
    }

    // Las filas que entran en un lote ahora mismo: las que el Gestor CAE ve con los filtros de ahora y que son elegibles.
    // Es la única definición del lote: marcar, podar y enviar parten de ella.
    private HashSet<Guid> ElegiblesVisibles =>
        [.. Visibles.Where(d => LogicaValidacionInbound.MotivoNoLote(d) is null).Select(d => d.Id)];

    // Una fila que los filtros esconden, o que la recarga deja fuera de lote, deja de estar marcada: el lote es lo que el
    // Gestor CAE ve y puede validar, no lo que marcó antes.
    private void PodarMarcas()
    {
        var elegibles = ElegiblesVisibles;
        _marcados.RemoveWhere(id => !elegibles.Contains(id));
    }

    private async Task EmpezarAsync(IReadOnlyList<DocumentoPorValidar> documentos)
    {
        if (documentos.Count == 0) return;
        // La cola abierta lleva el orden en que el Gestor CAE los ve: por requerimiento, Empresa contratista y subida.
        var ordenados = LogicaValidacionInbound.Ordenar(documentos);
        ColaEnCurso.Fijar(ordenados.Select(d => d.Id));
        Navegacion.NavigateTo($"/inbound/validacion/{ordenados[0].Id}");
        await Task.CompletedTask;
    }

    private Task AbrirAsync(Guid id)
    {
        ColaEnCurso.Fijar(Grupos.SelectMany(g => g.Documentos).Select(d => d.Id));
        Navegacion.NavigateTo($"/inbound/validacion/{id}");
        return Task.CompletedTask;
    }

    private void AlternarMarca(Guid id, bool marcada)
    {
        if (!marcada)
        {
            _marcados.Remove(id);
            return;
        }

        // El navegador puede reactivar una casilla deshabilitada: el servidor del circuito no se fía del atributo disabled.
        var documento = _cola?.Documentos.FirstOrDefault(d => d.Id == id);
        if (documento is null || LogicaValidacionInbound.MotivoNoLote(documento) is not null)
        {
            _marcados.Remove(id);
            return;
        }

        var tope = _cola?.TopeLote ?? 0;
        if (tope > 0 && _marcados.Count >= tope)
        {
            Toast.Mostrar($"Un lote admite como máximo {tope} documentos.");
            return;
        }

        _marcados.Add(id);
    }

    private async Task ValidarLoteAsync()
    {
        _enviandoLote = true;
        try
        {
            // Solo las filas que el Gestor CAE sigue viendo y que siguen siendo elegibles: una marca oculta no entra en el lote.
            var elegibles = ElegiblesVisibles;
            IReadOnlyList<Guid> ids = [.. _marcados.Where(elegibles.Contains)];
            if (ids.Count == 0)
            {
                _marcados.Clear();
                Toast.Mostrar("Ningún documento marcado puede validarse en lote con los filtros actuales.");
                return;
            }

            var respuesta = await Servicio.ValidarLoteAsync(ids);
            if (!respuesta.Disponible)
                Toast.Mostrar("La validación en lote no está disponible todavía.");
            else if (respuesta.Mensaje is not null)
                Toast.Mostrar(respuesta.Mensaje);
            else if (respuesta.Valor is { } lote)
            {
                _loteHecho = lote;
                _marcados.Clear();
                await CargarAsync();
            }
        }
        finally
        {
            _enviandoLote = false;
            _confirmarLote = false;
        }
    }

    private async Task DeshacerLoteAsync()
    {
        if (_loteHecho is not { } lote) return;
        var respuesta = await Servicio.DeshacerLoteAsync(lote.LoteId);
        _loteHecho = null;
        Toast.Mostrar(respuesta.Aceptada ? "Lote deshecho: los documentos vuelven a la cola." : respuesta.Mensaje ?? "No se pudo deshacer el lote.");
        await CargarAsync();
    }

    private void CerrarAvisoLote() => _loteHecho = null;
}
