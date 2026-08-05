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
/// Servicio scoped (una instancia por circuito de Blazor Server). Los toasts
/// se autodescartan a los 5s salvo los de error, que exigen descarte manual
/// (ver UX_PATTERNS.md, "Toasts"). Un toast con acción ("Deshacer", Fase D)
/// vive 8s en vez de 5 — el usuario necesita un instante extra para leer el
/// mensaje y decidir si actuar, no solo para leerlo y descartarlo.
/// </summary>
public class ToastService
{
    /// <summary>Pública para que AnfitrionToasts pueda sincronizar la barra de progreso con la misma duración.</summary>
    public static readonly TimeSpan DuracionAutoDescarte = TimeSpan.FromSeconds(5);

    /// <summary>Duración cuando el toast tiene una acción (p. ej. "Deshacer") — ver el comentario de la clase.</summary>
    public static readonly TimeSpan DuracionAutoDescarteConAccion = TimeSpan.FromSeconds(8);

    /// <summary>
    /// "Nunca apilar más de 3 visibles simultáneamente" (UX_PATTERNS.md,
    /// "Toasts", P2 #28 de docs/business/MATURITY_REVIEW.md — la regla ya
    /// estaba escrita, esto es lo que la hace cierta).
    /// </summary>
    public const int MaximoVisibles = 3;

    private readonly List<ToastMensaje> _mensajes = [];

    public event Action? OnCambio;

    public IReadOnlyList<ToastMensaje> Mensajes => _mensajes;

    public void Mostrar(string mensaje, TonoToast tono = TonoToast.Info, string? textoAccion = null, Func<Task>? onAccion = null)
    {
        // El más antiguo cede el sitio, incluido uno de error: la regla de
        // UX_PATTERNS.md no hace excepción por tono, y un error silenciado
        // por descarte automático (no ocurre aquí) sería peor que uno
        // desplazado por una acción del propio usuario.
        while (_mensajes.Count >= MaximoVisibles)
            _mensajes.RemoveAt(0);

        var toast = new ToastMensaje(Guid.NewGuid(), mensaje, tono, textoAccion, onAccion);
        _mensajes.Add(toast);
        OnCambio?.Invoke();

        if (tono != TonoToast.Error)
            _ = AutoDescartarAsync(toast.Id, onAccion is not null ? DuracionAutoDescarteConAccion : DuracionAutoDescarte);
    }

    public void Descartar(Guid id)
    {
        if (_mensajes.RemoveAll(m => m.Id == id) > 0)
            OnCambio?.Invoke();
    }

    /// <summary>Ejecuta la acción del toast (p. ej. "Deshacer") y lo descarta de inmediato — un clic no debe competir con el auto-descarte.</summary>
    public async Task EjecutarAccionAsync(Guid id)
    {
        var toast = _mensajes.FirstOrDefault(m => m.Id == id);
        Descartar(id);

        if (toast?.OnAccion is not null)
            await toast.OnAccion();
    }

    private async Task AutoDescartarAsync(Guid id, TimeSpan duracion)
    {
        await Task.Delay(duracion);
        Descartar(id);
    }
}
