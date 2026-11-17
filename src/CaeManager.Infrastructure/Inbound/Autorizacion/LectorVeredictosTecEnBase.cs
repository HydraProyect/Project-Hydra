using CaeManager.Application.Inbound.AutorizacionesLectura;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Inbound.Autorizacion;

/// <inheritdoc cref="ILectorVeredictosTec" />
/// <remarks>
/// Por la función <c>SECURITY DEFINER</c> <c>app_inbound_tec_veredictos()</c> y no por EF: las evidencias viven en los
/// Tenants TCE y la RLS <c>aislamiento_tenant</c> las oculta, como debe, a la conexión del Tenant TEC. No pasa ningún
/// parámetro: el Tenant TEC y el usuario los toma la función del contexto firmado. Mismo patrón que
/// <see cref="LectorDocumentosInboundEnBase"/>. No deja traza (no lee Documentos ni datos personales del Tenant TCE), así que,
/// a diferencia de las lecturas X-1, puede ir dentro de una transacción. Un estado o una causa que no sea uno de los nombres
/// del enumerado lanza en vez de leerse como otro valor.
/// </remarks>
public class LectorVeredictosTecEnBase(CaeManagerDbContext dbContext) : ILectorVeredictosTec
{
    public async Task<IReadOnlyList<VeredictoTecDto>> VeredictosAsync(CancellationToken cancellationToken = default)
    {
        var filas = await dbContext.Database.SqlQuery<FilaVeredicto>($"SELECT * FROM app_inbound_tec_veredictos()").ToListAsync(cancellationToken);

        return filas
            .Select(f => new VeredictoTecDto(
                f.ContratoAccesoId, f.DocumentoOrigenId, PorNombre<EstadoVeredictoTec>(f.Estado),
                f.CausaRechazo is null ? null : PorNombre<CausaRechazoEvidenciaInbound>(f.CausaRechazo),
                f.MotivoRechazo, f.FechaVeredictoUtc))
            .ToList();
    }

    // Solo el nombre exacto de un valor definido: Enum.TryParse aceptaría también un número o una lista con comas.
    private static T PorNombre<T>(string valor) where T : struct, Enum =>
        Enum.GetValues<T>().Where(v => v.ToString() == valor).Select(v => (T?)v).SingleOrDefault()
        ?? throw new InvalidOperationException($"app_inbound_tec_veredictos devolvió «{valor}», que no es un {typeof(T).Name}.");

    // La forma de las filas que devuelve la función: los nombres de propiedad son los de sus columnas de salida.
    private sealed class FilaVeredicto
    {
        public Guid ContratoAccesoId { get; init; }
        public Guid DocumentoOrigenId { get; init; }
        public string Estado { get; init; } = string.Empty;
        public string? CausaRechazo { get; init; }
        public string? MotivoRechazo { get; init; }
        public DateTime FechaVeredictoUtc { get; init; }
    }
}
