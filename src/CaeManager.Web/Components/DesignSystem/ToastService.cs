using CaeManager.Domain.Common;

namespace CaeManager.Web.Components.DesignSystem;

public enum TonoToast
{
    Info,
    Exito,
    Advertencia,
    Error
}

public record ToastMensaje(Guid Id, string Mensaje, TonoToast Tono, string? TextoAccion = null, Func<Task>? OnAccion = null);

/// <summary>
/// Servicio de avisos de la interfaz (P1-E1): el único canal para comunicar al
/// usuario el resultado de una acción, incluidos los errores esperados.
///
/// <para><b>Los tres canales de error, y cuándo se usa cada uno.</b></para>
/// <list type="number">
/// <item><b>Aviso en línea</b>, junto al campo o al formulario: validación de lo
/// que el usuario está escribiendo (<c>_erroresCampo</c>, <c>ValidationMessage</c>,
/// el <c>ValidationException</c> de un Command). El usuario tiene que corregir algo
/// que está viendo, así que el mensaje va donde está mirando y no desaparece solo.
/// También en línea: una región que no pudo cargar sus datos (<c>_errorCarga</c>),
/// porque lo que falta es esa región, no el resultado de un clic.</item>
/// <item><b>Toast, por este servicio</b>: el resultado de una acción ya lanzada
/// —guardar, eliminar, enviar, asignar—, con éxito o con un fallo esperado. Un
/// <see cref="Result"/> fallido se muestra con <see cref="MostrarError"/>, nunca
/// recomponiendo el texto a mano. Una excepción capturada en un manejador de acción
/// se registra en el log y se avisa con un texto genérico de la acción ("No pudimos
/// eliminar la empresa…"), nunca con <c>ex.Message</c>.</item>
/// <item><b><see cref="LimiteDeErrores"/></b>: lo que nadie esperaba — una excepción
/// no capturada. No se llama: se coloca alrededor de una región de contenido, y
/// alrededor de todo el marcado de cada página InteractiveServer, que además hereda de
/// <see cref="CaeManager.Web.Components.PaginaInteractiva"/> (P1-E1b).</item>
/// </list>
///
/// <para>Los errores de autorización (<c>Autorizacion.*</c>, incluidos los de una
/// Sesión Privilegiada de soporte TALVEG) son errores esperados con interfaz propia
/// —<c>AccesoDenegado</c>, <c>SoloConEscritura</c>, <c>AvisoSoloConsulta</c>,
/// <c>TrazaSoporte</c>— y, cuando además llegan como resultado de una acción, este
/// servicio los muestra con su propio mensaje, sin sustituirlo por uno genérico.</para>
///
/// Servicio scoped (una instancia por circuito de Blazor Server). Los toasts
/// se autodescartan a los 5s salvo los de error, que exigen descarte manual
/// mientras el usuario sigue en la pantalla donde ocurrieron: al pasar a otra
/// pantalla caducan como los demás (<see cref="AlCambiarDePantalla"/>)
/// (ver Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md, "Toasts"). Un toast con acción ("Deshacer", Fase D)
/// vive 8s en vez de 5 — el usuario necesita un instante extra para leer el
/// mensaje y decidir si actuar, no solo para leerlo y descartarlo — y enseña
/// los segundos que le quedan (<see cref="SegundosRestantes"/>).
/// La cuenta atrás se detiene mientras el puntero o el foco de teclado están sobre el toast
/// (<see cref="PunteroSobre"/>, <see cref="FocoEn"/>) y sigue por donde iba al salir.
/// </summary>
public class ToastService
{
    /// <summary>Pública para que AnfitrionToasts pueda sincronizar la barra de progreso con la misma duración.</summary>
    public static readonly TimeSpan DuracionAutoDescarte = TimeSpan.FromSeconds(5);

    /// <summary>Duración cuando el toast tiene una acción (p. ej. "Deshacer") — ver el comentario de la clase.</summary>
    public static readonly TimeSpan DuracionAutoDescarteConAccion = TimeSpan.FromSeconds(8);

    /// <summary>
    /// "Nunca apilar más de 3 visibles simultáneamente" (Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md,
    /// "Toasts", P2 #28 de Project-Hydra-Negocio/MATURITY_REVIEW.md — la regla ya
    /// estaba escrita, esto es lo que la hace cierta).
    /// </summary>
    public const int MaximoVisibles = 3;

    private readonly List<ToastMensaje> _mensajes = [];

    /// <summary>Cuenta atrás de autodescarte de cada toast que la tiene (los de error, solo tras cambiar de pantalla).</summary>
    private readonly Dictionary<Guid, CuentaAtras> _cuentas = [];

    private readonly TimeSpan _duracion;
    private readonly TimeSpan _duracionConAccion;

