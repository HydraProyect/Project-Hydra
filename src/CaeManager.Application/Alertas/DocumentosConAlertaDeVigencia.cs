using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Alertas;

/// <summary>La misma selección de vencimientos de Empresa para Alertas e Inicio, también sin Centros asociados.</summary>
public static class DocumentosConAlertaDeVigencia
{
    // Prefiltro SQL de cota superior compartido por Trabajador y Empresa; el estado lo decide la calculadora.
    public static DateOnly FechaLimite(DateOnly hoy, ParametroSistema parametros) => hoy.AddDays(parametros.UmbralAmbarDias);

    // Solo documentos cuya Empresa y cuyo Tipo siguen existiendo (el filtro global oculta lo eliminado): una Empresa dada de baja
    // conserva sus Documentos, pero no hay fila en Mi trabajo que los explique, así que tampoco avisan ni cuentan como pendiente.
    public static IQueryable<Documento> DeEmpresa(IDocumentosQueryContext contexto,
        IEmpresasQueryContext empresas, ITiposDocumentoQueryContext tipos,
        IReadOnlyList<Guid>? empresaIdsVisibles, DateOnly fechaLimite) =>
        contexto.Documentos.Operativos()
            .Where(d => d.EmpresaId != null && d.TrabajadorId == null && d.VehiculoId == null)
            .Where(d => empresaIdsVisibles == null || empresaIdsVisibles.Contains(d.EmpresaId!.Value))
            .Where(d => d.FechaVencimiento != null && d.FechaVencimiento <= fechaLimite)
            .Where(d => empresas.Empresas.Any(e => e.Id == d.EmpresaId))
            .Where(d => tipos.TiposDocumento.Any(t => t.Id == d.TipoDocumentoId));
}
