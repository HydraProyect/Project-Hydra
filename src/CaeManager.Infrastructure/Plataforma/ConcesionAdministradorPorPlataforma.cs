using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IConcesionAdministradorPorPlataforma" />
/// <remarks>
/// Mismo patrón que <c>RevocacionOperadorCaeExternoPorSoporte</c>: la función devuelve un código
/// en vez de lanzar, para que cada negativa llegue con su mensaje, y los valores van
/// parametrizados por EF.
/// </remarks>
public sealed class ConcesionAdministradorPorPlataforma(
    CaeManagerDbContext contexto,
    PuertaAccesoDatos puertaAccesoDatos) : IConcesionAdministradorPorPlataforma
{
    public Task<Result> ConcederAsync(
        Guid sesionPrivilegiadaId, Guid usuarioId, string motivo, CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_conceder_administrador_por_plataforma({sesionPrivilegiadaId}, {usuarioId}, {motivo}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Contexto RLS firmado inválido o conexión sin el rol de soporte: nunca una
                // escritura, y nunca un 500.
                codigo = "contexto_no_valido";
            }

            // La función escribió por debajo de EF: lo que el circuito tuviera rastreado de la
            // cuenta ya no es cierto.
            Desenganchar();

            return codigo switch
            {
                "concedido" => Result.Exito(),
                // Inexistente, de otro Tenant o desactivada: el mismo mensaje.
                "cuenta_no_encontrada" => Result.Fallo(
                    Error.Crear("Usuarios.NoEncontrado", "No encontramos esa cuenta en esta organización.")),
                "ya_es_administrador" => Result.Fallo(
                    Error.Crear("Usuarios.YaEsAdministrador", "Esta cuenta ya es Administrador.")),
                "motivo_no_valido" => Result.Fallo(
                    Error.Crear("Usuarios.MotivoNoValido", "Indica el motivo y la referencia de la petición.")),
                _ => Result.Fallo(Error.Crear(
                    "Usuarios.ConcesionPorPlataformaDenegada",
                    "La base de datos no autorizó la concesión (" + codigo + "). " +
                    "Comprueba que la sesión de soporte sigue abierta y tiene la concesión para esta organización.")),
            };
        }, cancellationToken);

    private void Desenganchar()
    {
        foreach (var entrada in contexto.ChangeTracker.Entries()
                     .Where(e => e.Entity is ApplicationUser)
                     .ToList())
            entrada.State = EntityState.Detached;
    }
}
