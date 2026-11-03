using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso;

/// <summary>
/// Lo que el Actor de Plataforma TALVEG hace sobre un incidente de seguridad sospechado y sus avisos al Tenant
/// beneficiario afectado (incremento 3b del aviso de violaciones de seguridad). Cada operación cruza Tenants y
/// lo que la permite está en la base, no aquí:
/// <list type="bullet">
/// <item>Lectura: la política <c>actor_de_plataforma_lee</c> de las tres tablas del incidente (concesión
/// AdminPlataforma global vigente sobre la identidad del contexto RLS firmado).</item>
/// <item>Decidir (confirmar, pedir más tiempo, descartar): la función <c>app_decidir_incidente_seguridad</c>.</item>
/// <item>Enviar: <c>app_reclamar_envio_aviso_seguridad</c> (crea o reclama la fila del envío, solo para un
/// incidente Confirmado, un Tenant afectado y un destinatario que la base resuelve como suyo) y
/// <c>app_resolver_envio_aviso_seguridad</c> (lo marca enviado o fallido y deja la auditoría en el Tenant).</item>
/// </list>
/// Las tres funciones repiten la concesión y que el actor trabaje desde su propio Tenant de plataforma, como
/// <c>app_resolver_contacto_seguridad</c>; los handlers lo comprueban antes para fallar pronto y con mensaje.
/// </summary>
public interface IIncidentesSeguridadPlataforma
{
    /// <summary>El incidente con sus transiciones y envíos, o null si no existe o la RLS no lo deja ver.</summary>
    Task<IncidenteSeguridadDto?> ObtenerAsync(Guid incidenteId, CancellationToken cancellationToken);

    /// <summary>Nombre de cada Tenant afectado, para la página y la plantilla.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NombresTenantsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);

    /// <summary>
    /// Cambia el estado del incidente. Confirmar un incidente ya Confirmado, o pedir más tiempo para uno ya
    /// en evaluación, no es un error: es el reintento del mismo clic.
    /// </summary>
    Task<Result> DecidirAsync(Guid incidenteId, DecisionIncidenteSeguridad decision, string? motivo, CancellationToken cancellationToken);

    /// <summary>
    /// Crea la fila del envío, o reclama la que ya existe si falló o quedó interrumpida. Solo quien la reclama
    /// envía: <see cref="EnvioAvisoReclamado.Reclamado"/> falso significa que ya salió o que otro clic la está
    /// enviando.
    /// </summary>
    Task<Result<EnvioAvisoReclamado>> ReclamarEnvioAsync(EnvioAvisoSolicitado envio, CancellationToken cancellationToken);

    /// <summary>Marca un envío reclamado como enviado o como fallido.</summary>
    Task<Result> ResolverEnvioAsync(Guid envioId, bool enviado, string? error, CancellationToken cancellationToken);
}

public enum DecisionIncidenteSeguridad
{
    /// <summary>«Necesito más tiempo para evaluarlo»: Sospechado pasa a EnEvaluacion; el plazo no empieza.</summary>
    Evaluar,

    /// <summary>Fija la hora de confirmación, desde la que cuentan las 24 y las 36 horas.</summary>
    Confirmar,

    /// <summary>Falsa alarma, con motivo: no se envía nada.</summary>
    Descartar,
}

public record IncidenteSeguridadDto(
    Guid Id,
    SenalIncidenteSeguridad Senal,
    EstadoIncidenteSeguridad Estado,
    DateTime DetectadoEnUtc,
    DateTime UltimaDeteccionEnUtc,
    int Detecciones,
    IReadOnlyList<Guid> TenantsAfectados,
    string Evidencia,
    DateTime? ConfirmadoEnUtc,
    IReadOnlyList<TransicionIncidenteDto> Transiciones,
    IReadOnlyList<EnvioAvisoDto> Envios);

public record TransicionIncidenteDto(
    EstadoIncidenteSeguridad? EstadoAnterior,
    EstadoIncidenteSeguridad EstadoNuevo,
    DateTime OcurridoEnUtc,
    Guid? ActorUsuarioId,
    string? Motivo);

public record EnvioAvisoDto(
    Guid Id,
    PlantillaAvisoIncidenteSeguridad Plantilla,
    Guid TenantAfectadoId,
    string DestinatarioNombre,
    string DestinatarioCorreo,
    bool SinContactoValidado,
    EstadoEnvioAvisoIncidenteSeguridad Estado,
    int Intentos,
    Guid ConfirmadoPorUsuarioId,
    DateTime ConfirmadoEnUtc,
    DateTime UltimoIntentoEnUtc,
    DateTime? EnviadoEnUtc,
    string? UltimoError);

