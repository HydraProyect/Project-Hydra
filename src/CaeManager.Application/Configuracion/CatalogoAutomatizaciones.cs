namespace CaeManager.Application.Configuracion;

/// <summary>
/// Catálogo fijo de los trabajos automáticos reales del sistema
/// (Configuración, Parte XVI PROMPT 08 — "Automatizaciones"). Los
/// identificadores son los MISMOS que ya usan los hosted services para
/// <c>IEleccionLiderService.IntentarEjecutarComoLiderAsync</c> — no se
/// inventan claves nuevas. Compartido entre Application (query/comando) e
/// Infrastructure (los propios hosted services), que sí puede referenciar
/// Application.
///
/// "Recálculo nocturno de semáforo" del mockup queda deliberadamente fuera:
/// no existe ningún trabajo por lotes real detrás — el estado de cada
/// Documento se calcula en el momento con <c>CalculadoraEstadoDocumento</c>
/// cada vez que se consulta, nunca se recalcula ni persiste en background.
/// Añadir un trabajo de "recálculo" fingido no tendría ningún efecto real
/// que mostrar ni que apagar.
/// </summary>
public static class CatalogoAutomatizaciones
{
    public const string IngestaCorreoM365 = "ingesta-webhook-microsoft365";
    public const string AlertasVencimientoDiarias = "envio-alertas-vencimiento";
    public const string VigilanciaVisitasUrgentes = "vigilancia-visitas-urgentes";
    public const string VigilanciaNormativaBoe = "vigilancia-normativa-boe";

    public static readonly IReadOnlyList<DefinicionAutomatizacion> Trabajos =
    [
        new(IngestaCorreoM365, "Ingesta de correo M365", "Lee los buzones conectados y extrae los adjuntos.", Conmutable: true),
        new(AlertasVencimientoDiarias, "Alertas de vencimiento diarias", "Correo diario a Administrador y Dirección CAE con la documentación pendiente de toda la cartera.", Conmutable: true),
        new(VigilanciaVisitasUrgentes, "Vigilancia de gestiones urgentes de visita", "Avisa por hora cuando hay visitas o sugerencias dentro de la ventana mínima de validación.", Conmutable: true),
        new(VigilanciaNormativaBoe, "Vigilancia normativa BOE", "Detecta publicaciones del BOE que afectan al catálogo de Tipos de documento — global para todos los tenants, no se apaga por uno solo sin afectar al resto.", Conmutable: false)
    ];
}

public record DefinicionAutomatizacion(string Id, string Nombre, string Descripcion, bool Conmutable);
