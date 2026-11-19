using CaeManager.Application.Comercial.LineaOutbound;

namespace CaeManager.Application.Common;

/// <summary>
/// El criterio único de «¿abre este papel Outbound en el Tenant activo?» (decisión de Chris del 2026-11-18, «Inbound sin
/// Outbound»). Es <see cref="CriterioDePapel.AdmiteLoQueNoEsInbound"/> más una condición: con el papel
/// <see cref="PapelActivo.TenantDeOrigen"/>, un Tenant cuyo Producto Contratado es solo Inbound
/// (<see cref="ITenantSoloInbound"/>: línea Inbound vigente y ninguna línea Outbound) no ve ni opera Outbound.
///
/// <para>
/// Lo usan las puertas de backend de Outbound: los comandos del módulo Outbound
/// (<see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/> con <see cref="PeticionDeOutbound"/>), el alcance de datos
/// de las peticiones de Outbound (<c>AlcanceDatosService</c>) y los endpoints mínimos marcados con <c>.DeOutbound()</c>
/// (<c>PaginaDeOutboundPorPapelAuthorizationHandler</c>). La presentación todavía no: las páginas de Outbound, el menú
/// lateral, el asistente y los paneles de Outbound del hub de Configuración siguen decidiendo solo por papel hasta que se
/// decida qué ve ese Tenant (ocultar, mostrar en gris con explicación o rutas mixtas). De lo que esas pantallas ejecutan,
/// la guarda cierra los comandos de Outbound, y el alcance cierra solo las queries de Outbound que lo consultan
/// (<c>IAlcanceDatosService</c>): una query de Outbound que no lo consulta lee lo que el filtro de Tenant y la RLS le
/// dejan. Las que hay hoy están inventariadas, con su motivo, en <c>Congelados/Queries-de-Outbound-sin-alcance.txt</c> de
/// los tests de arquitectura, para que una nueva no entre sin verse.
/// </para>
///
/// <para>
/// No toca el resto de papeles: <see cref="PapelActivo.Outbound"/> (Operador CAE externo con una Asignación de Operación
/// Outbound), <see cref="PapelActivo.SinPapel"/> (Sesión Privilegiada y vía heredada) y los papeles que ya se niegan
/// (<see cref="PapelActivo.Inbound"/> y <see cref="PapelActivo.OperacionQueYaNoAutoriza"/>) deciden como antes. Tampoco
/// toca Inbound ni el Núcleo: un Tenant solo Inbound sigue administrando sus usuarios y operando Inbound con el papel de
/// Tenant de origen.
/// </para>
///
/// <para>
/// Cuenta el Tenant activo (<see cref="ITenantActual"/>), que con el papel de Tenant de origen es el de origen. Sin Tenant
/// activo no cierra nada: sin Tenant no hay filas que abrir. Un error de lectura del Producto Contratado se propaga (ver
/// <see cref="ITenantSoloInbound"/>).
/// </para>
/// </summary>
public interface ICriterioDeOutbound
{
    /// <summary>
    /// ¿Abre <paramref name="papel"/>, en el Tenant activo, lo de Outbound? <see cref="CriterioDePapel.AdmiteLoQueNoEsInbound"/>
    /// y, además, que no lo cierre <see cref="CierraPorSoloInboundAsync"/>.
    /// </summary>
    Task<bool> AdmiteOutboundAsync(PapelActivo papel, CancellationToken cancellationToken = default);

