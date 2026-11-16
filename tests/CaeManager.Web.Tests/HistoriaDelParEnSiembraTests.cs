using System.Collections;
using System.Linq.Expressions;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Ronda 5 de Codex en la PR #2237 (media, previo, solo demostración): una Asignación de Operación Outbound externa del
/// Tenant entero suspendida, aunque siga amparada por su DelegacionTenant de origen, es historia del par. Si no lo
/// fuera, una siembra (<c>CoberturaPantallasSeeder</c> y las demás que llaman a <c>AbrirOperacionDelegadaAsync</c>)
/// abriría otra Vigente del Tenant entero: el writer solo reutiliza una Vigente y el índice único solo limita las
/// Vigentes, así que el acceso volvería durante la suspensión. Lo mismo con una programada, que ninguna siembra crea.
///
/// <para>
/// Lógica de la consulta, no RLS: el contexto sirve cada conjunto desde memoria (LINQ a objetos), con el proveedor
/// SQLite solo para que exista el modelo. La lectura bajo <c>cae_app_runtime</c> la cubre
/// <c>ProcedenciaDeOperacionExternaBajoRuntimeTests</c> en la suite de integración.
/// </para>
/// </summary>
public class HistoriaDelParEnSiembraTests
{
    private static readonly DateTime Ahora = DateTime.UtcNow;

    private readonly Guid _propietario = Guid.NewGuid();
    private readonly Guid _operador = Guid.NewGuid();

    private DelegacionTenant Delegacion() => new(_operador, _propietario);

    private AsignacionOperacion OperacionDelTenantEntero(DelegacionTenant origen, DateTime desde)
    {
        var operacion = AsignacionOperacion.Externa(
            _propietario, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, Ahora);
        operacion.FijarOrigen(origen);
        return operacion;
    }

    private static Task<bool> TieneHistoriaAsync(DelegacionTenant delegacion, params AsignacionOperacion[] operaciones)
    {
        var contexto = new ContextoEnMemoria()
            .Con(delegacion)
            .Con(operaciones)
            .Con<AsignacionOperadorDelegado>();
        return HistoriaDelParEnSiembra.TieneHistoriaAsync(contexto, delegacion, CancellationToken.None);
    }

    [Fact]
    public async Task Control_una_operacion_vigente_y_amparada_no_es_historia()
    {
        var delegacion = Delegacion();
        var vigente = OperacionDelTenantEntero(delegacion, Ahora.AddDays(-30));

        vigente.Estado.Should().Be(EstadoAsignacion.Vigente, "control: la operación nace vigente");
        (await TieneHistoriaAsync(delegacion, vigente)).Should().BeFalse(
            "una siembra ya hecha no tiene historia y la siembra sigue siendo idempotente");
    }

    [Fact]
    public async Task Una_operacion_del_Tenant_entero_suspendida_y_amparada_es_historia()
    {
        var delegacion = Delegacion();
        var suspendida = OperacionDelTenantEntero(delegacion, Ahora.AddDays(-30));
        suspendida.Suspender();

        suspendida.EstaAmparadaPor(delegacion, Ahora).Should().BeTrue(
            "control: la suspendida sigue amparada por su DelegacionTenant de origen");
        (await TieneHistoriaAsync(delegacion, suspendida)).Should().BeTrue(
            "la siembra no puede abrir otra Vigente del Tenant entero durante la suspensión");
    }

    [Fact]
    public async Task Una_suspendida_junto_a_una_vigente_amparada_es_historia()
    {
        var delegacion = Delegacion();
        var suspendida = OperacionDelTenantEntero(delegacion, Ahora.AddDays(-60));
        suspendida.Suspender();
        var vigente = OperacionDelTenantEntero(delegacion, Ahora.AddDays(-30));

        (await TieneHistoriaAsync(delegacion, suspendida, vigente)).Should().BeTrue(
            "la suspendida es historia aunque quede otra Vigente y amparada");
    }

    [Fact]
    public async Task Una_operacion_programada_es_historia()
    {
        var delegacion = Delegacion();
        var programada = OperacionDelTenantEntero(delegacion, Ahora.AddDays(30));

        programada.Estado.Should().Be(EstadoAsignacion.Programada, "control: nace con la vigencia en el futuro");
        (await TieneHistoriaAsync(delegacion, programada)).Should().BeTrue(
            "ninguna siembra programa operaciones: la dejó así alguien");
    }

    // ---------------------------------------------------------------- Infraestructura de la prueba

    /// <summary>Contexto con cada conjunto servido desde memoria; el proveedor SQLite no abre nunca la conexión.</summary>
    private sealed class ContextoEnMemoria() : CaeManagerDbContext(
        new DbContextOptionsBuilder<CaeManagerDbContext>().UseSqlite("Data Source=:memory:").Options,
        new EphemeralDataProtectionProvider(),
        new TenantActualAmbiental())
    {
        private readonly Dictionary<Type, object> conjuntos = [];

        public ContextoEnMemoria Con<T>(params T[] datos) where T : class
        {
            conjuntos[typeof(T)] = new ConjuntoEnMemoria<T>(datos);
            return this;
        }

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (!conjuntos.TryGetValue(typeof(TEntity), out var conjunto))
                conjuntos[typeof(TEntity)] = conjunto = new ConjuntoEnMemoria<TEntity>([]);
            return (DbSet<TEntity>)conjunto;
        }
    }

    private sealed class ConjuntoEnMemoria<T>(IEnumerable<T> datos) : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        private readonly IQueryable<T> consulta = new ConsultaAsincrona<T>(datos.ToList().AsQueryable());

        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => consulta.Expression;
        IQueryProvider IQueryable.Provider => consulta.Provider;
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => consulta.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => consulta.GetEnumerator();
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            ((IAsyncEnumerable<T>)consulta).GetAsyncEnumerator(cancellationToken);
    }

    private sealed class ConsultaAsincrona<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
    {
        public Type ElementType => typeof(T);
        public Expression Expression => interna.Expression;
        public IQueryProvider Provider => new ProveedorAsincrono(interna.Provider);
        public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new EnumeradorAsincrono<T>(GetEnumerator());
    }

    private sealed class ProveedorAsincrono(IQueryProvider interno) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new ConsultaAsincrona<TElement>(interno.CreateQuery<TElement>(expression));
        public object? Execute(Expression expression) => interno.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var tipoResultado = typeof(TResult).GetGenericArguments()[0];
            var resultado = interno.Execute(expression);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado).Invoke(null, [resultado])!;
        }
    }

    private sealed class EnumeradorAsincrono<T>(IEnumerator<T> interno) : IAsyncEnumerator<T>
    {
        public T Current => interno.Current;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());
        public ValueTask DisposeAsync() { interno.Dispose(); return ValueTask.CompletedTask; }
    }
}
