using CaeManager.Application.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// <b>La identidad administrativa del arranque</b>, separada de la del tráfico
/// normal. Produce un <see cref="CaeManagerDbContext"/> conectado con
/// <c>CaeManagerDb</c> —el rol propietario— en vez de con
/// <c>CaeManagerDbRuntime</c>.
///
/// <para>
/// <b>Por qué existe.</b> Dos seeders del arranque no son tráfico de aplicación y
/// no pueden ejecutarse bajo una identidad sometida a RLS por-tenant:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>IdentitySeeder</c> lee y escribe <c>EstadoBootstrapPlataforma</c>, que es
/// <b>estado de sistema</b>. Su política de <c>SELECT</c> exige
/// <c>app.usuario_id</c>, y en el arranque no hay sesión de usuario: la lectura
/// devolvía cero filas, la guarda "si no existe, créala" entraba siempre y el
/// segundo arranque moría con <c>23505</c> contra la clave primaria. Observado en
/// staging el 2026-08-23, en cuanto ese entorno adoptó el rol restringido.
/// </description></item>
/// <item><description>
/// <c>AsignacionesOperativasBackfillSeeder</c> es <b>cross-tenant por diseño</b>
/// —lo dice su propio código al justificar <c>IgnoreQueryFilters</c>— y escribe
/// una fila por cada tenant en un solo <c>SaveChanges</c>. La política de los
/// catálogos de asignación exige <c>PropietarioTenantId = app.tenant_id</c>, así
/// que todas las filas salvo las del tenant fijado serían rechazadas.
/// </description></item>
/// </list>
///
/// <para>
/// <b>Lo que NO es.</b> No es una excepción de RLS ni un permiso especial: es una
/// identidad distinta para una clase de operación distinta. Las políticas quedan
/// intactas — debilitarlas para que un seeder pase sería cambiar el enforcement
/// para acomodar al llamante, que es justo lo contrario de lo que hace falta.
/// </para>
///
/// <para>
/// <b>Alcance deliberadamente estrecho.</b> Solo esos dos. Los otros siete
/// seeders del arranque operan dentro de un <c>AmbitoTenantExplicito</c> que fija
/// <c>app.tenant_id</c> antes de escribir, que es exactamente lo que las políticas
/// por-tenant piden, y siguen usando el contexto inyectado. Esa clasificación es
/// <b>estructural y está pendiente de prueba de efecto</b> bajo el rol
/// restringido: no se ha demostrado, se ha razonado.
/// </para>
///
/// <para>
/// Lleva <b>los mismos interceptores</b> que el contexto inyectado —auditoría,
/// sellado, sesión y concurrencia—: lo único que cambia es con qué identidad se
/// conecta. Sin ellos, estas escrituras dejarían de auditarse, que sería un
/// precio que nadie ha aceptado.
/// </para>
/// </summary>
public sealed class FabricaContextoDeBootstrap(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IDataProtectionProvider dataProtectionProvider,
    ITenantActual tenantActual)
{
    /// <summary>
    /// Crea el contexto. El llamante lo posee y debe liberarlo — se usa en el
    /// arranque, una vez, dentro de un <c>await using</c>.
    /// </summary>
    public CaeManagerDbContext Crear()
    {
        var cadena = configuration.GetConnectionString("CaeManagerDb")
            ?? throw new InvalidOperationException(
                "Falta el connection string CaeManagerDb, que es la identidad administrativa del " +
                "arranque. Sin él no se puede sembrar el estado de sistema.");

        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>();
        ConfiguracionDeContexto.Aplicar(opciones, serviceProvider, cadena);

        return new CaeManagerDbContext(opciones.Options, dataProtectionProvider, tenantActual);
    }
}
