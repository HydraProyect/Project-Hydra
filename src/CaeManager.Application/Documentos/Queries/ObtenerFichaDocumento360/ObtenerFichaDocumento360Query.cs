using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerFichaDocumento360;

/// <summary>
/// Lo que la página «Documento 360» (<c>/documentos/{id}</c>) pinta además de la cabecera
/// (<see cref="ObtenerDocumentoPorIdQuery"/>): «Dónde se exige» y «Versiones». Devuelve <c>null</c> cuando el documento no
/// existe o el usuario no lo ve: la autorización es la de <see cref="ObtenerDocumentoPorIdQuery"/> (alcance de lectura del
/// propietario), no una regla nueva.
///
/// <para>
/// «Dónde se exige» solo existe para documentos de Trabajador (pares Centro × Trabajador × Tipo de
/// <see cref="ICalculoEstadoCentroService.ObtenerParesExigidosAsync"/>, eventos incluidos) y de Empresa (cada Centro con gestión
/// CAE donde la Empresa, o la Subcontrata del Trabajador sin Empresa, tiene un Trabajador con Asignación activa y al que el tipo le aplica,
/// la misma regla que el cumplimiento del Centro). Vehículo, Cliente y Proyecto no tienen exigencia por Centro: lista vacía.
/// </para>
/// </summary>
public record ObtenerFichaDocumento360Query(Guid DocumentoId) : IRequest<FichaDocumento360Dto?>;

/// <param name="Documento">La cabecera, tal como la devuelve <see cref="ObtenerDocumentoPorIdQuery"/>.</param>
/// <param name="Estado">Estado de vigencia del documento (sin la mirada de ningún Centro).</param>
/// <param name="DondeSeExige">Centros visibles que exigen el tipo del documento a su propietario, del peor estado al mejor.</param>
/// <param name="Versiones">Todos los documentos del mismo propietario y tipo: la versión actual primero, luego por emisión descendente.</param>
public record FichaDocumento360Dto(
    DocumentoDetalleDto Documento,
    EstadoDocumento Estado,
    IReadOnlyList<CentroQueExigeDocumentoDto> DondeSeExige,
    IReadOnlyList<VersionDocumentoDto> Versiones);

/// <param name="ClienteNombre">Cliente empresarial titular del Centro.</param>
/// <param name="PlataformaNombre">Plataforma del canal principal de gestión documental del Centro; <c>null</c> si no tiene.</param>
/// <param name="Estado">El estado de ESTE documento visto desde el Centro (puede ser «En tolerancia»).</param>
public record CentroQueExigeDocumentoDto(
    Guid CentroId,
    string CentroNombre,
    string ClienteNombre,
    string? PlataformaNombre,
    EstadoDocumento Estado,
    DateOnly? EnToleranciaHasta,
    bool BloqueaAcceso,
    int ToleranciaDias);

/// <param name="Estado">Estado de vigencia del documento; solo se muestra en la versión actual (<paramref name="EsActual"/>).</param>
public record VersionDocumentoDto(
    Guid Id,
    string NombreArchivo,
    bool TieneArchivo,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    bool EsActual,
    EstadoDocumento Estado,
    string? Comentarios);

