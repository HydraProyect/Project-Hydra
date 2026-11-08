using System.Globalization;

namespace CaeManager.Infrastructure.Gestiones;

/// <summary>
/// Disparador de la conciliación de Gestiones automáticas (<c>ConciliacionGestionesAutomaticasHostedService</c>): hace
/// nacer, en cada Tenant propietario, la Gestión de los actos con archivo que la ranura anterior atendió sin ella durante
/// el relevo azul/verde del despliegue que introduce la función.
///
/// <para>
/// <b>Apagado por defecto.</b> No se ejecuta sola en ningún entorno: cuándo corre en producción lo decide quien
/// despliega, encendiendo <see cref="EjecutarAlArrancar"/> (variable
/// <c>ConciliacionGestionesAutomaticas__EjecutarAlArrancar=true</c>) y fijando <see cref="DesdeUtc"/> para ese
/// despliegue, y apagándola después. Es idempotente: repetirla no duplica Gestiones. Solo agrega; nunca modifica ni da de
/// baja una Gestión ya guardada.
/// </para>
/// </summary>
public class ConciliacionGestionesAutomaticasOptions
{
    public const string SeccionConfiguracion = "ConciliacionGestionesAutomaticas";

    /// <summary>Si es verdadero, la instancia concilia todos los Tenants en segundo plano tras arrancar.</summary>
    public bool EjecutarAlArrancar { get; set; }

    /// <summary>
    /// Desde cuándo se miran los Documentos (UTC): el instante en que empezó el despliegue que introduce la función, antes
    /// de aplicar la migración <c>AnadeGestionesAutomaticas</c> (variable
    /// <c>ConciliacionGestionesAutomaticas__DesdeUtc=2026-11-10T08:00:00Z</c>). Acota el histórico anterior a la función
    /// sin depender de la hora del arranque: un reinicio días después sigue mirando el mismo relevo. Texto ISO 8601
    /// terminado en <c>Z</c>, leído con <see cref="LeerDesdeUtc"/>: el enlazador de configuración convertiría un
    /// <c>DateTime</c> a la hora local. Sin valor, o sin zona UTC, la conciliación no corre y lo registra como error: no hay
    /// ventana por defecto que pueda abarcar actos anteriores a la función o dejar fuera los del relevo.
    /// </summary>
    public string? DesdeUtc { get; set; }

    /// <summary>El instante de <see cref="DesdeUtc"/>, o null si falta o no está en UTC (<c>Z</c>).</summary>
    public DateTime? LeerDesdeUtc() =>
        DateTime.TryParse(DesdeUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var desde)
        && desde.Kind == DateTimeKind.Utc
            ? desde
            : null;

    /// <summary>
    /// Cada cuánto se repite la pasada. La ranura anterior puede seguir atendiendo mientras drena (hasta
    /// <c>DRENAJE_MAX_PRODUCCION</c>, 1800 s, o más si el retiro se reintenta), y el código no sabe cuándo termina: no se
    /// confía en una espera fija, sino en que alguna pasada caiga después del drenaje. 10 minutos por defecto.
    /// </summary>
    public TimeSpan IntervaloPasadas { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Durante cuánto tiempo desde el arranque se siguen empezando pasadas; la primera empieza al arrancar. Acota el coste
    /// si la opción se queda encendida, no la cobertura: como <see cref="DesdeUtc"/> es fijo, cualquier arranque posterior
    /// con la opción encendida vuelve a mirar todo el relevo. 12 horas por defecto, muy por encima del drenaje.
    /// </summary>
    public TimeSpan DuracionPasadas { get; set; } = TimeSpan.FromHours(12);
}
