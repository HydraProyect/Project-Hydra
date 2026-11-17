using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Visitas;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// El evaluador del expediente con la evaluación de acceso real (<see cref="EvaluacionDeAccesoPorCentroService"/>) sobre
/// los mismos contextos falsos: desde la decisión del 2026-11-15 (#2005) el sello usa la regla de bloqueo común, así que
/// un test del sello ejercita también esa regla y no una copia.
/// </summary>
internal static class EvaluadorExpedienteDePrueba
{
    public static EvaluadorExpedienteVisitaService Crear(
        IVisitasQueryContext visitas,
        ICentrosQueryContext centros,
        IDocumentosQueryContext documentos,
        ITiposDocumentoQueryContext tipos,
        ITrabajadoresQueryContext trabajadores,
        IConfiguracionQueryContext configuracion,
        IVisitaRepository repositorio,
        IAsignacionesQueryContext? asignaciones = null) =>
        new(
            visitas, centros, documentos, trabajadores,
            new EvaluacionDeAccesoPorCentroService(
                centros, tipos, trabajadores, asignaciones ?? new AsignacionesQueryContextFalso(), documentos,
                new AlcanceDatosServiceFalso()),
            configuracion, repositorio, new UnitOfWorkFalso(),
            NullLogger<EvaluadorExpedienteVisitaService>.Instance);
}
