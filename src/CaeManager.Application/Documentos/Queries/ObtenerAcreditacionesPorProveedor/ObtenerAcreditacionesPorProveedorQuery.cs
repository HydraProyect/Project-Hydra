using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;

/// <summary>
/// Cierra el hallazgo P-04 de la auditoría de producto 2026-08-16 en su
/// forma completa: <see cref="Dashboard.Queries.ObtenerPendientePorPlataformaQuery"/>
/// ya dice "Nalanda: 14 pendientes de subir · 3 rechazados" en Inicio, pero
/// no hay ningún sitio donde ver CUÁLES son esos 14 documentos ni marcarlos
/// — esta query es el drill-down real: Proveedor → Cliente (del Centro del
/// canal) → Documento, con el último motivo de rechazo si lo hay. Mismo
/// alcance por Centro que ObtenerPendientePorPlataformaQuery (la unidad real
/// de "dónde hay que subir esto" es el CanalGestionDocumental).
///
/// Segundo consumidor (Incremento 2 del MVP1 de extensión de navegador, ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <c>AcreditacionesPendientesEndpoints</c> expone esta misma query sin
/// duplicar su alcance por cartera ni su agrupación por proveedor — solo
/// necesitó dos campos más en <see cref="AcreditacionDrillDownDto"/> que la
/// UI de drill-down no pedía (<see cref="AcreditacionDrillDownDto.CanalGestionDocumentalId"/>
/// para el Incremento 3, y <see cref="AcreditacionDrillDownDto.TrabajadorDni"/>
/// para el emparejamiento de identidad de la propia extensión).
///
/// <see cref="ProveedorAcreditacionesDto.ProveedorActivo"/> (MVP2, § 14.5): el
/// *kill switch* remoto de la extensión. Deliberadamente NO se filtran aquí
/// los proveedores inactivos — la cartera pendiente sigue existiendo en Hydra
/// aunque el conector de la extensión esté apagado; es la extensión quien
/// decide, con este dato, si ofrece o no el botón "Subir" (ver
/// <c>extension/popup.js</c>). Bloquear la injección en sí no puede vivir en
/// esta query: ocurre enteramente en el navegador del gestor, fuera del
/// alcance de cualquier respuesta HTTP.
/// </summary>
/// <param name="IncluirAceptadas">
/// Por defecto la consulta devuelve solo lo que hay que trabajar —lo que
/// falta subir y lo que la plataforma rechazó—, que es lo que necesitan la
/// Bandeja y la extensión de navegador. El drill-down por
/// plataforma pide además las aceptadas, porque es la única pantalla desde la
/// que se puede anotar hasta cuándo vale un documento allí.
///
/// <para>
/// Se incluyen TODAS las aceptadas, no solo las que tienen la vigencia sin
/// confirmar o vencida. Acotarlo a esas dos dejaba fuera precisamente el caso
/// que hay que poder arreglar: una fecha futura mal tecleada, que no se podría
/// corregir hasta que venciera. La única salida habría sido rechazar y volver a
/// aceptar, escribiendo un rechazo que nunca ocurrió en un historial que es
/// inmutable a propósito.
/// </para>
/// </param>
/// <param name="IncluirSubidas">
/// Las acreditaciones <c>Subida</c> —ya enviadas, esperando la respuesta de la
/// plataforma— no son trabajo pendiente, así que la Bandeja y la extensión no
/// las piden. El drill-down por plataforma sí, porque es donde el Gestor CAE
/// registra esa respuesta (Aceptada o Rechazada): sin ellas, marcar «subido»
/// hacía desaparecer la fila y dejaba el estado sin salida.
/// </param>
public record ObtenerAcreditacionesPorProveedorQuery(bool IncluirAceptadas = false, bool IncluirSubidas = false)
    : IRequest<IReadOnlyList<ProveedorAcreditacionesDto>>;

public record ProveedorAcreditacionesDto(
    Guid ProveedorPlataformaCaeId, string ProveedorNombre, string ProveedorCodigo,
    IReadOnlyList<ClienteAcreditacionesDto> Clientes, bool ProveedorActivo = true);

public record ClienteAcreditacionesDto(Guid ClienteId, string ClienteNombre, IReadOnlyList<AcreditacionDrillDownDto> Documentos);

public record AcreditacionDrillDownDto(
    Guid AcreditacionId, Guid DocumentoId, string PropietarioNombre, string TipoDocumentoNombre,
    EstadoAcreditacion Estado, string? UltimoMotivoRechazo,
    Guid? TrabajadorId = null, Guid? EmpresaId = null, Guid? CentroId = null, Guid? TipoDocumentoId = null,
    Guid? CanalGestionDocumentalId = null, string? TrabajadorDni = null,
    EstadoVigenciaEnPlataforma EstadoVigencia = EstadoVigenciaEnPlataforma.SinConfirmar,
    DateOnly? FechaVencimientoEnPlataforma = null);

