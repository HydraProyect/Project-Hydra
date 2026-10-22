using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Alertas;

/// <summary>
/// La única selección de los documentos que cuentan para Alertas, Inicio (y su Visión de cartera) y Mi trabajo, que recibe
/// las alertas ya calculadas. Cada pantalla contaba antes con su propia consulta y un mismo Tenant beneficiario daba cifras
/// distintas: Inicio contaba los documentos de un Trabajador que ya no existe o de un voluntario inactivo, y no veía los de
/// Empresa. Sobre esta selección el estado lo decide <c>CalculadoraEstadoDocumento</c>.
/// </summary>
public static class DocumentosConAlertaDeVigencia
{
    // Prefiltro SQL de cota superior compartido por Trabajador y Empresa; el estado lo decide la calculadora.
    public static DateOnly FechaLimite(DateOnly hoy, ParametroSistema parametros) => hoy.AddDays(parametros.UmbralAmbarDias);

    /// <summary>
    /// Documentos operativos de Trabajador del alcance cuyo Trabajador y cuyo Tipo siguen existiendo (el filtro global oculta
    /// lo eliminado) y cuyo Trabajador no es un voluntario inactivo (sin evento vigente ni futuro: no se borra, pero sus
    /// documentos no avisan). Sin cota de fecha: Inicio la usa para todos los estados y Alertas le añade la suya.
    /// </summary>
    public static IQueryable<Documento> DeTrabajador(IDocumentosQueryContext contexto,
        ITrabajadoresQueryContext trabajadores, ITiposDocumentoQueryContext tipos, IProyectosQueryContext proyectos,
        IReadOnlyList<Guid>? trabajadorIdsVisibles, DateOnly hoy)
    {
        var voluntariosInactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadores.Trabajadores, proyectos, hoy);
        return contexto.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null)
            .Where(d => trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(d.TrabajadorId!.Value))
            .Where(d => trabajadores.Trabajadores.Any(t => t.Id == d.TrabajadorId))
            .Where(d => voluntariosInactivos.Contains(d.TrabajadorId!.Value) == false)
            .Where(d => tipos.TiposDocumento.Any(t => t.Id == d.TipoDocumentoId && !t.Archivado));
    }

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
            .Where(d => tipos.TiposDocumento.Any(t => t.Id == d.TipoDocumentoId && !t.Archivado));
}
