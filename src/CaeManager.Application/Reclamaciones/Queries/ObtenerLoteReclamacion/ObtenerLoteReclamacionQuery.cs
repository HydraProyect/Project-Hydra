using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Contactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;

/// <summary>
/// Agrupa por Cliente los Documentos de sus Trabajadores (vía Asignación
/// activa a un Centro de ese Cliente — mismo join que
/// ObtenerAlertasQueryHandler.ObtenerFaltantesAsync) que vencen dentro de los
/// próximos 3 meses o ya vencieron, para poder reclamarlos en un único correo
/// por Cliente en vez de uno por Documento. Cubre SOLO los Documentos de
/// Trabajador: los de ámbito Empresa tienen su propia hermana
/// (ObtenerLoteReclamacionEmpresaQuery), porque su titular es el propietario
/// del documento y no hay Asignación ni Centro que recorrer; los de Cliente,
/// Vehículo y Proyecto siguen sin camino de reclamación. Un mismo Trabajador con Asignaciones activas en Centros de
/// varios Clientes (relación Empresa-Cliente N:N, ver DOMAIN.md) puede
/// aparecer reclamado desde más de un Cliente — es el comportamiento
/// correcto: cada titular necesita saberlo para su propio Centro.
///
/// <paramref name="CentroId"/> acota el lote a un único Centro — lo usa
/// Centro 360 para "reclamar documentación de este centro" sin mandar de
/// paso lo pendiente de otros centros del mismo Cliente. Un Centro pertenece
/// a un único Cliente, así que con el filtro puesto el resultado tiene como
/// mucho un elemento. Null (el caso de /documentos) mantiene el
/// comportamiento de siempre: todos los clientes visibles.
///
/// <paramref name="ClienteId"/>, <paramref name="TrabajadorId"/> y
/// <paramref name="TipoDocumentoIds"/> son el eje del selector de lote
/// (FiltroLoteDocumental, DEC-7) para Ambito=Trabajador —
/// ObtenerLoteReclamacionPorFiltroQuery los traduce aquí en vez de duplicar
/// el join: <paramref name="TrabajadorId"/> es la entidad concreta que elige
/// el selector ("todos los documentos de un trabajador en concreto"),
/// <paramref name="ClienteId"/> sigue acotando por titular de la
/// reclamación (uso de Centro 360, no del selector). Los tres opcionales y
/// sin efecto sobre los dos llamadores existentes (ReclamacionesTab, Centro
/// 360) que nunca los pasan.
/// </summary>
public record ObtenerLoteReclamacionQuery(
    Guid? CentroId = null, Guid? ClienteId = null, Guid? TrabajadorId = null, IReadOnlyList<Guid>? TipoDocumentoIds = null)
    : IRequest<IReadOnlyList<LoteReclamacionClienteDto>>;

/// <param name="Destinatarios">
/// A quién le toca cada cosa según la agenda, con nombre, email y el motivo por
/// el que entra ("RLC/TC1", "Contacto predeterminado") — la pantalla los pinta
/// con casillas, así que necesita el ContactoId, no solo el email.
/// Vacío = perfil incompleto: no hay a quién reclamar y el envío se bloquea.
/// </param>
public record LoteReclamacionClienteDto(
    Guid ClienteId,
    string RazonSocialCliente,
    DateTime? UltimaReclamacionFechaUtc,
    IReadOnlyList<DocumentoReclamableDto> Documentos,
    Guid? UltimaReclamacionConversacionId = null,
    IReadOnlyList<DestinatarioAgendaDto>? Destinatarios = null);

/// <param name="TrabajadorId">
/// Null en un lote de ámbito Empresa (ObtenerLoteReclamacionEmpresaQuery): un
/// documento de empresa no cuelga de ningún Trabajador, su propietario es la
/// Empresa titular del propio lote. Nunca null en el lote de Trabajador, donde
/// es justamente lo que distingue una fila de otra.
/// </param>
/// <param name="TrabajadorNombre">Null por el mismo motivo — las dos superficies ocultan la columna "Trabajador" cuando el lote es de ámbito Empresa.</param>
public record DocumentoReclamableDto(
    Guid DocumentoId,
    Guid? TrabajadorId,
    string? TrabajadorNombre,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    DateOnly FechaVencimiento,
    EstadoDocumento Estado);

