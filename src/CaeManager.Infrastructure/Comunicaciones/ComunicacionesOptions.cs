namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>
/// Interruptor del módulo Comunicaciones completo (P2 #26 de
/// docs/business/MATURITY_REVIEW.md).
///
/// No hay ingesta real de Microsoft Graph detrás de esta bandeja — nada
/// escribe una <c>ConversacionCorreo</c> en producción salvo la propia UI a
/// mano (ver ROADMAP.md, Fase 59: "responder solo persiste el mensaje en
/// Hydra, no lo envía"). El comité de revisión lo describe como "una
/// maqueta funcional cara" y el propio criterio de <c>NavMenu.razor</c> ya
/// dice, desde antes de este cambio, que "un enlace a una pantalla que
/// todavía no existe [de verdad] es peor que no tener el enlace". Apagado
/// por defecto: se presenta como si no existiera (ver Bandeja.razor.cs/
/// Macros.razor.cs, que redirigen a /not-found) hasta que exista ingesta
/// real o una decisión comercial explícita de venderlo tal cual.
///
/// Construir la ingesta real de Microsoft Graph exige un App Registration
/// de Entra ID con permisos de aplicación y consentimiento de administrador
/// — credenciales reales que no existen en el entorno donde se tomó esta
/// decisión; por eso se congela en vez de completarse.
/// </summary>
public class ComunicacionesOptions
{
    public const string SeccionConfiguracion = "Comunicaciones";

    public bool Activo { get; set; }
}
