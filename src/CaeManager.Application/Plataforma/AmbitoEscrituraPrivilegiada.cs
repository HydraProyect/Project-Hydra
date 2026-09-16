namespace CaeManager.Application.Plataforma;

/// <summary>
/// Ámbito de elevación de escritura para una sesión privilegiada de
/// Aprovisionamiento (PD-A3). Calcado de
/// <see cref="CaeManager.Application.Common.AmbitoTenantExplicito"/>: un
/// <see cref="AsyncLocal{T}"/> que fluye a través de las llamadas
/// <c>await</c> anidadas dentro del <c>using</c> que lo establece.
///
/// <b>Invariante central del diseño</b>: la cookie nunca decide el rol de
/// escritura de PostgreSQL. Este ámbito es lo ÚNICO que puede hacerlo, y solo
/// lo abre <c>ElevacionEscrituraAprovisionamientoBehavior</c> DESPUÉS de que
/// <c>AutorizacionEscrituraBehavior</c> ya revalidó la sesión contra base de
/// datos en ese mismo comando — nunca desde el interceptor de conexión, nunca
/// desde el token.
///
/// <c>(SesionId, TenantObjetivoId)</c> es la sesión YA REVALIDADA, no la del
/// token: <c>TenantRlsConnectionInterceptor</c> compara estas dos coordenadas
/// contra el token y el tenant que <see cref="Common.ITenantActual"/> resuelve
/// en ese instante antes de elevar el rol — el ámbito abierto no basta por sí
/// solo, tiene que seguir coincidiendo con ambos en el momento de cada
/// apertura de conexión.
/// </summary>
public static class AmbitoEscrituraPrivilegiada
{
    private static readonly AsyncLocal<(Guid SesionId, Guid TenantObjetivoId)?> _actual = new();

    public static (Guid SesionId, Guid TenantObjetivoId)? Actual => _actual.Value;

    public static IDisposable Establecer(Guid sesionId, Guid tenantObjetivoId)
    {
        var anterior = _actual.Value;
        _actual.Value = (sesionId, tenantObjetivoId);
        return new Restaurador(anterior);
    }

    private sealed class Restaurador((Guid SesionId, Guid TenantObjetivoId)? valorAnterior) : IDisposable
    {
        public void Dispose() => _actual.Value = valorAnterior;
    }
}
