using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesDelDocumento;

/// <summary>
/// La sección «Plataformas CAE» de la vista rápida de un Documento (D-23, lienzo «Documentos · listado sin pestañas»): cada
/// acreditación del Documento en una Plataforma CAE, con su Cliente empresarial y su Centro, su estado y, solo para quien
/// gestiona el Centro del canal, el último motivo de rechazo y la dirección del portal.
///
/// <para>
/// Mismo alcance que las filas del listado agrupado por Plataforma CAE (<c>ObtenerDocumentosQuery.RepartirEnGruposAsync</c>):
/// el Documento tiene que ser legible (<see cref="DocumentoAlcanceExtensions.DocumentoLegibleAsync(IAlcanceDatosService, Guid, IDocumentosQueryContext, IProyectosQueryContext, CancellationToken)"/>),
/// solo cuentan los canales con Plataforma CAE de Centros visibles y con gestión CAE, y el motivo y la dirección se cortan con
/// <see cref="IAlcanceDatosService.ObtenerCentroIdsParaGestionAsync"/>, que decide por el rol efectivo: el rol Cliente recibe la
/// lista vacía, así que ve el estado pero ni el motivo ni la dirección viajan en el DTO (decisión de Chris del 2026-11-12, opción B).
/// D-3: solo cambia qué se muestra; cada Command del menú «Acreditación ▾» comprueba su propia autorización.
/// </para>
/// </summary>
public record ObtenerAcreditacionesDelDocumentoQuery(Guid DocumentoId) : IRequest<AcreditacionesDelDocumentoDto?>;

/// <param name="EstadoDocumental">El estado documental del Documento en TALVEG, para el panel «Gestionar acreditación…».</param>
/// <param name="Acreditaciones">Ordenadas por Plataforma CAE, Cliente empresarial y Centro.</param>
public record AcreditacionesDelDocumentoDto(EstadoDocumento EstadoDocumental, IReadOnlyList<AcreditacionDelDocumentoDto> Acreditaciones);

/// <param name="PlataformaCodigo">El código de la Plataforma CAE, para su logotipo (<c>LogoPlataforma</c>); null si no lo tiene.</param>
/// <param name="ClienteEmpresarialNombre">El Cliente empresarial del Centro del canal, no el Tenant propietario.</param>
/// <param name="UltimoMotivoRechazo">
/// El último motivo literal de rechazo, solo si está rechazada y el usuario gestiona el Centro del canal: <c>null</c> para el rol
/// Cliente, con el mismo corte que <c>AcreditacionEnGrupoDto.UltimoMotivoRechazo</c>.
/// </param>
/// <param name="UrlAccesoCanal">
/// La dirección del portal del canal de ESTA acreditación, con el mismo corte que el motivo; tal como está guardada (la pantalla la
/// normaliza y valida). Es la dirección, nunca la credencial.
/// </param>
public record AcreditacionDelDocumentoDto(Guid Id, Guid ProveedorId, string PlataformaNombre, string? PlataformaCodigo,
    string ClienteEmpresarialNombre, string CentroNombre, EstadoAcreditacion Estado, EstadoVigenciaEnPlataforma EstadoVigencia,
    string? UltimoMotivoRechazo, string? UrlAccesoCanal)
{
    /// <summary>La misma regla que «Sin vigencia» en el listado: validada sin vigencia confirmada en la plataforma.</summary>
    public bool SinVigencia => Estado == EstadoAcreditacion.Aceptada && EstadoVigencia == EstadoVigenciaEnPlataforma.SinConfirmar;
}

