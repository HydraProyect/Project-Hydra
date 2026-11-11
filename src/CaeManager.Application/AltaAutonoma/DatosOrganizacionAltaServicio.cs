using CaeManager.Domain.AltaAutonoma;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.AltaAutonoma;

/// <summary>Resultado del paso «Empresa» del alta autónoma (A4), tal como lo ve quien se registra.</summary>
public enum ResultadoDatosOrganizacionAlta
{
    /// <summary>El CIF no tiene Tenant propietario: la Solicitud de alta pasa a <c>DatosCompletos</c>.</summary>
    DatosCompletos,

    /// <summary>
    /// El CIF ya tiene Tenant propietario: la Solicitud de alta pasa a <c>DerivadaAAcceso</c>, que es final. Quien se
    /// registra recibe un mensaje genérico, sin saber qué Tenant es ni quién lo gestiona (RQ-13, RQ-28).
    /// </summary>
    DerivadaAAcceso,

    /// <summary>
    /// Credencial de continuación desconocido o caducado, Solicitud de alta fuera de <c>CorreoVerificado</c> o vencida:
    /// no cambia nada y hay que volver a empezar.
    /// </summary>
    NoValido,
}

/// <summary>
/// En qué punto está la Solicitud de alta de un credencial de continuación, sin cambiar nada: lo que la página del paso
/// «Empresa» necesita para pintar el formulario, lo ya guardado o el aviso de volver a empezar al recargar o reenviar.
/// </summary>
public enum EstadoContinuacionAlta
{
    /// <summary>Credencial desconocido o de una Solicitud de alta en un estado sin este paso.</summary>
    NoValido,

    /// <summary>
    /// El credencial existe pero caducó (sus dos horas o la Solicitud de alta): es el único caso, con
    /// <see cref="ResultadoDatosOrganizacionAlta.DerivadaAAcceso"/>, en que la página borra la cookie.
    /// </summary>
    Caducada,

    /// <summary>Correo verificado y datos de la organización por completar: se pinta el formulario.</summary>
    CorreoVerificado,

    /// <summary>Los datos ya están guardados: se pinta lo guardado, sin volver a transicionar, y la cookie sigue para A5.</summary>
    DatosCompletos,
}

/// <summary>Lo que responde la función de la base: el resultado y el Id de la propia Solicitud de alta (nulo si no vale).</summary>
public sealed record TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta Resultado, Guid? SolicitudAltaId);

/// <summary>
/// Paso «Empresa» del alta autónoma (A4): con el credencial de continuación que se entregó al verificar el correo, guarda
/// los datos de la organización y decide en la base, bajo candado y sin revelar nada del Tenant existente, si el CIF está
/// libre (<see cref="ResultadoDatosOrganizacionAlta.DatosCompletos"/>) o ya tiene Tenant propietario
/// (<see cref="ResultadoDatosOrganizacionAlta.DerivadaAAcceso"/>).
/// <para>
/// Aviso a Soporte TALVEG, versión mínima: un registro estructurado con <see cref="EventoDerivadaAAcceso"/> y el
/// <c>SolicitudAltaId</c>, sin datos personales (ni correo, ni CIF, ni razón social): los datos quedan en la propia
/// Solicitud de alta, que Soporte TALVEG consultará en el panel de altas (A10). Sin correo de ningún tipo.
/// </para>
/// <para>
/// No es un <c>ICommand</c> de MediatR por lo mismo que <see cref="ServicioRegistroAlta"/>: quien lo usa es anónimo. No
/// crea cuenta, ni sesión de Identity, ni Tenant (DA-2). El límite de consultas por CIF lo aplica la página (mira el cupo
/// antes de llamar y lo gasta solo si la base hizo la transición), porque vive con el resto del antiabuso de la Web.
/// </para>
/// </summary>
public sealed class ServicioDatosOrganizacionAlta(
    IAccesoSolicitudesAlta acceso, ILogger<ServicioDatosOrganizacionAlta> logger)
{
    /// <summary>Evento estable del aviso a Soporte TALVEG: una Solicitud de alta derivada a acceso.</summary>
    public static readonly EventId EventoDerivadaAAcceso = new(44001, "AltaAutonomaDerivadaAAcceso");

    /// <summary>Evento estable de una Solicitud de alta con los datos de la organización completos.</summary>
    public static readonly EventId EventoDatosCompletos = new(44002, "AltaAutonomaDatosCompletos");

    public async Task<ResultadoDatosOrganizacionAlta> CompletarAsync(
        string? credencialContinuacion, DatosOrganizacionAlta datos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(datos);

        if (!EsCredencialBienFormado(credencialContinuacion))
            return ResultadoDatosOrganizacionAlta.NoValido;

        var transicion = await acceso.CompletarDatosOrganizacionAsync(credencialContinuacion!, datos, cancellationToken);
        switch (transicion.Resultado)
        {
            case ResultadoDatosOrganizacionAlta.DerivadaAAcceso:
                logger.LogWarning(EventoDerivadaAAcceso,
                    "Solicitud de alta {SolicitudAltaId} derivada a acceso: su CIF ya tiene Tenant propietario. Soporte TALVEG la atiende desde el panel de altas.",
                    transicion.SolicitudAltaId);
                break;
            case ResultadoDatosOrganizacionAlta.DatosCompletos:
                logger.LogInformation(EventoDatosCompletos,
                    "Solicitud de alta {SolicitudAltaId} con los datos de la organización completos.", transicion.SolicitudAltaId);
                break;
        }

        return transicion.Resultado;
    }

    /// <summary>
    /// Lee en la base en qué punto está la Solicitud de alta del credencial, sin bloquear ni escribir. Un credencial sin la
    /// forma debida no llega a la base.
    /// </summary>
    public Task<EstadoContinuacionAlta> ConsultarContinuacionAsync(string? credencialContinuacion, CancellationToken cancellationToken) =>
        EsCredencialBienFormado(credencialContinuacion)
            ? acceso.ConsultarContinuacionAsync(credencialContinuacion!, cancellationToken)
            : Task.FromResult(EstadoContinuacionAlta.NoValido);

    /// <summary>Forma de <see cref="ServicioRegistroAlta.NuevoToken"/>: 43 caracteres Base64url.</summary>
    public static bool EsCredencialBienFormado(string? credencial) =>
        credencial is { Length: ServicioRegistroAlta.LongitudToken }
        && credencial.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