public record EnvioAvisoSolicitado(
    Guid IncidenteId,
    PlantillaAvisoIncidenteSeguridad Plantilla,
    Guid TenantAfectadoId,
    string DestinatarioNombre,
    string DestinatarioCorreo,
    bool SinContactoValidado,
    string Asunto,
    string Cuerpo);

/// <param name="Asunto">El guardado en la fila: en un reintento, el de la primera vez.</param>
/// <param name="Cuerpo">El guardado en la fila: en un reintento, el de la primera vez.</param>
public record EnvioAvisoReclamado(
    Guid EnvioId, bool Reclamado, EstadoEnvioAvisoIncidenteSeguridad Estado, string Asunto, string Cuerpo);

/// <summary>
/// Apartados del aviso (art. 33.3 RGPD). El primer aviso lleva los que se saben y dice que los demás llegarán
/// con la notificación completa; la completa exige los cinco.
/// </summary>
public record ContenidoAvisoIncidente(
    string Naturaleza,
    string DatosEInteresados,
    string Consecuencias,
    string Medidas,
    string Contacto)
{
    public const int LongitudMaximaApartado = 2_000;

    public IEnumerable<string> Apartados => [Naturaleza, DatosEInteresados, Consecuencias, Medidas, Contacto];
}

/// <summary>Plazos contados desde la confirmación (DPA § 7; decisión del propietario del 2026-10-26).</summary>
public static class PlazosAvisoIncidenteSeguridad
{
    public static readonly TimeSpan PrimerAviso = TimeSpan.FromHours(24);
    public static readonly TimeSpan NotificacionCompleta = TimeSpan.FromHours(36);
}

/// <summary>Resultado de un clic de envío, para la página.</summary>
/// <param name="Enviados">Salieron en este clic.</param>
/// <param name="YaEnviados">Ya habían salido antes: el reintento no los repite.</param>
/// <param name="EnCurso">Otro clic los está enviando ahora mismo.</param>
/// <param name="TenantsSinDestinatario">Tenants afectados sin contacto validado ni Administradores con correo.</param>
public record ResumenEnvioAviso(
    int Enviados, int YaEnviados, int Fallidos, int EnCurso, IReadOnlyList<Guid> TenantsSinDestinatario);

public static class ErroresIncidenteSeguridad
{
    public static readonly Error SoloActorDePlataforma = Error.Crear(
        "IncidenteSeguridad.SoloActorDePlataforma",
        "Solo un Actor de Plataforma TALVEG con administración de plataforma global, desde su propia cuenta y fuera de un acceso de soporte, puede hacer esto.");

    public static readonly Error SinDobleFactor = Error.Crear(
        "IncidenteSeguridad.SinDobleFactor",
        "Activa la autenticación en dos pasos en tu cuenta antes de decidir sobre un incidente de seguridad.");

    public static readonly Error TotpNoVerificado = Error.Crear(
        "IncidenteSeguridad.TotpNoVerificado",
        "Cierra sesión y vuelve a entrar con tu código de autenticación: decidir sobre un incidente exige el segundo factor en esta sesión.");

    public static readonly Error NoEncontrado = Error.Crear(
        "IncidenteSeguridad.NoEncontrado", "No encontramos ese incidente de seguridad.");

    public static readonly Error EstadoNoValido = Error.Crear(
        "IncidenteSeguridad.EstadoNoValido",
        "El incidente ya no admite esta decisión: otra persona lo ha confirmado o descartado. Recarga la página.");

    public static readonly Error NoConfirmado = Error.Crear(
        "IncidenteSeguridad.NoConfirmado", "Confirma el incidente antes de enviar la notificación completa.");

    public static readonly Error MotivoObligatorio = Error.Crear(
        "IncidenteSeguridad.MotivoObligatorio", "Indica el motivo por el que lo descartas.");

    public static readonly Error ContenidoIncompleto = Error.Crear(
        "IncidenteSeguridad.ContenidoIncompleto", "Completa todos los apartados del aviso antes de enviarlo.");

    public static readonly Error ContenidoDemasiadoLargo = Error.Crear(
        "IncidenteSeguridad.ContenidoDemasiadoLargo",
        $"Cada apartado del aviso puede tener como mucho {ContenidoAvisoIncidente.LongitudMaximaApartado} caracteres.");

    public static readonly Error CuerpoDemasiadoLargo = Error.Crear(
        "IncidenteSeguridad.CuerpoDemasiadoLargo",
        $"El correo del aviso supera los {EnvioAvisoIncidenteSeguridad.LongitudMaximaCuerpo} caracteres que admite el envío (los signos como < > & y las comillas ocupan más al enviarse). Acorta los apartados y vuelve a enviar: no ha salido ningún correo.");

    public static readonly Error DestinatarioNoValido = Error.Crear(
        "IncidenteSeguridad.DestinatarioNoValido",
        "Los destinatarios han cambiado mientras confirmabas. Recarga la página y vuelve a enviar.");
}
