using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IContactosSeguridadPlataforma" />
/// <remarks>
/// Las lecturas quitan el filtro de Tenant de EF porque el actor lee filas de todos los Tenants; lo que
/// limita las filas es la RLS (<c>actor_de_plataforma_lee</c>: concesión AdminPlataforma global vigente), no
/// este código. Validar, rechazar y resolver destinatarios llaman a funciones de la base con el mismo patrón
/// que <c>RevocacionOperadorCaeExternoPorSoporte</c>: código devuelto en vez de excepción y valores
/// parametrizados por EF.
/// </remarks>
public sealed class ContactosSeguridadPlataforma(
    CaeManagerDbContext contexto,
    PuertaAccesoDatos puertaAccesoDatos) : IContactosSeguridadPlataforma
{
    public Task<IReadOnlyList<ContactoSeguridadPropuestoDto>> ObtenerPropuestosAsync(CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyList<ContactoSeguridadPropuestoDto>>(async () =>
            await contexto.ContactosSeguridadTenant.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.Estado == EstadoContactoSeguridad.Propuesto)
                .Join(contexto.Tenants, c => c.TenantId, t => t.Id, (c, t) => new { Contacto = c, Organizacion = t.Nombre })
                // Ordenar antes de proyectar al DTO: EF no traduce un OrderBy sobre una propiedad del record.
                .OrderBy(x => x.Contacto.PropuestoEnUtc)
                .Select(x => new ContactoSeguridadPropuestoDto(
                    x.Contacto.Id, x.Contacto.TenantId, x.Organizacion, x.Contacto.Nombre, x.Contacto.Correo,
                    x.Contacto.Telefono, x.Contacto.PropuestoEnUtc))
                .ToListAsync(cancellationToken), cancellationToken);

    public Task<ContactoSeguridadDto?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(() =>
            contexto.ContactosSeguridadTenant.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Estado == EstadoContactoSeguridad.Validado)
                .Select(c => new ContactoSeguridadDto(
                    c.Id, c.Nombre, c.Correo, c.Telefono, c.PropuestoEnUtc, c.ResueltoEnUtc, c.MotivoRechazo))
                .SingleOrDefaultAsync(cancellationToken), cancellationToken);

    public Task<Result> ResolverAsync(Guid contactoId, string? motivoRechazo, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var validar = motivoRechazo is null;
            var motivo = motivoRechazo ?? string.Empty;
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_resolver_contacto_seguridad({contactoId}, {validar}, {motivo}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Contexto RLS firmado inválido: nunca una escritura, y nunca un 500.
                codigo = "no_autorizado";
            }

            // La función escribió por debajo de EF: lo rastreado de esa fila ya no es cierto.
            foreach (var entrada in contexto.ChangeTracker.Entries<ContactoSeguridadTenant>().ToList())
                entrada.State = EntityState.Detached;

            return codigo switch
            {
                "validado" or "rechazado" => Result.Exito(),
                "no_encontrado" => Result.Fallo(ErroresContactoSeguridad.NoEncontrado),
                "no_propuesto" => Result.Fallo(ErroresContactoSeguridad.YaResuelto),
                "motivo_no_valido" => Result.Fallo(ErroresContactoSeguridad.MotivoObligatorio),
                _ => Result.Fallo(ErroresContactoSeguridad.SoloActorDePlataforma),
            };
        }, cancellationToken);

    public Task<Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>> ResolverDestinatariosAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var ids = tenantIds.ToArray();
            List<FilaDestinatarioAvisoSeguridad> filas;
            try
            {
                filas = await contexto.Database
                    .SqlQuery<FilaDestinatarioAvisoSeguridad>(
                        $"SELECT \"TenantId\", \"SinContactoValidado\", \"Nombre\", \"Correo\", \"Telefono\" FROM app_destinatarios_aviso_seguridad({ids})")
                    .ToListAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return Result.Fallo<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>(ErroresContactoSeguridad.SoloActorDePlataforma);
            }

            IReadOnlyList<DestinatariosAvisoSeguridadTenant> porTenant = [.. filas
                .GroupBy(f => f.TenantId)
                .Select(g => new DestinatariosAvisoSeguridadTenant(
                    g.Key,
                    g.First().SinContactoValidado,
                    [.. g.Where(f => f.Correo is not null)
                        .Select(f => new DestinatarioAvisoSeguridad(f.Nombre ?? f.Correo!, f.Correo!, f.Telefono))]))];
            return Result.Exito(porTenant);
        }, cancellationToken);

    private sealed record FilaDestinatarioAvisoSeguridad(
        Guid TenantId, bool SinContactoValidado, string? Nombre, string? Correo, string? Telefono);
}
