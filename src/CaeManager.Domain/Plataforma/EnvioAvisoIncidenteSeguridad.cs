using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Qué se envía al Tenant beneficiario afectado por un <see cref="IncidenteSeguridadSospechado"/>
/// confirmado. Se guarda como texto: las funciones de envío y la restricción CHECK nombran los mismos valores.
/// </summary>
public enum PlantillaAvisoIncidenteSeguridad
{
    /// <summary>Primer aviso, en las 24 horas siguientes a la confirmación: lo que se sabe (art. 33.4 RGPD, por fases).</summary>
    PrimerAviso,

    /// <summary>Notificación completa, en las 36 horas siguientes a la confirmación, con los cinco apartados del art. 33.3 RGPD.</summary>
    NotificacionCompleta,
}

/// <summary>Estado de un envío. Nace <see cref="Enviando"/> en el mismo acto en que alguien lo reclama.</summary>
public enum EstadoEnvioAvisoIncidenteSeguridad
{
    /// <summary>Reclamado por un Actor de Plataforma TALVEG y en curso: nadie más lo envía mientras tanto.</summary>
    Enviando,

    Enviado,

    /// <summary>El servicio de correo no lo aceptó; un reintento lo vuelve a reclamar con el mismo asunto y cuerpo.</summary>
    Fallido,
}

/// <summary>
/// Registro de cada envío de un aviso de violación de seguridad a un destinatario de un Tenant afectado
/// (art. 33.5 RGPD: documentar la violación y las medidas, entre ellas a quién se avisó y cuándo).
///
/// <para>
/// <b>Plano de Plataforma.</b> Como su incidente, no pertenece a un Tenant: <see cref="TenantAfectadoId"/> es
/// un dato, no la coordenada de aislamiento. La lee solo un Actor de Plataforma TALVEG con concesión
/// AdminPlataforma global vigente y no la escribe ningún rol de la aplicación: las filas las crean y cambian
/// <c>app_reclamar_envio_aviso_seguridad</c> y <c>app_resolver_envio_aviso_seguridad</c>.
/// </para>
///
/// <para>
/// <b>Idempotente.</b> Hay una fila por incidente, plantilla, Tenant afectado y correo (índice único): un
/// reintento reclama la misma fila, se salta la ya enviada y reenvía la fallida con el asunto y el cuerpo que
/// se guardaron la primera vez, no con un borrador nuevo.
/// </para>
/// </summary>
public class EnvioAvisoIncidenteSeguridad : Entity
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaCorreo = 256;
    public const int LongitudMaximaAsunto = 300;
    public const int LongitudMaximaCuerpo = 20_000;
    public const int LongitudMaximaError = 500;

    /// <summary>
    /// Tras este tiempo, un envío que sigue <see cref="EstadoEnvioAvisoIncidenteSeguridad.Enviando"/> se da por
    /// interrumpido (el proceso cayó a mitad) y se puede volver a reclamar. Lo repite la función de reclamar.
    /// </summary>
    public static readonly TimeSpan EsperaEnvioInterrumpido = TimeSpan.FromMinutes(10);

    public Guid IncidenteId { get; private set; }

    public PlantillaAvisoIncidenteSeguridad Plantilla { get; private set; }

    /// <summary>Tenant beneficiario afectado al que pertenece el destinatario.</summary>
    public Guid TenantAfectadoId { get; private set; }

    public string DestinatarioNombre { get; private set; } = string.Empty;

    /// <summary>En minúsculas y sin espacios: forma parte de la clave de idempotencia.</summary>
    public string DestinatarioCorreo { get; private set; } = string.Empty;

    /// <summary>
    /// El Tenant no tenía contacto de seguridad validado y el destinatario es uno de sus Administradores
    /// (decisión del propietario del 2026-10-26).
    /// </summary>
    public bool SinContactoValidado { get; private set; }

    public string Asunto { get; private set; } = string.Empty;

    /// <summary>Contenido interior del correo tal como se envió (sin el envoltorio de marca).</summary>
    public string Cuerpo { get; private set; } = string.Empty;

    /// <summary>Actor de Plataforma TALVEG que confirmó el envío con su clic.</summary>
    public Guid ConfirmadoPorUsuarioId { get; private set; }

    public DateTime ConfirmadoEnUtc { get; private set; }

    public EstadoEnvioAvisoIncidenteSeguridad Estado { get; private set; }

    public int Intentos { get; private set; }

    public DateTime UltimoIntentoEnUtc { get; private set; }

    public Guid UltimoIntentoPorUsuarioId { get; private set; }

    public DateTime? EnviadoEnUtc { get; private set; }

    public string? UltimoError { get; private set; }

    /// <summary>Solo para EF: las filas las crea la función de reclamar.</summary>
    protected EnvioAvisoIncidenteSeguridad()
    {
    }
}
