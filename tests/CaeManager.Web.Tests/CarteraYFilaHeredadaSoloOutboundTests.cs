using System.Collections;
using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// B-02a, segunda revisión de Codex: ningún lector ni escritor de cartera, de marca de principal o de fila
/// heredada de Operador Delegado reacciona ante una Asignación de Operación de un servicio distinto de
/// Outbound. La fila heredada abre Outbound por sí sola (vía heredada de <c>TenantsBeneficiariosAutorizados</c>),
/// así que una cartera Inbound no puede emitirla (relevo), sostenerla (retirada), contar como cartera viva del
/// Operador CAE externo (guarda de revocación de <c>EquipoDelOperadorCaeExterno</c>) ni dejar a un Tenant
/// propietario «ya en cartera».
///
/// <para>
/// Lógica de las consultas, no RLS: el contexto sirve cada conjunto desde memoria (LINQ a objetos), con el
/// proveedor SQLite solo para que el seguimiento de cambios exista. La RLS de estos catálogos la cubren las
/// pruebas de integración (<c>ExpiracionAsignacionesBajoRlsTests</c>, <c>PrincipalDeCarteraBajoRuntimeTests</c>).
/// </para>
/// </summary>
public class CarteraYFilaHeredadaSoloOutboundTests
{
    private static readonly DateTime Ahora = DateTime.UtcNow;
    private static readonly DateTime Desde = Ahora.AddDays(-1);

    private readonly Tenant _operador = new("Operador CAE externo", PerfilVocabularioTenant.Consultora);
    private readonly Tenant _propietario = new("Tenant propietario con Outbound e Inbound");
    private readonly Guid _gestor = Guid.NewGuid();

    private AsignacionOperacion Operacion(Tenant propietario, ServicioCae servicio) =>
        AsignacionOperacion.Externa(propietario.Id, _operador.Id, servicio, AmbitoAsignacion.Universal, Desde, null, Desde);

    private static AsignacionCartera Cartera(AsignacionOperacion operacion, Guid usuario, string rol = Roles.GestorCae) =>
        AsignacionCartera.Externa(operacion, usuario, rol, AmbitoAsignacion.Universal, Desde, null, Desde);

    /// <summary>Hallazgo 1: la marca de principal bajo una operación Inbound no se lista, no se apaga ni se releva.</summary>
    [Fact]
    public async Task La_marca_de_principal_bajo_una_operacion_Inbound_no_se_lista_no_se_apaga_ni_se_releva()
    {
        var conOutbound = new Tenant("Tenant propietario de control, Outbound");
        var sinPrincipal = new Tenant("Tenant propietario con Inbound sin principal");
        var inbound = Operacion(_propietario, ServicioCae.Inbound);
        var outbound = Operacion(conOutbound, ServicioCae.Outbound);
        var inboundSinPrincipal = Operacion(sinPrincipal, ServicioCae.Inbound);
        var principalInbound = Cartera(inbound, _gestor);
        principalInbound.DesignarPrincipal();
        var principalOutbound = Cartera(outbound, _gestor);
        principalOutbound.DesignarPrincipal();
        var coordinador = Guid.NewGuid();
        var delCoordinador = Cartera(inboundSinPrincipal, coordinador, Roles.CoordinadorCae);

        await using var db = new ContextoEnMemoria()
            .Con(inbound, outbound, inboundSinPrincipal)
            .Con(principalInbound, principalOutbound, delCoordinador)
            .Con(_operador, _propietario, conOutbound, sinPrincipal)
            .Con(new DelegacionTenant(_operador.Id, sinPrincipal.Id));
        var catalogo = new CatalogoIncorporacionCartera(db, null!);

        (await catalogo.ObtenerOperacionesDondeEsPrincipalAsync(_operador.Id, _gestor))
            .Should().ContainSingle("la cartera principal bajo la operación Inbound no es una principal de Outbound")
            .Which.AsignacionOperacionId.Should().Be(outbound.Id);

        (await catalogo.ApagarPrincipalAsync(_operador.Id, inbound.Id, _gestor)).Should().BeFalse();
        principalInbound.EsPrincipal.Should().BeTrue("apagar desde Outbound no toca la marca de Inbound");

        (await catalogo.RelevarPrincipalAsync(sinPrincipal.Id, _operador.Id, inboundSinPrincipal.Id, coordinador))
            .Should().Be(ResultadoRelevoPrincipal.SinRelevo,
                "el relevo sobre una operación Inbound emitiría o marcaría una cartera que abre Outbound por la fila heredada");
        delCoordinador.EsPrincipal.Should().BeFalse();
        (await catalogo.EncenderPrincipalAsync(_operador.Id, inboundSinPrincipal.Id, coordinador)).Should().BeFalse();
        delCoordinador.EsPrincipal.Should().BeFalse();

        (await catalogo.ApagarPrincipalAsync(_operador.Id, outbound.Id, _gestor)).Should().BeTrue("control: en Outbound sí");
    }

