using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="ILectorDocumentosInbound" />
/// <remarks>
/// Por las funciones <c>SECURITY DEFINER</c> de la migración <c>AnadeLecturaInboundEntreTenants</c> y no por EF: los
/// Documentos son de otro Tenant y RLS los oculta, como debe, a la conexión del Tenant lector. Solo pasa el identificador
/// del contrato (y del Documento), parametrizado por EF; el Tenant lector y el usuario los toma la función del contexto
/// firmado. Mismo patrón que <see cref="TenantConAdministradorPropioEnBase"/>.
/// </remarks>
public class LectorDocumentosInboundEnBase(CaeManagerDbContext dbContext) : ILectorDocumentosInbound
{
    public async Task<AutorizacionInboundDto?> AutorizacionDelContratoAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default)
    {
        var fila = await dbContext.Database.SqlQuery<FilaAutorizacion>($"SELECT * FROM app_inbound_autorizacion_del_contrato({contratoAccesoId})").ToListAsync(cancellationToken);

        return fila.SingleOrDefault() is { } a
            ? new AutorizacionInboundDto(a.AutorizacionId, a.VigenciaDesde, a.VigenciaHasta, a.Revocada, a.IncluyeDocumentosEmpresa, a.Trabajadores)
            : null;
    }

    public async Task<IReadOnlyList<DocumentoInboundDto>> DocumentosAutorizadosAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default)
    {
        var filas = await dbContext.Database.SqlQuery<FilaDocumento>($"SELECT * FROM app_inbound_documentos_autorizados({contratoAccesoId})").ToListAsync(cancellationToken);

        return filas
            .Select(d => new DocumentoInboundDto(
                d.DocumentoId, d.TrabajadorId, d.TrabajadorNombre, d.TrabajadorApellidos, d.TrabajadorDni, d.TipoDocumentoNombre,
                d.FechaEmision, d.FechaVencimiento, (EstadoVigenciaDocumento)d.EstadoVigencia))
            .ToList();
    }

    public async Task<ArchivoInboundDto?> ArchivoAutorizadoAsync(Guid contratoAccesoId, Guid documentoId, CancellationToken cancellationToken = default)
    {
        var fila = await dbContext.Database.SqlQuery<FilaArchivo>($"SELECT * FROM app_inbound_archivo_autorizado({contratoAccesoId}, {documentoId})").ToListAsync(cancellationToken);

        return fila.SingleOrDefault() is { } a ? new ArchivoInboundDto(a.DocumentoId, a.ArchivoUrl) : null;
    }

    // Formas de las filas que devuelven las funciones: los nombres de propiedad son los de sus columnas de salida.
    private sealed class FilaAutorizacion
    {
        public Guid AutorizacionId { get; init; }
        public DateOnly VigenciaDesde { get; init; }
        public DateOnly? VigenciaHasta { get; init; }
        public bool Revocada { get; init; }
        public bool IncluyeDocumentosEmpresa { get; init; }
        public int Trabajadores { get; init; }
    }

    private sealed class FilaDocumento
    {
        public Guid DocumentoId { get; init; }
        public Guid? TrabajadorId { get; init; }
        public string? TrabajadorNombre { get; init; }
        public string? TrabajadorApellidos { get; init; }
        public string? TrabajadorDni { get; init; }
        public string TipoDocumentoNombre { get; init; } = string.Empty;
        public DateOnly FechaEmision { get; init; }
        public DateOnly? FechaVencimiento { get; init; }
        public int EstadoVigencia { get; init; }
    }

    private sealed class FilaArchivo
    {
        public Guid DocumentoId { get; init; }
        public string? ArchivoUrl { get; init; }
    }
}
