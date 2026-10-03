using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;

/// <summary>
/// Mi trabajo: los Trabajadores con Asignación activa a un Centro que hoy NO pueden entrar a ESE Centro porque les falta,
/// o ya no vale con las condiciones de ese Centro, un documento bloqueante (<c>TipoDocumentoCentro.BloqueaAcceso</c>). La
/// regla es única y vive en <see cref="ReglaBloqueoDeAcceso"/>, evaluada POR CENTRO por
/// <see cref="IEvaluacionDeAccesoPorCentroService"/> (decisión del propietario del producto, 2026-10-03, y su corrección de la
/// tarde): un bloqueante ausente y uno vencido bloquean igual, con la vigencia propia y la tolerancia de ese Centro; el
/// sujeto es el Trabajador (documento de Trabajador) o todos los Trabajadores de su Empresa (documento de Empresa), siempre
/// y solo en los Centros que exigen ese documento. Una fila por Trabajador, Centro y requisito. «Bloqueado» es un estado del
/// Trabajador, nunca del Centro. Un Trabajador sin documentación es una fila como cualquier otra (no hay «alta nueva» exenta).
/// Sustituye a ObtenerRequisitosDocumentalesPendientesQuery/RequisitoDocumental (retirados): antes era un check manual a nivel
/// de Centro, ahora es automático y por Trabajador.
/// </summary>
/// <param name="CentroId">Limita el resultado a ese Centro (el detalle por Trabajador del Centro 360); <c>null</c> = todos los visibles (Mi trabajo).</param>
public record ObtenerDocumentacionBloqueantePendienteQuery(Guid? CentroId = null)
    : IRequest<IReadOnlyList<DocumentacionBloqueantePendienteDto>>;

/// <param name="ClienteId">Cliente del Centro — alimenta la agrupación "por situación" del rediseño de Inicio (hallazgo P-03 de la auditoría de producto 2026-08-16). El Centro ya es exacto aquí, así que no hace falta ningún criterio de desambiguación.</param>
/// <param name="EmpresaId">Empresa del Trabajador (<c>Trabajador.EmpresaId</c>) — sub-agrupación Empresa→Trabajador de "Requiere atención" en vocabulario Consultora (GrupoCola). En una fila de ámbito Empresa es la Empresa dueña del requisito, sea cual sea la columna que la guarde.</param>
/// <param name="Ambito">Quién es el sujeto del requisito: el Trabajador o su Empresa.</param>
/// <param name="Situacion">Ausente o Vencido (los dos bloquean igual; solo cambia lo que hay que hacer).</param>
/// <param name="ToleranciaDias">Tolerancia que rige en este Centro para el Tipo; con valor mayor que 0, un Vencido es un fin de tolerancia.</param>
/// <param name="VencimientoEfectivo">Cuándo venció en este Centro el documento (con su vigencia propia); <c>null</c> si está ausente.</param>
public record DocumentacionBloqueantePendienteDto(
    Guid CentroId, string CentroNombre, Guid TrabajadorId, string TrabajadorNombre,
    Guid TipoDocumentoId, string TipoDocumentoNombre,
    Guid? ClienteId = null, string? ClienteNombre = null,
    Guid? EmpresaId = null, string? EmpresaNombre = null,
    AmbitoAplicacion Ambito = AmbitoAplicacion.Trabajador,
    SituacionDeRequisitoBloqueante Situacion = SituacionDeRequisitoBloqueante.Ausente,
    int ToleranciaDias = 0,
    DateOnly? VencimientoEfectivo = null);

public class ObtenerDocumentacionBloqueantePendienteQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    IEvaluacionDeAccesoPorCentroService evaluacionDeAcceso)
    : IRequestHandler<ObtenerDocumentacionBloqueantePendienteQuery, IReadOnlyList<DocumentacionBloqueantePendienteDto>>
{
    public async Task<IReadOnlyList<DocumentacionBloqueantePendienteDto>> Handle(
        ObtenerDocumentacionBloqueantePendienteQuery request, CancellationToken cancellationToken)
    {
        var evaluacion = await evaluacionDeAcceso.EvaluarAsync(
            request.CentroId is { } centroSolicitado ? [centroSolicitado] : null, cancellationToken);

        var bloqueos = evaluacion.Requisitos
            .Where(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion))
            .ToList();

        if (bloqueos.Count == 0)
            return [];

        var centroIds = bloqueos.Select(b => b.CentroId).Distinct().ToList();
        var tipoIds = bloqueos.Select(b => b.TipoDocumentoId).Distinct().ToList();

        var centros = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToDictionaryAsync(c => c.Id, c => c.Nombre, cancellationToken);

        // Centro.ClienteId repunta contra Empresas desde F3b: "Cliente" es una
        // Empresa contraparte (Empresa.CrearComoCliente).
        var clientesPorCentro = await (
            from centro in centrosContext.Centros
            where centroIds.Contains(centro.Id)
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new { centro.Id, ClienteId = cliente.Id, cliente.RazonSocial })
            .ToDictionaryAsync(x => x.Id, x => (x.ClienteId, x.RazonSocial), cancellationToken);

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var trabajadores = evaluacion.Asignaciones
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.First());

        var empresaIdsParaNombre = bloqueos
            .Select(b => b.EmpresaId ?? trabajadores[b.TrabajadorId].EmpresaPropiaId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var nombresPorEmpresa = empresaIdsParaNombre.Count == 0
            ? new Dictionary<Guid, string>()
            : await empresasContext.Empresas
                .Where(e => empresaIdsParaNombre.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var pendientes = new List<DocumentacionBloqueantePendienteDto>();
        foreach (var bloqueo in bloqueos)
        {
            if (!centros.TryGetValue(bloqueo.CentroId, out var centroNombre)) continue;
            if (!tipos.TryGetValue(bloqueo.TipoDocumentoId, out var tipoNombre)) continue;

            var trabajador = trabajadores[bloqueo.TrabajadorId];
            var cliente = clientesPorCentro.TryGetValue(bloqueo.CentroId, out var c) ? c : ((Guid?)null, (string?)null);
            var empresaId = bloqueo.EmpresaId ?? trabajador.EmpresaPropiaId;
            var empresaNombre = empresaId is { } id && nombresPorEmpresa.TryGetValue(id, out var nombreEmpresa)
                ? nombreEmpresa
                : null;

            pendientes.Add(new DocumentacionBloqueantePendienteDto(
                bloqueo.CentroId, centroNombre, bloqueo.TrabajadorId, trabajador.TrabajadorNombre,
                bloqueo.TipoDocumentoId, tipoNombre,
                ClienteId: cliente.Item1, ClienteNombre: cliente.Item2,
                EmpresaId: empresaId, EmpresaNombre: empresaNombre,
                Ambito: bloqueo.Ambito, Situacion: bloqueo.Resultado.Situacion,
                ToleranciaDias: bloqueo.ToleranciaDias, VencimientoEfectivo: bloqueo.Resultado.VencimientoEfectivo));
        }

        return pendientes.OrderBy(p => p.CentroNombre).ThenBy(p => p.TrabajadorNombre).ToList();
    }
}
