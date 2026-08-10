using CaeManager.Application.Telemetria.Commands.RegistrarTramoGestion;
using CaeManager.Application.Telemetria.Queries.ObtenerTiempoGestionConversacion;
using CaeManager.Domain.Telemetria;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Comunicaciones.Components;

/// <summary>
/// Mide cuánto tiempo lleva el Gestor trabajando esta conversación y lo muestra
/// en pantalla mientras lo hace.
///
/// Tres decisiones que no son cosméticas y no deben deshacerse sin volver a
/// pasar por la circular del art. 87 LOPDGDD:
/// <list type="number">
/// <item>El contador es <b>visible</b>. Nada se cuenta en segundo plano sin que
/// la persona medida lo vea, y lo que ve es exactamente lo que se guarda.</item>
/// <item>Hay <b>pausa manual</b>. Una llamada o una reunión no son tiempo de
/// gestión, y quien trabaja debe poder decirlo sin pedir permiso.</item>
/// <item>La señal de actividad es el <b>ciclo de vida de la página</b>, no los
/// periféricos (ver <c>wwwroot/js/medidor-tiempo.js</c>).</item>
/// </list>
///
/// Todo el componente se desactiva con <c>ParametroSistema.MedicionTiempoActiva</c>:
/// apagado no se carga el módulo JS ni se escribe una sola fila.
/// </summary>
public partial class MedidorTiempoGestion : ComponentBase, IAsyncDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    [Parameter, EditorRequired] public Guid ConversacionId { get; set; }

    private IJSObjectReference? _modulo;
    private DotNetObjectReference<MedidorTiempoGestion>? _referencia;

    private bool _medicionActiva;
    private int _segundosInactividad = 120;

    /// <summary>Segundos ya guardados en tramos anteriores de esta persona sobre este hilo — el contador arranca desde aquí, no desde cero.</summary>
    private int _segundosPrevios;

    private Guid _conversacionMedida;
    private DateTime? _tramoInicioUtc;
    private DateTime? _ultimoReanudadoUtc;
    private int _segundosAcumuladosDelTramo;
    private bool _pausaManual;
    private bool _pausaEntorno;
    private string? _motivoPausaEntorno;

    private PeriodicTimer? _reloj;
    private CancellationTokenSource? _cancelacionReloj;

    private bool EnPausa => _pausaManual || _pausaEntorno;

    private string TextoEstado => _pausaManual
        ? "En pausa — tarea externa"
        : _pausaEntorno
            ? _motivoPausaEntorno switch
            {
                "inactividad" => "En pausa — sin actividad",
                _ => "En pausa — pestaña en segundo plano"
            }
            : "Midiendo";

    private string TextoTiempo
    {
        get
        {
            var total = TimeSpan.FromSeconds(_segundosPrevios + SegundosDelTramoAhora());
            return total.TotalHours >= 1
                ? $"{(int)total.TotalHours}h {total.Minutes:00}m"
                : $"{total.Minutes:00}:{total.Seconds:00}";
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ConversacionId == _conversacionMedida) return;

        // Cambiar de hilo cierra el tramo del anterior: lo que se estaba midiendo era
        // trabajo sobre aquella conversación, no sobre esta.
        await CerrarTramoAsync(MotivoCierreSesionGestion.NavegacionFuera);

        _conversacionMedida = ConversacionId;
        _segundosPrevios = 0;
        _pausaManual = false;
        _pausaEntorno = false;
        _motivoPausaEntorno = null;

        if (ConversacionId == Guid.Empty)
        {
            _medicionActiva = false;
            return;
        }

        var estado = await Mediator.Send(new ObtenerTiempoGestionConversacionQuery(ConversacionId));
        _medicionActiva = estado.MedicionActiva;
        _segundosPrevios = estado.SegundosAcumulados;
        _segundosInactividad = estado.SegundosInactividadPausa;

        if (_medicionActiva) await ArrancarTramoAsync();
    }

    protected override async Task OnAfterRenderAsync(bool primeraVez)
    {
        if (!_medicionActiva || _modulo is not null || !RendererInfo.IsInteractive) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/medidor-tiempo.js");
        _referencia = DotNetObjectReference.Create(this);
        await _modulo.InvokeVoidAsync("iniciar", ClaveJs, _referencia, _segundosInactividad);
    }

    private string ClaveJs => $"medidor-{_conversacionMedida}";

    private Task ArrancarTramoAsync()
    {
        var ahora = DateTime.UtcNow;
        _tramoInicioUtc = ahora;
        _ultimoReanudadoUtc = ahora;
        _segundosAcumuladosDelTramo = 0;
        ArrancarReloj();
        return Task.CompletedTask;
    }

    /// <summary>Refresco del contador visible, una vez por segundo. No escribe nada — solo hace visible lo que ya se está midiendo.</summary>
    private void ArrancarReloj()
    {
        DetenerReloj();
        _cancelacionReloj = new CancellationTokenSource();
        var token = _cancelacionReloj.Token;
        _reloj = new PeriodicTimer(TimeSpan.FromSeconds(1));

        _ = Task.Run(async () =>
        {
            try
            {
                while (await _reloj.WaitForNextTickAsync(token))
                    await InvokeAsync(StateHasChanged);
            }
            catch (OperationCanceledException)
            {
                // Cierre normal del componente.
            }
        }, token);
    }

    private void DetenerReloj()
    {
        _cancelacionReloj?.Cancel();
        _cancelacionReloj?.Dispose();
        _cancelacionReloj = null;
        _reloj?.Dispose();
        _reloj = null;
    }

    private int SegundosDelTramoAhora()
    {
        var enCurso = !EnPausa && _ultimoReanudadoUtc is { } desde
            ? (int)(DateTime.UtcNow - desde).TotalSeconds
            : 0;

        return Math.Min(_segundosAcumuladosDelTramo + enCurso, RegistrarTramoGestionCommandHandler.SegundosMaximosPorTramo);
    }

    private void Congelar()
    {
        if (_ultimoReanudadoUtc is { } desde)
            _segundosAcumuladosDelTramo += (int)(DateTime.UtcNow - desde).TotalSeconds;

        _ultimoReanudadoUtc = null;
    }

    [JSInvokable]
    public async Task EnPausaPorEntorno(string? motivo)
    {
        if (_pausaEntorno) return;

        if (!_pausaManual) Congelar();
        _pausaEntorno = true;
        _motivoPausaEntorno = motivo;

        // La inactividad cierra el tramo, no solo lo pausa: si nadie vuelve, lo
        // trabajado hasta aquí ya está guardado en vez de perderse con el circuito.
        if (motivo == "inactividad")
            await CerrarTramoAsync(MotivoCierreSesionGestion.Inactividad, reiniciar: true);

        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task ReanudadoPorEntorno(string? _)
    {
        if (!_pausaEntorno) return;

        _pausaEntorno = false;
        _motivoPausaEntorno = null;
        if (!_pausaManual) _ultimoReanudadoUtc = DateTime.UtcNow;

        await InvokeAsync(StateHasChanged);
    }

    private async Task AlternarPausaManualAsync()
    {
        if (_pausaManual)
        {
            _pausaManual = false;
            if (!_pausaEntorno) _ultimoReanudadoUtc = DateTime.UtcNow;
            return;
        }

        Congelar();
        _pausaManual = true;
        await CerrarTramoAsync(MotivoCierreSesionGestion.PausaManual, reiniciar: true);
    }

    /// <summary>
    /// Lo llama la Bandeja cuando el Gestor completa una acción real sobre el hilo
    /// (responder, confirmar una sugerencia, cambiar el estado): cierra el tramo con
    /// motivo <see cref="MotivoCierreSesionGestion.AccionCompletada"/> y abre uno nuevo.
    /// </summary>
    public async Task RegistrarAccionCompletadaAsync()
    {
        if (!_medicionActiva) return;

        Congelar();
        await CerrarTramoAsync(MotivoCierreSesionGestion.AccionCompletada, reiniciar: true);

        if (_modulo is not null)
        {
            try { await _modulo.InvokeVoidAsync("marcarInteraccion", ClaveJs); }
            catch (JSDisconnectedException) { }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task CerrarTramoAsync(MotivoCierreSesionGestion motivo, bool reiniciar = false)
    {
        if (!_medicionActiva || _conversacionMedida == Guid.Empty || _tramoInicioUtc is not { } inicio) return;

        Congelar();
        var segundos = Math.Min(_segundosAcumuladosDelTramo, RegistrarTramoGestionCommandHandler.SegundosMaximosPorTramo);
        var fin = DateTime.UtcNow;

        _segundosPrevios += segundos;
        _segundosAcumuladosDelTramo = 0;
        _tramoInicioUtc = reiniciar ? fin : null;
        _ultimoReanudadoUtc = reiniciar && !EnPausa ? fin : null;

        if (segundos == 0) return;

        try
        {
            await Mediator.Send(new RegistrarTramoGestionCommand(_conversacionMedida, inicio, fin, segundos, motivo));
        }
        catch (Exception)
        {
            // Mejor esfuerzo: perder un tramo de telemetría nunca debe romper la
            // pantalla ni bloquear la gestión que el usuario está haciendo.
        }
    }

    public async ValueTask DisposeAsync()
    {
        DetenerReloj();
        await CerrarTramoAsync(MotivoCierreSesionGestion.CierreCircuito);

        if (_modulo is not null)
        {
            try
            {
                await _modulo.InvokeVoidAsync("detener", ClaveJs);
                await _modulo.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // El circuito ya se fue; no hay nada que limpiar en el navegador.
            }
        }

        _referencia?.Dispose();
        GC.SuppressFinalize(this);
    }
}
