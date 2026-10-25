using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Eventos;

namespace CaeManager.Web.Services;

/// <summary>
/// La suscripción de un componente a los mensajes en tiempo real (<see cref="INotificadorMensajesTiempoReal"/>) del Tenant
/// que da <see cref="ITenantActual"/>, y que lo sigue cuando la selección de Tenant beneficiario se retira en caliente
/// (<see cref="IAvisoSeleccionRetirada"/>): se da de baja del Tenant anterior en ese momento y se suscribe al que haya
/// ahora, en vez de seguir recibiendo los avisos del Tenant retirado hasta que se cierre el circuito.
///
/// <para>
/// El notificador ya entrega cada aviso solo a las suscripciones de su Tenant; lo que añade esto es la baja a tiempo y el
/// descarte de un aviso del Tenant anterior que ya estuviera publicándose (el notificador recorre una instantánea de las
/// suscripciones, así que puede llamar a una recién soltada). No autoriza nada: lo que el componente relea al recibir el
/// aviso pasa por el filtro global de EF y la RLS del Tenant actual, como cualquier lectura.
/// </para>
/// </summary>
public sealed class SuscripcionMensajesDelTenantActual : IDisposable
{
    private readonly INotificadorMensajesTiempoReal _notificador;
    private readonly ITenantActual _tenantActual;
    private readonly IAvisoSeleccionRetirada? _avisoRetirada;
    private readonly Func<MensajeWhatsAppRecibidoEvent, Task> _alRecibir;
    private readonly object _cerrojo = new();
    private IDisposable? _suscripcion;
    private Guid? _tenantSuscrito;
    private bool _liberada;

    private SuscripcionMensajesDelTenantActual(
        INotificadorMensajesTiempoReal notificador, ITenantActual tenantActual, Func<MensajeWhatsAppRecibidoEvent, Task> alRecibir)
    {
        _notificador = notificador;
        _tenantActual = tenantActual;
        _alRecibir = alRecibir;
        _avisoRetirada = tenantActual as IAvisoSeleccionRetirada;
    }

    /// <summary>
    /// Suscribe <paramref name="alRecibir"/> a los mensajes del Tenant actual (ninguno si no hay Tenant) y se engancha a la
    /// retirada de la selección si <paramref name="tenantActual"/> la avisa (<see cref="IAvisoSeleccionRetirada"/>; el
    /// <see cref="TenantActual"/> de la aplicación lo hace). <paramref name="alRecibir"/> llega en el hilo de
    /// quien publica: el componente vuelve a su despachador antes de tocar su estado.
    /// </summary>
    public static SuscripcionMensajesDelTenantActual Abrir(
        INotificadorMensajesTiempoReal notificador, ITenantActual tenantActual, Func<MensajeWhatsAppRecibidoEvent, Task> alRecibir)
    {
        ArgumentNullException.ThrowIfNull(notificador);
        ArgumentNullException.ThrowIfNull(tenantActual);
        ArgumentNullException.ThrowIfNull(alRecibir);

        var suscripcion = new SuscripcionMensajesDelTenantActual(notificador, tenantActual, alRecibir);
        // Primero el oyente y después la suscripción: una retirada entre las dos dejaría, al revés, la suscripción al
        // Tenant retirado sin nadie que la soltara. Así, si llega antes, la suscripción ya lee el Tenant nuevo; si llega
        // después, la cambia.
        if (suscripcion._avisoRetirada is not null)
            suscripcion._avisoRetirada.TenantSeleccionadoRetirado += suscripcion.AlRetirarseLaSeleccion;
        suscripcion.SuscribirAlTenantActual();
        return suscripcion;
    }

    /// <summary>El Tenant cuyos mensajes recibe ahora, o <c>null</c> si ninguno.</summary>
    public Guid? TenantSuscrito
    {
        get
        {
            lock (_cerrojo) return _tenantSuscrito;
        }
    }

    public void Dispose()
    {
        if (_avisoRetirada is not null)
            _avisoRetirada.TenantSeleccionadoRetirado -= AlRetirarseLaSeleccion;

        IDisposable? suelta;
        lock (_cerrojo)
        {
            if (_liberada)
                return;
            _liberada = true;
            suelta = _suscripcion;
            _suscripcion = null;
            _tenantSuscrito = null;
        }

        suelta?.Dispose();
    }

    private void AlRetirarseLaSeleccion(Guid retirado) => SuscribirAlTenantActual();

    /// <summary>
    /// Suelta la suscripción anterior y se suscribe al Tenant que da ahora <see cref="ITenantActual"/>. Al abrir y al
    /// retirarse la selección (en el hilo de quien la retira, por eso con el cerrojo). Si el Tenant no cambió, no hace nada:
    /// nunca deja dos suscripciones a la vez. El Tenant se lee <b>dentro</b> del cerrojo: leído fuera, una apertura que leyó
    /// el Tenant retirado justo antes de que la retirada se completara podría entrar después y volver a suscribirlo.
    /// </summary>
    private void SuscribirAlTenantActual()
    {
        IDisposable? suelta = null;
        lock (_cerrojo)
        {
            Guid? tenantNuevo;
            try
            {
                tenantNuevo = _tenantActual.TenantId;
            }
            catch
            {
                // Sin Tenant que se pueda resolver, sin suscripción: mejor no recibir avisos que recibirlos del anterior.
                tenantNuevo = null;
            }

            if (_liberada || (_suscripcion is not null && _tenantSuscrito == tenantNuevo))
                return;

            suelta = _suscripcion;
            _suscripcion = null;
            _tenantSuscrito = tenantNuevo;
            if (tenantNuevo is { } tenantId)
                _suscripcion = _notificador.Suscribir(tenantId, Entregar);
        }

        suelta?.Dispose();
    }

    /// <summary>Solo pasa al componente lo del Tenant suscrito ahora: un aviso del Tenant anterior en vuelo se descarta.</summary>
    private Task Entregar(MensajeWhatsAppRecibidoEvent aviso)
    {
        lock (_cerrojo)
        {
            if (_liberada || _tenantSuscrito != aviso.TenantId)
                return Task.CompletedTask;
        }

        return _alRecibir(aviso);
    }
}
