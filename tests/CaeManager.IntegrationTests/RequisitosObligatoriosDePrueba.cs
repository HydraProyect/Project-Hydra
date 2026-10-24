using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;

namespace CaeManager.IntegrationTests;

/// <summary>
/// <see cref="RequisitosObligatoriosIncumplidosService"/> montado sobre un mismo contexto, como lo resuelve la inyección de
/// dependencias en producción. Sin alcance, ve todos los Centros (igual que <see cref="AlcanceDatosServiceFalso"/> por
/// defecto); un test de alcance le pasa el mismo que al handler.
/// </summary>
internal static class RequisitosObligatoriosDePrueba
{
    public static RequisitosObligatoriosIncumplidosService Servicio<T>(T contexto, IAlcanceDatosService? alcance = null)
        where T : ICentrosQueryContext, ITiposDocumentoQueryContext, ITrabajadoresQueryContext, IAsignacionesQueryContext,
            IDocumentosQueryContext =>
        new(
            new EvaluacionDeAccesoPorCentroService(
                contexto, contexto, contexto, contexto, contexto, alcance ?? new AlcanceDatosServiceFalso()),
            contexto, contexto, contexto);
}
