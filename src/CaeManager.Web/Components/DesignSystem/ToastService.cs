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
/// no capturada. No se llama: se coloca alrededor de una región de contenido.</item>
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
/// (ver Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md, "Toasts"). Un toast con acción ("Deshacer", Fase D)
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
    /// "Nunca apilar más de 3 visibles simultáneamente" (Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md,
    /// "Toasts", P2 #28 de Project-Hydra-Negocio/MATURITY_REVIEW.md — la regla ya
    /// estaba escrita, esto es lo que la hace cierta).
    /// </summary>
    public const int MaximoVisibles = 3;

    private readonly List<ToastMensaje> _mensajes = [];

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
        OnCambio?.Invoke();

        if (tono != TonoToast.Error)
            _ = AutoDescartarAsync(toast.Id, onAccion is not null ? DuracionAutoDescarteConAccion : DuracionAutoDescarte);
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
