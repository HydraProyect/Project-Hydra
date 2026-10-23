using System.Collections;
using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Tests;

public partial class LoginLocalEnAsignacionDelegadaTests
{
    [Theory]
    [InlineData(Roles.GestorCae, Roles.GestorCae, false)]
    [InlineData(Roles.GestorCae, Roles.GestorCae, true)]
    [InlineData(Roles.CoordinadorCae, Roles.GestorCae, true)]
    [InlineData(Roles.GestorCae, Roles.CoordinadorCae, false)]
    [InlineData(Roles.GestorCae, Roles.CoordinadorCae, true)]
    [InlineData(Roles.CoordinadorCae, Roles.Consulta, false)]
    public async Task El_login_local_heredado_conserva_el_ambito_efectivo_del_rol_de_la_asignacion(
        string rolOrigen, string rolAsignado, bool conCartera)
    {
        var clienteEmpresarialId = Guid.NewGuid();
        foreach (var esSso in new[] { true, false })
        {
            using var escenario = await Escenario.CrearAsync(rolOrigen, rolAsignado, esSso: esSso);
            await escenario.AplicarMiddlewareAsync();
            var tenantActual = new TenantActualAmbiental { TenantId = escenario.TenantVisitado };
            var usuarioConCartera = rolAsignado == Roles.CoordinadorCae ? Guid.NewGuid() : escenario.Usuario;
            var ahora = DateTime.UtcNow;
            var operacion = AsignacionOperacion.Externa(escenario.TenantVisitado, escenario.TenantOrigen,
                ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteEmpresarialId), ahora.AddHours(-1), null, ahora);
            var carteras = conCartera
                ? new[] { AsignacionCartera.Externa(operacion, usuarioConCartera, Roles.GestorCae,
                    AmbitoAsignacion.Universal, ahora.AddHours(-1), null, ahora) }
                : [];
            // El Coordinador CAE hereda la cartera de su equipo; no tiene cartera propia.
            var usuarios = new[] { new ApplicationUser { Id = usuarioConCartera,
                CoordinadorUsuarioId = rolAsignado == Roles.CoordinadorCae ? escenario.Usuario : null } };
            await using var db = new ContextoDeCarteras(escenario, tenantActual, conCartera ? [operacion] : [], carteras, usuarios);
            var alcance = new AlcanceDatosService(db, escenario.Servicio, tenantActual, new SesionPrivilegiadaAusente());

            (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(esSso ? rolAsignado : Roles.Consulta);
            var accesoTotalEsperado = rolAsignado == Roles.Consulta;
            (await alcance.TieneAccesoTotalAsync()).Should().Be(accesoTotalEsperado,
                "el login local debe ver los mismos datos que SSO con esta asignación heredada");
            var visibles = await alcance.ObtenerClienteIdsVisiblesAsync();
            if (accesoTotalEsperado) visibles.Should().BeNull();
            else visibles.Should().Equal(conCartera ? [clienteEmpresarialId] : []);
        }
    }

    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData("")]
    public async Task Un_techo_distinto_de_Consulta_no_da_rol_en_la_asignacion_heredada_Consulta(string rolOrigen)
    {
        using var escenario = await Escenario.CrearAsync(rolOrigen, Roles.Consulta);

        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
        await escenario.AplicarMiddlewareAsync();
        escenario.Contexto.User.FindAll(System.Security.Claims.ClaimTypes.Role).Should().BeEmpty();
        var tenantActual = new TenantActualAmbiental { TenantId = escenario.TenantVisitado };
        await using var db = new ContextoDeCarteras(escenario, tenantActual, [], [], []);
        var alcance = new AlcanceDatosService(db, escenario.Servicio, tenantActual, new SesionPrivilegiadaAusente());
        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task El_rol_de_asignacion_heredada_se_revalida_en_cada_Tenant_propietario()
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.CoordinadorCae);
        await escenario.AplicarMiddlewareAsync();

        (await escenario.Servicio.ObtenerRolDeAsignacionHeredadaAsync()).Should().Be(Roles.CoordinadorCae);
        using (AmbitoTenantExplicito.Establecer(escenario.TenantOrigen))
            (await escenario.Servicio.ObtenerRolDeAsignacionHeredadaAsync()).Should().BeNull();
        using (AmbitoTenantExplicito.Establecer(Guid.NewGuid()))
            (await escenario.Servicio.ObtenerRolDeAsignacionHeredadaAsync()).Should().BeNull();
        using (AmbitoTenantExplicito.Establecer(escenario.TenantVisitado))
            (await escenario.Servicio.ObtenerRolDeAsignacionHeredadaAsync()).Should().Be(Roles.CoordinadorCae);
        escenario.Asignaciones.Clear();
        (await escenario.Servicio.ObtenerRolDeAsignacionHeredadaAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task El_rol_de_asignacion_heredada_no_se_expone_por_Operacion_ni_Sesion_Privilegiada(
        bool porOperacion, bool privilegiada)
    {
        using var escenario = await Escenario.CrearAsync(Roles.CoordinadorCae, Roles.GestorCae,
            porOperacion: porOperacion, privilegiada: privilegiada);

        (await escenario.Servicio.ObtenerRolDeAsignacionHeredadaAsync()).Should().BeNull();
    }
    private sealed class ContextoDeCarteras : CaeManagerDbContext
    {
        private readonly AsignacionOperacion[] operaciones;
        private readonly AsignacionCartera[] carteras;

        public ContextoDeCarteras(Escenario escenario, TenantActualAmbiental tenantActual, AsignacionOperacion[] operaciones,
            AsignacionCartera[] carteras, ApplicationUser[] usuarios)
            : base(new DbContextOptionsBuilder<CaeManagerDbContext>().Options, escenario.Protector, tenantActual)
        {
            this.operaciones = operaciones;
            this.carteras = carteras;
            // Después del constructor de EF, que inicializa los DbSets de Identity.
            Users = new ConjuntoDePruebas<ApplicationUser>(usuarios);
        }

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(AsignacionOperacion))
                return (DbSet<TEntity>)(object)new ConjuntoDePruebas<AsignacionOperacion>(operaciones);
            if (typeof(TEntity) == typeof(AsignacionCartera))
                return (DbSet<TEntity>)(object)new ConjuntoDePruebas<AsignacionCartera>(carteras);
            throw new NotSupportedException($"La prueba no debe consultar {typeof(TEntity).Name}.");
        }
    }

    private sealed class ConjuntoDePruebas<T>(IEnumerable<T> datos) : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        private readonly IQueryable<T> consulta = Consulta(datos);
        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => consulta.Expression;
        IQueryProvider IQueryable.Provider => consulta.Provider;
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => consulta.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => consulta.GetEnumerator();
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            ((IAsyncEnumerable<T>)consulta).GetAsyncEnumerator(cancellationToken);
    }
}