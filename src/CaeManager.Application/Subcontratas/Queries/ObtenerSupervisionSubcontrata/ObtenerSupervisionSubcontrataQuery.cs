using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;

/// <summary>
/// La vista de supervisión de ADR-005 § 2.3: para cada Centro relevante de la
/// Subcontrata, los tipos documentales que ese Centro exige
/// (<c>TipoDocumentoCentro</c> vía <see cref="ResolucionTipoDocumentoCentro"/> —
/// no existe un segundo catálogo de requisitos) con la última verificación
/// externa no eliminada y el estado derivado
/// (<see cref="CalculadoraEstadoSupervision"/>, nunca almacenado).
///
/// Centros relevantes = donde tiene actividad real (trabajadores con
/// asignación activa) ∪ donde ya hay verificaciones registradas. Para la
/// primera verificación de un centro sin nada, <c>CentrosSeleccionables</c>
/// ofrece los centros de los Clientes vinculados a la subcontrata — sin
/// relación directa Subcontrata–Centro en el modelo (decisión explícita,
/// ADR-005 § 2.4).
/// </summary>
public record ObtenerSupervisionSubcontrataQuery(Guid SubcontrataId) : IRequest<SupervisionSubcontrataDto?>;

public record SupervisionSubcontrataDto(
    IReadOnlyList<SupervisionCentroDto> Centros,
    IReadOnlyList<CentroSeleccionableDto> CentrosSeleccionables);

public record SupervisionCentroDto(
    Guid CentroId, string CentroNombre, string ClienteRazonSocial, IReadOnlyList<SupervisionTipoDto> Tipos);

public record SupervisionTipoDto(
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    bool Exigido,
    EstadoSupervision Estado,
    UltimaVerificacionDto? UltimaVerificacion);

public record UltimaVerificacionDto(
    Guid Id,
    DateOnly FechaVerificacion,
    ResultadoVerificacionExterna Resultado,
    DateOnly? ValidoHasta,
    string? Observaciones,
    bool TieneEvidencia,
    string? EvidenciaNombreArchivo);

public record CentroSeleccionableDto(Guid CentroId, string CentroNombre, string ClienteRazonSocial);

