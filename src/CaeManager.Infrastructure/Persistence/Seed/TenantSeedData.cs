namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El tenant #1: la organización que ya usa el sistema en producción,
/// migrando a multi-tenant (ver ADR-003-saas-multitenant.md,
/// PLAN-MIGRACION-MULTITENANT.md § 3, Etapa 2 — backfill). Id fijo y
/// determinista, mismo criterio que <see cref="ParametroSistemaSeedData"/>
/// y <see cref="TipoDocumentoSeedData"/>, para que la migración de backfill
/// sea reproducible en cualquier entorno (dev, tests, producción).
///
/// El nombre es un placeholder deliberado — se renombra después del
/// despliegue con <c>Tenant.RenombrarA(...)</c>, no bloquea el backfill ni
/// depende de conocer el nombre real de la organización en este momento.
/// </summary>
public static class TenantSeedData
{
    public static readonly Guid IdPorDefecto = new("00000000-0000-0000-0000-000000000001");
    public const string NombrePorDefecto = "Organización principal";
}
