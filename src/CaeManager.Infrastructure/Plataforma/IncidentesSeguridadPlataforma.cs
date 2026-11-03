using CaeManager.Application.Common;
using CaeManager.Application.Seguridad.Incidentes.Aviso;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IIncidentesSeguridadPlataforma" />
/// <remarks>
/// Las tablas del incidente son del plano de Plataforma y no tienen filtro de Tenant de EF: lo que limita las
/// filas es la RLS (<c>actor_de_plataforma_lee</c>). Los nombres de los Tenants afectados quitan el filtro de
/// Tenant porque son de otros Tenants, con el mismo criterio que la cola de contactos de seguridad
/// (<see cref="ContactosSeguridadPlataforma"/>). Decidir, reclamar y resolver llaman a funciones de la base con
/// el patrón de <c>app_resolver_contacto_seguridad</c>: código devuelto en vez de excepción y valores
/// parametrizados por EF.
/// </remarks>
public sealed class IncidentesSeguridadPlataforma(
    CaeManagerDbContext contexto,
    PuertaAccesoDatos puertaAccesoDatos) : IIncidentesSeguridadPlataforma
{
    public Task<IncidenteSeguridadDto?> ObtenerAsync(Guid incidenteId, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync<IncidenteSeguridadDto?>(async () =>
        {
            var incidente = await contexto.IncidentesSeguridadSospechados.AsNoTracking()
                .Where(i => i.Id == incidenteId)
                .Select(i => new
                {
                    i.Id,
                    i.Senal,
                    i.Estado,
                    i.DetectadoEnUtc,
                    i.UltimaDeteccionEnUtc,
                    i.Detecciones,
                    i.TenantsAfectados,
                    i.Evidencia,
                    i.ConfirmadoEnUtc,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (incidente is null)
                return null;

            var transiciones = await contexto.TransicionesIncidenteSeguridad.AsNoTracking()
                .Where(t => t.IncidenteId == incidenteId)
                .OrderBy(t => t.OcurridoEnUtc)
                .Select(t => new TransicionIncidenteDto(t.EstadoAnterior, t.EstadoNuevo, t.OcurridoEnUtc, t.ActorUsuarioId, t.Motivo))
                .ToListAsync(cancellationToken);

            var envios = await contexto.EnviosAvisoIncidenteSeguridad.AsNoTracking()
                .Where(e => e.IncidenteId == incidenteId)
                .OrderBy(e => e.ConfirmadoEnUtc)
                .Select(e => new EnvioAvisoDto(
                    e.Id, e.Plantilla, e.TenantAfectadoId, e.DestinatarioNombre, e.DestinatarioCorreo,
                    e.SinContactoValidado, e.Estado, e.Intentos, e.ConfirmadoPorUsuarioId, e.ConfirmadoEnUtc,
                    e.UltimoIntentoEnUtc, e.EnviadoEnUtc, e.UltimoError))
                .ToListAsync(cancellationToken);

            return new IncidenteSeguridadDto(
                incidente.Id, incidente.Senal, incidente.Estado, incidente.DetectadoEnUtc, incidente.UltimaDeteccionEnUtc,
                incidente.Detecciones, incidente.TenantsAfectados, incidente.Evidencia, incidente.ConfirmadoEnUtc,
                transiciones, envios);
        }, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> NombresTenantsAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyDictionary<Guid, string>>(async () =>
        {
            var ids = tenantIds.Distinct().ToList();
            return await contexto.Tenants.IgnoreQueryFilters().AsNoTracking()
                .Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
        }, cancellationToken);

    public Task<Result> DecidirAsync(
        Guid incidenteId, DecisionIncidenteSeguridad decision, string? motivo, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var codigoDecision = decision.ToString();
            var texto = motivo ?? string.Empty;
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_decidir_incidente_seguridad({incidenteId}, {codigoDecision}, {texto}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Contexto RLS firmado inválido: nunca una escritura, y nunca un 500.
                codigo = "no_autorizado";
            }

            return codigo switch
            {
                "confirmado" or "ya_confirmado" or "en_evaluacion" or "ya_en_evaluacion" or "descartado" => Result.Exito(),
                "no_encontrado" => Result.Fallo(ErroresIncidenteSeguridad.NoEncontrado),
                "estado_no_valido" => Result.Fallo(ErroresIncidenteSeguridad.EstadoNoValido),
                "motivo_no_valido" => Result.Fallo(ErroresIncidenteSeguridad.MotivoObligatorio),
                _ => Result.Fallo(ErroresIncidenteSeguridad.SoloActorDePlataforma),
            };
        }, cancellationToken);

    public Task<Result<EnvioAvisoReclamado>> ReclamarEnvioAsync(EnvioAvisoSolicitado envio, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var plantilla = envio.Plantilla.ToString();
            List<FilaEnvioReclamado> filas;
            try
            {
                filas = await contexto.Database
                    .SqlQuery<FilaEnvioReclamado>(
                        $"SELECT codigo AS \"Codigo\", envio_id AS \"EnvioId\", reclamado AS \"Reclamado\", estado_envio AS \"Estado\", asunto_envio AS \"Asunto\", cuerpo_envio AS \"Cuerpo\" FROM app_reclamar_envio_aviso_seguridad({envio.IncidenteId}, {plantilla}, {envio.TenantAfectadoId}, {envio.DestinatarioNombre}, {envio.DestinatarioCorreo}, {envio.SinContactoValidado}, {envio.Asunto}, {envio.Cuerpo})")
                    .ToListAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.SoloActorDePlataforma);
            }

            var fila = filas.Single();
            return fila.Codigo switch
            {
                "ok" when fila.EnvioId is { } id && Enum.TryParse<EstadoEnvioAvisoIncidenteSeguridad>(fila.Estado, out var estado) =>
                    Result.Exito(new EnvioAvisoReclamado(id, fila.Reclamado, estado, fila.Asunto ?? "", fila.Cuerpo ?? "")),
                "no_encontrado" => Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.NoEncontrado),
                "no_confirmado" => Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.NoConfirmado),
                "destinatario_no_valido" => Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.DestinatarioNoValido),
                "contenido_no_valido" => Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.ContenidoDemasiadoLargo),
                _ => Result.Fallo<EnvioAvisoReclamado>(ErroresIncidenteSeguridad.SoloActorDePlataforma),
            };
        }, cancellationToken);

    public Task<Result> ResolverEnvioAsync(Guid envioId, bool enviado, string? error, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var texto = error ?? string.Empty;
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_resolver_envio_aviso_seguridad({envioId}, {enviado}, {texto}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                codigo = "no_autorizado";
            }

            return codigo switch
            {
                "enviado" or "fallido" => Result.Exito(),
                "no_encontrado" or "no_enviando" => Result.Fallo(ErroresIncidenteSeguridad.EstadoNoValido),
                _ => Result.Fallo(ErroresIncidenteSeguridad.SoloActorDePlataforma),
            };
        }, cancellationToken);

    private sealed record FilaEnvioReclamado(
        string Codigo, Guid? EnvioId, bool Reclamado, string? Estado, string? Asunto, string? Cuerpo);
}
