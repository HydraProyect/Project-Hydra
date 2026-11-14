using System.Security.Claims;
using CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Commands.CorregirRevisionIaDocumento;
using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class RevisionIaTab : ComponentBase, IDisposable
{
    /// <summary>
    /// «Confirmar todos los ≥95 %»: la confirmación en lote exige esta confianza (y fecha detectada, sin alerta, con la
    /// vigencia que fija el tipo). No es el umbral del modelo: solo decide qué lecturas entran en el botón de la cabecera,
    /// y el Gestor CAE lo confirma en un diálogo antes de que se acepte nada.
    /// </summary>
    private const int UmbralLote = 95;

    /// <summary>
    /// D-49: el avance automático al siguiente se recuerda por usuario en este navegador (no hay almacén de preferencias
    /// de vista en servidor para esta pantalla: <c>PantallasConVistaRecordada</c> es de los listados). Misma convención
    /// que las preferencias del Cuadro de dirección: la clave lleva el id del usuario, sin usuario no se recuerda nada.
    /// </summary>
    private const string PrefijoClaveAvance = "revision-ia:avance-automatico";

    private const string ModuloPreferencias = "./js/revision-ia.js";

    private sealed record FalloAceptacion(string Propietario, string Tipo, string Motivo);

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private IReadOnlyList<FalloAceptacion> _fallosBloque = [];

    private IReadOnlyList<RevisionIaDocumentoDto> _revisiones = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private bool _confirmandoLote;
    private bool _operacionEnCurso;
    private bool _confirmacionLoteVisible;
    private bool _confirmacionDescartarVisible;
    private bool _correccionManualVisible;
    private bool _desplegableAbierto;
    private bool _tecladoInstalado;
    private bool _devolverFoco;
    private ElementReference _popRef;
    private ElementReference _botonSelector;
    private bool _dispose;
    private Guid? _procesandoId;
    private Guid? _revisionIdSeleccionada;

    /// <summary>Con el avance automático apagado, al terminar una revisión no se elige otra: el Gestor CAE escoge la siguiente.</summary>
    private bool _sinSeleccion;

    /// <summary>La revisión a la que se pasa tras terminar una (se consume en la carga que sigue).</summary>
    private Guid? _proximaSeleccion;

    private bool _avanceAutomatico = true;

    /// <summary>El usuario ya cambió la casilla: la lectura inicial (asíncrona) de la preferencia guardada no la pisa.</summary>
    private bool _avanceTocadoPorElUsuario;
    private string? _claveAvance;

    /// <summary>Cuántas lecturas se han resuelto en esta visita: el «Has revisado los N» de cuando no queda ninguna.</summary>
    private int _revisadas;

    private string _fechaEmisionManual = string.Empty;
    private string? _errorFechaManual;

    /// <summary>La fecha de emisión corregida tiene un texto que no es una fecha entera: Guardar no la envía.</summary>
    private bool _emisionManualNoValida;
    private string? _mensajeErrorCorreccion;
    private FiltroRevision _filtro;
    private CancellationTokenSource? _cargaCts;
    private int _generacionCarga;
    private int _generacionEntidad;

    private enum FiltroRevision
    {
        Todas,
        Confirmables,
        Manual,
        SinFecha
    }

    private static IReadOnlyList<FiltroRevision> Filtros { get; } =
    [
        FiltroRevision.Todas,
        FiltroRevision.Confirmables,
        FiltroRevision.Manual,
        FiltroRevision.SinFecha
    ];

    private IReadOnlyList<RevisionIaDocumentoDto> RevisionesVisibles => _revisiones.Where(CumpleFiltro).ToList();

    // «Confirmar todos» es una acción de la cola entera, no de la lista que deja ver el filtro: con «Manual» o «Sin fecha»
    // activos (donde por definición no hay confirmables a la vista) el botón sigue ahí con el recuento de la cola, y el
    // diálogo dice cuántas se aceptan. Con más de MaximoRevisionesPorBloque, el rótulo dice «las primeras N de M».
    private IEnumerable<RevisionIaDocumentoDto> TodasLasConfirmables => _revisiones.Where(EsConfirmable);

    /// <summary>
    /// El texto del diálogo de «Confirmar todos»: cuántas lecturas se aceptan de cuántas (con recorte, «las primeras N de M»)
    /// y, si el filtro oculta alguna de ellas, que entran igual porque el botón actúa sobre toda la cola.
    /// </summary>
    private string MensajeConfirmacionLote
    {
        get
        {
            var seleccionadas = ConfirmablesEnBloque;
            var enBloque = seleccionadas.Count;
            var total = TodasLasConfirmables.Count();
            var pendientes = _revisiones.Count;

            // Concordancia: «la única», «las N» (todas entran: no hay «demás») o «N de las M» (el resto sigue pendiente).
            var mensaje = total > enBloque
                ? Textos["RevIaDialogoMensajePrimeras", enBloque, UmbralLote, total].Value
                : pendientes == 1 && enBloque == 1
                    ? Textos["RevIaDialogoMensajeUna", enBloque, UmbralLote, pendientes].Value
                    : enBloque == pendientes
                        ? Textos["RevIaDialogoMensajeTodas", enBloque, UmbralLote, pendientes].Value
                        : Textos["RevIaDialogoMensaje", enBloque, UmbralLote, pendientes].Value;

            // Las ocultas se cuentan sobre lo que se envía de verdad (recortado al máximo del bloque), no sobre toda la cola.
            var ocultas = seleccionadas.Count(r => !CumpleFiltro(r));
            return ocultas switch
            {
                0 => mensaje,
                1 => $"{mensaje} {string.Format(Textos["RevIaDialogoFiltroOcultaUna"].Value, ocultas)}",
                _ => $"{mensaje} {string.Format(Textos["RevIaDialogoFiltroOculta"].Value, ocultas)}"
            };
        }
    }

    private IReadOnlyList<RevisionIaDocumentoDto> ConfirmablesEnBloque => TodasLasConfirmables
        .Take(AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque)
        .ToList();

    private string TextoConfirmarTodos
    {
        get
        {
            var enBloque = ConfirmablesEnBloque.Count;
            var total = TodasLasConfirmables.Count();
            return total > enBloque
                ? Textos["RevIaConfirmarPrimeras", enBloque, total, UmbralLote]
                : Textos["RevIaConfirmarTodos", UmbralLote, enBloque];
        }
    }

    private RevisionIaDocumentoDto? RevisionSeleccionada => _revisiones.FirstOrDefault(r => r.Id == _revisionIdSeleccionada);

    private int IndiceSeleccionada => RevisionesVisibles.ToList().FindIndex(r => r.Id == _revisionIdSeleccionada);
    private bool HayAnterior => IndiceSeleccionada > 0;
    private bool HaySiguiente => RevisionesVisibles.Count > 0 && IndiceSeleccionada < RevisionesVisibles.Count - 1;
    private RevisionIaDocumentoDto? SiguienteDeLaLista => HaySiguiente ? RevisionesVisibles[IndiceSeleccionada + 1] : null;

    /// <summary>«1 de 4» sobre lo que el filtro deja ver; sin revisión elegida, solo el total.</summary>
    private string PosicionTexto => IndiceSeleccionada >= 0
        ? string.Format(Textos["RevIaNDeM"].Value, IndiceSeleccionada + 1, RevisionesVisibles.Count)
        : RevisionesVisibles.Count.ToString();

    protected override async Task OnInitializedAsync()
    {
        if (EstadoAutenticacion is not null)
        {
            var usuario = (await EstadoAutenticacion).User;
            if (usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } usuarioId)
            {
                _claveAvance = $"{PrefijoClaveAvance}:{usuarioId}";
            }
        }

        await CargarAsync();
    }

    private async Task AjustarFocoDelDesplegableAsync()
    {
        if (_dispose)
        {
            return;
        }

        try
        {
            if (_desplegableAbierto && !_tecladoInstalado)
            {
                _tecladoInstalado = true;
                await using var modulo = await Js.InvokeAsync<IJSObjectReference>("import", ModuloPreferencias);
                await modulo.InvokeVoidAsync("instalarTecladoOpciones", _popRef);
            }
            else if (!_desplegableAbierto)
            {
                _tecladoInstalado = false;
                if (_devolverFoco)
                {
                    _devolverFoco = false;
                    await _botonSelector.FocusAsync();
                }
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Sin circuito o sin el elemento (la cola quedó vacía): el desplegable sigue siendo operable con el ratón y con Tab.
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await AjustarFocoDelDesplegableAsync();

        if (!firstRender || _claveAvance is null)
        {
            return;
        }

        try
        {
            await using var modulo = await Js.InvokeAsync<IJSObjectReference>("import", ModuloPreferencias);
            var guardado = await modulo.InvokeAsync<string?>("leer", _claveAvance);
            // La lectura es asíncrona: si el usuario ya movió la casilla mientras tanto, su gesto manda sobre el valor guardado.
            if (!_dispose && !_avanceTocadoPorElUsuario && guardado is "0" or "1")
            {
                _avanceAutomatico = guardado == "1";
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Sin almacenamiento o sin circuito: el avance sigue activado, que es el valor por defecto de D-49.
        }
    }

    private async Task CambiarAvanceAsync(bool activado)
    {
        _avanceAutomatico = activado;
        _avanceTocadoPorElUsuario = true;
        if (_claveAvance is null)
        {
            return;
        }

        try
        {
            await using var modulo = await Js.InvokeAsync<IJSObjectReference>("import", ModuloPreferencias);
            await modulo.InvokeVoidAsync("guardar", _claveAvance, activado ? "1" : "0");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Recordarlo es una comodidad: si no se puede, el cambio vale solo para esta visita.
        }
    }

    private async Task CargarAsync(bool silenciosa = false)
    {
        _cargaCts?.Cancel();
        _cargaCts?.Dispose();
        var cts = _cargaCts = new CancellationTokenSource();
        var generacion = ++_generacionCarga;
        var token = cts.Token;
        _cargando = !silenciosa;
        _errorCarga = false;

        StateHasChanged();

        try
        {
            var revisiones = await Mediator.Send(new ObtenerRevisionesIaPendientesQuery(), token);
            if (_dispose || generacion != _generacionCarga)
            {
                return;
            }

            _revisiones = revisiones;

            // Un filtro que se queda sin lecturas (se resolvió la última «Manual») vuelve a «Todas»: no hay pantalla vacía con
            // la cola aún llena.
            if (_filtro != FiltroRevision.Todas && RevisionesVisibles.Count == 0 && revisiones.Count > 0)
            {
                _filtro = FiltroRevision.Todas;
            }

            if (_proximaSeleccion is { } proxima)
            {
                _proximaSeleccion = null;
                if (RevisionesVisibles.Any(r => r.Id == proxima))
                {
                    _revisionIdSeleccionada = proxima;
                    ReiniciarOperacionPorCambioDeEntidad();
                }
                else
                {
                    SeleccionarPrimeraVisible();
                }
            }
            else if (_sinSeleccion && !RevisionesVisibles.Any(r => r.Id == _revisionIdSeleccionada))
            {
                _revisionIdSeleccionada = null;
            }
            else
            {
                SeleccionarPrimeraVisible();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_dispose && generacion == _generacionCarga)
            {
                _errorCarga = true;
            }
        }
        finally
        {
            if (!_dispose && generacion == _generacionCarga)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// A qué revisión se pasa al terminar <paramref name="resuelta"/>: la que sigue en la lista que se está viendo; si era la
    /// última, la primera que quede. <c>null</c> si no queda otra.
    /// </summary>
    private RevisionIaDocumentoDto? SiguienteTras(Guid resuelta)
    {
        var visibles = RevisionesVisibles;
        var indice = visibles.ToList().FindIndex(r => r.Id == resuelta);
        return (indice >= 0 ? visibles.Skip(indice + 1).FirstOrDefault() : null)
            ?? visibles.FirstOrDefault(r => r.Id != resuelta);
    }

    /// <summary>
    /// Cierra una revisión resuelta: avisa, pasa a la siguiente si el avance automático está activado (D-49) y recarga la
    /// cola. Con el avance apagado no se elige ninguna: el Gestor CAE escoge del desplegable.
    /// </summary>
    private async Task TerminarRevisionAsync(RevisionIaDocumentoDto resuelta, string avisoSinAvance)
    {
        _revisadas++;
        var siguiente = _avanceAutomatico ? SiguienteTras(resuelta.Id) : null;
        _proximaSeleccion = siguiente?.Id;
        _sinSeleccion = !_avanceAutomatico;
        ToastService.Mostrar(
            siguiente is null
                ? avisoSinAvance
                : string.Format(Textos["RevIaAvisoRevisado"].Value, resuelta.PropietarioNombre, resuelta.TipoDocumentoNombre, siguiente.PropietarioNombre),
            TonoToast.Exito);
        await CargarAsync(silenciosa: true);
    }

    private async Task AceptarDeteccionAsync(Guid revisionId)
    {
        if (_operacionEnCurso || _revisiones.FirstOrDefault(r => r.Id == revisionId) is not { } revision)
        {
            return;
        }

        var generacionEntidad = _generacionEntidad;
        _operacionEnCurso = true;
        _procesandoId = revisionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AplicarDeteccionIaDocumentoCommand(revisionId));
            if (resultado.EsFallido)
            {
                if (!_dispose && generacionEntidad == _generacionEntidad)
                {
                    ToastService.MostrarError(resultado.Error);
                }

                return;
            }

            if (_dispose || generacionEntidad != _generacionEntidad)
            {
                return;
            }

            await TerminarRevisionAsync(revision, Textos["RevIaAvisoAceptada"]);
        }
        finally
        {
            if (!_dispose && generacionEntidad == _generacionEntidad)
            {
                _procesandoId = null;
            }

            _operacionEnCurso = false;
        }
    }

    private async Task ResolverSeleccionadaAsync()
    {
        if (_revisionIdSeleccionada is not { } revisionId || RevisionSeleccionada is not { } revision || _operacionEnCurso)
        {
            return;
        }

        var generacionEntidad = _generacionEntidad;
        _operacionEnCurso = true;
        _procesandoId = revisionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverRevisionIaDocumentoCommand(revisionId));
            if (resultado.EsFallido)
            {
                if (!_dispose && generacionEntidad == _generacionEntidad)
                {
                    ToastService.MostrarError(resultado.Error);
                }

                return;
            }

            if (_dispose || generacionEntidad != _generacionEntidad)
            {
                return;
            }

            _confirmacionDescartarVisible = false;
            await TerminarRevisionAsync(revision, Textos["RevIaAvisoDescartada"]);
        }
        finally
        {
            if (!_dispose && generacionEntidad == _generacionEntidad)
            {
                _procesandoId = null;
            }

            _operacionEnCurso = false;
        }
    }

    private async Task CorregirSeleccionadaAsync()
    {
        if (_revisionIdSeleccionada is not { } revisionId || RevisionSeleccionada is not { } revision || _operacionEnCurso)
        {
            return;
        }

        // Una emisión a medias ya llega como «»; la marca dice que hay que corregirla, no que falte.
        if (_emisionManualNoValida || !DateOnly.TryParse(_fechaEmisionManual, out var fechaEmision))
        {
            _errorFechaManual = Textos["RevIaFechaNoValida"];
            return;
        }

        var generacionEntidad = _generacionEntidad;
        _operacionEnCurso = true;
        _procesandoId = revisionId;
        _errorFechaManual = null;
        _mensajeErrorCorreccion = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CorregirRevisionIaDocumentoCommand(revisionId, fechaEmision));
            if (resultado.EsFallido)
            {
                _mensajeErrorCorreccion = resultado.Error.Mensaje;
                if (!_dispose && generacionEntidad == _generacionEntidad)
                {
                    ToastService.MostrarError(resultado.Error);
                }

                return;
            }

            if (_dispose || generacionEntidad != _generacionEntidad)
            {
                return;
            }

            _correccionManualVisible = false;
            await TerminarRevisionAsync(revision, Textos["RevIaAvisoCorregida"]);
        }
        finally
        {
            if (!_dispose && generacionEntidad == _generacionEntidad)
            {
                _procesandoId = null;
            }

            _operacionEnCurso = false;
        }
    }

    private async Task AceptarConfirmablesAsync()
    {
        if (_confirmandoLote || _operacionEnCurso)
        {
            return;
        }

        var seleccionadas = ConfirmablesEnBloque;
        if (seleccionadas.Count == 0)
        {
            return;
        }

        var datos = seleccionadas.ToDictionary(r => r.Id);
        _confirmandoLote = true;
        _operacionEnCurso = true;
        _fallosBloque = [];
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AceptarDeteccionesIaEnBloqueCommand(seleccionadas.Select(r => r.Id).ToList()));
            if (_dispose)
            {
                return;
            }

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            var desenlace = resultado.Valor;
            _fallosBloque = desenlace.Resultados
                .Where(r => !r.Aceptada)
                .Select(r => new FalloAceptacion(
                    datos[r.RevisionId].PropietarioNombre, datos[r.RevisionId].TipoDocumentoNombre, r.Mensaje ?? Textos["RevIaFalloGenerico"]))
                .ToList();
            ToastService.Mostrar(
                ResumenBloque(desenlace.Aceptadas, seleccionadas.Count, desenlace.Fallidas),
                desenlace.Fallidas == 0 ? TonoToast.Exito : TonoToast.Advertencia);
            _revisadas += desenlace.Aceptadas;
            _confirmacionLoteVisible = false;
            await CargarAsync(silenciosa: true);
        }
        finally
        {
            _confirmandoLote = false;
            _operacionEnCurso = false;
        }
    }

    private void CerrarFallosBloque() => _fallosBloque = [];

    private void CambiarFiltro(FiltroRevision filtro)
    {
        if (_operacionEnCurso)
        {
            return;
        }

        _filtro = filtro;
        _confirmacionLoteVisible = false;
        _confirmacionDescartarVisible = false;
        _correccionManualVisible = false;
        SeleccionarPrimeraVisible();
    }

    private int Recuento(FiltroRevision filtro) => _revisiones.Count(r => CumpleFiltro(r, filtro));

    private void AlternarDesplegable() => _desplegableAbierto = !_desplegableAbierto;
    private void CerrarDesplegable() => _desplegableAbierto = false;

    private void TeclaEnLateral(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            _devolverFoco = _desplegableAbierto;
            _desplegableAbierto = false;
        }
    }

    private void SeleccionarDesdeDesplegable(Guid revisionId)
    {
        _desplegableAbierto = false;
        _devolverFoco = true;

        // Como ‹ ›, j/k y los filtros: con un Command en vuelo no se cambia de revisión. El desplegable se cierra igual, así
        // que el foco vuelve al disparador también aquí.
        if (_operacionEnCurso)
        {
            return;
        }

        SeleccionarRevision(revisionId);
    }

    private void SeleccionarRevision(Guid revisionId)
    {
        if (_revisionIdSeleccionada == revisionId)
        {
            return;
        }

        _sinSeleccion = false;
        _revisionIdSeleccionada = revisionId;
        ReiniciarOperacionPorCambioDeEntidad();
    }

    private void SeleccionarPrimeraVisible()
    {
        if (RevisionSeleccionada is not null && RevisionesVisibles.Any(r => r.Id == _revisionIdSeleccionada))
        {
            return;
        }

        _sinSeleccion = false;
        _revisionIdSeleccionada = RevisionesVisibles.FirstOrDefault()?.Id;
        ReiniciarOperacionPorCambioDeEntidad();
    }

    private void ReiniciarOperacionPorCambioDeEntidad()
    {
        _generacionEntidad++;
        _confirmacionDescartarVisible = false;
        _correccionManualVisible = false;
        _desplegableAbierto = false;
        _procesandoId = null;
    }

    private Task IrAlSiguienteAsync()
    {
        if (_operacionEnCurso)
        {
            return Task.CompletedTask;
        }

        var visibles = RevisionesVisibles;
        if (visibles.Count == 0)
        {
            return Task.CompletedTask;
        }

        var indice = IndiceSeleccionada;
        if (indice < visibles.Count - 1)
        {
            SeleccionarRevision(visibles[indice + 1].Id);
        }

        return Task.CompletedTask;
    }

    private Task IrAlAnteriorAsync()
    {
        if (_operacionEnCurso)
        {
            return Task.CompletedTask;
        }

        var indice = IndiceSeleccionada;
        if (indice > 0)
        {
            SeleccionarRevision(RevisionesVisibles[indice - 1].Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// j / k, como en el resto de listas: siguiente y anterior. Con un panel abierto (corrección a mano, confirmaciones) o una
    /// operación en curso no se cambia de revisión: el cambio cerraría lo que se está haciendo.
    /// </summary>
    private Task ManejarAtajoAsync(string tecla)
    {
        if (_operacionEnCurso || _correccionManualVisible || _confirmacionLoteVisible || _confirmacionDescartarVisible)
        {
            return Task.CompletedTask;
        }

        return tecla switch
        {
            "j" => IrAlSiguienteAsync(),
            "k" => IrAlAnteriorAsync(),
            _ => Task.CompletedTask
        };
    }

    private void PrepararConfirmacionLote() => _confirmacionLoteVisible = ConfirmablesEnBloque.Count > 0;
    private void PrepararDescartar() => _confirmacionDescartarVisible = RevisionSeleccionada is not null;
    private void PrepararCorreccionManual()
    {
        if (RevisionSeleccionada is not { } revision)
        {
            return;
        }

        _fechaEmisionManual = revision.FechaEmisionIntroducida?.ToString("yyyy-MM-dd") ?? string.Empty;
        _fechaEmisionManualAlAbrir = _fechaEmisionManual;
        _errorFechaManual = null;
        _emisionManualNoValida = false;
        _mensajeErrorCorreccion = null;
        _correccionManualVisible = true;
    }

    // P1-E2: único punto de verdad de «hay cambios» en la corrección manual: la fecha
    // tecleada frente a la precargada al abrir.
    private string _fechaEmisionManualAlAbrir = string.Empty;

    private bool HayCambiosSinGuardar => _correccionManualVisible && (_fechaEmisionManual != _fechaEmisionManualAlAbrir || _emisionManualNoValida);

    private bool CumpleFiltro(RevisionIaDocumentoDto revision) => CumpleFiltro(revision, _filtro);

    private static bool CumpleFiltro(RevisionIaDocumentoDto revision, FiltroRevision filtro) => filtro switch
    {
        FiltroRevision.Confirmables => EsConfirmable(revision),
        FiltroRevision.Manual => revision.FechaEmisionDetectada is not null && !EsConfirmable(revision),
        FiltroRevision.SinFecha => revision.FechaEmisionDetectada is null,
        _ => true
    };

    // Aceptable en bloque = hay fecha de emisión leída, el tipo calcula el vencimiento desde ella y no hay alerta
    // determinista ni lectura parcial: lo único que el servidor deja aceptar sin que nadie vea la lectura. La vigencia que
    // confirma el Gestor CAE a mano y las alertas se aceptan de una en una, con el motivo a la vista. El servidor lo impone
    // igual (AplicarDeteccionIaDocumentoCommand): este predicado solo evita ofrecer lo que rechazaría.
    private static bool EsAceptableEnBloque(RevisionIaDocumentoDto revision) =>
        revision.FechaEmisionDetectada is not null && revision.VigenciaLaFijaElTipo && !revision.TieneAlertaDeterminista;

    // Confirmable = aceptable en bloque Y con la confianza del umbral (UmbralLote, «≥95 %»). Es el único criterio de «Confirmar
    // todos», del filtro «Confirmables» y de la etiqueta «Confirmable», para que el recuento del filtro y el del botón coincidan
    // (artboard «Revision-ia»). Una lectura con fecha y 58 % no es confirmable: va a «Manual», con su motivo a la vista.
    private static bool EsConfirmable(RevisionIaDocumentoDto revision) =>
        EsAceptableEnBloque(revision) && revision.ConfianzaGeneral >= UmbralLote;

    private string TextoFiltro(FiltroRevision f) => f switch
    {
        FiltroRevision.Confirmables => Textos["RevIaFiltroConfirmables"],
        FiltroRevision.Manual => Textos["RevIaFiltroManual"],
        FiltroRevision.SinFecha => Textos["RevIaFiltroSinFecha"],
        _ => Textos["RevIaFiltroTodas"]
    };

    private string Ambito(RevisionIaDocumentoDto r) => r.TrabajadorId is null ? Textos["RevIaAmbitoEmpresa"] : Textos["RevIaAmbitoTrabajador"];

    private string EtiquetaTratamiento(RevisionIaDocumentoDto r) => r.TieneAlertaDeterminista
        ? Textos["RevIaConAlerta"]
        : EsConfirmable(r)
            ? Textos["RevIaConfirmable"]
            : r.FechaEmisionDetectada is null ? Textos["RevIaSinFechaDetectada"] : Textos["RevIaRevisionManual"];

    private string ResumenBloque(int aceptadas, int solicitadas, int fallidas) => aceptadas == solicitadas
        ? Textos["RevIaResumenTodas", aceptadas]
        : Textos["RevIaResumenParcial", aceptadas, solicitadas, fallidas];

    private string Fecha(DateOnly? fecha) => fecha?.ToString("dd/MM/yyyy") ?? Textos["RevIaNoDisponible"].Value;

    // La lista de campos pinta las fechas en dd/MM/yyyy (formato único) y «—» sin dato. El botón de copiar de
    // TextoFechaCopiable copia el mismo dd/MM/yyyy.
    private static string FechaCorta(DateOnly? fecha) => fecha?.ToString("dd/MM/yyyy") ?? "—";

    /// <summary>
    /// Una fila de la lista corta de campos. <c>Introducido</c> es lo que se tecleó al subir; <c>Leido</c> lo que dice la IA.
    /// <c>Coincide</c> pinta «Igual»; <c>Difiere</c> resalta la fila (lectura distinta, o la IA no leyó lo que sí se introdujo).
    /// </summary>
    private sealed record FilaComparacion(
        string Campo,
        string Introducido,
        DateOnly? FechaIntroducida,
        string Leido,
        DateOnly? FechaLeida,
        bool Coincide,
        bool Difiere);

    // Solo presentación: lo leído y lo introducido ya venían en el DTO. «Igual» solo cuando hay dos valores que comparar;
    // con la lectura vacía no se afirma una coincidencia que nadie ha visto.
    private IReadOnlyList<FilaComparacion> FilasComparacion(RevisionIaDocumentoDto r)
    {
        var tipoLeido = !string.IsNullOrWhiteSpace(r.TipoDetectado);
        var tipoCoincide = tipoLeido && string.Equals(r.TipoDetectado!.Trim(), r.TipoDocumentoNombre.Trim(), StringComparison.OrdinalIgnoreCase);
        return
        [
            new FilaComparacion(
                Textos["RevIaCampoTipo"],
                r.TipoDocumentoNombre,
                null,
                !tipoLeido ? Textos["RevIaTipoNoDeterminado"].Value : tipoCoincide ? Textos["RevIaIgual"].Value : r.TipoDetectado!,
                null,
                tipoCoincide,
                tipoLeido && !tipoCoincide),
            FilaFecha(Textos["RevIaCampoEmision"], r.FechaEmisionDetectada, r.FechaEmisionIntroducida),
            FilaFecha(Textos["RevIaCampoVencimiento"], r.FechaVencimientoDetectada, r.FechaVencimientoIntroducida),
            new FilaComparacion(Textos["RevIaCampoFirma"], Textos["RevIaFirmaNoSolicitada"].Value, null, Firma(r.TieneFirmaDetectada), null, false, false)
        ];
    }

    private FilaComparacion FilaFecha(string campo, DateOnly? leida, DateOnly? introducida)
    {
        var coincide = leida is not null && introducida is not null && leida == introducida;
        return new FilaComparacion(
            campo,
            FechaCorta(introducida),
            introducida,
            coincide ? Textos["RevIaIgual"].Value : leida is null ? Textos["RevIaNoDetectada"].Value : FechaCorta(leida),
            coincide || leida is null ? null : leida,
            coincide,
            leida is null ? introducida is not null : introducida is not null && leida != introducida);
    }

    private string Firma(bool? tieneFirma) => tieneFirma switch
    {
        true => Textos["RevIaFirmaDetectada"],
        false => Textos["RevIaFirmaNoDetectada"],
        _ => Textos["RevIaFirmaNoDeterminada"]
    };

    // Una alerta no se pinta en verde aunque la IA declare confianza alta.
    private static TonoBadge TonoTratamiento(RevisionIaDocumentoDto r) => r.TieneAlertaDeterminista && r.ConfianzaGeneral >= 70
        ? TonoBadge.Advertencia
        : TonoDeConfianzaIa.De(r.ConfianzaGeneral);

    public void Dispose()
    {
        _dispose = true;
        _cargaCts?.Cancel();
        _cargaCts?.Dispose();
        _cargaCts = null;
    }
}