public class ObtenerAcreditacionesPorProveedorQueryHandler(
    IDocumentosQueryContext documentosContext, ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAcreditacionesPorProveedorQuery, IReadOnlyList<ProveedorAcreditacionesDto>>
{
    public async Task<IReadOnlyList<ProveedorAcreditacionesDto>> Handle(
        ObtenerAcreditacionesPorProveedorQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN, no de lectura: esta consulta lleva el NIF del
        // Trabajador y es el artefacto interno con el que el Gestor CAE sube a
        // la plataforma del cliente. El alcance de lectura le daría al rol
        // Cliente (usuario de portal) los Centros de su propio Cliente y, con
        // ellos, el DNI de los Trabajadores de las contratistas — ver
        // ObtenerCentroIdsParaGestionAsync (REC-153).
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        var incluirAceptadas = request.IncluirAceptadas;
        var incluirSubidas = request.IncluirSubidas;

        var canalesQuery = centrosContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma);
        if (centroIdsVisibles is not null)
            canalesQuery = canalesQuery.Where(c => centroIdsVisibles.Contains(c.CentroId));

        var filas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.Estado == EstadoAcreditacion.PendienteDeSubir
                  || acreditacion.Estado == EstadoAcreditacion.Rechazada
                  || (incluirAceptadas && acreditacion.Estado == EstadoAcreditacion.Aceptada)
                  || (incluirSubidas && acreditacion.Estado == EstadoAcreditacion.Subida)
            join canal in canalesQuery on acreditacion.CanalGestionDocumentalId equals canal.Id
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            join documento in documentosContext.Documentos on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                acreditacion.Id,
                acreditacion.DocumentoId,
                acreditacion.Estado,
                acreditacion.EstadoVigencia,
                acreditacion.FechaVencimientoEnPlataforma,
                ProveedorId = canal.ProveedorPlataformaCaeId,
                CanalGestionDocumentalId = canal.Id,
                CentroId = centro.Id,
                centro.ClienteId,
                documento.TrabajadorId,
                documento.EmpresaId,
                documento.TipoDocumentoId,
                TipoDocumentoNombre = tipoDocumento.Nombre
            })
            .ToListAsync(cancellationToken);

        if (filas.Count == 0) return [];

        var proveedorIds = filas.Where(f => f.ProveedorId is not null).Select(f => f.ProveedorId!.Value).Distinct().ToList();
        var proveedores = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => (p.Nombre, p.Codigo, p.Activo), cancellationToken);

        // Centro.ClienteId ya apunta a Empresas (F3): el "Cliente" dueño del Centro
        // se resuelve contra Empresas, no contra la tabla Clientes congelada.
        var clienteIds = filas.Select(f => f.ClienteId).Distinct().ToList();
        var clientes = await empresasContext.Empresas
            .Where(e => clienteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var trabajadorIds = filas.Where(f => f.TrabajadorId is not null).Select(f => f.TrabajadorId!.Value).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => (Nombre: t.Nombre + " " + t.Apellidos, t.Dni), cancellationToken);

        var empresaIds = filas.Where(f => f.EmpresaId is not null).Select(f => f.EmpresaId!.Value).Distinct().ToList();
        var empresas = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var acreditacionIds = filas.Select(f => f.Id).ToList();
        var motivosPorAcreditacion = await documentosContext.RechazosAcreditacionDocumentoPlataforma
            .Where(r => acreditacionIds.Contains(r.AcreditacionId))
            .OrderByDescending(r => r.FechaUtc)
            .GroupBy(r => r.AcreditacionId)
            .Select(g => new { AcreditacionId = g.Key, Motivo = g.First().MotivoLiteral })
            .ToDictionaryAsync(x => x.AcreditacionId, x => x.Motivo, cancellationToken);

        string PropietarioNombre(Guid? trabajadorId, Guid? empresaId) =>
            trabajadorId is { } tId && trabajadores.TryGetValue(tId, out var trabajador) ? trabajador.Nombre
            : empresaId is { } eId && empresas.TryGetValue(eId, out var nombreEmpresa) ? nombreEmpresa
            : "—";

        // Solo un Documento de Trabajador tiene NIF/NIE que emparejar — el de
        // Empresa no lo necesita (ARQUITECTURA-INTEGRACIONES.md § 14.5 del
        // repositorio de negocio: la extensión empareja por NIF, nunca por
        // nombre, para no subir el documento de un trabajador a la ficha de
        // otro).
        string? TrabajadorDni(Guid? trabajadorId) =>
            trabajadorId is { } tId && trabajadores.TryGetValue(tId, out var trabajador) ? trabajador.Dni : null;

        return filas
            .Where(f => f.ProveedorId is not null)
            .GroupBy(f => f.ProveedorId!.Value)
            .Select(porProveedor =>
            {
                var (nombreProveedor, codigoProveedor, proveedorActivo) = proveedores.GetValueOrDefault(porProveedor.Key, ("Plataforma", "", true));
                var porCliente = porProveedor
                    .GroupBy(f => f.ClienteId)
                    .Select(g => new ClienteAcreditacionesDto(
                        g.Key,
                        clientes.GetValueOrDefault(g.Key, "—"),
                        g.Select(f => new AcreditacionDrillDownDto(
                                f.Id, f.DocumentoId, PropietarioNombre(f.TrabajadorId, f.EmpresaId), f.TipoDocumentoNombre,
                                f.Estado, motivosPorAcreditacion.GetValueOrDefault(f.Id),
                                f.TrabajadorId, f.EmpresaId, f.CentroId, f.TipoDocumentoId,
                                f.CanalGestionDocumentalId, TrabajadorDni(f.TrabajadorId),
                                f.EstadoVigencia, f.FechaVencimientoEnPlataforma))
                            .OrderBy(d => d.PropietarioNombre)
                            .ToList()))
                    .OrderBy(c => c.ClienteNombre)
                    .ToList();

                return new ProveedorAcreditacionesDto(porProveedor.Key, nombreProveedor, codigoProveedor, porCliente, proveedorActivo);
            })
            .OrderByDescending(p => p.Clientes.Sum(c => c.Documentos.Count(d => d.Estado == EstadoAcreditacion.PendienteDeSubir)))
            .ToList();
    }
}
