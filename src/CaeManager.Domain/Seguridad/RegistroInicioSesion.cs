using CaeManager.Domain.Common;

namespace CaeManager.Domain.Seguridad;

/// <summary>Cómo se intentó entrar. Se persiste por nombre, nunca por ordinal.</summary>
public enum MetodoInicioSesion
{
    /// <summary>Usuario y contraseña locales (primer paso, con o sin segundo factor después).</summary>
    Contrasena = 0,

    /// <summary>Segundo paso con código de la aplicación autenticadora.</summary>
    SegundoFactor = 1,

    /// <summary>Segundo paso con un código de recuperación.</summary>
    CodigoRecuperacion = 2,

    /// <summary>Cuenta de Microsoft (Entra ID).</summary>
    Microsoft = 3,
}

/// <summary>Cómo terminó el intento. Se persiste por nombre, nunca por ordinal.</summary>
public enum ResultadoInicioSesion
{
    /// <summary>Sesión emitida.</summary>
    Correcto = 0,

    /// <summary>Credencial o código incorrectos.</summary>
    Fallido = 1,

    /// <summary>La cuenta estaba bloqueada (intentos fallidos o desactivación).</summary>
    Bloqueado = 2,

    /// <summary>Contraseña correcta; falta el segundo factor. No es todavía una sesión.</summary>
    SegundoFactorRequerido = 3,

    /// <summary>Credencial válida, pero la entrada no se permite (cuenta desactivada, país no permitido, correo sin confirmar).</summary>
    Rechazado = 4,
}

/// <summary>
/// Señales de inicio de sesión sospechoso. Combinables: se persisten como
/// entero de bits, así que los valores existentes no se reutilizan nunca.
/// </summary>
[Flags]
public enum SenalesInicioSesion
{
    Ninguna = 0,

    /// <summary>País no visto en los inicios correctos de los últimos 90 días (o, sin historial reciente, distinto del habitual).</summary>
    PaisNuevo = 1,

    /// <summary>Navegador sin el identificador de dispositivo de ningún inicio correcto anterior.</summary>
    DispositivoNuevo = 2,

    /// <summary>Distancia al inicio correcto anterior imposible de recorrer en el tiempo transcurrido.</summary>
    ViajeImposible = 4,

    /// <summary>La IP de origen acaba de superar el umbral de intentos fallidos en la ventana.</summary>
    RafagaFallos = 8,

    /// <summary>El Tenant tiene activo el bloqueo por país y el país no está en su lista.</summary>
    PaisNoPermitido = 16,
}

/// <summary>
/// Un intento de inicio de sesión de una cuenta conocida: registro de
/// auditoría append-only, con RLS por Tenant (el Tenant propietario de la
/// cuenta), como <see cref="Auditoria.RegistroAccesoDocumentoSensible"/>.
///
/// <para>
/// <b>Minimización (RGPD art. 5.1.c).</b> Se guarda lo necesario para detectar
/// un acceso indebido y para que la persona revise su propio historial:
/// IP, país y ciudad aproximados, coordenadas redondeadas a 0,1° (unos 11 km,
/// lo justo para el viaje imposible), una descripción corta del navegador
/// («Chrome · Windows») y la huella del identificador de dispositivo, que es
/// un resumen SHA-256 de una cookie aleatoria, no el valor de la cookie ni el
/// User-Agent completo. Nunca el correo tecleado ni la contraseña. Los intentos
/// contra cuentas que no existen no se guardan aquí (no hay Tenant al que
/// pertenezcan ni persona a la que proteger): solo cuentan para la ráfaga por IP,
/// en memoria.
/// </para>
///
/// <para>
/// <b>Retención: 12 meses.</b> La purga la ejecuta la función
/// <c>app_purgar_inicios_sesion()</c> (propietario de la migración, SECURITY
/// DEFINER) con el corte fijado dentro de la propia función: el rol de
/// aplicación puede pedir la purga, pero no puede borrar nada más reciente ni
/// borrar filas sueltas (no tiene UPDATE ni DELETE sobre la tabla).
/// </para>
///
/// <para>
/// <b>Planos separados.</b> <see cref="UsuarioId"/> es la cuenta para la que
/// se emite (o se intenta emitir) la sesión. Si quien actúa es un Actor de
/// Plataforma TALVEG en Sesión Privilegiada, su cuenta va en
/// <see cref="ActorRealUsuarioId"/> y la de <see cref="UsuarioId"/> es el
/// Usuario simulado: nunca se funden en una sola columna, y esos inicios no
/// disparan avisos a la persona simulada.
/// </para>
/// </summary>
public class RegistroInicioSesion : EntidadConTenant
{
    /// <summary>Retención declarada del registro.</summary>
    public const int MesesRetencion = 12;

