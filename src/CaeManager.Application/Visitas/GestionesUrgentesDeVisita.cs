using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Visitas;

namespace CaeManager.Application.Visitas;

/// <summary>
/// Qué es una gestión urgente de visita: una única definición para la cola de Mi trabajo y para la
/// vigilancia horaria, que se calcula sin usuario y no puede pasar por la Query de la cola.
/// </summary>
public static class GestionesUrgentesDeVisita
{
    /// <summary>
    /// Las Visitas candidatas. Solo es la candidatura: trae también las Visitas en curso, que
    /// <see cref="EsUrgente(VisitaListaDto)"/> descarta.
    /// </summary>
    public static ObtenerVisitasQuery ConsultaCandidatas(int pagina = 1) =>
        new(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true, Pagina: pagina, TamanoPagina: 200);

    public static bool EsUrgente(VisitaListaDto visita) =>
        visita.NivelUrgencia is NivelUrgenciaVisita.Urgente or NivelUrgenciaVisita.Critica;

    /// <summary>
    /// Sin fecha detectada la IA no pudo precisar cuándo es: se trata como «visita sorpresa» (mismo
    /// día, sin margen) en vez de descartarla por falta de dato, porque el pedido original explícito
    /// es avisar cuando llega una visita sorpresa para el mismo día.
    /// </summary>
    public static bool EsUrgente(SugerenciaVisitaCorreoPendienteDto sugerencia, DateOnly hoy, int horasAvisoVisita, int horasCriticasVisita) =>
        sugerencia.FechaInicioSugerida is not { } fecha
        || CalculadoraUrgenciaVisita.Calcular(fecha, fecha, hoy, horasAvisoVisita, horasCriticasVisita) is not NivelUrgenciaVisita.Normal;
}
