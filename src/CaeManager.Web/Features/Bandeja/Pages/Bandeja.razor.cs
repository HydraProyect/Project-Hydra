using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Bandeja.Pages;

public partial class Bandeja : ComponentBase, IDisposable
{
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "tipo")]
    public string? TipoInicial { get; set; }

    private enum OrdenBandeja { Impacto, Fecha }

    private BandejaAgrupadaDto? _bandeja;
    private string _tipoFiltro = string.Empty;
    private OrdenBandeja _orden = OrdenBandeja.Impacto;
    private bool _agruparPorEmpresa;
    private bool _cargando = true;
    private bool _errorCarga;
    private string? _idEnfocado;
    private bool _reclamacionLoteVisible;
    private AmbitoAplicacion? _ambitoPreseed;
    private Guid? _entidadIdPreseed;

    /// <summary>
    /// Se cancela al salir de la pantalla: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente ya
    /// retirado. Mismo patrón que Empresas y DeteccionTrabajadores.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>
    /// Número de la última carga. Cada carga captura el suyo ANTES del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente. Sin esto, dos «Reintentar» seguidos —o una recarga tras enviar
    /// una reclamación mientras la anterior seguía en vuelo— dejaban que la
    /// respuesta lenta de la carga superada pisara la cola ya pintada, y que su
    /// fallo encendiera el estado de error de una carga que había ido bien.
    /// </summary>
    private int _cargaVigente;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la pregunta vigente y la pantalla sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    /// <summary>Todos los items sueltos, sin filtrar ni agrupar — base para las cuentas de cada chip (siempre sobre el total, nunca sobre lo ya filtrado) y para j/k/Enter.</summary>
    private IReadOnlyList<ItemBandejaDto> Items =>
        _bandeja is null ? [] : [.. _bandeja.Grupos.SelectMany(g => g.Items), .. _bandeja.SinGrupo];

    private IReadOnlyList<ItemBandejaDto> ItemsFiltrados =>
        Enum.TryParse<TipoItemBandeja>(_tipoFiltro, out var tipo)
            ? Items.Where(i => i.Tipo == tipo).ToList()
            : Items;

    /// <summary>
    /// El chip es el único filtro de esta pantalla, así que «hay filtros» es
    /// exactamente «hay un chip distinto de Todos». Separa los dos vacíos: una
    /// cola vacía de verdad y una cola de la que este chip no deja ver nada
    /// son situaciones opuestas y piden respuestas opuestas —celebrar la
    /// primera a quien acaba de filtrar por «Revisión IA» le dice que no queda
    /// trabajo cuando le quedan decenas de tareas de otro tipo.
    /// </summary>
    private bool HayFiltrosActivos => !string.IsNullOrEmpty(_tipoFiltro);

    /// <summary>
    /// Los grupos ya vienen ordenados "por impacto" (bloquea acceso primero,
    /// luego severidad, ver ObtenerBandejaAgrupadaQueryHandler.Agrupar) —
    /// filtrar reduce los ITEMS de cada grupo (un grupo puede tener vencidos
    /// Y próximos a la vez), así que hay que reagrupar sobre
    /// ItemsFiltrados, no solo esconder grupos enteros. El orden "Fecha
    /// límite" del mockup reordena los GRUPOS por su vencimiento más próximo
    /// — el mismo criterio "qué urge antes" que Impacto, solo que por fecha en
    /// vez de por severidad.
    /// </summary>
    private IReadOnlyList<GrupoColaDto> GruposOrdenados
    {
        get
        {
            var agrupados = ObtenerBandejaAgrupadaQueryHandler.Agrupar(ItemsFiltrados).Grupos;
            return _orden == OrdenBandeja.Impacto
                ? agrupados
                : [.. agrupados.OrderBy(g => g.Items.Min(i => i.Fecha) ?? DateOnly.MaxValue)];
        }
    }

    /// <summary>
    /// Un chip de filtro con el texto que explica su número. El mockup pone ese
    /// texto en el <c>title</c> del recuento: «6» no dice si son seis
    /// documentos, seis personas o seis avisos, y la etiqueta del chip tampoco
    /// lo aclara del todo.
    /// </summary>
    /// <param name="Singular">Frase tras el número cuando vale 1, sin el número.</param>
    /// <param name="Plural">Frase tras el número en los demás casos, incluido el cero.</param>
    private sealed record ChipBandeja(string Tipo, string Etiqueta, int Cantidad, string Singular, string Plural)
    {
        public string Titulo => $"{Cantidad} {(Cantidad == 1 ? Singular : Plural)}";
    }

    private IReadOnlyList<ChipBandeja> Chips =>
    [
        new(string.Empty, "Todos", Items.Count,
            "tarea en tu cola de trabajo", "tareas en tu cola de trabajo"),
        new(nameof(TipoItemBandeja.SugerenciaVisitaUrgente), "Visita sorpresa", Contador(TipoItemBandeja.SugerenciaVisitaUrgente),
            "visita sorpresa sugerida sin confirmar", "visitas sorpresa sugeridas sin confirmar"),
        new(nameof(TipoItemBandeja.Faltante), "Falta", Contador(TipoItemBandeja.Faltante),
            "documento requerido que nunca se aportó", "documentos requeridos que nunca se aportaron"),
        new(nameof(TipoItemBandeja.Vencido), "Vencido", Contador(TipoItemBandeja.Vencido),
            "documento que ya está fuera de vigencia", "documentos que ya están fuera de vigencia"),
        new(nameof(TipoItemBandeja.VisitaUrgente), "Visita próxima", Contador(TipoItemBandeja.VisitaUrgente),
            "visita ya programada que toca preparar", "visitas ya programadas que tocan preparar"),
        new(nameof(TipoItemBandeja.RequisitoPendiente), "Bloquea el centro", Contador(TipoItemBandeja.RequisitoPendiente),
            "requisito que hoy impide el acceso a un Centro", "requisitos que hoy impiden el acceso a un Centro"),
        new(nameof(TipoItemBandeja.Urgente), "Urgente", Contador(TipoItemBandeja.Urgente),
            "documento a punto de vencer", "documentos a punto de vencer"),
        new(nameof(TipoItemBandeja.RevisionIa), "Revisión IA", Contador(TipoItemBandeja.RevisionIa),
            "lectura de la IA pendiente de confirmar o corregir", "lecturas de la IA pendientes de confirmar o corregir"),
        new(nameof(TipoItemBandeja.DeteccionPendiente), "Detección de personal", Contador(TipoItemBandeja.DeteccionPendiente),
            "alta o baja detectada sin confirmar", "altas o bajas detectadas sin confirmar"),
        new(nameof(TipoItemBandeja.PlataformaPendiente), "Pendiente por plataforma", Contador(TipoItemBandeja.PlataformaPendiente),
            "documento al día en TALVEG que falta por subir a la plataforma de acreditación",
            "documentos al día en TALVEG que faltan por subir a la plataforma de acreditación"),
    ];

    /// <summary>
    /// «7 grupos · 47 tareas visibles» del mockup: dice de un vistazo cuánto
    /// tapa el filtro actual. Cuenta sobre lo VISIBLE (ItemsFiltrados), al
    /// revés que <see cref="Contador"/>, que cuenta siempre sobre el total.
    /// </summary>
    private string ResumenVisible
    {
        get
        {
            var grupos = GruposOrdenados.Count;
            var tareas = ItemsFiltrados.Count;
            return $"{grupos} {(grupos == 1 ? "grupo" : "grupos")} · {tareas} {(tareas == 1 ? "tarea visible" : "tareas visibles")}";
        }
    }

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>
    /// La URL es la fuente de verdad del filtro, no solo su semilla inicial
    /// — mismo patrón que el resto de listados (P1-18 de
    /// docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _tipoFiltro = !string.IsNullOrWhiteSpace(TipoInicial) && Enum.TryParse<TipoItemBandeja>(TipoInicial, out _)
            ? TipoInicial
            : string.Empty;
    }

    private Task CambiarFiltroAsync(string valor)
    {
        _tipoFiltro = valor;
        _idEnfocado = null;
        NavigationManager.ActualizarFiltroEnUrl("tipo", valor);
        return Task.CompletedTask;
    }

    /// <summary>Vuelve a «Todos» desde el estado vacío por filtro — el mismo camino que pulsar el chip, para que la URL quede igual de limpia.</summary>
    private Task QuitarFiltrosAsync() => CambiarFiltroAsync(string.Empty);

    private Task CambiarOrdenAsync(ChangeEventArgs e)
    {
        _orden = Enum.TryParse<OrdenBandeja>(e.Value?.ToString(), out var orden) ? orden : OrdenBandeja.Impacto;
        return Task.CompletedTask;
    }

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        var carga = ++_cargaVigente;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var bandeja = await Mediator.Send(new ObtenerBandejaAgrupadaQuery(), _ciclo.Token);
            var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery(), _ciclo.Token);

            if (!EsVigente(carga))
                return;

            _bandeja = bandeja;
            _agruparPorEmpresa = perfil == PerfilVocabularioTenant.Consultora;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error.
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

    /// <summary>"Reclamar en lote" desde la cabecera — sin ítem de partida, el gestor CAE elige todo desde cero en el selector.</summary>
    private Task AbrirReclamacionLoteAsync()
    {
        _ambitoPreseed = null;
        _entidadIdPreseed = null;
        _reclamacionLoteVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>U-2: puerta terminal de reclamación desde un ítem concreto (TipoItemBandejaUi.EsReclamable) — abre el mismo Drawer que "Reclamar en lote", preseed a ese Trabajador para no obligar a repetir la búsqueda.</summary>
    private Task AbrirReclamacionDeItemAsync(ItemBandejaDto item)
    {
        _ambitoPreseed = AmbitoAplicacion.Trabajador;
        _entidadIdPreseed = item.TrabajadorId;
        _reclamacionLoteVisible = true;
        return Task.CompletedTask;
    }

    private static bool CoincideFiltro(ItemBandejaDto item, string tipoFiltro) =>
        !Enum.TryParse<TipoItemBandeja>(tipoFiltro, out var tipo) || item.Tipo == tipo;

    /// <summary>H2 (docs/ux-audit/10-bandeja-alertas-calendario.md): "¿qué atiendo primero?" pide los números antes de filtrar, no después. Cuenta siempre sobre Items completo, nunca sobre ItemsFiltrados, para que el número de cada chip no cambie según cuál esté seleccionado.</summary>
    private int Contador(TipoItemBandeja tipo) => Items.Count(i => i.Tipo == tipo);

    private async Task ManejarAtajoAsync(string tecla)
    {
        var items = ItemsFiltrados;
        if (items.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : items.ToList().FindIndex(i => i.Id == _idEnfocado);
                    _idEnfocado = items[Math.Min(indiceActual + 1, items.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : items.ToList().FindIndex(i => i.Id == _idEnfocado);
                    _idEnfocado = items[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                {
                    var item = items.FirstOrDefault(i => i.Id == idAbrir);
                    if (item is not null)
                        await AccionesBandeja.AbrirAsync(item, NavigationManager, WorkspaceService);
                }
                break;
        }

        StateHasChanged();
    }
}