    public Guid UsuarioId { get; private set; }

    public Guid? ActorRealUsuarioId { get; private set; }

    public DateTime OcurridoEnUtc { get; private set; }

    public MetodoInicioSesion Metodo { get; private set; }

    public ResultadoInicioSesion Resultado { get; private set; }

    /// <summary>IP del cliente tal como la resolvió <c>IpClienteDeConfianza</c> (nunca la cabecera sin verificar).</summary>
    public string? DireccionIp { get; private set; }

    /// <summary>ISO 3166-1 alfa-2; nulo = desconocido (sin base de geolocalización o IP privada).</summary>
    public string? PaisIso { get; private set; }

    public string? Ciudad { get; private set; }

    public double? Latitud { get; private set; }

    public double? Longitud { get; private set; }

    /// <summary>Resumen del identificador de dispositivo (cookie aleatoria), no el identificador.</summary>
    public string? HuellaDispositivo { get; private set; }

    /// <summary>Descripción corta del navegador y del sistema, para la persona («Chrome · Windows»).</summary>
    public string? Dispositivo { get; private set; }

    public SenalesInicioSesion Senales { get; private set; }

    public bool EsSospechoso => Senales != SenalesInicioSesion.Ninguna;

    private RegistroInicioSesion()
    {
    }

    public RegistroInicioSesion(
        Guid usuarioId,
        Guid? actorRealUsuarioId,
        DateTime ocurridoEnUtc,
        MetodoInicioSesion metodo,
        ResultadoInicioSesion resultado,
        string? direccionIp,
        string? paisIso,
        string? ciudad,
        double? latitud,
        double? longitud,
        string? huellaDispositivo,
        string? dispositivo,
        SenalesInicioSesion senales)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El registro de inicio de sesión debe identificar la cuenta.", nameof(usuarioId));
        if (ocurridoEnUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El momento del inicio de sesión debe ir en UTC.", nameof(ocurridoEnUtc));
        if (paisIso is not null && (paisIso.Length != 2 || !paisIso.All(char.IsAsciiLetterUpper)))
            throw new ArgumentException("El país debe ser un código ISO 3166-1 alfa-2 en mayúsculas.", nameof(paisIso));

        UsuarioId = usuarioId;
        ActorRealUsuarioId = actorRealUsuarioId == usuarioId ? null : actorRealUsuarioId;
        OcurridoEnUtc = ocurridoEnUtc;
        Metodo = metodo;
        Resultado = resultado;
        DireccionIp = Recortar(direccionIp, 45);
        PaisIso = paisIso;
        Ciudad = Recortar(ciudad, 100);
        Latitud = latitud is null ? null : Math.Round(latitud.Value, 1);
        Longitud = longitud is null ? null : Math.Round(longitud.Value, 1);
        HuellaDispositivo = Recortar(huellaDispositivo, 64);
        Dispositivo = Recortar(dispositivo, 60);
        Senales = senales;
    }

    private static string? Recortar(string? valor, int maximo)
        => string.IsNullOrWhiteSpace(valor) ? null : valor.Length <= maximo ? valor : valor[..maximo];
}
