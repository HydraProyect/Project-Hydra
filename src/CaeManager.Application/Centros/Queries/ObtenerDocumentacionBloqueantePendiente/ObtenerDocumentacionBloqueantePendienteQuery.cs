using CaeManager.Domain.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;

/// <summary>
/// Mi trabajo: los Trabajadores con Asignación activa a un Centro que hoy NO pueden entrar porque les falta,
/// o tienen vencido, un documento bloqueante (<c>TipoDocumentoCentro.BloqueaAcceso</c>). La regla es única y vive en
/// <see cref="ReglaBloqueoDeAcceso"/> (decisión del propietario, 2026-10-03): un bloqueante ausente y uno vencido
/// bloquean igual; el sujeto es el Trabajador (documento de Trabajador) o su Empresa (documento de Empresa, que
/// bloquea a TODOS los Trabajadores de la Empresa en TODOS los Centros del Tenant). Una fila por Trabajador y Centro
/// bloqueados. El Centro no es el sujeto del bloqueo; qué enseña el Centro con Trabajadores bloqueados es una decisión
/// pendiente, y este resultado no la toma. Sustituye a ObtenerRequisitosDocumentalesPendientesQuery/RequisitoDocumental
/// (retirados): antes era un check manual a nivel de Centro, ahora es automático y por Trabajador.
/// </summary>
public record ObtenerDocumentacionBloqueantePendienteQuery : IRequest<IReadOnlyList<DocumentacionBloqueantePendienteDto>>;

/// <param name="ClienteId">Cliente del Centro — alimenta la agrupación "por situación" del rediseño de Inicio (hallazgo P-03 de la auditoría de producto 2026-08-16). El Centro ya es exacto aquí, así que no hace falta ningún criterio de desambiguación.</param>
/// <param name="EmpresaId">Empresa del Trabajador (<c>Trabajador.EmpresaId</c>) — sub-agrupación Empresa→Trabajador de "Requiere atención" en vocabulario Consultora (GrupoCola). En una fila de ámbito Empresa es la Empresa dueña del requisito, sea cual sea la columna que la guarde.</param>
/// <param name="EsAltaNueva">
/// True cuando el Trabajador no tiene NINGÚN documento (ni siquiera vencido) de los tipos
/// de Trabajador que bloquean acceso en este Centro — nunca llegó a completar el alta, no
/// es que se le haya caducado uno. Señal sin umbral (decisión de producto
/// 2026-08-16): no depende de FechaAlta/CreadoEnUtc, solo de si ya hay algo
/// o no. Distingue "sigue de alta" (visita tradicional, algo ya
/// vigente) de "nunca llegó a entrar" (alta nueva) para dar un tratamiento de
/// UI distinto — ver TipoItemBandejaUi. Nunca es true en una fila de ámbito Empresa.
/// </param>
/// <param name="Ambito">Quién es el sujeto del requisito: el Trabajador o su Empresa (R2).</param>
/// <param name="Situacion">Ausente o Vencido (los dos bloquean igual; solo cambia lo que hay que hacer).</param>
public record DocumentacionBloqueantePendienteDto(
    Guid CentroId, string CentroNombre, Guid TrabajadorId, string TrabajadorNombre,
    Guid TipoDocumentoId, string TipoDocumentoNombre,
    Guid? ClienteId = null, string? ClienteNombre = null,
    Guid? EmpresaId = null, string? EmpresaNombre = null,
    bool EsAltaNueva = false,
    AmbitoAplicacion Ambito = AmbitoAplicacion.Trabajador,
    SituacionDeRequisitoBloqueante Situacion = SituacionDeRequisitoBloqueante.Ausente);

