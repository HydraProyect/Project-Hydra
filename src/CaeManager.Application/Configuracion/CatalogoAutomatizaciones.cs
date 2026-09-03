namespace CaeManager.Application.Configuracion;

/// <summary>
/// Catálogo fijo de los trabajos automáticos reales del sistema que son
/// **automatización de negocio** (Configuración, Parte XVI PROMPT 08 —
/// "Automatizaciones"; alcance fijado en la auditoría de producto A-06, 2026-
/// 09-02). Los identificadores son los MISMOS que ya usan los hosted
/// services para <c>IEleccionLiderService.IntentarEjecutarComoLiderAsync</c>
/// — no se inventan claves nuevas. Compartido entre Application (query/
/// comando) e Infrastructure (los propios hosted services), que sí puede
/// referenciar Application.
///
/// El árbol registra 13 <c>AddHostedService</c> (13 tipos distintos,
/// verificado 2026-09-02 al añadir <c>RetencionHostedService</c> en
/// HO-084-01 — antes eran 12, cifra corregida entonces del informe del
/// Módulo 6 que traía 14; y este comentario decía por error "Solo 4" cuando
/// el catálogo ya listaba 5, con VigilanciaNormativaBoe). Son 6
/// "automatización de negocio" y viven aquí; los otros 7 se excluyen por
/// motivo, no por olvido:
///
/// <list type="bullet">
/// <item><description><b>Con canal de observabilidad propio, mejor que el de
/// este catálogo:</b> <c>ProcesadorAnalisisDocumentoHostedService</c> (IA
/// documental) ya reporta profundidad de cola y alerta a guardia vía
/// <c>IAlertaOperativa</c> cuando se estanca — un simple interruptor on/off
/// aquí sería además una decisión de producto (apagar la revisión IA) que no
/// se puede tomar de pasada, y menos con la demo en producción dependiendo
/// de que la IA esté operativa (decisión del propietario, 2026-08-27).</description></item>
/// <item><description><b>Mantenimiento técnico, no accionable por el
/// Administrador del tenant</b> — no pertenecen a una pantalla de producto:
/// <c>RenovacionSuscripcionWebhookHostedService</c> (su salud se refleja en
/// Integraciones — <see cref="Domain.Integraciones.ConexionIntegracion.UltimoError"/>
/// — no aquí), <c>RedaccionPayloadWebhookHostedService</c> (purga de PII por
/// retención) y <c>ExpiracionAsignacionesHostedService</c> (integridad
/// interna de datos, requisito de esquema).</description></item>
/// <item><description><b>Verificación de infraestructura</b> — gates de
/// arranque que comprueban si un sistema de la propia plataforma (no del
/// tenant) está operativo, y no corren por tenant: <c>VerificacionKmsHostedService</c>,
/// <c>VerificacionDataProtectionS3HostedService</c>,
/// <c>VerificacionSignalRRedisHostedService</c>. Si algún día hace falta una
/// pantalla para esto, es del Actor de Plataforma TALVEG, no de este
/// catálogo — mostrarlos aquí, en la Configuración de UN tenant, sería
/// atribuirle a ese tenant algo que ni le pertenece ni puede controlar.</description></item>
/// </list>
///
/// "Recálculo nocturno de semáforo" del mockup queda deliberadamente fuera
/// por la misma razón que las anteriores: no existe ningún trabajo por lotes
/// real detrás — el estado de cada Documento se calcula en el momento con
/// <c>CalculadoraEstadoDocumento</c> cada vez que se consulta, nunca se
/// recalcula ni persiste en background. Añadir un trabajo de "recálculo"
/// fingido no tendría ningún efecto real que mostrar ni que apagar.
/// </summary>
public static class CatalogoAutomatizaciones
{
    public const string IngestaCorreoM365 = "ingesta-webhook-microsoft365";
    public const string IngestaWhatsApp = "ingesta-webhook-whatsapp";
    public const string AlertasVencimientoDiarias = "envio-alertas-vencimiento";
    public const string VigilanciaVisitasUrgentes = "vigilancia-visitas-urgentes";
    public const string VigilanciaNormativaBoe = "vigilancia-normativa-boe";

    /// <summary>
    /// HO-084-01 (REC-084, DEC-35): con política de retención aprobada y
    /// efectiva, el barrido de detección corre solo; sin ella, corre en modo
    /// diagnóstico y no propone nada ejecutable. Nunca destruye — la
    /// ejecución de una purga ya autorizada sigue siendo manual (Retencion.razor).
    /// </summary>
    public const string BarridoRetencionDatos = "barrido-retencion-datos";

    public static readonly IReadOnlyList<DefinicionAutomatizacion> Trabajos =
    [
        // Sin Cadencia (null): sondean cada 10 s — un "próximo ciclo" con
        // marca de tiempo sería ruido que envejece antes de que la pantalla
        // termine de renderizar. El panel lo muestra como "Continuo".
        new(IngestaCorreoM365, "Ingesta de correo M365", "Lee los buzones conectados y extrae los adjuntos.", Conmutable: true),
        new(IngestaWhatsApp, "Ingesta de WhatsApp", "Lee las líneas de WhatsApp conectadas y extrae los mensajes entrantes.", Conmutable: true),
        new(AlertasVencimientoDiarias, "Alertas de vencimiento diarias", "Correo diario a Administrador y Dirección CAE con la documentación pendiente de toda la cartera.", Conmutable: true, Cadencia: TimeSpan.FromHours(24)),
        new(VigilanciaVisitasUrgentes, "Vigilancia de gestiones urgentes de visita", "Avisa por hora cuando hay visitas o sugerencias dentro de la ventana mínima de validación.", Conmutable: true, Cadencia: TimeSpan.FromHours(1)),
        new(VigilanciaNormativaBoe, "Vigilancia normativa BOE", "Detecta publicaciones del BOE que afectan al catálogo de Tipos de documento — global para todos los tenants, no se apaga por uno solo sin afectar al resto.", Conmutable: false, Cadencia: TimeSpan.FromHours(12)),
        new(BarridoRetencionDatos, "Barrido de retención de datos", "Con política de retención aprobada, detecta lo purgable y deja propuestas pendientes de revisión; sin política, solo diagnostica.", Conmutable: true, Cadencia: TimeSpan.FromHours(24))
    ];
}

/// <summary><paramref name="Cadencia"/> es el intervalo de sondeo real del hosted service (mismo valor que su <c>IntervaloSondeo</c>) — null para los que sondean en segundos, donde una marca de "próximo ciclo" no aporta nada.</summary>
public record DefinicionAutomatizacion(string Id, string Nombre, string Descripcion, bool Conmutable, TimeSpan? Cadencia = null);