    public ToastService() : this(DuracionAutoDescarte, DuracionAutoDescarteConAccion)
    {
    }

    /// <summary>Duraciones propias: solo para probar el temporizador sin esperar 5 u 8 segundos.</summary>
    public ToastService(TimeSpan duracion, TimeSpan duracionConAccion)
    {
        _duracion = duracion;
        _duracionConAccion = duracionConAccion;
    }

    public event Action? OnCambio;

    public IReadOnlyList<ToastMensaje> Mensajes => _mensajes;

    public void Mostrar(string mensaje, TonoToast tono = TonoToast.Info, string? textoAccion = null, Func<Task>? onAccion = null)
    {
        // El más antiguo cede el sitio, incluido uno de error: la regla de
        // Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md no hace excepción por tono, y un error silenciado
        // por descarte automático (no ocurre aquí) sería peor que uno
        // desplazado por una acción del propio usuario.
        while (_mensajes.Count >= MaximoVisibles)
            _mensajes.RemoveAt(0);

        var toast = new ToastMensaje(Guid.NewGuid(), mensaje, tono, textoAccion, onAccion);
        _mensajes.Add(toast);

        // Antes de avisar a la interfaz: el primer pintado ya tiene que encontrar la cuenta atrás
        // (SegundosRestantes), o el aviso nacería sin el número y no lo enseñaría hasta el primer tic.
        if (tono != TonoToast.Error)
            Programar(toast.Id, onAccion is not null ? _duracionConAccion : _duracion, cuentaVisible: onAccion is not null);

        OnCambio?.Invoke();
    }

    /// <summary>
    /// El usuario ha pasado a otra pantalla (LV-10 del recorrido del piloto Outbound). Un error exige
    /// descarte manual para que se lea donde ocurrió; fuera de esa pantalla ya no describe lo que el
    /// usuario está haciendo, y antes se quedaba indefinidamente sobre altas correctas hechas después.
    /// No se retira en el acto: una pantalla que avisa de un error y redirige (un detalle que ya no
    /// existe) lo dejaría sin leer. Empieza la misma cuenta atrás que cualquier otro aviso, que el
    /// puntero y el foco siguen deteniendo. Los avisos que ya tenían cuenta no se tocan.
    /// </summary>
    public void AlCambiarDePantalla()
    {
        List<Guid> erroresSinCuenta;
        lock (_cuentas)
            erroresSinCuenta = _mensajes.Where(m => m.Tono == TonoToast.Error && !_cuentas.ContainsKey(m.Id)).Select(m => m.Id).ToList();

        foreach (var id in erroresSinCuenta)
            Programar(id, _duracion, cuentaVisible: false);
    }

    /// <summary>
    /// Segundos que le quedan a un toast con acción antes de desaparecer, redondeados hacia arriba
    /// (decisión del 2026-10-08: quien acaba de eliminar algo ve cuánto tiempo tiene para
    /// «Deshacer»). <c>null</c> si el toast no tiene acción o no tiene cuenta atrás. Con el puntero o
    /// el foco encima la cuenta está detenida y el número no baja.
    /// </summary>
    public int? SegundosRestantes(Guid id)
    {
        lock (_cuentas)
        {
            if (!_cuentas.TryGetValue(id, out var cuenta) || !cuenta.Visible)
                return null;

            var restante = cuenta.Pausada ? cuenta.Restante : cuenta.Restante - (DateTime.UtcNow - cuenta.Inicio);

            // El margen absorbe la resolución del reloj: un tic que llega justo en el cambio de
            // segundo no debe volver a pintar el segundo que acaba de terminar.
            return Math.Max(0, (int)Math.Ceiling(restante.TotalSeconds - 0.05));
        }
    }

