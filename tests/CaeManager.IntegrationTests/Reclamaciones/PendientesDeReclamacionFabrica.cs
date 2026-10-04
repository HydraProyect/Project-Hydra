using CaeManager.Application.Alertas;
using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Reclamaciones;
using CaeManager.Infrastructure.Persistence;

namespace CaeManager.IntegrationTests.Reclamaciones;

/// <summary>
/// Monta el <see cref="PendientesDeReclamacionService"/> REAL sobre un contexto de pruebas (con sus dos dependencias reales: el
/// cálculo de faltantes de Trabajador y la evaluación de acceso por Centro), para los handlers de reclamación que lo necesitan.
/// Los tests de integración no usan dobles de esta pieza: lo que prueban es justamente que la consulta traduce a SQL.
/// </summary>
internal static class PendientesDeReclamacionFabrica
{
    public static IPendientesDeReclamacionService Crear(CaeManagerDbContext c, IAlcanceDatosService alcance) =>
        new PendientesDeReclamacionService(
            c, c, c, c, c, c,
            new DocumentosFaltantesService(c, c, c),
            new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance),
            alcance);
}
