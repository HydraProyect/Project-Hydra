using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// El cableado del <see cref="CaeManagerDbContext"/> en un solo sitio.
///
/// <para>
/// Existe porque hay <b>dos</b> contextos con la misma forma y distinta identidad
/// de conexión: el inyectado (tráfico normal, <c>CaeManagerDbRuntime</c>) y el de
/// <see cref="FabricaContextoDeBootstrap"/> (arranque administrativo,
/// <c>CaeManagerDb</c>). Si cada uno montara su propia lista de interceptores,
/// divergirían — y la divergencia silenciosa sería justo la clase de fallo que el
/// bootstrap acaba de destapar: un interceptor de menos en el camino de arranque
/// significa escrituras sin auditar sin que nada lo advierta.
/// </para>
///
/// <para>
/// Lo único que <b>no</b> se comparte es la cadena de conexión, que es
/// precisamente la diferencia que justifica que existan los dos.
/// </para>
/// </summary>
internal static class ConfiguracionDeContexto
{
    internal static void Aplicar(DbContextOptionsBuilder opciones, IServiceProvider servicios, string? cadena)
    {
        opciones.UseNpgsql(cadena, npgsql =>
        {
            // Las migraciones viven en su propio ensamblado, separado de
            // Infrastructure — EF Core descubre las migraciones escaneando el
            // ensamblado entero, así que conviene que sea uno dedicado.
            npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");

            // Contra un servidor de red hay errores transitorios que con un
            // archivo local sencillamente no existían. Parámetros explícitos
            // (auditoría Módulo 8) — son los mismos que EF Core aplicaría por
            // defecto sin argumentos, pero como número mágico implícito no
            // había ni un sitio que decidiera cambiarlos ni un valor que leer
            // sin ir a la documentación de Npgsql.
            npgsql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);

            // Límite explícito por comando: sin él, una consulta colgada
            // (lock inesperado, plan de ejecución degenerado) se queda
            // esperando indefinidamente en vez de fallar de forma observable.
            // 30s es generoso para las consultas de pantalla; los jobs de
            // fondo que necesiten más (informes, importaciones masivas) lo
            // fijan en su propio DbContext/comando, no aquí.
            npgsql.CommandTimeout(30);
        });

        opciones.AddInterceptors(
            servicios.GetRequiredService<AuditoriaInterceptor>(),
            servicios.GetRequiredService<TenantSelladoInterceptor>(),
            servicios.GetRequiredService<TenantRlsConnectionInterceptor>(),
            servicios.GetRequiredService<ConcurrenciaOptimistaInterceptor>());
    }
}