    /// <summary>
    /// Avisa del fallo esperado de una acción: el <see cref="Error"/> de un
    /// <see cref="Result"/> de Application, cuyo <see cref="Error.Mensaje"/> ya está
    /// redactado para el usuario. Se muestra tal cual —también los de autorización y
    /// los de Sesión Privilegiada, que nunca se generalizan— y como error, que no se
    /// autodescarta.
    /// </summary>
    public void MostrarError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Mostrar(error.Mensaje, TonoToast.Error);
    }

    /// <summary>
    /// Como <see cref="MostrarError(Error)"/>, cuando el aviso tiene que decir además de
    /// qué era la acción (p. ej. el formulario ya se cerró y el fallo llega a otra
    /// pantalla): pinta «{contexto}: {mensaje}» con el <see cref="Error.Mensaje"/>
    /// literal —tampoco aquí se generalizan los de autorización ni los de Sesión
    /// Privilegiada—, y el <paramref name="complemento"/>, si lo hay, detrás. Un contexto
    /// vacío (un nombre que no llegó) no rompe el aviso: se pinta solo el mensaje.
    /// </summary>
    public void MostrarError(string? contexto, Error error, string? complemento = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var mensaje = string.IsNullOrWhiteSpace(contexto) ? error.Mensaje : $"{contexto}: {error.Mensaje}";
        Mostrar(string.IsNullOrWhiteSpace(complemento) ? mensaje : $"{mensaje} {complemento}", TonoToast.Error);
    }

    public void Descartar(Guid id)
    {
        Cancelar(id);
        if (_mensajes.RemoveAll(m => m.Id == id) > 0)
            OnCambio?.Invoke();
    }

    /// <summary>
    /// El puntero entra o sale del toast. Mientras está encima, la cuenta atrás de
    /// autodescarte se detiene (WCAG 2.2.1: quien lee despacio, o está a punto de pulsar
    /// «Deshacer», no ve desaparecer el aviso bajo el puntero). La barra de tiempo se
    /// detiene por CSS con el mismo criterio; esto detiene el temporizador de verdad.
    /// </summary>
    public void PunteroSobre(Guid id, bool dentro) => Pausar(id, c => c.Puntero = dentro);

    /// <summary>Lo mismo con el foco de teclado dentro del toast (sus botones).</summary>
    public void FocoEn(Guid id, bool dentro) => Pausar(id, c => c.Foco = dentro);

    /// <summary>Ejecuta la acción del toast (p. ej. "Deshacer") y lo descarta de inmediato — un clic no debe competir con el auto-descarte.</summary>
    public async Task EjecutarAccionAsync(Guid id)
    {
        var toast = _mensajes.FirstOrDefault(m => m.Id == id);
        Descartar(id);

        if (toast?.OnAccion is not null)
            await toast.OnAccion();
    }

    private void Programar(Guid id, TimeSpan duracion, bool cuentaVisible)
    {
        var cuenta = new CuentaAtras(duracion, cuentaVisible);
        lock (_cuentas)
        {
            _cuentas[id] = cuenta;
            Arrancar(id, cuenta);
        }
    }

    private void Arrancar(Guid id, CuentaAtras cuenta)
    {
        var cts = new CancellationTokenSource();
        cuenta.Cts = cts;
        cuenta.Inicio = DateTime.UtcNow;
        _ = EsperarAsync(id, cuenta.Restante, cuenta.Visible, cts.Token);
    }

    /// <summary>
    /// Espera lo que queda y descarta. Con la cuenta a la vista, la espera va por tramos que
    /// terminan en cada cambio de segundo y avisa a la interfaz para que repinte el número.
    /// </summary>
    private async Task EsperarAsync(Guid id, TimeSpan restante, bool cuentaVisible, CancellationToken ct)
    {
        try
        {
            while (restante > TimeSpan.Zero)
            {
                var tramo = cuentaVisible ? HastaElCambioDeSegundo(restante) : restante;
                await Task.Delay(tramo, ct);
                restante -= tramo;

                if (cuentaVisible && restante > TimeSpan.Zero)
                    OnCambio?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Descartar(id);
    }

    private static TimeSpan HastaElCambioDeSegundo(TimeSpan restante)
    {
        var fraccion = TimeSpan.FromTicks(restante.Ticks % TimeSpan.TicksPerSecond);
        return fraccion > TimeSpan.Zero ? fraccion : TimeSpan.FromSeconds(1);
    }

    private void Pausar(Guid id, Action<CuentaAtras> cambiar)
    {
        lock (_cuentas)
        {
            if (!_cuentas.TryGetValue(id, out var cuenta))
                return;

            var estabaPausada = cuenta.Pausada;
            cambiar(cuenta);

            if (!estabaPausada && cuenta.Pausada)
            {
                cuenta.Cts?.Cancel();
                cuenta.Restante -= DateTime.UtcNow - cuenta.Inicio;
                if (cuenta.Restante < TimeSpan.Zero)
                    cuenta.Restante = TimeSpan.Zero;
            }
            else if (estabaPausada && !cuenta.Pausada)
            {
                Arrancar(id, cuenta);
            }
        }
    }

    private void Cancelar(Guid id)
    {
        lock (_cuentas)
        {
            if (_cuentas.Remove(id, out var cuenta))
                cuenta.Cts?.Cancel();
        }
    }

    private sealed class CuentaAtras(TimeSpan restante, bool visible)
    {
        public TimeSpan Restante { get; set; } = restante;

        /// <summary>El toast enseña los segundos que le quedan (los que tienen acción).</summary>
        public bool Visible { get; } = visible;
        public DateTime Inicio { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public bool Puntero { get; set; }
        public bool Foco { get; set; }
        public bool Pausada => Puntero || Foco;
    }
}
