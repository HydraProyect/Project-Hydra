using CaeManager.Application.Asignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros.Queries.ObtenerVehiculosConActividadDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionVehiculosDeCentro;

/// <summary>
/// Respalda la vista «Vehículos» de la pestaña Trabajadores de Centro 360: los Vehículos con actividad en el Centro,
/// cada uno con sus Documentos y el estado de cada uno en las plataformas de ESTE Centro.
///
/// <para>
/// Qué Vehículos: exactamente los de <see cref="ObtenerVehiculosConActividadDeCentroQuery"/> (los de las Empresas
/// empleadoras con algún Trabajador con Asignación activa en el Centro), con el mismo alcance que los Trabajadores
/// del Centro: <see cref="IAlcanceDatosService.CentroVisibleAsync"/> más los filtros de Tenant propietario, baja lógica
/// y RLS de los query contexts. Esta consulta no añade Vehículos: los pide a aquella y solo quita los que no están en
/// <see cref="IAlcanceDatosService.ObtenerVehiculoIdsVisiblesAsync"/>, porque enseña sus Documentos.
/// </para>
///
/// <para>
/// Qué Documentos: el Centro no exige documentación por Vehículo (sus requisitos son de Trabajador y de Empresa), así
/// que se listan los Documentos operativos registrados del Vehículo, uno por tipo (el efectivo, como en el % del
/// Centro). No hay filas «Falta»: no se sabe qué debería existir.
/// </para>
/// </summary>
public record ObtenerDocumentacionVehiculosDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<VehiculoDocumentacionCentroDto>>;

/// <param name="SubcontrataId">La Subcontrata a la que pertenece el Vehículo, si es de una (ver <see cref="VehiculoConActividadDto.SubcontrataId"/>).</param>
/// <param name="EmpresaContratistaNombre">Con <paramref name="SubcontrataId"/>: la Empresa contratista de la relación
/// (ver <see cref="ContratistasDeSubcontratasEnCentro"/>); <c>null</c> si no hay ninguna visible.</param>
/// <param name="Documentos">Uno por tipo, el efectivo, con sus acreditaciones en las plataformas de ESTE Centro.</param>
public record VehiculoDocumentacionCentroDto(
    Guid VehiculoId, string Nombre, string Modelo, string NumeroPlaca, string EmpleadorNombre,
    Guid? SubcontrataId, string? EmpresaContratistaNombre, IReadOnlyList<DocumentoRequeridoDto> Documentos);

public class ObtenerDocumentacionVehiculosDeCentroQueryHandler(
    IMediator mediator, IDocumentosQueryContext documentos, ITiposDocumentoQueryContext tiposDocumento,
    IConfiguracionQueryContext configuracion, ISituacionDocumentosEnCentrosService situacionDocumentos,
    ICentrosQueryContext centros, IAsignacionesQueryContext asignaciones, ITrabajadoresQueryContext trabajadores,
    IEmpresasQueryContext empresas, IAlcanceDatosService alcance)
    : IRequestHandler<ObtenerDocumentacionVehiculosDeCentroQuery, IReadOnlyList<VehiculoDocumentacionCentroDto>>
{
    public async Task<IReadOnlyList<VehiculoDocumentacionCentroDto>> Handle(ObtenerDocumentacionVehiculosDeCentroQuery request, CancellationToken ct)
    {
        // El alcance (Centro visible, filtros de Tenant y baja lógica) lo aplica la consulta de Vehículos con actividad.
        var conActividad = await mediator.Send(new ObtenerVehiculosConActividadDeCentroQuery(request.CentroId), ct);
        if (conActividad.Count == 0) return [];

        // Esta vista enseña los Documentos del Vehículo y deja cambiar su estado en la plataforma: se acota además a
        // los Vehículos visibles (Empresa o Subcontrata visible), el mismo alcance que /documentos y que el comando
        // que cambia el estado. Sin él, una Subcontrata fuera de cartera con un Trabajador asignado aquí enseñaría
        // Documentos que el usuario no ve en ningún otro sitio. null = sin restricción.
        var visibles = await alcance.ObtenerVehiculoIdsVisiblesAsync(ct);
        var vehiculos = visibles is null ? conActividad : conActividad.Where(v => visibles.Contains(v.Id)).ToList();
        if (vehiculos.Count == 0) return [];

        var ids = vehiculos.Select(v => v.Id).ToList();
        var docs = await documentos.Documentos.Operativos()
            .Where(d => d.VehiculoId != null && ids.Contains(d.VehiculoId.Value))
            .Select(d => new { d.Id, VehiculoId = d.VehiculoId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(ct);
        var hoy = DiaDeNegocio.Hoy();
        var efectivos = DocumentoEfectivo.UnoPorClave(docs, d => (d.VehiculoId, d.TipoDocumentoId), d => d.EstadoVigencia,
            d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);
        var tipoIds = efectivos.Values.Select(d => d.TipoDocumentoId).Distinct().ToList();
        var nombresTipo = await tiposDocumento.TiposDocumento.Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, ct);
        var situacion = await situacionDocumentos.CargarAsync([request.CentroId], efectivos.Values.Select(d => d.Id).ToList(), [], [], ct);
        var parametros = await configuracion.ParametrosSistema.SingleAsync(ct);
        var contratistas = await ContratistasDeSubcontratasEnCentro.CargarAsync(request.CentroId,
            vehiculos.Where(v => v.SubcontrataId != null).Select(v => v.SubcontrataId!.Value).ToList(),
            centros, asignaciones, trabajadores, empresas, alcance, ct);

        var porVehiculo = efectivos.Values.ToLookup(d => d.VehiculoId);
        return vehiculos.Select(v => new VehiculoDocumentacionCentroDto(
                v.Id, v.Nombre, v.Modelo, v.NumeroPlaca, v.EmpleadorNombre, v.SubcontrataId,
                v.SubcontrataId is { } subcontrataId ? contratistas.GetValueOrDefault(subcontrataId) : null,
                porVehiculo[v.Id]
                    .Select(d => new DocumentoRequeridoDto(
                        d.Id, d.TipoDocumentoId, nombresTipo.GetValueOrDefault(d.TipoDocumentoId) ?? string.Empty,
                        CalculadoraEstadoDocumento.Calcular(d.EstadoVigencia, d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                        d.FechaVencimiento,
                        AcreditacionesEnElCentro: situacion.AcreditacionesEn(request.CentroId, d.Id),
                        UltimaReclamacion: situacion.UltimaReclamacionDe(d.Id)))
                    .OrderBy(d => SeveridadEstadoDocumento.Rango(d.Estado)).ThenBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCulture)
                    .ToList()))
            .ToList();
    }
}
