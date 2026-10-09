using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Para los contenedores hechos a mano que construyen el <c>CurrentUserService</c> REAL: desde el
/// Encargo de administración (decisión D-8), el rol por la vía de Operación lo resuelve
/// <see cref="TechoDeRolPorEncargo"/>, que producción registra en <c>AddInfrastructure</c>. Un
/// contenedor que no lo registra revienta en cuanto el usuario alcanza un Tenant por Operación.
///
/// <para>
/// Se registra el techo REAL sobre el mismo contexto que el test ya da como
/// <see cref="IOperacionesQueryContext"/> (bajo su RLS), de modo que la consulta del encargo
/// corre de verdad. Lo único que se sustituye es el perfil de Propiedad en origen, que en
/// producción lee de Identity: estos tests no siembran ningún encargo, así que el techo no
/// llega a consultarlo. Si lo consultara, el test ha dejado de ser lo que dice y tiene que
/// componer el perfil real: por eso el doble lanza en vez de responder «sin perfil».
/// </para>
/// </summary>
internal static class TechoDeRolSinEncargoSembrado
{
    public static IServiceCollection AddTechoDeRolSinEncargoSembrado(this IServiceCollection servicios)
    {
        servicios.AddSingleton(sp =>
        {
            var operaciones = sp.GetRequiredService<IOperacionesQueryContext>();
            return new TechoDeRolPorEncargo(
                operaciones, (IEncargosAdministracionQueryContext)operaciones, new PerfilQueNoSeConsulta());
        });
        return servicios;
    }

    private sealed class PerfilQueNoSeConsulta : IPerfilDePropiedadEnOrigen
    {
        public Task<string?> ObtenerAsync(
            Guid usuarioId, Guid tenantOrigenId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Este contenedor de prueba se compone sin Encargo de administración sembrado: el perfil de "
                + "Propiedad en origen no debería consultarse. Si el test siembra un encargo, compón el "
                + "TechoDeRolPorEncargo con PerfilDePropiedadEnOrigenEnBase.");
    }
}
