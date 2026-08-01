namespace CaeManager.Web.Components.DesignSystem;

public enum TonoToast
{
    Info,
    Exito,
    Advertencia,
    Error
}

public record ToastMensaje(Guid Id, string Mensaje, TonoToast Tono);

/// <summary>
/// Servicio scoped (una instancia por circuito de Blazor Server). Los toasts
/// se autodescartan a los 5s salvo los de error, que exigen descarte manual
/// (ver UX_PATTERNS.md, "Toasts").
/// </summary>
public class ToastService
{
    /// <summary>Pública para que AnfitrionToasts pueda sincronizar la barra de progreso con la misma duración.</summary>
    public static readonly TimeSpan DuracionAutoDescarte = TimeSpan.FromSeconds(5);

    /// <summary>
    /// "Nunca apilar más de 3 visibles simultáneamente" (UX_PATTERNS.md,
    /// "Toasts", P2 #28 de docs/business/MATURITY_REVIEW.md — la regla ya
    /// estaba escrita, esto es lo que la hace cierta).
    /// </summary>
    public const int MaximoVisibles = 3;

    private readonly List<ToastMensaje> _mensajes = [];

    public event Action? OnCambio;

    public IReadOnlyList<ToastMensaje> Mensajes => _mensajes;

    public void Mostrar(string mensaje, TonoToast tono = TonoToast.Info)
    {
        // El más antiguo cede el sitio, incluido uno de error: la regla de
        // UX_PATTERNS.md no hace excepción por tono, y un error silenciado
        // por descarte automático (no ocurre aquí) sería peor que uno
        // desplazado por una acción del propio usuario.
        while (_mensajes.Count >= MaximoVisibles)
            _mensajes.RemoveAt(0);

        var toast = new ToastMensaje(Guid.NewGuid(), mensaje, tono);
        _mensajes.Add(toast);
        OnCambio?.Invoke();

        if (tono != TonoToast.Error)
            _ = AutoDescartarAsync(toast.Id);
    }

    public void Descartar(Guid id)
    {
        if (_mensajes.RemoveAll(m => m.Id == id) > 0)
            OnCambio?.Invoke();
    }

    private async Task AutoDescartarAsync(Guid id)
    {
        await Task.Delay(DuracionAutoDescarte);
        Descartar(id);
    }
}
