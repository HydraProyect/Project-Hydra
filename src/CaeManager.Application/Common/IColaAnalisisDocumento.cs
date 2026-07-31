namespace CaeManager.Application.Common;

/// <summary>Qué análisis pesado hay que hacer sobre un Documento ya guardado.</summary>
public enum TipoAnalisisDocumento
{
    /// <summary>Verificación IA con confidence score (ver Issue #19).</summary>
    VerificacionIa,

    /// <summary>Detección de altas/bajas de personal a partir del documento (Fase 36).</summary>
    DeteccionTrabajadores
}

/// <summary>
/// Encargo de análisis. Lleva <paramref name="TenantId"/> porque el trabajo se
/// ejecuta fuera del circuito, donde no hay claim de sesión que resolver: el
/// procesador lo restablece con <c>AmbitoTenantExplicito</c>, que es el modo
/// previsto para procesos de fondo en docs/MULTITENANCY.md § 8. Sin él, el
/// filtro global no encontraría el documento (fallo cerrado) y el análisis
/// quedaría en nada.
///
/// <paramref name="UsuarioSolicitanteId"/> es a quién se avisa al terminar.
/// </summary>
public record EncargoAnalisisDocumento(
    Guid DocumentoId,
    Guid TenantId,
    Guid? UsuarioSolicitanteId,
    TipoAnalisisDocumento Tipo);

/// <summary>
/// Saca del circuito de Blazor los análisis que se hacían al guardar un
/// Documento.
///
/// Antes, subir un documento esperaba a la verificación IA y a la detección de
/// trabajadores dentro del propio Command: llamadas a un modelo externo, con
/// su latencia, bloqueando el circuito del usuario y ocupando memoria de
/// servidor mientras tanto. Ya eran "mejor esfuerzo" —un fallo solo se
/// registraba en el log y no impedía la subida—, así que sacarlos no cambia
/// ninguna garantía: solo deja de hacer esperar a quien sube el archivo.
///
/// La detección de campos previa a guardar NO pasa por aquí a propósito: ahí
/// el usuario está esperando el resultado para rellenar el formulario, es
/// interactiva por naturaleza y volverla asíncrona rompería el flujo.
/// </summary>
public interface IColaAnalisisDocumento
{
    void Encolar(EncargoAnalisisDocumento encargo);
}
