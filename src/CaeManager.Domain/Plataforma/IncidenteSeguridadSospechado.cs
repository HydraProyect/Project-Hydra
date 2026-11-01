using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Señal de alta certeza que abre un <see cref="IncidenteSeguridadSospechado"/>.
/// Se guarda como texto: la función de registro y la restricción CHECK de la
/// tabla nombran los mismos valores.
/// </summary>
public enum SenalIncidenteSeguridad
{
    /// <summary>La persona titular de una cuenta confirma «No he sido yo» sobre un inicio de sesión inusual.</summary>
    InicioSesionNoReconocido,

    /// <summary>Un mismo usuario exporta filas o abre documentos sensibles por encima del umbral en la ventana.</summary>
    ExtraccionMasiva,

    /// <summary>Un mismo usuario pide abrir un Tenant que no le está autorizado, repetidamente, y la autorización lo rechaza.</summary>
    AccesoRepetidoAOtroTenant,
}

/// <summary>
/// Estado del incidente. Nace <see cref="Sospechado"/>; las transiciones las hace el Actor de
/// Plataforma TALVEG desde la página del incidente, por la función <c>app_decidir_incidente_seguridad</c>:
/// <see cref="EnEvaluacion"/> («Necesito más tiempo»), <see cref="Confirmado"/> («Confirmar y enviar el
/// primer aviso») o <see cref="Descartado"/> (falsa alarma, con motivo). Confirmado y Descartado son finales.
/// </summary>
public enum EstadoIncidenteSeguridad
{
    Sospechado,
    EnEvaluacion,
    Confirmado,
    Descartado,
}

/// <summary>
/// Incidente de seguridad sospechado, del plano de Plataforma (ADR-011, plano 3).
///
/// <para>
/// <b>No pertenece a un Tenant.</b> Una violación puede afectar a varios, así que los
/// Tenants afectados son un dato (<see cref="TenantsAfectados"/>), no la coordenada de
/// aislamiento: la tabla no lleva <c>TenantId</c> ni la política <c>aislamiento_tenant</c>.
/// La lee solo un Actor de Plataforma TALVEG con concesión AdminPlataforma global vigente
/// (política <c>actor_de_plataforma_lee</c>) y no la escribe ningún rol de la aplicación:
/// la única puerta de escritura es la función <c>app_registrar_incidente_seguridad</c>,
/// que deduplica y deja la primera transición.
/// </para>
///
/// <para>
/// <b>Sin datos personales en claro.</b> <see cref="Evidencia"/> solo lleva identificadores,
/// recuentos, marcas de tiempo y códigos (lo garantiza el constructor de evidencia de
/// Application); <see cref="SujetoClave"/> es un identificador seudónimo, nunca un nombre ni
/// un correo.
/// </para>
///
/// <para>
/// <b>Plazo.</b> El primer aviso al Cliente comercial TALVEG y la notificación completa se
/// cuentan desde <see cref="ConfirmadoEnUtc"/>, no desde <see cref="DetectadoEnUtc"/>.
/// </para>
/// </summary>
public class IncidenteSeguridadSospechado : Entity
{
    public const int LongitudMaximaSujeto = 80;

    /// <summary>Tope de Tenants afectados por incidente; la función de registro rechaza más.</summary>
    public const int MaximoTenantsAfectados = 50;

    /// <summary>Tope, en bytes, de la evidencia serializada; la función de registro rechaza más.</summary>
    public const int MaximoBytesEvidencia = 8 * 1024;

    public SenalIncidenteSeguridad Senal { get; private set; }

    /// <summary>Sobre quién recae la señal, seudónimo (p. ej. <c>usuario:{guid}</c>). La deduplicación agrupa por señal y sujeto, dentro de la misma procedencia del registro.</summary>
    public string SujetoClave { get; private set; } = string.Empty;

    public DateTime DetectadoEnUtc { get; private set; }

    /// <summary>Última vez que la señal se agrupó en este incidente dentro de la ventana de deduplicación.</summary>
    public DateTime UltimaDeteccionEnUtc { get; private set; }

    /// <summary>Cuántas veces se ha detectado la señal agrupada en este incidente (1 al nacer).</summary>
    public int Detecciones { get; private set; }

    public List<Guid> TenantsAfectados { get; private set; } = [];

    /// <summary>JSON con identificadores, recuentos y códigos; nunca nombres ni correos.</summary>
    public string Evidencia { get; private set; } = "{}";

    public EstadoIncidenteSeguridad Estado { get; private set; }

    /// <summary>Hora en que el Actor de Plataforma TALVEG de guardia confirma la violación: desde aquí cuentan las 24 y las 36 horas.</summary>
    public DateTime? ConfirmadoEnUtc { get; private set; }

    public List<TransicionIncidenteSeguridad> Transiciones { get; private set; } = [];

    /// <summary>Solo para EF: las filas las crea la función de registro.</summary>
    protected IncidenteSeguridadSospechado()
    {
    }
}

/// <summary>
/// Registro de cada cambio de estado de un incidente (art. 33.5 RGPD: documentar toda
/// violación, sus efectos y las medidas). Solo se añade, nunca se reescribe.
/// </summary>
public class TransicionIncidenteSeguridad : Entity
{
    public const int LongitudMaximaMotivo = 500;

    public Guid IncidenteId { get; private set; }

    /// <summary>Nulo en la primera transición, la del nacimiento.</summary>
    public EstadoIncidenteSeguridad? EstadoAnterior { get; private set; }

    public EstadoIncidenteSeguridad EstadoNuevo { get; private set; }

    public DateTime OcurridoEnUtc { get; private set; }

    /// <summary>Actor de Plataforma TALVEG que la hizo; nulo cuando la hizo el sistema (detección).</summary>
    public Guid? ActorUsuarioId { get; private set; }

    public string? Motivo { get; private set; }

    /// <summary>Solo para EF: las filas las crea la función de registro.</summary>
    protected TransicionIncidenteSeguridad()
    {
    }
}
