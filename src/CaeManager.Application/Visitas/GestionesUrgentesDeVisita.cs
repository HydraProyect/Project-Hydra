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
    /// Las Visitas por empezar dentro de la ventana de aviso, ya en SQL: son justo las que
    /// <see cref="EsUrgente(VisitaListaDto)"/> acepta, para que las Visitas en curso no ocupen sitio en la
    /// lectura. El tamaño es el de cada página de <c>LeerTodasAsync</c>, no un tope: leer solo una página
    /// dejaría contar en un sitio una Visita que otro no enseña.
    /// </summary>
    public static ObtenerVisitasQuery ConsultaCandidatas() =>
        new(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true, TamanoPagina: 200, SoloPorEmpezar: true);

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
