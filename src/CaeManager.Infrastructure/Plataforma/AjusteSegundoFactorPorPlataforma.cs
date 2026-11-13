using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IAjusteSegundoFactorPorPlataforma" />
/// <remarks>
/// Mismo patrón que <c>ConcesionAdministradorPorPlataforma</c>: la función devuelve un código en vez
/// de lanzar, para que cada negativa llegue con su mensaje, y los valores van parametrizados por EF.
/// </remarks>
public sealed class AjusteSegundoFactorPorPlataforma(
    CaeManagerDbContext contexto,
    PuertaAccesoDatos puertaAccesoDatos) : IAjusteSegundoFactorPorPlataforma
{
    public Task<Result> CambiarAsync(
        Guid sesionPrivilegiadaId, bool exigeSegundoFactorTodosLosPerfiles, string motivo,
        CancellationToken cancellationToken) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_cambiar_segundo_factor_tenant_por_plataforma({sesionPrivilegiadaId}, {exigeSegundoFactorTodosLosPerfiles}, {motivo}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Contexto RLS firmado inválido o conexión sin el rol de soporte: nunca una
                // escritura, y nunca un 500.
                codigo = "contexto_no_valido";
            }

            // La función escribió por debajo de EF: lo que el circuito tuviera rastreado de los
            // parámetros ya no es cierto.
            Desenganchar();

            return codigo switch
            {
                "cambiado" => Result.Exito(),
                "sin_cambios" => Result.Fallo(Error.Crear(
                    "SeguridadAcceso.SinCambios", "La organización ya tiene ese ajuste.")),
                "motivo_no_valido" => Result.Fallo(Error.Crear(
                    "SeguridadAcceso.MotivoNoValido", "Indica el motivo y la referencia de la petición.")),
                _ => Result.Fallo(Error.Crear(
                    "SeguridadAcceso.AjustePorPlataformaDenegado",
                    "La base de datos no autorizó el cambio (" + codigo + "). " +
                    "Comprueba que la sesión de soporte sigue abierta y tiene la concesión para esta organización.")),
            };
        }, cancellationToken);

    private void Desenganchar()
    {
        foreach (var entrada in contexto.ChangeTracker.Entries()
                     .Where(e => e.Entity is ParametroSistema)
                     .ToList())
            entrada.State = EntityState.Detached;
    }
}
