using CaeManager.Application.Documentos;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Alertas;

/// <summary>La misma selección de vencimientos de Empresa para Alertas e Inicio, también sin Centros asociados.</summary>
public static class DocumentosConAlertaDeVigencia
{
    // Prefiltro SQL de cota superior compartido por Trabajador y Empresa; el estado lo decide la calculadora.
    public static DateOnly FechaLimite(DateOnly hoy, ParametroSistema parametros) => hoy.AddDays(parametros.UmbralAmbarDias);

    public static IQueryable<Documento> DeEmpresa(IDocumentosQueryContext contexto,
        IReadOnlyList<Guid>? empresaIdsVisibles, DateOnly fechaLimite) =>
        contexto.Documentos.Operativos()
            .Where(d => d.EmpresaId != null && d.TrabajadorId == null && d.VehiculoId == null)
            .Where(d => empresaIdsVisibles == null || empresaIdsVisibles.Contains(d.EmpresaId!.Value))
            .Where(d => d.FechaVencimiento != null && d.FechaVencimiento <= fechaLimite);
}