public class ObtenerAcreditacionesDelDocumentoQueryHandler(
    IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext,
    IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAcreditacionesDelDocumentoQuery, AcreditacionesDelDocumentoDto?>
{
    public async Task<AcreditacionesDelDocumentoDto?> Handle(ObtenerAcreditacionesDelDocumentoQuery request, CancellationToken cancellationToken)
    {
        // Fuera de alcance responde igual que inexistente: nada que pintar y ningún oráculo de existencia.
        if (!await alcanceDatos.DocumentoLegibleAsync(request.DocumentoId, documentosContext, proyectosContext, cancellationToken))
            return null;

        var documento = await documentosContext.Documentos
            .Where(d => d.Id == request.DocumentoId)
            .Select(d => new { d.EstadoVigencia, d.FechaVencimiento })
            .FirstOrDefaultAsync(cancellationToken);
        if (documento is null) return null;

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var estadoDocumental = CalculadoraEstadoDocumento.Calcular(documento.EstadoVigencia, documento.FechaVencimiento,
            DiaDeNegocio.Hoy(), parametros.UmbralAmbarDias, parametros.UmbralRojoDias);

        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var crudas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.DocumentoId == request.DocumentoId
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            where centrosVisibles == null || centrosVisibles.Contains(centro.Id)
            // P1-X2, como el listado agrupado: un Centro sin gestión CAE no genera trabajo de acreditación.
            where centro.GestionCae != ModalidadGestionCae.SinGestionCae
            // «Plataformas CAE»: solo los canales de una Plataforma CAE, como el agrupado por Plataforma CAE.
            where canal.ProveedorPlataformaCaeId != null
            select new
            {
                acreditacion.Id,
                acreditacion.Estado,
                acreditacion.EstadoVigencia,
                ProveedorId = canal.ProveedorPlataformaCaeId!.Value,
                canal.UrlAcceso,
                CentroId = centro.Id,
                CentroNombre = centro.Nombre,
                ClienteEmpresarialId = centro.ClienteId
            }).ToListAsync(cancellationToken);
        if (crudas.Count == 0) return new AcreditacionesDelDocumentoDto(estadoDocumental, []);

        // El motivo y la dirección, solo en Centros que el usuario gestiona: lista vacía para el rol Cliente (fallo cerrado).
        // Se corta aquí y no en la pantalla: para quien no gestiona, el motivo ni se lee.
        var centrosGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        bool Gestiona(Guid centroId) => centrosGestion == null || centrosGestion.Contains(centroId);

        var rechazadas = crudas.Where(c => c.Estado == EstadoAcreditacion.Rechazada && Gestiona(c.CentroId)).Select(c => c.Id).ToList();
        var motivos = rechazadas.Count == 0
            ? new Dictionary<Guid, string>()
            : await documentosContext.RechazosAcreditacionDocumentoPlataforma
                .Where(r => rechazadas.Contains(r.AcreditacionId))
                .OrderByDescending(r => r.FechaUtc)
                .GroupBy(r => r.AcreditacionId)
                .Select(g => new { AcreditacionId = g.Key, Motivo = g.First().MotivoLiteral })
                .ToDictionaryAsync(x => x.AcreditacionId, x => x.Motivo, cancellationToken);

        var proveedorIds = crudas.Select(c => c.ProveedorId).Distinct().ToList();
        var proveedores = await proveedoresContext.ProveedoresPlataformaCae.Where(p => proveedorIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Nombre, p.Codigo })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var clienteEmpresarialIds = crudas.Select(c => c.ClienteEmpresarialId).Distinct().ToList();
        var clientesEmpresariales = await empresasContext.Empresas.Where(e => clienteEmpresarialIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var acreditaciones = crudas
            .Select(c => new AcreditacionDelDocumentoDto(
                c.Id, c.ProveedorId,
                proveedores.TryGetValue(c.ProveedorId, out var proveedor) ? proveedor.Nombre : "—",
                proveedores.TryGetValue(c.ProveedorId, out var conCodigo) && !string.IsNullOrWhiteSpace(conCodigo.Codigo) ? conCodigo.Codigo : null,
                clientesEmpresariales.GetValueOrDefault(c.ClienteEmpresarialId, "—"), c.CentroNombre, c.Estado, c.EstadoVigencia,
                Gestiona(c.CentroId) ? motivos.GetValueOrDefault(c.Id) : null,
                Gestiona(c.CentroId) ? c.UrlAcceso : null))
            .OrderBy(a => a.PlataformaNombre, StringComparer.CurrentCulture)
            .ThenBy(a => a.ClienteEmpresarialNombre, StringComparer.CurrentCulture)
            .ThenBy(a => a.CentroNombre, StringComparer.CurrentCulture)
            .ThenBy(a => a.Id)
            .ToList();
        return new AcreditacionesDelDocumentoDto(estadoDocumental, acreditaciones);
    }
}
