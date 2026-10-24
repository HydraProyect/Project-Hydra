using System.Collections;
using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Integraciones;
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

    /// <summary>
    /// Revisión de seguridad 2026-10-23, Z1-3. La FK compuesta de AsignacionesCartera impide en
    /// PostgreSQL una cartera del Tenant visitado bajo una Asignación de Operación de otro Tenant
    /// propietario; este contexto sin base de datos no la aplica, que es justo lo que permite probar
    /// que la consulta falla en cerrado por sí misma si esa fila mal formada existiera.
    /// </summary>
    [Fact]
    public async Task La_cartera_no_concede_el_ambito_de_una_Operacion_de_otro_Tenant_propietario()
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.GestorCae);
        await escenario.AplicarMiddlewareAsync();
        var tenantActual = new TenantActualAmbiental { TenantId = escenario.TenantVisitado };
        var ahora = DateTime.UtcNow;
        var clientePropio = Guid.NewGuid();
        var clienteDeOtroTenant = Guid.NewGuid();
        var operacionPropia = AsignacionOperacion.Externa(escenario.TenantVisitado, escenario.TenantOrigen,
            ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clientePropio), ahora.AddHours(-1), null, ahora);
        // Misma posición (Operador CAE de origen, vigente), pero el Tenant propietario es otro.
        var operacionDeOtroTenant = AsignacionOperacion.Externa(Guid.NewGuid(), escenario.TenantOrigen,
            ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDeOtroTenant), ahora.AddHours(-1), null, ahora);
        var cartera = AsignacionCartera.Externa(operacionPropia, escenario.Usuario, Roles.GestorCae,
            AmbitoAsignacion.Universal, ahora.AddHours(-1), null, ahora);
        var usuarios = new[] { new ApplicationUser { Id = escenario.Usuario } };

        // Control: la cartera bien formada sí concede el ámbito de su Operación.
        await using (var db = new ContextoDeCarteras(escenario, tenantActual, [operacionPropia, operacionDeOtroTenant], [cartera], usuarios))
        {
            var alcance = new AlcanceDatosService(db, escenario.Servicio, tenantActual, new SesionPrivilegiadaAusente());
            (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().Equal(clientePropio);
        }

        // La misma cartera, apuntando a la Operación de otro Tenant propietario: no concede nada.
        typeof(AsignacionCartera).GetProperty(nameof(AsignacionCartera.AsignacionOperacionId))!
            .SetValue(cartera, operacionDeOtroTenant.Id);
        await using (var db = new ContextoDeCarteras(escenario, tenantActual, [operacionPropia, operacionDeOtroTenant], [cartera], usuarios))
        {
            var alcance = new AlcanceDatosService(db, escenario.Servicio, tenantActual, new SesionPrivilegiadaAusente());
            (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty(
                "una Asignación de Operación de otro Tenant propietario no ampara una cartera del Tenant en el que se opera");
        }
    }

    /// <summary>
    /// Revisión de seguridad 2026-10-23, Z1-5: una conexión que no se puede leer no es visible
    /// (antes, «sin fila» se confundía con «buzón general» y devolvía true). Los tres casos con fila
    /// fijan que el arreglo no rompe los buzones generales ni el personal propio.
    /// </summary>
    [Fact]
    public async Task Una_conexion_de_integracion_que_no_se_puede_leer_no_es_visible()
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.GestorCae);
        await escenario.AplicarMiddlewareAsync();
        var tenantActual = new TenantActualAmbiental { TenantId = escenario.TenantVisitado };
        var general = new ConexionIntegracion("cae@buzon-simulado.local", "General");
        var personalPropio = new ConexionIntegracion("propio@buzon-simulado.local", "Propio", gestorPropietarioId: escenario.Usuario);
        var personalAjeno = new ConexionIntegracion("ajeno@buzon-simulado.local", "Ajeno", gestorPropietarioId: Guid.NewGuid());
        await using var db = new ContextoDeCarteras(escenario, tenantActual, [], [], [],
            [general, personalPropio, personalAjeno]);
        var alcance = new AlcanceDatosService(db, escenario.Servicio, tenantActual, new SesionPrivilegiadaAusente());

        (await alcance.ConexionIntegracionVisibleAsync(Guid.NewGuid())).Should().BeFalse(
            "una conexión inexistente, de otro Tenant o eliminada falla en cerrado");
        (await alcance.ConexionIntegracionVisibleAsync(general.Id)).Should().BeTrue();
        (await alcance.ConexionIntegracionVisibleAsync(personalPropio.Id)).Should().BeTrue();
        (await alcance.ConexionIntegracionVisibleAsync(personalAjeno.Id)).Should().BeFalse();
    }

    private sealed class ContextoDeCarteras : CaeManagerDbContext
    {
        private readonly AsignacionOperacion[] operaciones;
        private readonly AsignacionCartera[] carteras;
        private readonly ConexionIntegracion[] conexiones;

        public ContextoDeCarteras(Escenario escenario, TenantActualAmbiental tenantActual, AsignacionOperacion[] operaciones,
            AsignacionCartera[] carteras, ApplicationUser[] usuarios, ConexionIntegracion[]? conexiones = null)
            : base(new DbContextOptionsBuilder<CaeManagerDbContext>().Options, escenario.Protector, tenantActual)
        {
            this.operaciones = operaciones;
            this.carteras = carteras;
            this.conexiones = conexiones ?? [];
            // Después del constructor de EF, que inicializa los DbSets de Identity.
            Users = new ConjuntoDePruebas<ApplicationUser>(usuarios);
        }

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(AsignacionOperacion))
                return (DbSet<TEntity>)(object)new ConjuntoDePruebas<AsignacionOperacion>(operaciones);
            if (typeof(TEntity) == typeof(AsignacionCartera))
                return (DbSet<TEntity>)(object)new ConjuntoDePruebas<AsignacionCartera>(carteras);
            if (typeof(TEntity) == typeof(ConexionIntegracion))
                return (DbSet<TEntity>)(object)new ConjuntoDePruebas<ConexionIntegracion>(conexiones);
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