public class ObtenerDocumentacionBloqueantePendienteQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IDocumentosQueryContext documentosContext,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionBloqueantePendienteQuery, IReadOnlyList<DocumentacionBloqueantePendienteDto>>
{
    public async Task<IReadOnlyList<DocumentacionBloqueantePendienteDto>> Handle(
        ObtenerDocumentacionBloqueantePendienteQuery request, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();

        // Todas las filas bloqueantes del Tenant (RLS acota al Tenant), no solo las de los Centros visibles: que un
        // tipo de Empresa sea bloqueante lo decide el Tenant (R2) y alcanza a todos sus Centros; el alcance de
        // cartera solo limita QUÉ filas se devuelven, no si el requisito existe.
        var filasBloqueantes = (await (
            from tc in tiposDocumentoContext.TiposDocumentoCentros
            where tc.Incluido && tc.BloqueaAcceso
            join tipo in tiposDocumentoContext.TiposDocumento on tc.TipoDocumentoId equals tipo.Id
            select new { tc.TipoDocumentoId, tc.CentroId, tipo.AmbitoAplicacion })
            .ToListAsync(cancellationToken))
            .Where(f => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(f.AmbitoAplicacion))
            .ToList();

        if (filasBloqueantes.Count == 0)
            return [];

        // P1-X2: una fila BloqueaAcceso que quedó de cuando el Centro exigía gestión CAE no bloquea nada en un
        // Centro que ya no la requiere (tampoco declara un requisito de Empresa para el resto de Centros).
        var centrosDeFilas = filasBloqueantes.Select(f => f.CentroId).Distinct().ToList();
        var centrosExistentes = await centrosContext.Centros
            .Where(c => centrosDeFilas.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centrosDeFilas, cancellationToken);
        var centrosConGestion = centrosExistentes.Where(id => !sinGestionCae.Contains(id)).ToHashSet();
        filasBloqueantes = filasBloqueantes.Where(f => centrosConGestion.Contains(f.CentroId)).ToList();

        if (filasBloqueantes.Count == 0)
            return [];

        var tiposDeEmpresaBloqueantes = filasBloqueantes
            .Where(f => f.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .Select(f => f.TipoDocumentoId)
            .ToHashSet();

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        bool Visible(Guid centroId) => centroIdsVisibles is null || centroIdsVisibles.Contains(centroId);

        var tiposDeTrabajadorBloqueantesPorCentro = filasBloqueantes
            .Where(f => f.AmbitoAplicacion == AmbitoAplicacion.Trabajador && Visible(f.CentroId))
            .GroupBy(f => f.CentroId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(f => f.TipoDocumentoId).ToHashSet());

        // Con un requisito de Empresa en el Tenant, un Trabajador está bloqueado en cualquier Centro donde esté
        // asignado (R2); sin él, solo importan los Centros con filas de Trabajador.
        var asignacionesQuery = asignacionesContext.Asignaciones.Where(a => a.FechaBaja == null);
        if (tiposDeEmpresaBloqueantes.Count == 0)
        {
            var centrosConFilas = tiposDeTrabajadorBloqueantesPorCentro.Keys.ToList();
            asignacionesQuery = asignacionesQuery.Where(a => centrosConFilas.Contains(a.CentroId));
        }
        else if (centroIdsVisibles is not null)
        {
            var visibles = centroIdsVisibles.ToList();
            asignacionesQuery = asignacionesQuery.Where(a => visibles.Contains(a.CentroId));
        }

        var asignacionesActivas = await (
            from asignacion in asignacionesQuery
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                trabajador.EmpresaId,
                trabajador.SubcontrataId
            })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count > 0 && tiposDeEmpresaBloqueantes.Count > 0)
        {
            // Los Centros sin gestión CAE no exigen documentación a nadie (tampoco por R2).
            var sinGestion = await CentrosSinGestionCae.FiltrarAsync(
                centrosContext, asignacionesActivas.Select(a => a.CentroId), cancellationToken);
            if (sinGestion.Count > 0)
                asignacionesActivas = asignacionesActivas.Where(a => !sinGestion.Contains(a.CentroId)).ToList();
        }

        if (asignacionesActivas.Count == 0)
            return [];

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();
        var empresasDeTrabajadores = asignacionesActivas
            .Select(a => a.EmpresaId ?? a.SubcontrataId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var tiposDeTrabajadorIds = tiposDeTrabajadorBloqueantesPorCentro.Values.SelectMany(t => t).Distinct().ToList();
        var tiposDeEmpresaIds = tiposDeEmpresaBloqueantes.ToList();

        // La vigencia no se decide en SQL: se traen estado y fecha y la regla única los evalúa en memoria.
        var documentos = new List<DocumentoParaBloqueo>();

        if (tiposDeTrabajadorIds.Count > 0)
        {
            var delTrabajador = await documentosContext.Documentos
                .Where(d => d.TrabajadorId != null
                    && trabajadorIds.Contains(d.TrabajadorId!.Value)
                    && tiposDeTrabajadorIds.Contains(d.TipoDocumentoId))
                .Select(d => new { d.TrabajadorId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento })
                .ToListAsync(cancellationToken);
            documentos.AddRange(delTrabajador.Select(d => new DocumentoParaBloqueo(
                d.TrabajadorId, null, d.TipoDocumentoId, VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento))));
        }

        if (tiposDeEmpresaIds.Count > 0 && empresasDeTrabajadores.Count > 0)
        {
            var delaEmpresa = await documentosContext.Documentos
                .Where(d => d.EmpresaId != null
                    && empresasDeTrabajadores.Contains(d.EmpresaId!.Value)
                    && tiposDeEmpresaIds.Contains(d.TipoDocumentoId))
                .Select(d => new { d.EmpresaId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento })
                .ToListAsync(cancellationToken);
            documentos.AddRange(delaEmpresa.Select(d => new DocumentoParaBloqueo(
                null, d.EmpresaId, d.TipoDocumentoId, VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento))));
        }

        var bloqueos = CalculoBloqueoDeAccesoDeTrabajadores.Calcular(
            asignacionesActivas
                .Select(a => new AsignacionParaBloqueo(a.CentroId, a.TrabajadorId, a.EmpresaId ?? a.SubcontrataId))
                .ToList(),
            tiposDeTrabajadorBloqueantesPorCentro,
            tiposDeEmpresaBloqueantes,
            documentos,
            hoy);

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

        var trabajadores = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.First());

        var empresaIdsParaNombre = bloqueos
            .Select(b => b.EmpresaId ?? trabajadores[b.TrabajadorId].EmpresaId)
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
            var empresaId = bloqueo.EmpresaId ?? trabajador.EmpresaId;
            var empresaNombre = empresaId is { } id && nombresPorEmpresa.TryGetValue(id, out var nombreEmpresa)
                ? nombreEmpresa
                : null;

            pendientes.Add(new DocumentacionBloqueantePendienteDto(
                bloqueo.CentroId, centroNombre, bloqueo.TrabajadorId, trabajador.TrabajadorNombre,
                bloqueo.TipoDocumentoId, tipoNombre,
                ClienteId: cliente.Item1, ClienteNombre: cliente.Item2,
                EmpresaId: empresaId, EmpresaNombre: empresaNombre,
                EsAltaNueva: bloqueo.EsAltaNueva,
                Ambito: bloqueo.Ambito, Situacion: bloqueo.Situacion));
        }

        return pendientes.OrderBy(p => p.CentroNombre).ThenBy(p => p.TrabajadorNombre).ToList();
    }
}