    /// <summary>
    /// Hallazgo 2: una cartera solo Inbound no es cartera viva del Operador CAE externo. Es la lista con la que
    /// <c>EquipoDelOperadorCaeExterno</c> decide si un Gestor CAE puede revocar el acceso de Outbound.
    /// </summary>
    [Fact]
    public async Task Una_cartera_solo_Inbound_no_es_cartera_viva_del_Operador_CAE_externo()
    {
        var inbound = Operacion(_propietario, ServicioCae.Inbound);
        var conOutbound = new Tenant("Tenant propietario de control, Outbound");
        var outbound = Operacion(conOutbound, ServicioCae.Outbound);
        var otroGestor = Guid.NewGuid();

        await using var db = new ContextoEnMemoria()
            .Con(inbound, outbound)
            .Con(Cartera(inbound, _gestor), Cartera(outbound, otroGestor))
            .Con(_operador, _propietario, conOutbound);
        var catalogo = new CatalogoIncorporacionCartera(db, null!);

        var vivas = await catalogo.ObtenerCarterasVivasAsync(_operador.Id, null);

        vivas.Select(c => (c.UsuarioId, c.PropietarioTenantId)).Should().Equal((otroGestor, conOutbound.Id));
        (await catalogo.ObtenerCarterasVivasAsync(_operador.Id, _propietario.Id)).Should().BeEmpty(
            "con una cartera solo Inbound el Gestor CAE no supera la guarda de revocación de Outbound");
    }

    /// <summary>
    /// Hallazgo 3: el listado y la retirada de la cartera de un Gestor CAE no ven la Inbound, y una cartera Inbound
    /// no deja al Tenant propietario «ya en cartera».
    /// </summary>
    [Fact]
    public async Task La_cartera_Inbound_no_se_lista_no_se_retira_ni_cuenta_como_ya_en_cartera()
    {
        var inbound = Operacion(_propietario, ServicioCae.Inbound);
        var outbound = Operacion(_propietario, ServicioCae.Outbound);
        var carteraInbound = Cartera(inbound, _gestor);

        await using var db = new ContextoEnMemoria()
            .Con(inbound, outbound)
            .Con(carteraInbound)
            .Con(_operador, _propietario)
            .Con(new DelegacionTenant(_operador.Id, _propietario.Id));
        var catalogo = new CatalogoIncorporacionCartera(db, null!);

        (await catalogo.ObtenerCarteraUniversalAsync(_operador.Id, _gestor)).Should().BeEmpty();
        (await catalogo.RetirarCarteraUniversalAsync(_propietario.Id, _operador.Id, _gestor, Guid.NewGuid()))
            .Should().BeFalse("retirar desde la gestión de Outbound no cierra la cartera Inbound");
        carteraInbound.Estado.Should().Be(EstadoAsignacion.Vigente);

        (await catalogo.ObtenerCandidatosAsync(_operador.Id, _gestor))
            .Should().ContainSingle("la cartera Inbound no deja al Tenant propietario ya en cartera de Outbound")
            .Which.AsignacionOperacionId.Should().Be(outbound.Id);

        (await catalogo.FiltrarCarterasVigentesAsync([carteraInbound.Id])).Should().BeEmpty();

        // La puerta de emisión de la solicitud y de la propuesta de apoyo: una operación Inbound no es vigente para emitir.
        (await catalogo.ObtenerOperacionVigenteAsync(inbound.Id)).Should().BeNull();
        (await catalogo.ObtenerOperacionVigenteAsync(outbound.Id)).Should().NotBeNull("control");
    }

