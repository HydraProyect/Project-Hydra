using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerEmpresasProyecto;

public record ObtenerEmpresasProyectoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<EmpresaProyectoDto>>;

/// <param name="DesdeRelacionSubcontrata">Ruta contextual del empleador, no un tipo permanente de Empresa.</param>
public record EmpresaProyectoDto(Guid Id, string RazonSocial, int TecnicosActivos, bool DesdeRelacionSubcontrata,
    FraccionCumplimiento Cumplimiento, EstadoDocumento? PeorEstado);

/// <summary>
/// Empresas empleadoras derivadas de los técnicos activos del Proyecto. La fracción mide exclusivamente
/// los tipos de ámbito Empresa que exige su Centro, nunca la documentación de los técnicos ni el
/// cumplimiento global de la Empresa. Misma autoridad de entrada que ObtenerTecnicosProyecto;
/// también se comprueba el Centro antes de leer sus exigencias. Los query contexts conservan Tenant
/// propietario y baja lógica. No guarda Empresas en el Proyecto ni crea asignaciones.
/// </summary>
public class ObtenerEmpresasProyectoQueryHandler(
    IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IConfiguracionQueryContext configuracionContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresasProyectoQuery, IReadOnlyList<EmpresaProyectoDto>>
{
    public async Task<IReadOnlyList<EmpresaProyectoDto>> Handle(ObtenerEmpresasProyectoQuery request, CancellationToken cancellationToken)
    {
        var proyecto = await proyectosContext.Proyectos.FirstOrDefaultAsync(p => p.Id == request.ProyectoId, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return [];
        if (!await alcanceDatos.CentroVisibleAsync(proyecto.CentroId, cancellationToken)) return [];
        var centro = await centrosContext.Centros.FirstOrDefaultAsync(c => c.Id == proyecto.CentroId, cancellationToken);
        if (centro is null) return [];

        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        var participantes = await (
            from tecnico in proyectosContext.ProyectosTecnicos
            where tecnico.ProyectoId == proyecto.Id && tecnico.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on tecnico.TrabajadorId equals trabajador.Id
            join empresa in empresasContext.Empresas on (trabajador.EmpresaId ?? trabajador.SubcontrataId) equals (Guid?)empresa.Id
            where trabajador.EmpresaId != null
                ? empresaIdsVisibles == null || empresaIdsVisibles.Contains(empresa.Id)
                : subcontrataIdsVisibles == null || subcontrataIdsVisibles.Contains(empresa.Id)
            select new { EmpresaId = empresa.Id, empresa.RazonSocial, TrabajadorId = trabajador.Id, DesdeRelacionSubcontrata = trabajador.EmpresaId == null })
            .ToListAsync(cancellationToken);
        if (participantes.Count == 0) return [];

        var tipos = await tiposDocumentoContext.TiposDocumento.Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa).ToListAsync(cancellationToken);
        var filas = await tiposDocumentoContext.TiposDocumentoCentros.Where(t => t.CentroId == centro.Id).ToListAsync(cancellationToken);
        var porPar = filas.ToDictionary(t => (t.TipoDocumentoId, t.CentroId));
        var sinGestion = await CentrosSinGestionCae.FiltrarAsync(centrosContext, [centro.Id], cancellationToken);
        var exigidos = sinGestion.Contains(centro.Id) ? [] : tipos.Where(t =>
            ResolucionTipoDocumentoCentro.Aplica(porPar, t.Id, centro.Id, t.CuentaParaCumplimiento)).ToList();
        var idsEmpresa = participantes.Select(p => p.EmpresaId).Distinct().ToList();
        var idsTipo = exigidos.Select(t => t.Id).ToList();
        var documentos = await documentosContext.Documentos.Operativos()
            .Where(d => d.EmpresaId != null && idsEmpresa.Contains(d.EmpresaId.Value) && idsTipo.Contains(d.TipoDocumentoId))
            .ToListAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();
        var efectivos = DocumentoEfectivo.UnoPorClave<Documento, (Guid EmpresaId, Guid TipoDocumentoId)>(documentos, d => (d.EmpresaId!.Value, d.TipoDocumentoId),
            d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);
        var tolerancias = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(tiposDocumentoContext, [centro.ClienteId], idsTipo, cancellationToken);
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var resultado = new List<EmpresaProyectoDto>();
        foreach (var grupo in participantes.GroupBy(p => p.EmpresaId))
        {
            var vigencias = new List<EstadoDocumento>();
            var estadosEnCentro = new List<EstadoDocumento>();
            foreach (var tipo in exigidos)
            {
                if (!efectivos.TryGetValue((grupo.Key, tipo.Id), out var documento))
                {
                    vigencias.Add(EstadoDocumento.Faltante);
                    estadosEnCentro.Add(EstadoDocumento.Faltante);
                    continue;
                }
                var estado = CalculadoraEstadoDocumento.Calcular(documento.EstadoVigencia, documento.FechaVencimiento,
                    hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                // La tolerancia se presenta, pero no convierte un vencido en documentación al día.
                vigencias.Add(estado);
                porPar.TryGetValue((tipo.Id, centro.Id), out var fila);
                var condiciones = VigenciaEnCentro.Condiciones(fila, tolerancias.GetValueOrDefault((centro.ClienteId, tipo.Id)));
                var contextual = VigenciaEnCentro.Aplicar(estado, documento.EstadoVigencia, documento.FechaVencimiento,
                    documento.FechaEmision, condiciones, hoy).Estado;
                estadosEnCentro.Add(contextual == EstadoDocumento.SinCaducidad ? EstadoDocumento.Vigente : contextual);
            }
            var primero = grupo.First();
            resultado.Add(new(grupo.Key, primero.RazonSocial, grupo.Select(p => p.TrabajadorId).Distinct().Count(),
                grupo.Any(p => p.DesdeRelacionSubcontrata), CumplimientoDocumental.Evaluar(vigencias),
                estadosEnCentro.Count == 0 ? null : estadosEnCentro.MinBy(SeveridadEstadoDocumento.Rango)));
        }
        return resultado.OrderBy(e => e.PeorEstado is { } estado ? SeveridadEstadoDocumento.Rango(estado) : int.MaxValue)
            .ThenBy(e => e.RazonSocial).ToList();
    }
}
