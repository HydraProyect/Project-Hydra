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
/// decida qué ve ese Tenant (ocultar, mostrar en gris con explicación o rutas mixtas). Lo que esas pantallas ejecutan lo
/// cierran igualmente la guarda y el alcance.
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
/// </summary>
public sealed class CriterioDeOutbound(IServiceProvider servicios) : ICriterioDeOutbound
{
    public async Task<bool> AdmiteOutboundAsync(PapelActivo papel, CancellationToken cancellationToken = default) =>
        CriterioDePapel.AdmiteLoQueNoEsInbound(papel) && !await CierraPorSoloInboundAsync(papel, cancellationToken);

    public async Task<bool> CierraPorSoloInboundAsync(PapelActivo papel, CancellationToken cancellationToken = default)
    {
        if (papel != PapelActivo.TenantDeOrigen)
            return false;

        if (servicios.GetService(typeof(ITenantActual)) is not ITenantActual { TenantId: { } tenantId })
            return false;

        var soloInbound = (ITenantSoloInbound?)servicios.GetService(typeof(ITenantSoloInbound))
            ?? throw new InvalidOperationException(
                $"{nameof(ITenantSoloInbound)} no está registrado: sin él no se puede decidir si el Tenant de origen es solo Inbound.");
        return await soloInbound.EsSoloInboundAsync(tenantId, cancellationToken);
    }
}
