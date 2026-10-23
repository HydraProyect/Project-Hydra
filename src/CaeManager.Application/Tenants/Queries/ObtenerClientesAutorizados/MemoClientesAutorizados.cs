using CaeManager.Application.Common;

namespace CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

/// <summary>
/// Memoización de <see cref="ObtenerClientesAutorizadosQuery"/> dentro de UN ámbito de DI (aislamiento, clave y
/// caducidad: <see cref="MemoPorContexto{TValor}"/>). Una sola visita a una pantalla la pedía cinco veces (NavMenu y
/// SelectorClienteActivo en el prerenderizado y en el circuito, más <c>TenantBeneficiarioPorDefectoMiddleware</c>),
/// cada una con sus cinco lecturas.
///
/// <para>
/// La lente de demo Gestor no entra en la clave porque se aplica DESPUÉS, en cada llamada, sobre una copia: lo
/// memoizado es siempre la lista real. La lista no autoriza nada por sí sola: el POST de cambio de Tenant activo, la
/// revalidación y RLS siguen decidiendo con el dato vigente.
/// </para>
/// </summary>
public sealed class MemoClientesAutorizados(TimeProvider? reloj = null)
    : MemoPorContexto<IReadOnlyList<ClienteAutorizadoDto>>(reloj)
{
    /// <summary>Colección de solo lectura compartida entre llamadas: quien necesite modificarla copia antes.</summary>
    protected override IReadOnlyList<ClienteAutorizadoDto> Congelar(IReadOnlyList<ClienteAutorizadoDto> valor) =>
        Array.AsReadOnly(valor.ToArray());
}
