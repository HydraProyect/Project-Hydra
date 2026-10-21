using CaeManager.Application.Common;

namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>
/// Estado de Vega compartido por las raíces interactivas del mismo circuito: el botón
/// flotante, el panel del layout y la pantalla completa (<c>/asistente</c>). Mismo patrón
/// que BusquedaGlobalService, con dos cosas más:
/// <list type="bullet">
/// <item><see cref="Abierto"/>: abierto o plegado. Lo recuerda la pestaña (vega.js,
/// sessionStorage); aquí solo se refleja para que botón y panel pinten lo mismo.</item>
/// <item>La conversación (<see cref="Hilo"/> y <see cref="Historial"/>): vive en el circuito,
/// así que sobrevive a navegar entre pantallas y se pierde al recargar. Guardarla en
/// servidor por usuario y Tenant (ConversacionAsistente y MensajeAsistente) es un
/// incremento propio: hoy no existe esa entidad.</item>
/// </list>
/// Es un servicio con ámbito de circuito: nunca se comparte entre usuarios ni pestañas.
/// </summary>
public class AsistenteIaService
{
    public event Action? Cambio;

    public bool Abierto { get; private set; }

    /// <summary>Lo que ve la persona: mensajes, avisos y planes, en orden.</summary>
    public List<EntradaHilo> Hilo { get; } = [];

    /// <summary>Lo que se le manda al modelo de consultas (solo Consulta PRL): sin planes ni avisos.</summary>
    public List<MensajeChatDto> Historial { get; } = [];

    /// <summary>
    /// Tenant en el que se habló. Si el circuito pasa a otro Tenant (cambiar de Empresa
    /// gestionada recarga la página, pero por si acaso), la conversación no viaja con él.
    /// </summary>
    public Guid? TenantDeLaConversacion { get; private set; }

    public void Abrir()
    {
        if (Abierto) return;
        Abierto = true;
        Cambio?.Invoke();
    }

    public void Plegar()
    {
        if (!Abierto) return;
        Abierto = false;
        Cambio?.Invoke();
    }

    public void Alternar()
    {
        if (Abierto) Plegar();
        else Abrir();
    }

    public void NuevaConversacion()
    {
        Hilo.Clear();
        Historial.Clear();
        Cambio?.Invoke();
    }

    /// <summary>Vacía el hilo si se habló en otro Tenant: lo dicho con los datos de una Empresa no se enseña en otra.</summary>
    public void AsegurarTenant(Guid? tenantId)
    {
        if (TenantDeLaConversacion == tenantId) return;
        TenantDeLaConversacion = tenantId;
        Hilo.Clear();
        Historial.Clear();
    }

    public void Notificar() => Cambio?.Invoke();
}
