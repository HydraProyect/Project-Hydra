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
    private static readonly TimeSpan DuracionAutoDescarte = TimeSpan.FromSeconds(5);

    private readonly List<ToastMensaje> _mensajes = [];

    public event Action? OnCambio;

    public IReadOnlyList<ToastMensaje> Mensajes => _mensajes;

    public void Mostrar(string mensaje, TonoToast tono = TonoToast.Info)
    {
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
