using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;

public record ObtenerDocumentacionEmpresasDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<DocumentacionEmpresaCentroDto>>;
public record DocumentoEmpresaCentroDto(Guid TipoDocumentoId, string Nombre, Guid? DocumentoId,
    EstadoDocumento Estado, DateOnly? FechaVencimiento, bool BloqueaAcceso, DateOnly? FechaEmision = null, DateOnly? EnToleranciaHasta = null,
    IReadOnlyList<AcreditacionResumenDto>? AcreditacionesEnElCentro = null, UltimaReclamacionDocumentoDto? UltimaReclamacion = null);
public record DocumentacionEmpresaCentroDto(Guid EmpresaId, string Nombre, bool DesdeRelacionSubcontrata,
    IReadOnlyList<DocumentoEmpresaCentroDto> Documentos);

/// <summary>
/// Documentación exigida a la Empresa configurada en ESTE Centro y a las Empresas empleadoras con Asignaciones activas.
/// Reutiliza la lectura autorizada de requisitos y las reglas canónicas de documento efectivo y tolerancia.
/// Los query contexts mantienen Tenant propietario y baja lógica; no cambia asignaciones ni requisitos.
/// </summary>
public class ObtenerDocumentacionEmpresasDeCentroQueryHandler(
    ICentrosQueryContext centros, IAsignacionesQueryContext asignaciones, ITrabajadoresQueryContext trabajadores,
    IEmpresasQueryContext empresas, IDocumentosQueryContext documentos, IConfiguracionQueryContext configuracion,
    IAlcanceDatosService alcance, IMediator mediator, ISituacionDocumentosEnCentrosService situacionDocumentos)
    : IRequestHandler<ObtenerDocumentacionEmpresasDeCentroQuery, IReadOnlyList<DocumentacionEmpresaCentroDto>>
{
    public async Task<IReadOnlyList<DocumentacionEmpresaCentroDto>> Handle(ObtenerDocumentacionEmpresasDeCentroQuery request, CancellationToken ct)
    {
        if (!await alcance.CentroVisibleAsync(request.CentroId, ct)) return [];
        var centro = await centros.Centros.FirstOrDefaultAsync(c => c.Id == request.CentroId, ct);
        if (centro is null || centro.GestionCae == CaeManager.Domain.Centros.ModalidadGestionCae.SinGestionCae) return [];
        var requisitos = (await mediator.Send(new ObtenerDocumentacionRequeridaDeCentroQuery(request.CentroId), ct) ?? [])
            .Where(r => r.Aplica && r.AmbitoAplicacion == AmbitoAplicacion.Empresa).ToList();
        var visibles = await alcance.ObtenerEmpresaIdsVisiblesAsync(ct);
        var subcontratasVisibles = await alcance.ObtenerSubcontrataIdsVisiblesAsync(ct);
        var empleadoras = (await (
            from asignacion in asignaciones.Asignaciones
            where asignacion.CentroId == request.CentroId && asignacion.FechaBaja == null
            join trabajador in trabajadores.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join empresa in empresas.Empresas on (trabajador.EmpresaId ?? trabajador.SubcontrataId) equals (Guid?)empresa.Id
            where trabajador.EmpresaId != null
                ? visibles == null || visibles.Contains(empresa.Id)
                : subcontratasVisibles == null || subcontratasVisibles.Contains(empresa.Id)
            select new { empresa.Id, empresa.RazonSocial, DesdeRelacionSubcontrata = trabajador.EmpresaId == null })
            .ToListAsync(ct)).GroupBy(e => e.Id).Select(g => g.First()).ToList();
        // Centro.EmpresaId también puede tener documentos exigidos sin Asignaciones activas;
        // el lector de estado del Centro contempla esa Empresa configurada. No es un tipo de Empresa.
        var principal = await empresas.Empresas.Where(e => e.Id == centro.EmpresaId && (visibles == null || visibles.Contains(e.Id)))
            .Select(e => new { e.Id, e.RazonSocial, DesdeRelacionSubcontrata = false }).ToListAsync(ct);
        empleadoras = empleadoras.Concat(principal).GroupBy(e => e.Id).Select(g => g.First()).ToList();
        if (empleadoras.Count == 0) return [];
        var ids = empleadoras.Select(e => e.Id).ToList();
        var tipos = requisitos.Select(r => r.TipoDocumentoId).ToList();
        var docs = await documentos.Documentos.Operativos()
            .Where(d => d.EmpresaId != null && ids.Contains(d.EmpresaId.Value) && tipos.Contains(d.TipoDocumentoId)).ToListAsync(ct);
        var hoy = DiaDeNegocio.Hoy();
        var situacion = await situacionDocumentos.CargarAsync([request.CentroId], docs.Select(d => d.Id).ToList(), [], [], ct);
        var efectivos = DocumentoEfectivo.UnoPorClave<Documento, (Guid, Guid)>(docs,
            d => (d.EmpresaId!.Value, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento,
            d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, d => d.EsRenuncia, hoy);
        var parametros = await configuracion.ParametrosSistema.SingleAsync(ct);
        var resultado = new List<DocumentacionEmpresaCentroDto>();
        foreach (var empresa in empleadoras)
        {
            var filas = new List<DocumentoEmpresaCentroDto>();
            foreach (var requisito in requisitos)
            {
                efectivos.TryGetValue((empresa.Id, requisito.TipoDocumentoId), out var doc);
                var vigencia = doc is null ? EstadoDocumento.Faltante : CalculadoraEstadoDocumento.Calcular(
                    doc.EstadoVigencia, doc.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                var contextual = doc is null ? (Estado: vigencia, EnToleranciaHasta: (DateOnly?)null) : VigenciaEnCentro.Aplicar(vigencia, doc.EstadoVigencia,
                    doc.FechaVencimiento, doc.FechaEmision, new CondicionesDeAccesoDelCentro(requisito.PeriodicidadEspecialMeses,
                        ReglaBloqueoDeAcceso.ResolverToleranciaDias(requisito.ToleranciaDias, requisito.ToleranciaHeredadaDias)), hoy);
                filas.Add(new(requisito.TipoDocumentoId, requisito.Nombre, doc?.Id,
                    contextual.Estado == EstadoDocumento.SinCaducidad ? EstadoDocumento.Vigente : contextual.Estado, doc?.FechaVencimiento, requisito.BloqueaAcceso, doc?.FechaEmision, contextual.EnToleranciaHasta,
                    doc is null ? null : situacion.AcreditacionesEn(request.CentroId, doc.Id),
                    doc is null ? null : situacion.UltimaReclamacionDe(doc.Id)));
            }
            resultado.Add(new(empresa.Id, empresa.RazonSocial, empresa.DesdeRelacionSubcontrata,
                filas.OrderBy(d => SeveridadEstadoDocumento.Rango(d.Estado)).ThenBy(d => d.Nombre).ToList()));
        }
        return resultado.OrderBy(e => e.Nombre).ToList();
    }
}
