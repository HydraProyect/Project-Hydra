namespace CaeManager.Infrastructure.Documentos;

/// <summary>
/// Disparador de la conciliación de la regla A-1 (<c>ConciliacionAcreditacionesEmpresaHostedService</c>): crea, en
/// cada Tenant propietario, las acreditaciones de plataforma que falten para sus Documentos de Empresa operativos.
///
/// <para>
/// <b>Apagado por defecto.</b> No se ejecuta sola en ningún entorno: cuándo corre en producción lo decide quien
/// despliega, encendiendo <see cref="EjecutarAlArrancar"/> (variable
/// <c>ConciliacionAcreditacionesEmpresa__EjecutarAlArrancar=true</c>) para un arranque y apagándola después. Es
/// idempotente, así que repetirla no duplica nada; solo agrega acreditaciones, nunca da de baja ni modifica una.
/// </para>
///
/// <para>
/// <b>Relevo azul/verde.</b> Mientras conviven las dos ranuras, la anterior puede seguir creando Documentos de
/// Empresa sin las acreditaciones de la regla A-1. Esa ventana la cubre la conciliación cuando se ejecute, siempre
/// que se ejecute después de que la ranura anterior deje de atender: lo creado después de una pasada solo lo cubre
/// la pasada siguiente.
/// </para>
/// </summary>
public class ConciliacionAcreditacionesEmpresaOptions
{
    public const string SeccionConfiguracion = "ConciliacionAcreditacionesEmpresa";

    /// <summary>Si es verdadero, la conciliación recorre todos los Tenants una vez, en segundo plano, al arrancar.</summary>
    public bool EjecutarAlArrancar { get; set; }
}
