using System.Data;
using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La <see cref="TransaccionDeComando"/> real, con su estrategia de ejecución, ante un fallo transitorio del primer intento
/// (revisión Codex r4 de la PR #2172, hallazgo ALTO): el reintento tiene que volver a resolver el alcance de datos y no
/// servirse de la lista memoizada en el primer intento. Sin servidor: la conexión, la transacción y su confirmación se
/// interceptan antes de llegar a ninguna base, y la estrategia reintenta una vez ante <see cref="FalloTransitorio"/>,
/// como haría <c>NpgsqlRetryingExecutionStrategy</c> ante un error transitorio de PostgreSQL. El alcance es un doble que
/// memoiza como <c>AlcanceDatosService</c> hasta que alguien lo invalida; la composición con el alcance real y la
/// Asignación de Cartera revocada en PostgreSQL queda para la suite de integración.
/// </summary>
public class TransaccionDeComandoReintentoAlcanceTests
{
    private static readonly Error NoEncontrada = Error.Crear("Prueba.NoEncontrada", "No encontramos esta acreditación.");

    [Fact]
    public async Task El_reintento_vuelve_a_resolver_el_alcance_y_no_confirma_con_la_cartera_revocada_entre_intentos()
    {
        var centro = Guid.NewGuid();
        var alcance = new AlcanceQueMemoiza(centro);
        var registro = new RegistroTransacciones();
        using var contexto = CrearContextoSinServidor(registro);
        var intentos = 0;

        var resultado = await new TransaccionDeComando(contexto, alcance).EjecutarAsync(_ =>
        {
            intentos++;
            if (!alcance.CentroVisible(centro))
                return Task.FromResult(Result.Fallo(NoEncontrada));
            if (intentos == 1)
            {
                // El primer intento ya autorizó con la cartera vigente; antes de confirmar, un Coordinador CAE cierra la
                // Asignación de Cartera del Gestor CAE y la conexión cae de forma transitoria.
                alcance.Revocar(centro);
                throw new FalloTransitorio();
            }
            return Task.FromResult(Result.Exito());
        });

        intentos.Should().Be(2, "la estrategia repite la operación entera tras el fallo transitorio");
        resultado.EsFallido.Should().BeTrue("el reintento autoriza con la cartera de ahora, ya sin el Centro");
        resultado.Error.Should().Be(NoEncontrada);
        registro.Confirmadas.Should().Be(0, "nada se confirma con el alcance del primer intento");
        alcance.Resoluciones.Should().Be(2, "cada intento resuelve el alcance desde la fuente");
    }

    [Fact]
    public async Task Sin_revocacion_el_reintento_confirma()
    {
        // Control del instrumento: la estrategia reintenta de verdad y la confirmación se cuenta, así que el cero del test
        // anterior es una decisión de autorización y no una transacción que nunca llega a confirmar.
        var centro = Guid.NewGuid();
        var alcance = new AlcanceQueMemoiza(centro);
        var registro = new RegistroTransacciones();
        using var contexto = CrearContextoSinServidor(registro);
        var intentos = 0;

        var resultado = await new TransaccionDeComando(contexto, alcance).EjecutarAsync(_ =>
        {
            intentos++;
            if (!alcance.CentroVisible(centro))
                return Task.FromResult(Result.Fallo(NoEncontrada));
            return intentos == 1 ? throw new FalloTransitorio() : Task.FromResult(Result.Exito());
        });

        intentos.Should().Be(2);
        resultado.EsExitoso.Should().BeTrue();
        registro.Confirmadas.Should().Be(1);
    }

    private static TenantActualAmbiental Tenant() => new() { TenantId = Guid.NewGuid() };

    private static CaeManagerDbContext CrearContextoSinServidor(RegistroTransacciones registro)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=sin-servidor;Username=x;Password=x",
                npgsql => npgsql.ExecutionStrategy(dependencias => new EstrategiaQueReintentaUnaVez(dependencias)))
            .AddInterceptors(new ConexionSinServidor(), new TransaccionSinServidor(registro))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), Tenant());
    }

    private sealed class FalloTransitorio : Exception;

    /// <summary>Memoiza los Centros visibles hasta que se invalida, como <c>AlcanceDatosService</c>.</summary>
    private sealed class AlcanceQueMemoiza(Guid centroEnCartera) : IInvalidadorAlcance
    {
        private readonly HashSet<Guid> _cartera = [centroEnCartera];
        private HashSet<Guid>? _memo;

        public int Resoluciones { get; private set; }

        public bool CentroVisible(Guid centroId)
        {
            if (_memo is null)
            {
                Resoluciones++;
                _memo = [.. _cartera];
            }
            return _memo.Contains(centroId);
        }

        public void Revocar(Guid centroId) => _cartera.Remove(centroId);

        public void Invalidar() => _memo = null;
    }

    private sealed class RegistroTransacciones
    {
        public int Confirmadas { get; set; }
    }

    private sealed class EstrategiaQueReintentaUnaVez(ExecutionStrategyDependencies dependencias)
        : ExecutionStrategy(dependencias, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is FalloTransitorio;
    }

    private sealed class ConexionSinServidor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class TransaccionSinServidor(RegistroTransacciones registro) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbTransaction>.SuppressWithResult(new TransaccionFalsa(connection, registro)));
    }

    private sealed class TransaccionFalsa(DbConnection conexion, RegistroTransacciones registro) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conexion;
        public override void Commit() => registro.Confirmadas++;
        public override void Rollback() { }
    }
}
