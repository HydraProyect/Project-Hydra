using CaeManager.Domain.Common;

namespace CaeManager.Domain.Dashboard;

/// <summary>Foto inmutable por Tenant beneficiario y mes. La reapertura conserva la foto hasta el recierre.</summary>
public class CierreMensualTenantBeneficiario : EntidadBase
{
    public int Anyo { get; private set; }
    public int Mes { get; private set; }
    public string FotoJson { get; private set; } = string.Empty;
    public bool Estimado { get; private set; }
    public DateTime CerradoEnUtc { get; private set; }
    public DateTime? ReabiertoEnUtc { get; private set; }
    public string? MotivoReapertura { get; private set; }

    private CierreMensualTenantBeneficiario() { }

    public static CierreMensualTenantBeneficiario Crear(int anyo, int mes, string fotoJson, bool estimado, DateTime ahoraUtc)
    {
        var fin = DiaDeNegocio.InicioEnUtc(new DateOnly(anyo, mes, 1).AddMonths(1));
        if (ahoraUtc.Kind != DateTimeKind.Utc || ahoraUtc < fin)
            throw new ArgumentException("Solo se pueden cerrar meses terminados, con un instante UTC.", nameof(ahoraUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(fotoJson);
        return new() { Anyo = anyo, Mes = mes, FotoJson = fotoJson, Estimado = estimado, CerradoEnUtc = ahoraUtc };
    }

    public void Reabrir(string motivo, DateTime ahoraUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        if (motivo.Length > 500 || ahoraUtc.Kind != DateTimeKind.Utc || ahoraUtc < CerradoEnUtc)
            throw new ArgumentException("La reapertura requiere un motivo de hasta 500 caracteres y un instante UTC posterior al cierre.");
        if (ReabiertoEnUtc is not null)
            throw new InvalidOperationException("El mes ya está reabierto.");
        MotivoReapertura = motivo.Trim();
        ReabiertoEnUtc = ahoraUtc;
    }

    public void Recerrar(string fotoJson, DateTime ahoraUtc)
    {
        if (ReabiertoEnUtc is null || ahoraUtc.Kind != DateTimeKind.Utc || ahoraUtc < ReabiertoEnUtc)
            throw new InvalidOperationException("El recierre requiere una reapertura anterior.");
        ArgumentException.ThrowIfNullOrWhiteSpace(fotoJson);
        FotoJson = fotoJson;
        CerradoEnUtc = ahoraUtc;
        ReabiertoEnUtc = null;
    }
}
