using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;

/// <summary>Un paso de una Gestión automática tal como lo ve quien consulta. El estado es el de su acreditación (§ 4.3).</summary>
public sealed record PasoVisibleGestionAutomatica(
    Guid GestionId, Guid AcreditacionId, EstadoAcreditacion Estado, DateOnly? FechaVencimientoEnPlataforma,
    Guid CanalId, string Plataforma, Guid CentroId, string CentroNombre, Guid ClienteEmpresarialId);

/// <summary>
/// <b>El universo</b> de las Gestiones automáticas: los pasos que ve quien consulta. Lo usan el listado, la
/// pantalla de la Gestión y cualquier recuento, para que el avance y las cifras salgan de la misma consulta que
/// las filas (§ 4.2, D-7).
///
/// <para>
/// Un paso se ve cuando se ve su Gestión —su Documento es de un Trabajador visible o de una Empresa sobre la que
/// se opera desde el lado de gestión, el mismo criterio que <c>DocumentoVisibleAsync</c> aplica a los comandos
/// que marcan acreditaciones— y además su Centro está en la cartera de gestión
/// (<see cref="IAlcanceDatosService.ObtenerCentroIdsParaGestionAsync"/>). El rol Cliente (portal) no ve
/// ninguna: es un artefacto interno de gestión. Un paso de un Centro que no se gestiona no aparece ni cuenta, y
/// una Gestión sin pasos visibles no aparece (nunca «hay pasos que no puedes ver»). Un acceso o Centro dado de
/// baja deja de dar paso visible. Bajo RLS: no cruza Tenants.
/// </para>
/// <para>
/// Ver al Trabajador titular no da a ver su Empresa empleadora: el universo admite su Gestión, pero la cabecera
/// solo nombra a esa Empresa si está en algún alcance de cartera (<c>CabecerasGestionesAutomaticas</c>), la regla de
/// la Semana por Empresa y del Mes.
/// </para>
/// </summary>
public static class PasosVisiblesDeGestionesAutomaticas
{
    public static async Task<List<PasoVisibleGestionAutomatica>> CargarAsync(
        IGestionesQueryContext gestionesContext, IDocumentosQueryContext documentosContext,
        ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
        IAlcanceDatosService alcanceDatos, Guid? gestionId, Guid? documentoId, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return [];

        var trabajadorIds = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var empresaIds = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var centroIds = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);

        var gestiones = gestionesContext.GestionesAutomaticas.AsQueryable();
        if (gestionId is not null)
            gestiones = gestiones.Where(g => g.Id == gestionId);
        if (documentoId is not null)
            gestiones = gestiones.Where(g => g.DocumentoId == documentoId);

        var documentos = documentosContext.Documentos.Where(d => d.TrabajadorId != null || d.EmpresaId != null);
        if (trabajadorIds is not null)
            documentos = documentos.Where(d => d.TrabajadorId == null || trabajadorIds.Contains(d.TrabajadorId.Value));
        if (empresaIds is not null)
            documentos = documentos.Where(d => d.TrabajadorId != null || empresaIds.Contains(d.EmpresaId!.Value));

        var centros = centrosContext.Centros.AsQueryable();
        if (centroIds is not null)
            centros = centros.Where(c => centroIds.Contains(c.Id));

        var filas = await (
                from gestion in gestiones
                join documento in documentos on gestion.DocumentoId equals documento.Id
                join paso in gestionesContext.PasosGestionAutomatica on gestion.Id equals paso.GestionAutomaticaId
                join acreditacion in documentosContext.AcreditacionesDocumentoPlataforma on paso.AcreditacionId equals acreditacion.Id
                join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
                join centro in centros on canal.CentroId equals centro.Id
                join proveedor in proveedoresContext.ProveedoresPlataformaCae on canal.ProveedorPlataformaCaeId equals proveedor.Id into proveedores
                from proveedor in proveedores.DefaultIfEmpty()
                select new
                {
                    GestionId = gestion.Id,
                    AcreditacionId = acreditacion.Id,
                    acreditacion.Estado,
                    acreditacion.FechaVencimientoEnPlataforma,
                    CanalId = canal.Id,
                    Proveedor = proveedor != null ? proveedor.Nombre : null,
                    canal.EtiquetaProposito,
                    CentroId = centro.Id,
                    CentroNombre = centro.Nombre,
                    // Deuda terminológica: la columna legacy del Centro es su Cliente empresarial.
                    ClienteEmpresarialId = centro.ClienteId
                })
            .ToListAsync(cancellationToken);

        return filas
            .Select(f => new PasoVisibleGestionAutomatica(
                f.GestionId, f.AcreditacionId, f.Estado, f.FechaVencimientoEnPlataforma, f.CanalId,
                NombrePlataforma(f.Proveedor, f.EtiquetaProposito), f.CentroId, f.CentroNombre, f.ClienteEmpresarialId))
            .ToList();
    }

    /// <summary>Orden por defecto de los pasos (D-5): por Plataforma CAE, después por Centro.</summary>
    public static IEnumerable<PasoVisibleGestionAutomatica> Ordenar(IEnumerable<PasoVisibleGestionAutomatica> pasos) =>
        pasos
            .OrderBy(p => p.Plataforma, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.CentroNombre, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.AcreditacionId);

    private static string NombrePlataforma(string? proveedor, string etiqueta) =>
        !string.IsNullOrWhiteSpace(proveedor) ? proveedor
        : !string.IsNullOrWhiteSpace(etiqueta) ? etiqueta
        : "Plataforma CAE";
}
