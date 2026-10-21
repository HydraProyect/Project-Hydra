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
    /// Tenant efectivo en el que se habló. Cambiar de Empresa gestionada recarga la página,
    /// pero el Tenant efectivo también cambia dentro del mismo circuito: la revalidación
    /// periódica invalida la selección cuando se revoca la delegación y el circuito vuelve a
    /// su Tenant de origen. La conversación no viaja con él (ver <see cref="AsegurarTenant"/>).
    /// </summary>
    public Guid? TenantDeLaConversacion { get; private set; }

    private bool _tenantFijado;

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

    /// <summary>
    /// Ata la conversación al Tenant efectivo <paramref name="tenantId"/>. Si ya estaba atada a
    /// otro, vacía el hilo y el historial: lo dicho con los datos de una Empresa no se enseña
    /// en otra ni se reenvía al proveedor de IA.
    /// </summary>
    /// <returns>true si la conversación estaba atada a otro Tenant (el Tenant efectivo ha cambiado).</returns>
    public bool AsegurarTenant(Guid? tenantId)
    {
        if (_tenantFijado && TenantDeLaConversacion == tenantId) return false;

        var cambio = _tenantFijado;
        _tenantFijado = true;
        TenantDeLaConversacion = tenantId;
        Hilo.Clear();
        Historial.Clear();
        return cambio;
    }

    public void Notificar() => Cambio?.Invoke();
}
