using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Inbound.Autorizacion;

/// <inheritdoc cref="ILectorRequerimientosTec" />
/// <remarks>
/// Por la función <c>SECURITY DEFINER</c> <c>app_inbound_tec_requerimientos()</c> y no por EF: los requisitos viven en los
/// Tenants TCE y la RLS <c>aislamiento_tenant</c> los oculta, como debe, a la conexión del Tenant TEC. No pasa ningún
/// parámetro: el Tenant TEC y el usuario los toma la función del contexto firmado. Mismo patrón que
/// <see cref="LectorDocumentosInboundEnBase"/>. No deja traza (no lee Documentos ni datos personales), así que, a diferencia
/// de las lecturas X-1, puede ir dentro de una transacción.
/// </remarks>
public class LectorRequerimientosTecEnBase(CaeManagerDbContext dbContext) : ILectorRequerimientosTec
{
    public async Task<IReadOnlyList<RequisitoTecDto>> RequisitosAsync(CancellationToken cancellationToken = default)
    {
        var filas = await dbContext.Database
            .SqlQuery<FilaRequisito>($"SELECT * FROM app_inbound_tec_requerimientos()")
            .ToListAsync(cancellationToken);

        return filas
            .Select(f => new RequisitoTecDto(
                f.ContratoAccesoId, f.TenantTceNombre, f.CentroId, f.CentroNombre, f.ClienteEmpresarialNombre, f.RequisitoId,
                f.TipoDocumentoId, f.TipoDocumentoNombre, f.Entidad, f.Criterio, f.ModoVigencia, f.VigenciaMeses, f.ToleranciaDias,
                f.BloqueaAcceso, f.AmbitoDocumento, f.CapaPlantilla, f.SePideA, f.TiposContratacion))
            .ToList();
    }

    // La forma de las filas que devuelve la función: los nombres de propiedad son los de sus columnas de salida.
    private sealed class FilaRequisito
    {
        public Guid ContratoAccesoId { get; init; }
        public string TenantTceNombre { get; init; } = string.Empty;
        public Guid CentroId { get; init; }
        public string CentroNombre { get; init; } = string.Empty;
        public string ClienteEmpresarialNombre { get; init; } = string.Empty;
        public Guid RequisitoId { get; init; }
        public Guid TipoDocumentoId { get; init; }
        public string TipoDocumentoNombre { get; init; } = string.Empty;
        public string Entidad { get; init; } = string.Empty;
        public string? Criterio { get; init; }
        public string ModoVigencia { get; init; } = string.Empty;
        public int? VigenciaMeses { get; init; }
        public int? ToleranciaDias { get; init; }
        public bool BloqueaAcceso { get; init; }
        public string AmbitoDocumento { get; init; } = string.Empty;
        public string CapaPlantilla { get; init; } = string.Empty;
        public string SePideA { get; init; } = string.Empty;
        public int TiposContratacion { get; init; }
    }
}