public class ObtenerFichaDocumento360QueryHandler(
    IMediator mediator,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IConfiguracionQueryContext configuracionContext,
    ICalculoEstadoCentroService calculoEstadoCentro,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerFichaDocumento360Query, FichaDocumento360Dto?>
{
    private sealed record DocumentoBase(
        Guid Id, Guid TipoDocumentoId, Guid? TrabajadorId, Guid? ClienteId, Guid? EmpresaId, Guid? VehiculoId, Guid? ProyectoId,
        EstadoVigenciaDocumento EstadoVigencia, DateOnly? FechaVencimiento, DateOnly FechaEmision);

    public async Task<FichaDocumento360Dto?> Handle(ObtenerFichaDocumento360Query request, CancellationToken cancellationToken)
    {
        // Misma puerta que la cabecera: sin ella (no existe, o fuera de la cartera) no se revela nada más.
        var detalle = await mediator.Send(new ObtenerDocumentoPorIdQuery(request.DocumentoId), cancellationToken);
        if (detalle is null) return null;

        var documento = await documentosContext.Documentos
            .Where(d => d.Id == request.DocumentoId)
            .Select(d => new DocumentoBase(
                d.Id, d.TipoDocumentoId, d.TrabajadorId, d.ClienteId, d.EmpresaId, d.VehiculoId, d.ProyectoId,
                d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision))
            .FirstAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();
        var estado = CalculadoraEstadoDocumento.Calcular(
            documento.EstadoVigencia, documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);

        var versiones = await CargarVersionesAsync(
            documento, detalle, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, cancellationToken);
        // Un documento sustituido es historial: el cumplimiento de los Centros lo mide el vigente, no este.
        IReadOnlyList<CentroQueExigeDocumentoDto> centros = detalle.SustitutoId is not null
            ? []
            : await CargarDondeSeExigeAsync(documento, detalle.Ambito, estado, hoy, cancellationToken);

        return new FichaDocumento360Dto(detalle, estado, centros, versiones);
    }

    private async Task<IReadOnlyList<VersionDocumentoDto>> CargarVersionesAsync(
        DocumentoBase documento, DocumentoDetalleDto detalle, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        CancellationToken cancellationToken)
    {
        var mismas = await documentosContext.Documentos
            .Where(d => d.TipoDocumentoId == documento.TipoDocumentoId
                && d.TrabajadorId == documento.TrabajadorId
                && d.ClienteId == documento.ClienteId
                && d.EmpresaId == documento.EmpresaId
                && d.VehiculoId == documento.VehiculoId
                && d.ProyectoId == documento.ProyectoId)
            .ToListAsync(cancellationToken);

        // El mismo nombre canónico que la descarga de la cabecera (ObtenerDocumentoPorIdQuery): en un Trabajador, «Apellidos Nombre».
        var nombreEnArchivo = documento.TrabajadorId is { } trabajadorId
            ? await trabajadoresContext.Trabajadores
                .Where(t => t.Id == trabajadorId)
                .Select(t => t.Apellidos + " " + t.Nombre)
                .FirstAsync(cancellationToken)
            : detalle.PropietarioNombre;

        // El historial se ve a propósito: se leen también las versiones que otra sustituyó, y «versión actual» es el documento operativo.
        return mismas
            .OrderByDescending(DocumentoOperativo.Es)
            .ThenByDescending(d => d.FechaEmision)
            .ThenByDescending(d => d.CreadoEnUtc)
            .ThenBy(d => d.Id)
            .Select(d =>
            {
                // Como en la cabecera: la más antigua del mismo día conserva el nombre sin sufijo, las siguientes llevan _v2, _v3…
                var ordinal = 1 + mismas.Count(o => o.Id != d.Id && o.FechaEmision == d.FechaEmision
                    && (o.CreadoEnUtc < d.CreadoEnUtc || (o.CreadoEnUtc == d.CreadoEnUtc && o.Id.CompareTo(d.Id) < 0)));
                return new VersionDocumentoDto(
                    d.Id,
                    NombreArchivoDocumento.Suelto(nombreEnArchivo, detalle.TipoDocumentoNombre, d.FechaEmision, ordinal),
                    d.ArchivoUrl is not null,
                    d.FechaEmision,
                    d.FechaVencimiento,
                    EsActual: DocumentoOperativo.Es(d),
                    CalculadoraEstadoDocumento.Calcular(d.EstadoVigencia, d.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias),
                    d.Comentarios);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<CentroQueExigeDocumentoDto>> CargarDondeSeExigeAsync(
        DocumentoBase documento, AmbitoAplicacion ambito, EstadoDocumento estado, DateOnly hoy, CancellationToken cancellationToken)
    {
        if (ambito is not (AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa)) return [];

        // Alcance de cartera: null = sin restricción (todos los Centros del Tenant, que acota RLS).
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var centros = await (centrosVisibles is null ? centrosContext.Centros : centrosContext.Centros.Where(c => centrosVisibles.Contains(c.Id)))
            .Select(c => new { c.Id, c.Nombre, ClienteEmpresarialId = c.ClienteId })
            .ToListAsync(cancellationToken);
        if (centros.Count == 0) return [];

        var centroIds = centros.Select(c => c.Id).ToList();
        var exigidos = ambito == AmbitoAplicacion.Trabajador
            ? await CentrosQueExigenAlTrabajadorAsync(documento, centroIds, cancellationToken)
            : await CentrosQueExigenALaEmpresaAsync(documento, centroIds, cancellationToken);
        if (exigidos.Count == 0) return [];

        var filasDeCentro = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => tc.TipoDocumentoId == documento.TipoDocumentoId && exigidos.Contains(tc.CentroId))
                .ToListAsync(cancellationToken))
            .ToDictionary(tc => tc.CentroId);
        var clientesEmpresariales = centros.Where(c => exigidos.Contains(c.Id)).Select(c => c.ClienteEmpresarialId).Distinct().ToList();
        var toleranciasDeClientes = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, clientesEmpresariales, [documento.TipoDocumentoId], cancellationToken);
        var nombresDeClientes = await empresasContext.Empresas
            .Where(e => clientesEmpresariales.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);
        var plataformas = await PlataformaPrincipalPorCentroAsync(exigidos, cancellationToken);

        return centros
            .Where(c => exigidos.Contains(c.Id))
            .Select(c =>
            {
                var fila = filasDeCentro.GetValueOrDefault(c.Id);
                var condiciones = VigenciaEnCentro.Condiciones(
                    fila, toleranciasDeClientes.TryGetValue((c.ClienteEmpresarialId, documento.TipoDocumentoId), out var dias) ? dias : null);
                var (estadoEnCentro, enToleranciaHasta) = VigenciaEnCentro.Aplicar(
                    estado, documento.EstadoVigencia, documento.FechaVencimiento, documento.FechaEmision, condiciones, hoy);
                return new CentroQueExigeDocumentoDto(
                    c.Id, c.Nombre, nombresDeClientes.GetValueOrDefault(c.ClienteEmpresarialId, string.Empty),
                    plataformas.GetValueOrDefault(c.Id), estadoEnCentro, enToleranciaHasta,
                    fila is { Incluido: true, BloqueaAcceso: true }, condiciones.ToleranciaDias);
            })
            .OrderBy(c => EstadoTipoDocumentoGravedad(c.Estado))
            .ThenBy(c => c.CentroNombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<HashSet<Guid>> CentrosQueExigenAlTrabajadorAsync(
        DocumentoBase documento, List<Guid> centroIds, CancellationToken cancellationToken) =>
        (await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken))
            .Where(p => p.TrabajadorId == documento.TrabajadorId && p.TipoDocumentoId == documento.TipoDocumentoId)
            .Select(p => p.CentroId)
            .ToHashSet();

    private async Task<HashSet<Guid>> CentrosQueExigenALaEmpresaAsync(
        DocumentoBase documento, List<Guid> centroIds, CancellationToken cancellationToken)
    {
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centroIds, cancellationToken);
        var conGestionCae = centroIds.Where(id => !sinGestionCae.Contains(id)).ToList();
        if (conGestionCae.Count == 0) return [];

        var empresaId = documento.EmpresaId;
        var centrosDeLaEmpresa = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && conGestionCae.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where (trabajador.EmpresaId ?? trabajador.SubcontrataId) == empresaId
            select asignacion.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (centrosDeLaEmpresa.Count == 0) return [];

        var requerido = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == documento.TipoDocumentoId)
            .Select(t => t.Requerido == RequisitoDocumental.Si)
            .FirstAsync(cancellationToken);
        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => tc.TipoDocumentoId == documento.TipoDocumentoId && centrosDeLaEmpresa.Contains(tc.CentroId))
                .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        return centrosDeLaEmpresa
            .Where(centroId => ResolucionTipoDocumentoCentro.Aplica(filasPorPar, documento.TipoDocumentoId, centroId, requerido))
            .ToHashSet();
    }

    /// <summary>La plataforma del canal principal de cada Centro; sin canal principal con plataforma, el Centro no aparece.</summary>
    private async Task<Dictionary<Guid, string>> PlataformaPrincipalPorCentroAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        var canales = await centrosContext.CanalesGestionDocumental
            .Where(c => centroIds.Contains(c.CentroId) && c.EsPrincipal && c.ProveedorPlataformaCaeId != null)
            .Select(c => new { c.CentroId, ProveedorId = c.ProveedorPlataformaCaeId!.Value })
            .ToListAsync(cancellationToken);
        if (canales.Count == 0) return [];

        var proveedorIds = canales.Select(c => c.ProveedorId).Distinct().ToList();
        var nombres = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return canales
            .Where(c => nombres.ContainsKey(c.ProveedorId))
            .GroupBy(c => c.CentroId)
            .ToDictionary(g => g.Key, g => nombres[g.OrderBy(c => c.ProveedorId).First().ProveedorId]);
    }

    private static int EstadoTipoDocumentoGravedad(EstadoDocumento estado) =>
        CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento.EstadoTipoDocumentoCalculo.Gravedad(estado);
}
