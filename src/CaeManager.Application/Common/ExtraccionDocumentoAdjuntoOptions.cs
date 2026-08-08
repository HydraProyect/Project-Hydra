namespace CaeManager.Application.Common;

/// <summary>
/// Kill switch de <see cref="Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto.DetectarActualizacionDocumentoDesdeAdjuntoQuery"/>
/// — decisión explícita del usuario, 2026-08-08. Envía el adjunto completo
/// de un mensaje de WhatsApp/correo (contenido externo, sin vetar por nadie
/// del equipo) a un proveedor de IA externo (Anthropic/Mistral/Gemini),
/// mismo riesgo que <see cref="DeteccionPreviaDocumentoOptions"/> pero
/// peor: aquello procesaba una subida manual ya iniciada por un usuario del
/// sistema; esto procesaría cualquier fichero que le llegue al buzón
/// conectado, sin que el remitente sea de confianza. Apagado por defecto
/// hasta que el DPA declare explícitamente este tratamiento — mientras
/// tanto, el flujo "Actualizar documentación desde conversación" existe
/// pero no hace ninguna llamada a IA, solo deja los campos en blanco para
/// que el gestor los rellene a mano.
/// </summary>
public class ExtraccionDocumentoAdjuntoOptions
{
    public const string SeccionConfiguracion = "ExtraccionDocumentoAdjunto";

    public bool Activa { get; set; }
}
