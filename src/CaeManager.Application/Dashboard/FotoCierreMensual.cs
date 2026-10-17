using System.Text.Json;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Facturacion;

namespace CaeManager.Application.Dashboard;

public record TarifaAplicadaCuadro(Guid EmpresaId, ConceptoFacturable Concepto, decimal PrecioUnitario, string MonedaIso);
public record FotoCierreMensual(MedicionCuadroDireccion Medicion, IReadOnlyList<TarifaAplicadaCuadro> TarifasAplicadas)
{
    public string Serializar() => JsonSerializer.Serialize(this);
    public static FotoCierreMensual Leer(string json) => JsonSerializer.Deserialize<FotoCierreMensual>(json)
        ?? throw new InvalidOperationException("La foto mensual no contiene datos.");
}