public class ObtenerLoteReclamacionQueryHandler(
    IConfiguracionQueryContext configuracionContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    IReclamacionesQueryContext reclamacionesContext,
    IAlcanceDatosService alcanceDatos,
    Contactos.IResolucionDestinatariosAgendaService resolucionDestinatarios)
    : IRequestHandler<ObtenerLoteReclamacionQuery, IReadOnlyList<LoteReclamacionClienteDto>>
{
    public async Task<IReadOnlyList<LoteReclamacionClienteDto>> Handle(
        ObtenerLoteReclamacionQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var limiteVentana = hoy.AddMonths(3);

        var filas = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where request.TrabajadorId == null || documento.TrabajadorId == request.TrabajadorId
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            where documento.FechaVencimiento != null && documento.FechaVencimiento <= limiteVentana
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join asignacion in asignacionesContext.Asignaciones on trabajador.Id equals asignacion.TrabajadorId
            where asignacion.FechaBaja == null
            where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            where request.CentroId == null || centro.Id == request.CentroId
            where request.ClienteId == null || centro.ClienteId == request.ClienteId
            where request.TipoDocumentoIds == null || request.TipoDocumentoIds.Contains(tipoDocumento.Id)
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(centro.ClienteId)
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new
            {
                cliente.Id,
                cliente.RazonSocial,
                DocumentoId = documento.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoId = tipoDocumento.Id,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        // GetValueOrDefault sobre un Dictionary<Guid, DateTime> devolvería
        // DateTime.MinValue para un cliente sin reclamaciones previas, no
        // null — de ahí el valor explícitamente nullable aquí.
        //
        // ConversacionId es el de la ÚLTIMA reclamación, que es la que la
        // pestaña muestra ("Última reclamación: hace X"): null si esa salió sin
        // buzón conectado, o si es anterior a que existiera el vínculo.
        var ultimasReclamaciones = await reclamacionesContext.ReclamacionesDocumentales
            // Solo las de titular Cliente: desde que el titular es polimórfico,
            // las de titular Empresa traen ClienteId NULL y sin este filtro
            // caerían todas en un mismo grupo de clave nula, que después se
            // leería como "última reclamación" de un Cliente cualquiera.
            .Where(r => r.ClienteId != null)
            .GroupBy(r => r.ClienteId!.Value)
            .Select(g => new
            {
                ClienteId = g.Key,
                Ultima = (DateTime?)g.Max(r => r.FechaEnvioUtc),
                ConversacionId = g.OrderByDescending(r => r.FechaEnvioUtc).Select(r => r.ConversacionId).FirstOrDefault()
            })
            .ToDictionaryAsync(x => x.ClienteId, x => x, cancellationToken);

        // Sin filtrar por Estado: el filtro SQL de arriba (FechaVencimiento
        // <= limiteVentana) ya acota la ventana de 1 a 3 meses que pidió el
        // usuario. Filtrar además por Proximo/Urgente/Vencido reintroduciría
        // el umbral corto de Alertas (30/15 días por defecto) y dejaría
        // fuera justo los documentos a 2-3 meses que esta ventana existe
        // para capturar con antelación — Estado se calcula solo para
        // mostrarlo como badge informativo en la fila.
        var documentosPorCliente = filas
            .GroupBy(f => new { f.Id, f.RazonSocial })
            .Select(grupoCliente => (
                Cliente: grupoCliente.Key,
                Documentos: grupoCliente
                    .Select(f => new DocumentoReclamableDto(
                        f.DocumentoId, f.TrabajadorId, f.TrabajadorNombre, f.TipoDocumentoId, f.TipoDocumentoNombre,
                        f.FechaVencimiento!.Value,
                        CalculadoraEstadoDocumento.Calcular(
                            f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
                    .OrderBy(d => d.FechaVencimiento)
                    .ToList()))
            .Where(x => x.Documentos.Count > 0)
            .ToList();

        // Destinatarios desde la agenda, no desde los usuarios de portal (ver
        // IResolucionDestinatariosAgendaService): la vista previa debe enseñar
        // exactamente a quién va a salir el correo. Resuelto para todos los
        // Clientes del lote de una vez (ResolverParaClientesAsync) en vez de
        // uno por uno: con CentroId null (caso /documentos) esto puede ser un
        // Cliente por cada email de reclamación pendiente.
        var tipoDocumentoIdsPorCliente = documentosPorCliente
            .ToDictionary(
                x => x.Cliente.Id,
                IReadOnlyList<Guid> (x) => x.Documentos.Select(d => d.TipoDocumentoId).Distinct().ToList());

        var destinatariosPorCliente = await resolucionDestinatarios.ResolverParaClientesAsync(
            request.CentroId, tipoDocumentoIdsPorCliente, cancellationToken);

        var resultado = documentosPorCliente
            .Select(x =>
            {
                var ultima = ultimasReclamaciones.GetValueOrDefault(x.Cliente.Id);
                return new LoteReclamacionClienteDto(
                    x.Cliente.Id,
                    x.Cliente.RazonSocial,
                    ultima?.Ultima,
                    x.Documentos,
                    ultima?.ConversacionId,
                    destinatariosPorCliente.GetValueOrDefault(x.Cliente.Id, []));
            })
            .ToList();

        return resultado
            .OrderByDescending(r => r.Documentos.Any(d => d.Estado == EstadoDocumento.Vencido))
            .ThenBy(r => r.RazonSocialCliente)
            .ToList();
    }
}
