using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <inheritdoc cref="IRegistroCambiosEstado"/>
/// <remarks>
/// Anota en el <see cref="AvisoEstadoTrasConfirmarInterceptor"/> del <see cref="CaeManagerDbContext"/> del ámbito, el mismo con
/// el que escribe <c>EscritorEstadoMaterializado</c>. Un contexto sin ese interceptor no tiene a quién avisar en el
/// <c>COMMIT</c>: el aviso se pierde con un registro, y la caché lo cubre releyendo entero al ver la versión nueva.
/// </remarks>
public sealed class RegistroCambiosEstado(CaeManagerDbContext dbContext, ILogger<RegistroCambiosEstado>? logger = null) : IRegistroCambiosEstado
{
    public void Anotar(AvisoCambioEstado aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        if (AvisoEstadoTrasConfirmarInterceptor.De(dbContext) is { } interceptor)
            interceptor.Anotar(dbContext, aviso);
        else
            logger?.LogWarning("Estado materializado: el contexto no lleva el interceptor de avisos; la versión {Version} del Tenant {Tenant} no se avisa.",
                aviso.Version, aviso.TenantId);
    }
}