public class ObtenerSupervisionSubcontrataQueryHandler(
    ISubcontratasQueryContext subcontratasContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSupervisionSubcontrataQuery, SupervisionSubcontrataDto?>
{
    public async Task<SupervisionSubcontrataDto?> Handle(
        ObtenerSupervisionSubcontrataQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.SubcontrataId, cancellationToken))
            return null;

        // Centros de los Clientes vinculados — candidatos para registrar la
        // primera verificación de un centro sin historial ni actividad.
        //
        // F4.2b: repuntado de SubcontratasClientes a RelacionEmpresarial. El
        // filtro de vigencia es el cambio de comportamiento real: legacy
        // borraba la fila al desvincular, así que "esta subcontrata dejó de
        // trabajar con ese cliente" no era un estado representable; ahora sí
        // lo es, y sin filtrar seguiríamos ofreciendo los centros de un
        // cliente ya desvinculado como candidatos a primera verificación.
        //
        // El discriminador EsCritico != null hace explícita una garantía que
        // hoy presta el JOIN a Centros (Centro.ClienteId siempre referencia
        // una fila ex-Cliente): al declararlo aquí, deja de depender de una
        // tabla ajena — que además es justo la que F5 va a reescribir. De
        // paso ahorra el tercer JOIN, porque centro.ClienteId == clienteReal.Id
        // por construcción.
        var centrosSeleccionables = await (
            from r in empresasContext.RelacionesEmpresariales
            where r.ProveedoraId == request.SubcontrataId && r.VigenciaHasta == null
            join clienteReal in empresasContext.Empresas.Where(e => e.EsCritico != null)
                on r.ClienteId equals clienteReal.Id
            join centro in centrosContext.Centros on clienteReal.Id equals centro.ClienteId
            select new CentroSeleccionableDto(centro.Id, centro.Nombre, clienteReal.RazonSocial))
            .Distinct()
            .ToListAsync(cancellationToken);

        var centroIdsConActividad = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.SubcontrataId == request.SubcontrataId
            select asignacion.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var verificaciones = await subcontratasContext.VerificacionesExternaSubcontrata
            .Where(v => v.SubcontrataId == request.SubcontrataId)
            .Select(v => new
            {
                v.Id,
                v.CentroId,
                v.TipoDocumentoId,
                v.FechaVerificacion,
                v.Resultado,
                v.ValidoHasta,
                v.Observaciones,
                v.EvidenciaArchivoRuta,
                v.EvidenciaNombreArchivo,
                v.CreadoEnUtc
            })
            .ToListAsync(cancellationToken);

        var centroIds = centroIdsConActividad
            .Union(verificaciones.Select(v => v.CentroId))
            .ToList();

        if (centroIds.Count == 0)
            return new SupervisionSubcontrataDto([], OrdenarSeleccionables(centrosSeleccionables));

        var centros = await (
            from centro in centrosContext.Centros
            where centroIds.Contains(centro.Id)
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new { centro.Id, centro.Nombre, ClienteRazonSocial = cliente.RazonSocial })
            .ToListAsync(cancellationToken);

        // El portal del titular exige documentación de la empresa subcontratista
        // y de su personal — los ámbitos Cliente/Vehiculo/Proyecto no describen
        // exigencias a una subcontrata.
        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador || t.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .Select(t => new { t.Id, t.Nombre, t.EsObligatorio, t.Orden })
            .OrderBy(t => t.Orden)
            .ToListAsync(cancellationToken);

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var ultimaPorPar = verificaciones
            .GroupBy(v => (v.CentroId, v.TipoDocumentoId))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.FechaVerificacion).ThenByDescending(v => v.CreadoEnUtc).First());

        var nombreTipoPorId = tiposCandidatos.ToDictionary(t => t.Id, t => t.Nombre);

        var resultado = new List<SupervisionCentroDto>();

        foreach (var centro in centros.OrderBy(c => c.ClienteRazonSocial).ThenBy(c => c.Nombre))
        {
            var tipos = new List<SupervisionTipoDto>();

            foreach (var tipo in tiposCandidatos)
            {
                var exigido = ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, centro.Id, tipo.EsObligatorio);
                var tieneVerificacion = ultimaPorPar.TryGetValue((centro.Id, tipo.Id), out var ultima);

                // Un tipo no exigido sin verificaciones no es una fila: el
                // checklist son las exigencias del Centro más los hechos ya
                // registrados (aunque el requisito se haya excluido después).
                if (!exigido && !tieneVerificacion)
                    continue;

                var estado = CalculadoraEstadoSupervision.Calcular(
                    tieneVerificacion ? ultima!.Resultado : null,
                    tieneVerificacion ? ultima!.ValidoHasta : null,
                    hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);

                tipos.Add(new SupervisionTipoDto(
                    tipo.Id, tipo.Nombre, exigido, estado,
                    tieneVerificacion
                        ? new UltimaVerificacionDto(
                            ultima!.Id, ultima.FechaVerificacion, ultima.Resultado, ultima.ValidoHasta,
                            ultima.Observaciones, ultima.EvidenciaArchivoRuta is not null, ultima.EvidenciaNombreArchivo)
                        : null));
            }

            // Verificaciones de tipos que ya no están en el catálogo candidato
            // (p. ej. tipo cambiado de ámbito) no se muestran — el hecho sigue
            // en la base de datos, la vista se limita al checklist vivo.
            resultado.Add(new SupervisionCentroDto(centro.Id, centro.Nombre, centro.ClienteRazonSocial, tipos));
        }

        return new SupervisionSubcontrataDto(resultado, OrdenarSeleccionables(centrosSeleccionables));
    }

    private static List<CentroSeleccionableDto> OrdenarSeleccionables(List<CentroSeleccionableDto> centros) =>
        centros.OrderBy(c => c.ClienteRazonSocial).ThenBy(c => c.CentroNombre).ToList();
}
