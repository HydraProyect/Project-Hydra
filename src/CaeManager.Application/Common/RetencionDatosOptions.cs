using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Common;

/// <summary>
/// Política de retención de datos personales, configurable fuera del código
/// (RGPD-TRATAMIENTO-DATOS.md § 5).
///
/// Está en configuración y no fijada en el código a propósito: los plazos son
/// una decisión legal, sujeta a revisión por un asesor y a cambios
/// normativos, y no debería hacer falta un despliegue para ajustarlos. El
/// código aporta el mecanismo; los valores los pone quien responde de ellos.
///
/// <b>Apagado por defecto.</b> Mismo patrón que <c>Backups:Activo</c> y
/// <c>DataProtection:Kms:Activo</c>: una purga que se active sola tras un
/// despliegue es la clase de accidente que no tiene vuelta atrás.
/// </summary>
public class RetencionDatosOptions
{
    public const string SeccionConfiguracion = "RetencionDatos";

    /// <summary>
    /// Con false —el valor por defecto— nada se purga ni se anonimiza; el
    /// mecanismo puede calcular y listar qué sería purgable, que es lo que
    /// permite revisarlo antes de encenderlo.
    /// </summary>
    public bool Activa { get; set; }

    /// <summary>
    /// Plazo para Documentos. Cinco años decididos por el propietario del
    /// producto (2026-07-18) por coincidir con la prescripción de
    /// responsabilidades en el orden social.
    /// </summary>
    public int AniosRetencionDocumentos { get; set; } = 5;

    /// <summary>
    /// Desde qué evento cuenta el plazo. El valor por defecto —el evento más
    /// temprano— lo decidió el propietario del producto: un documento subido
    /// el último día de su vigencia hay que conservarlo su plazo desde ahí, y
    /// contar desde la emisión haría que dos documentos idénticos se purgaran
    /// en momentos distintos según cuándo se subieron.
    /// </summary>
    public CriterioInicioRetencion CriterioInicio { get; set; } = CriterioInicioRetencion.EventoMasTemprano;

    /// <summary>
    /// Retención del Trabajador como entidad. Sin valor por defecto a
    /// propósito: sigue sin decidirse (RGPD-TRATAMIENTO-DATOS.md § 5) y
    /// afecta a datos de salud, así que el mecanismo no debe inventarse uno.
    /// Null significa "no purgar trabajadores".
    /// </summary>
    public int? AniosRetencionTrabajadores { get; set; }
}
