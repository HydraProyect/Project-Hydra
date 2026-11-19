using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Inbound.Autorizacion;

/// <summary>
/// Las capacidades con nombre de Inbound (B-03; plan de backend de Inbound § 2). Dicen qué puede hacer la persona en el
/// Tenant activo con su papel y su rol; el alcance de datos (qué Cliente empresarial, Centro o contrato) lo sigue
/// decidiendo <see cref="IAlcanceDatosService"/>, con la cartera del servicio del papel activo.
/// </summary>
public enum CapacidadInbound
{
    /// <summary>Leer lo de Inbound del Tenant TCE (contratos de acceso, plantillas, evidencias, lectura entre Tenants).</summary>
    LeerInbound,

    /// <summary>G: dar de alta y cambiar contratos de acceso y plantillas de requisitos del Tenant TCE.</summary>
    GestionarInbound,

    /// <summary>G: validar y rechazar la documentación que llega de la Empresa contratista.</summary>
    ValidarInbound,

    /// <summary>A-TCE: actos de gobierno del Tenant TCE (pantalla 20). Solo su Administrador, en su propio Tenant.</summary>
    GobernarInbound,

    /// <summary>
    /// U-TEC: actos del lado del Tenant TEC sobre sus propios documentos (Quién lee, autorizaciones de lectura). Un único
    /// Usuario, sin reparto de permisos (R-15).
    /// </summary>
    ParticipanteTec,

    /// <summary>
    /// Portería del Tenant TCE (AF-01 del plan de control de acceso físico): comprobar credenciales y registrar entradas y
    /// salidas. Del lado del Tenant TCE; la tienen el rol «Control de acceso» y los roles con escritura, y NO implica
    /// <see cref="LeerInbound"/>: el rol de portería no ve contratos, documentos ni validación. Los Centros en los que opera
    /// los acota <c>UsuarioControlAccesoCentro</c>, no esta capacidad.
    /// </summary>
    ControlarAcceso,
}

/// <summary>
/// La autorización de Inbound (B-03): una sola puerta por la que pasan todos los handlers de
/// <c>CaeManager.Application.Inbound</c>, salvo los de Plataforma (lo exige <c>LlamadaAIAutorizacionInboundTests</c>).
/// Falla cerrado: sin papel, con papel Outbound en el lado del Tenant TCE, con una operación que ya no autoriza o con un
/// rol que no tiene la capacidad, responde que no. No sustituye a la RLS ni al filtro de Tenant, ni al alcance de
/// cartera de cada recurso: <c>tenant isolation ≠ operational delegation ≠ platform privilege</c>.
/// </summary>
public interface IAutorizacionInbound
{
    Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken);
}

/// <summary>Errores de <see cref="IAutorizacionInbound"/>. Uno solo: no dice qué capacidad faltaba.</summary>
public static class AutorizacionInboundErrores
{
    public static readonly Error SinCapacidad = Error.Crear(
        "AutorizacionInbound.SinCapacidad", "No tienes permiso para esta acción de Valida en esta organización.");
}

/// <summary>
/// La resolución de las capacidades (B-03), desde el papel activo (<see cref="ICurrentUserService.ObtenerPapelActivoAsync"/>,
/// B-02b) y el rol efectivo, con las decisiones de Chris del 2026-11-16 (DECISION_LOG, entrada de la cuarta ronda de la
/// #2293 y tres reglas de B-03):
/// <list type="bullet">
/// <item><b>Lado del Tenant TCE</b> (Leer, Gestionar, Validar): en su propio Tenant (papel de origen) o con papel Inbound.
/// El Administrador del Tenant TCE conserva Gestionar y Validar en su propio Tenant, además de gobernar. Los perfiles de
/// organización de un Operador CAE externo con papel Inbound entran sin Asignación de Cartera, como en Outbound (la
/// vía de Operación Inbound ya exige para ellos una operación del Tenant entero); el Gestor CAE, con su cartera Inbound,
/// que acota <see cref="IAlcanceDatosService"/>. Consulta solo lee. <see cref="CapacidadInbound.ControlarAcceso"/> la tienen además el rol de Control de
/// acceso (portería) y los roles con escritura, y solo ella: el rol de Control de acceso no lee ni valida.</item>
/// <item><b>Gobierno</b>: solo el Administrador, en su propio Tenant.</item>
/// <item><b>Lado del Tenant TEC</b> (ParticipanteTec): el rol efectivo Administrador, en su propio Tenant o con papel
/// Outbound (el Operador CAE externo que lo opera, DN-2). Nunca con papel Inbound: un Tenant TCE no actúa como Tenant
/// TEC de sí mismo. El marcador de Tenant TEC participante (EP-4) y sus límites no están aquí: los fija su propio
/// incremento.</item>
/// </list>
/// Una Sesión Privilegiada no tiene papel ni rol de negocio, así que no tiene ninguna capacidad.
/// </summary>
public sealed class AutorizacionInbound(ICurrentUserService currentUserService) : IAutorizacionInbound
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismo motivo que en AutorizacionEscrituraBehavior.
    private const string Administrador = "Administrador";
    private const string Consulta = "Consulta";
    private const string ControlAcceso = "ControlAcceso";

    public async Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken)
    {
        var papel = await currentUserService.ObtenerPapelActivoAsync();
        if (papel is PapelActivo.SinPapel or PapelActivo.OperacionQueYaNoAutoriza)
            return false;

        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol is null)
            return false;

        var ladoTce = papel is PapelActivo.TenantDeOrigen or PapelActivo.Inbound;
        var escribe = AutorizacionEscrituraBehavior<object, object>.RolesConEscritura.Contains(rol);

        return capacidad switch
        {
            CapacidadInbound.LeerInbound => ladoTce && (escribe || rol == Consulta),
            CapacidadInbound.GestionarInbound or CapacidadInbound.ValidarInbound => ladoTce && escribe,
            CapacidadInbound.ControlarAcceso => ladoTce && (escribe || rol == ControlAcceso),
            CapacidadInbound.GobernarInbound => papel == PapelActivo.TenantDeOrigen && rol == Administrador,
            CapacidadInbound.ParticipanteTec =>
                papel is PapelActivo.TenantDeOrigen or PapelActivo.Outbound && rol == Administrador,
            _ => false,
        };
    }
}
