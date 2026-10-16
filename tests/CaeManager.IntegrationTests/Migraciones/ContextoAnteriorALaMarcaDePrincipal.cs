using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// El contexto de EF tal y como veía <c>AsignacionesCartera</c> antes de la migración
/// <c>AnadePrincipalALasCarteras</c>: sin la columna <c>EsPrincipal</c>. Las pruebas de migración que
/// retroceden a un esquema anterior a esa columna y siembran o leen carteras con EF la necesitan: con el
/// modelo actual, el INSERT y el SELECT nombran <c>"EsPrincipal"</c> y PostgreSQL responde 42703 (columna
/// inexistente), un fallo que no tiene nada que ver con lo que la prueba mide.
///
/// <para>
/// Sirve también sobre el esquema final —la columna tiene valor por omisión y este contexto simplemente no
/// la nombra—, así que una clase de prueba puede usarlo para todo <b>salvo para migrar</b>: EF descubre
/// las migraciones por el tipo exacto del contexto (<c>[DbContext(typeof(CaeManagerDbContext))]</c>) y con
/// este tipo no encontraría ninguna. Para <c>IMigrator</c> y <c>GetMigrations()</c> se usa el contexto real.
/// </para>
/// </summary>
internal sealed class ContextoAnteriorALaMarcaDePrincipal(
    DbContextOptions<CaeManagerDbContext> opciones,
    IDataProtectionProvider proteccionDeDatos,
    ITenantActual tenantActual)
    : CaeManagerDbContext(opciones, proteccionDeDatos, tenantActual)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AsignacionCartera>().Ignore(c => c.EsPrincipal);
        // Posterior a EsPrincipal (D-9): un esquema anterior a la marca tampoco tiene esta columna.
        builder.Entity<AsignacionCartera>().Ignore(c => c.EraPrincipalAlCerrarsePorCascada);
        // Posterior también (AnadeFichaEmpresa): las clases que usan este contexto siembran Empresas sobre un
        // esquema sin las quince columnas de la ficha de Empresa 360.
        foreach (var campo in CamposDeLaFichaDeEmpresa) builder.Entity<Empresa>().Ignore(campo);
    }

    private static readonly string[] CamposDeLaFichaDeEmpresa =
    [
        nameof(Empresa.CanalFirmaDocumentos), nameof(Empresa.SelloCustodiadoPorOperadorCae),
        nameof(Empresa.FirmanteDocumentos), nameof(Empresa.ModalidadOrganizacionPreventiva),
        nameof(Empresa.OrganizacionSpa), nameof(Empresa.ContactoSpaNombre), nameof(Empresa.ContactoSpaTelefono),
        nameof(Empresa.ContactoSpaCorreo), nameof(Empresa.Mutua), nameof(Empresa.ContactoFormacionNombre),
        nameof(Empresa.ContactoFormacionTelefono), nameof(Empresa.ContactoFormacionCorreo),
        nameof(Empresa.TecnicoPrlNombre), nameof(Empresa.TecnicoPrlTelefono), nameof(Empresa.TecnicoPrlCorreo),
    ];
}
