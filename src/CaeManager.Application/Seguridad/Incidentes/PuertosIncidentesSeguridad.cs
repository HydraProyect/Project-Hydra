using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Seguridad.Incidentes;

/// <summary>Desenlace del registro: el incidente (nuevo o el abierto que agrupa la detección).</summary>
public readonly record struct IncidenteRegistrado(Guid IncidenteId, bool EsNuevo);

/// <summary>
/// Puerto de persistencia de los incidentes de seguridad sospechados. La implementación
/// llama a la función <c>app_registrar_incidente_seguridad</c>, única puerta de escritura
/// de la tabla, que deduplica dentro de la ventana bajo un cerrojo por señal y sujeto,
/// solo entre registros del mismo usuario del contexto firmado. Sin usuario, la detección
/// se marca como tal y abre siempre su incidente, para que no absorba una señal real ni
/// revele si existe.
/// Puede lanzar: quien la llama (<see cref="SenalesIncidenteSeguridad"/>) lo absorbe.
/// </summary>
public interface IRegistroIncidentesSeguridad
{
    Task<IncidenteRegistrado> RegistrarAsync(
        SenalIncidenteSeguridad senal, string sujetoClave, IReadOnlyCollection<Guid> tenantsAfectados,
        EvidenciaIncidenteSeguridad evidencia, TimeSpan ventanaDeduplicacion, CancellationToken ct = default);
}

/// <summary>
/// Alerta a la guardia de TALVEG (responsable de seguridad y persona sustituta) de que hay un incidente nuevo. Solo lleva el identificador
/// del incidente y el enlace a su página de Plataforma, nunca datos personales ni
/// nombres de Tenant. <see cref="Encolar"/> no bloquea ni lanza: el envío, con sus
/// reintentos, ocurre en segundo plano y nunca rompe la operación que disparó la señal.
/// </summary>
public interface IAlertaIncidenteSeguridad
{
    void Encolar(Guid incidenteId);
}

/// <summary>
/// Canal saliente de la alerta (adaptador HTTP hacia Better Stack, o el nulo). Un
/// intento por llamada: los reintentos los decide la cola.
/// </summary>
public interface ICanalAlertaIncidenteSeguridad
{
    /// <summary>Falso si el canal está desactivado (sin URL configurada).</summary>
    bool Activo { get; }

    Task EnviarAsync(Guid incidenteId, CancellationToken ct = default);
}

/// <summary>
/// Puntos de enganche de las señales. Ninguno lanza ni espera a la alerta: un fallo al
/// registrar el incidente queda en el log como error y la operación que disparó la
/// señal sigue (detectar no puede romper exportar, iniciar sesión ni cambiar de Tenant).
/// </summary>
public interface ISenalesIncidenteSeguridad
{
    /// <summary>
    /// La persona titular de la cuenta confirmó «No he sido yo». Alta certeza por sí
    /// sola: sin umbral, crea (o agrupa) el incidente en el acto.
    /// </summary>
    Task InicioSesionNoReconocidoAsync(Guid usuarioId, Guid tenantPropietarioId, bool teniaContrasena, CancellationToken ct = default);

    /// <summary>Exportación de <paramref name="filas"/> filas por el actor real, sobre el Tenant activo.</summary>
    Task ExportacionAsync(Guid actorRealUsuarioId, Guid? tenantId, int filas, CancellationToken ct = default);

    /// <summary>Apertura o descarga de un documento sensible por el actor real, sobre el Tenant activo.</summary>
    Task DocumentoSensibleAbiertoAsync(Guid actorRealUsuarioId, Guid? tenantId, CancellationToken ct = default);

    /// <summary>La autorización rechazó abrir <paramref name="tenantObjetivoId"/> a este usuario.</summary>
    Task AccesoAOtroTenantRechazadoAsync(Guid usuarioId, Guid tenantOrigenId, Guid tenantObjetivoId, CancellationToken ct = default);
}
