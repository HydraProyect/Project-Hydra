namespace CaeManager.Application.Common;

/// <summary>
/// Ámbito de tenant explícito para contextos sin sesión de usuario —
/// siembra al arrancar, jobs de fondo (ver docs/MULTITENANCY.md § 8.4,
/// "ámbito de tenant explícito para procesos de fondo").
///
/// La implementación Web de <see cref="ITenantActual"/> consulta este
/// ámbito **antes** de intentar resolver el claim de sesión: fuera de un
/// circuito de Blazor no hay sesión que leer, pero puede haber un tenant
/// explícito establecido por quien orquesta el trabajo (Program.cs
/// sembrando datos al arrancar, un futuro job recorriendo tenants uno a
/// uno). Sin este ámbito, cualquier escritura de una entidad de dominio
/// fuera de una petición HTTP real sería rechazada por
/// <c>TenantSelladoInterceptor</c> (fallo cerrado, por diseño) — este es el
/// mecanismo correcto para dar ese contexto, no una excepción al fallo
/// cerrado.
///
/// Basado en <see cref="AsyncLocal{T}"/>: fluye a través de las llamadas
/// <c>await</c> anidadas dentro del <c>using</c> que lo establece, sin
/// tener que pasar el tenant como parámetro por cada capa.
/// </summary>
public static class AmbitoTenantExplicito
{
    private static readonly AsyncLocal<Guid?> _tenantId = new();

    public static Guid? TenantIdActual => _tenantId.Value;

    public static IDisposable Establecer(Guid tenantId)
    {
        var anterior = _tenantId.Value;
        _tenantId.Value = tenantId;
        return new Restaurador(anterior);
    }

    private sealed class Restaurador(Guid? valorAnterior) : IDisposable
    {
        public void Dispose() => _tenantId.Value = valorAnterior;
    }
}
