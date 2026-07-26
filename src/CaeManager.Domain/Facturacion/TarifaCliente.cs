using CaeManager.Domain.Common;

namespace CaeManager.Domain.Facturacion;

/// <summary>
/// Precio unitario que el consultor (tenant) aplica a un <see cref="ConceptoFacturable"/>
/// concreto para un cliente. Refleja el acuerdo tarifario pactado; no es un
/// catálogo global — cada (tenant, cliente, concepto) tiene a lo sumo una
/// tarifa activa. El módulo de facturación la usa junto con los conteos
/// derivados de los datos de dominio existentes para calcular el resumen
/// mensual de facturación.
/// </summary>
public class TarifaCliente : EntidadBase
{
    public const int LongitudMonedaIso = 3;

    public Guid ClienteId { get; private set; }
    public ConceptoFacturable Concepto { get; private set; }
    public decimal PrecioUnitario { get; private set; }
    public string MonedaIso { get; private set; } = "EUR";

    private TarifaCliente() { }

    public static TarifaCliente Crear(Guid clienteId, ConceptoFacturable concepto, decimal precioUnitario, string monedaIso)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La tarifa debe estar asociada a un cliente.", nameof(clienteId));
        if (precioUnitario < 0)
            throw new ArgumentException("El precio unitario no puede ser negativo.", nameof(precioUnitario));
        if (string.IsNullOrWhiteSpace(monedaIso) || monedaIso.Trim().Length != LongitudMonedaIso)
            throw new ArgumentException("La moneda debe ser un código ISO de 3 caracteres (p. ej. EUR, USD).", nameof(monedaIso));

        return new TarifaCliente
        {
            ClienteId = clienteId,
            Concepto = concepto,
            PrecioUnitario = precioUnitario,
            MonedaIso = monedaIso.Trim().ToUpperInvariant()
        };
    }

    public void Actualizar(decimal precioUnitario, string monedaIso)
    {
        if (precioUnitario < 0)
            throw new ArgumentException("El precio unitario no puede ser negativo.", nameof(precioUnitario));
        if (string.IsNullOrWhiteSpace(monedaIso) || monedaIso.Trim().Length != LongitudMonedaIso)
            throw new ArgumentException("La moneda debe ser un código ISO de 3 caracteres.", nameof(monedaIso));

        PrecioUnitario = precioUnitario;
        MonedaIso = monedaIso.Trim().ToUpperInvariant();
    }
}