    /// <summary>
    /// ¿Cierra Outbound el Producto Contratado? Solo con <see cref="PapelActivo.TenantDeOrigen"/> y un Tenant activo solo
    /// Inbound; con cualquier otro papel, no (y no lee la base).
    /// </summary>
    Task<bool> CierraPorSoloInboundAsync(PapelActivo papel, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="ICriterioDeOutbound"/>. El Tenant activo y la lectura del Producto Contratado se resuelven
/// solo cuando el papel es el de Tenant de origen: la guarda de comandos depende de este criterio en cada despacho, y así
/// una composición que no registra la lectura (un contenedor de prueba sin Infrastructure) no deja de resolver la guarda;
/// si llega a necesitarla, falla al resolverla, nunca abre ni cierra en silencio.
///
/// <para>
/// <b>Memoizado como el alcance</b> (BAJA 4 de la revisión de Outbound de la PR #2379). La guarda pregunta en cada comando
/// y en cada petición de Comunicaciones, el alcance en cada petición de Outbound y el handler de los endpoints mínimos en
/// cada uno: antes, cada pregunta era un SELECT de <c>ProductosContratados</c>. La respuesta se guarda por Tenant activo,
/// que es a la vez el Tenant preguntado y el que fija la RLS con la que se lee, con la misma vida que el alcance memoizado:
/// <list type="bullet">
/// <item>caduca a la <c>CaducidadAlcanceOptions</c> configurada, que Infrastructure le pasa (60 s como máximo y por
/// defecto), con reloj monotónico;</item>
/// <item>se descarta antes y después de cada Command (<see cref="IMemoPorContexto"/>), y
/// <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> va antes que la guarda en el pipeline: un comando se
/// autoriza con una lectura hecha dentro de él, y la lectura siguiente ve la línea que contrató o retiró;</item>
/// <item>una lectura que empezó antes de un descarte se devuelve, pero no se guarda (la misma regla de generación que
/// <c>AlcanceDatosService</c>). Un error de lectura tampoco se guarda: se propaga.</item>
/// </list>
/// Lo memoizado es solo la respuesta de <see cref="ITenantSoloInbound"/>; el papel se pregunta en cada llamada.
/// </para>
/// </summary>
public sealed class CriterioDeOutbound(IServiceProvider servicios, TimeSpan? caducidad = null) : ICriterioDeOutbound, IMemoPorContexto
{
    /// <summary>Techo y valor por defecto: el de la caducidad del alcance (<c>CaducidadAlcanceOptions.MaximoSegundos</c>).</summary>
    private static readonly TimeSpan CaducidadMaxima = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _caducidad = caducidad is { } valor
        ? TimeSpan.FromTicks(Math.Clamp(valor.Ticks, TimeSpan.TicksPerSecond, CaducidadMaxima.Ticks))
        : CaducidadMaxima;

    private readonly object _cerrojo = new();
    private readonly Dictionary<Guid, (long Inicio, bool SoloInbound)> _memo = new();
    private long _generacion;
    private TimeProvider? _reloj;

    private TimeProvider Reloj => _reloj ??= servicios.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;

    public async Task<bool> AdmiteOutboundAsync(PapelActivo papel, CancellationToken cancellationToken = default) =>
        CriterioDePapel.AdmiteLoQueNoEsInbound(papel) && !await CierraPorSoloInboundAsync(papel, cancellationToken);

    public async Task<bool> CierraPorSoloInboundAsync(PapelActivo papel, CancellationToken cancellationToken = default)
    {
        if (papel != PapelActivo.TenantDeOrigen)
            return false;

        if (servicios.GetService(typeof(ITenantActual)) is not ITenantActual { TenantId: { } tenantId })
            return false;

        long generacion;
        lock (_cerrojo)
        {
            if (_memo.TryGetValue(tenantId, out var guardada) && Reloj.GetElapsedTime(guardada.Inicio) < _caducidad)
                return guardada.SoloInbound;
            generacion = _generacion;
        }

        var soloInbound = (ITenantSoloInbound?)servicios.GetService(typeof(ITenantSoloInbound))
            ?? throw new InvalidOperationException(
                $"{nameof(ITenantSoloInbound)} no está registrado: sin él no se puede decidir si el Tenant de origen es solo Inbound.");
        var inicio = Reloj.GetTimestamp();
        var respuesta = await soloInbound.EsSoloInboundAsync(tenantId, cancellationToken);

        lock (_cerrojo)
        {
            if (generacion == _generacion)
                _memo[tenantId] = (inicio, respuesta);
        }
        return respuesta;
    }

    /// <summary>Descarta lo memoizado. Lo invoca <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> en cada Command.</summary>
    public void Invalidar()
    {
        lock (_cerrojo)
        {
            _generacion++;
            _memo.Clear();
        }
    }
}
