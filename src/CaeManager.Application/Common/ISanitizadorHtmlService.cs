namespace CaeManager.Application.Common;

/// <summary>
/// Depura HTML de origen no confiable dejando solo una lista blanca de
/// etiquetas y atributos seguros. El cuerpo de un correo entrante lo escribe
/// quien envía el mensaje, no un usuario del sistema: cuando llegue la ingesta
/// por Microsoft Graph (ver <c>Conversacion</c>), bastará con conocer la
/// dirección del buzón para inyectar marcado en una pantalla autenticada
/// (hallazgo N-1 de INFORME-AUDITORIA-2.md).
///
/// Se sanea en la frontera del DTO, no en la vista: es el punto por el que
/// pasa toda lectura del cuerpo, así que cualquier UI futura (o una API) queda
/// cubierta sin volver a acordarse. Guardar el original sin tocar es
/// deliberado — el saneado es una decisión de presentación y sus reglas
/// cambian con el tiempo; destruir el correo recibido no se puede deshacer.
/// </summary>
public interface ISanitizadorHtmlService
{
    /// <summary>
    /// Devuelve el marcado sin nada ejecutable: sin <c>script</c>, sin
    /// manejadores <c>on*</c>, sin <c>javascript:</c> y sin las etiquetas que
    /// permiten cargar contenido externo. Un valor nulo o vacío se devuelve
    /// como cadena vacía.
    /// </summary>
    string Sanear(string? html);
}