    /// <summary>
    /// Hallazgo 3: al retirar la cartera Outbound, una cartera Inbound vigente del mismo Gestor CAE sobre el
    /// mismo Tenant propietario no mantiene viva la fila heredada, que abre Outbound por sí sola.
    /// </summary>
    [Fact]
    public async Task Una_cartera_Inbound_vigente_no_mantiene_viva_la_fila_heredada_al_retirar_la_Outbound()
    {
        var inbound = Operacion(_propietario, ServicioCae.Inbound);
        var outbound = Operacion(_propietario, ServicioCae.Outbound);
        var carteraOutbound = Cartera(outbound, _gestor);
        var vinculo = new DelegacionTenant(_operador.Id, _propietario.Id);
        var fila = new AsignacionOperadorDelegado(vinculo.Id, _gestor, Roles.GestorCae);

        await using var db = new ContextoEnMemoria()
            .Con(inbound, outbound)
            .Con(carteraOutbound, Cartera(inbound, _gestor))
            .Con(_operador, _propietario)
            .Con(vinculo)
            .Con(fila);
        var catalogo = new CatalogoIncorporacionCartera(db, null!);

        (await catalogo.RetirarCarteraUniversalAsync(_propietario.Id, _operador.Id, _gestor, Guid.NewGuid()))
            .Should().BeTrue();

        carteraOutbound.Estado.Should().Be(EstadoAsignacion.Cerrada, "control: la Outbound se retira");
        db.De<AsignacionOperadorDelegado>().Retirados.Should().Equal([fila],
            "sin cartera Outbound vigente la fila heredada sobra, aunque quede una cartera Inbound");
    }

    /// <summary>
    /// Hallazgo 4: el directorio de /usuarios no pinta como alcance de Outbound una cartera bajo una operación Inbound.
    /// </summary>
    [Fact]
    public async Task El_directorio_no_cuenta_como_cartera_vigente_la_de_una_operacion_Inbound()
    {
        var inbound = Operacion(_propietario, ServicioCae.Inbound);
        var outboundDelMismo = Operacion(_propietario, ServicioCae.Outbound);
        var otroGestor = Guid.NewGuid();

        var tenant = new TenantActualAmbiental { TenantId = _propietario.Id };
        await using var db = new ContextoEnMemoria(tenant)
            .Con(inbound, outboundDelMismo)
            .Con(Cartera(inbound, _gestor), Cartera(outboundDelMismo, otroGestor))
            .ConUsuarios(
                new ApplicationUser { Id = _gestor, TenantId = _operador.Id },
                new ApplicationUser { Id = otroGestor, TenantId = _operador.Id });
        var directorio = new DirectorioUsuariosTenant(null!, db, tenant, new PuertaAccesoDatos(), db, null!);

        var carteras = await directorio.ObtenerCarterasVigentesAsync();

        carteras.Keys.Should().Equal([otroGestor], "la cartera bajo la operación Inbound no da alcance en Outbound");
    }

    // ---------------------------------------------------------------- Infraestructura de la prueba

    /// <summary>
    /// Contexto con cada conjunto servido desde memoria. El proveedor SQLite no abre nunca la conexión: está
    /// para que existan el modelo y el seguimiento de cambios (<c>PrincipalDeOperacion</c> los consulta).
    /// </summary>
    private sealed class ContextoEnMemoria(TenantActualAmbiental? tenant = null) : CaeManagerDbContext(
        new DbContextOptionsBuilder<CaeManagerDbContext>().UseSqlite("Data Source=:memory:").Options,
        new EphemeralDataProtectionProvider(),
        tenant ?? new TenantActualAmbiental())
    {
        private readonly Dictionary<Type, object> conjuntos = [];

        public ContextoEnMemoria Con<T>(params T[] datos) where T : class
        {
            conjuntos[typeof(T)] = new ConjuntoEnMemoria<T>(datos);
            return this;
        }

        public ContextoEnMemoria ConUsuarios(params ApplicationUser[] usuarios)
        {
            Users = new ConjuntoEnMemoria<ApplicationUser>(usuarios);
            return this;
        }

        public ConjuntoEnMemoria<T> De<T>() where T : class => (ConjuntoEnMemoria<T>)Set<T>();

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

        /// <summary>Lo que el código pidió borrar, en orden.</summary>
        public List<T> Retirados { get; } = [];

        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => consulta.Expression;
        IQueryProvider IQueryable.Provider => consulta.Provider;
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => consulta.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => consulta.GetEnumerator();
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            ((IAsyncEnumerable<T>)consulta).GetAsyncEnumerator(cancellationToken);

        public override EntityEntry<T> Remove(T entity)
        {
            Retirados.Add(entity);
            return null!;
        }

        public override void RemoveRange(IEnumerable<T> entities) => Retirados.AddRange(entities);
        public override void RemoveRange(params T[] entities) => Retirados.AddRange(entities);
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
