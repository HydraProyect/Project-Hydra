namespace CaeManager.Infrastructure.Gestiones;

/// <summary>
/// Disparador de la conciliación de Gestiones automáticas (<c>ConciliacionGestionesAutomaticasHostedService</c>): hace
/// nacer, en cada Tenant propietario, la Gestión de los actos con archivo que la ranura anterior atendió sin ella durante
/// el relevo azul/verde del despliegue que introduce la función.
///
/// <para>
/// <b>Apagado por defecto.</b> No se ejecuta sola en ningún entorno: cuándo corre en producción lo decide quien
/// despliega, encendiendo <see cref="EjecutarAlArrancar"/> (variable
/// <c>ConciliacionGestionesAutomaticas__EjecutarAlArrancar=true</c>) para ese despliegue y apagándola después. Es
/// idempotente: repetirla no duplica Gestiones. Solo agrega; nunca modifica ni da de baja una Gestión.
/// </para>
/// </summary>
public class ConciliacionGestionesAutomaticasOptions
{
    public const string SeccionConfiguracion = "ConciliacionGestionesAutomaticas";

    /// <summary>Si es verdadero, la instancia concilia todos los Tenants en segundo plano tras arrancar.</summary>
    public bool EjecutarAlArrancar { get; set; }

    /// <summary>
    /// Hasta cuánto antes del arranque se miran los Documentos: acota el histórico anterior a la función. Los actos de
    /// la ranura anterior durante la migración caen pocos minutos antes del arranque de la nueva, y los del drenaje,
    /// después. 24 horas por defecto, para que encenderla en un reinicio del mismo día siga cubriendo el relevo.
    /// </summary>
    public TimeSpan Ventana { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Espera antes de la primera pasada, para que la ranura anterior haya terminado de drenar: lo que cree después de
    /// una pasada completa solo lo cubre otro arranque. 35 minutos por defecto, por encima del drenaje máximo de
    /// producción (<c>DRENAJE_MAX_PRODUCCION</c>, 1800 s en <c>deploy/relevo-app.sh</c>).
    /// </summary>
    public TimeSpan EsperaInicial { get; set; } = TimeSpan.FromMinutes(35);

    /// <summary>Espera entre un intento que no completó la pasada (otra instancia la tenía, o falló algo) y el siguiente.</summary>
    public TimeSpan IntervaloReintento { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Intentos como mucho. Agotados sin una pasada completa, queda en el registro como error.</summary>
    public int MaximoIntentos { get; set; } = 12;
}
