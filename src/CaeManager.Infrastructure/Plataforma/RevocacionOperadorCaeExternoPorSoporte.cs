using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IRevocacionOperadorCaeExternoPorSoporte" />
/// <remarks>
/// Mismo patrón que <c>SegundoFactorDeCuentasIdentity.RestablecerPorSesionPrivilegiadaAsync</c>:
/// la función devuelve un código en vez de lanzar, para que cada negativa llegue con su
/// mensaje, y los valores van parametrizados por EF.
/// </remarks>
public sealed class RevocacionOperadorCaeExternoPorSoporte(
    CaeManagerDbContext contexto,
    PuertaAccesoDatos puertaAccesoDatos) : IRevocacionOperadorCaeExternoPorSoporte
{
    public Task<Result> RevocarAsync(
        Guid sesionPrivilegiadaId, Guid delegacionTenantId, string motivo, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_revocar_operador_cae_externo_por_soporte({sesionPrivilegiadaId}, {delegacionTenantId}, {motivo}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Contexto RLS firmado inválido o conexión sin el rol de soporte: nunca una
                // escritura, y nunca un 500.
                codigo = "contexto_no_valido";
            }

            // La función escribió por debajo de EF: lo que el circuito tuviera rastreado de la
            // delegación, la operación y las carteras ya no es cierto.
            Desenganchar();

            return codigo switch
            {
                "revocada" => Result.Exito(),
                // Inexistente, de otro Tenant o de Soporte: el mismo mensaje, para no revelar
                // qué Operadores CAE externos operan sobre qué Tenants propietarios.
                "delegacion_no_encontrada" => Result.Fallo(
                    Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación.")),
                "ya_inactiva" => Result.Fallo(
                    Error.Crear("DelegacionTenant.YaInactiva", "Esta delegación ya estaba revocada.")),
                "motivo_no_valido" => Result.Fallo(Error.Crear(
                    "DelegacionTenant.MotivoNoValido",
                    "Indica el motivo y la referencia de la petición del Tenant propietario.")),
                _ => Result.Fallo(Error.Crear(
                    "DelegacionTenant.RevocacionPorSoporteDenegada",
                    "La base de datos no autorizó la revocación (" + codigo + "). " +
                    "Comprueba que la sesión de soporte sigue abierta y tiene la concesión para esta organización.")),
            };
        }, cancellationToken);

    private void Desenganchar()
    {
        foreach (var entrada in contexto.ChangeTracker.Entries()
                     .Where(e => e.Entity is DelegacionTenant or AsignacionOperacion or AsignacionCartera)
                     .ToList())
            entrada.State = EntityState.Detached;
    }
}
