using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// La vía de Operación Inbound de <see cref="TenantsBeneficiariosAutorizados"/> (B-02b; DN-21; decisión de Chris del
/// 2026-11-16, opción A): una Asignación de Operación Inbound externa se ampara sola, sin DelegacionTenant, con la línea
/// Inbound vigente del Tenant TCE, y se elige explícitamente. Nunca se cruza con la vía de Outbound: cada operación
/// autoriza solo por el predicado de su servicio.
/// </summary>
public class ViaDeOperacionInboundTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid OperadorCaeExterno = Guid.NewGuid();
    private static readonly Guid TenantTce = Guid.NewGuid();
    private static readonly DateTime Ahora = DateTime.UtcNow;

    /// <summary>Sin ninguna DelegacionTenant: la vía Inbound no la necesita, y la de Outbound sin ella falla cerrada.</summary>
    private static readonly TenantsQueryContextFalso SinDelegaciones = new();

    private static T ConEstado<T>(T asignacion, EstadoAsignacion estado) where T : class
    {
        typeof(T).GetProperty("Estado")!.SetValue(asignacion, estado);
        return asignacion;
    }

    private static AsignacionOperacion Inbound(
        AmbitoAsignacion? ambito = null, Guid? operador = null, Guid? propietario = null, DateTime? hasta = null) =>
        ConEstado(AsignacionOperacion.Externa(
            propietario ?? TenantTce, operador ?? OperadorCaeExterno, ServicioCae.Inbound, ambito ?? AmbitoAsignacion.Universal,
            Ahora.AddDays(-10), hasta, Ahora), EstadoAsignacion.Vigente);

    private static AsignacionCartera Cartera(AsignacionOperacion operacion, Guid? usuario = null) =>
        ConEstado(AsignacionCartera.Externa(
            operacion, usuario ?? Usuario, "GestorCae", AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora),
            EstadoAsignacion.Vigente);

    /// <summary>
    /// Catálogo en memoria con la línea Inbound vigente en <paramref name="conLinea"/>: el papel de
    /// <c>app_operacion_inbound_amparada</c> en PostgreSQL.
    /// </summary>
    private sealed class ConLineaInbound(
        IEnumerable<AsignacionOperacion> operaciones, IEnumerable<AsignacionCartera> carteras, IEnumerable<Guid> conLinea,
        IEnumerable<Guid>? suspendidos = null) : IOperacionesQueryContext
    {
        private readonly OperacionesEnMemoria _interno = new(operaciones, carteras, delegaciones: []);
        private readonly HashSet<Guid> _conLinea = conLinea.ToHashSet();
        private readonly List<Guid> _suspendidos = suspendidos?.ToList() ?? [];

        public IQueryable<AsignacionOperacion> AsignacionesOperacion => _interno.AsignacionesOperacion;
        public IQueryable<AsignacionCartera> AsignacionesCartera => _interno.AsignacionesCartera;
        public IQueryable<Guid> TenantsSuspendidosIds => new TestAsyncQueryable<Guid>(_suspendidos.AsQueryable());
        public IQueryable<DelegacionTenant> DelegacionesTenant => _interno.DelegacionesTenant;

        public IQueryable<AsignacionOperacion> OperacionesInboundAmparadas =>
            AsignacionesOperacion.Where(o => o.Servicio == ServicioCae.Inbound && _conLinea.Contains(o.PropietarioTenantId));
    }

    private static Task<Guid?> ElegirInbound(IOperacionesQueryContext contexto, string? rol) =>
        TenantsBeneficiariosAutorizados.OperacionInboundQueAutorizaAsync(
            contexto, Usuario, OperadorCaeExterno, TenantTce, rol, Ahora, default);

    private static Task<ServicioCae?> Revalidar(IOperacionesQueryContext contexto, Guid operacionId, string? rol) =>
        TenantsBeneficiariosAutorizados.ServicioQueAutorizaLaOperacionAsync(
            contexto, SinDelegaciones, Usuario, OperadorCaeExterno, TenantTce, operacionId, rol, Ahora, default);

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    public async Task Un_perfil_de_organizacion_abre_con_papel_Inbound_la_operacion_del_Tenant_entero_sin_DelegacionTenant(string rol)
    {
        var operacion = Inbound();
        var contexto = new ConLineaInbound([operacion], [], [TenantTce]);

        (await ElegirInbound(contexto, rol)).Should().Be(operacion.Id);
        (await Revalidar(contexto, operacion.Id, rol)).Should().Be(ServicioCae.Inbound);
        (await TenantsBeneficiariosAutorizados.SigueAutorizadoPorOperacionAsync(
            contexto, SinDelegaciones, Usuario, OperadorCaeExterno, TenantTce, operacion.Id, rol, Ahora, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Sin_la_linea_Inbound_vigente_del_Tenant_TCE_ninguna_operacion_Inbound_autoriza()
    {
        var operacion = Inbound();
        var contexto = new ConLineaInbound([operacion], [Cartera(operacion)], conLinea: []);

        (await ElegirInbound(contexto, "Administrador")).Should().BeNull();
        (await ElegirInbound(contexto, "GestorCae")).Should().BeNull();
        (await Revalidar(contexto, operacion.Id, "Administrador")).Should().BeNull();
    }

    [Fact]
    public async Task El_Gestor_CAE_necesita_su_Asignacion_de_Cartera_vigente_bajo_la_operacion_Inbound()
    {
        var operacion = Inbound();
        var deOtro = Cartera(operacion, usuario: Guid.NewGuid());
        var cerrada = ConEstado(Cartera(operacion), EstadoAsignacion.Cerrada);

        (await ElegirInbound(new ConLineaInbound([operacion], [], [TenantTce]), "GestorCae")).Should().BeNull();
        (await ElegirInbound(new ConLineaInbound([operacion], [deOtro, cerrada], [TenantTce]), "GestorCae")).Should().BeNull();
        (await ElegirInbound(new ConLineaInbound([operacion], [Cartera(operacion)], [TenantTce]), "GestorCae"))
            .Should().Be(operacion.Id);
    }

    [Fact]
    public async Task Una_operacion_Inbound_acotada_no_se_abre_sin_cartera_y_si_con_ella()
    {
        var acotada = Inbound(AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()));

        (await ElegirInbound(new ConLineaInbound([acotada], [], [TenantTce]), "Administrador")).Should().BeNull();
        (await ElegirInbound(new ConLineaInbound([acotada], [Cartera(acotada)], [TenantTce]), "GestorCae"))
            .Should().Be(acotada.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Cliente")]
    [InlineData("SoporteLectura")]
    public async Task Otro_perfil_o_ninguno_no_abre_ninguna_operacion_Inbound(string? rol)
    {
        var operacion = Inbound();
        var contexto = new ConLineaInbound([operacion], [Cartera(operacion)], [TenantTce]);

        (await ElegirInbound(contexto, rol)).Should().BeNull();
        (await Revalidar(contexto, operacion.Id, rol)).Should().BeNull();
    }

    [Fact]
    public async Task Cerrar_la_operacion_Inbound_la_revoca()
    {
        var cerrada = ConEstado(Inbound(), EstadoAsignacion.Cerrada);
        var caducada = Inbound(hasta: Ahora.AddMinutes(-1));
        var contexto = new ConLineaInbound([cerrada, caducada], [], [TenantTce]);

        (await ElegirInbound(contexto, "Administrador")).Should().BeNull();
        (await Revalidar(contexto, cerrada.Id, "Administrador")).Should().BeNull();
        (await Revalidar(contexto, caducada.Id, "Administrador")).Should().BeNull();
    }

    [Fact]
    public async Task La_operacion_Inbound_de_otro_Operador_CAE_no_autoriza_a_esta_organizacion()
    {
        var deOtro = Inbound(operador: Guid.NewGuid());
        var contexto = new ConLineaInbound([deOtro], [Cartera(deOtro)], [TenantTce]);

        (await ElegirInbound(contexto, "Administrador")).Should().BeNull();
        (await Revalidar(contexto, deOtro.Id, "Administrador")).Should().BeNull();
    }

    [Fact]
    public async Task Ni_el_Tenant_TCE_suspendido_ni_el_Operador_CAE_suspendido_se_abren_con_papel_Inbound()
    {
        var operacion = Inbound();

        (await ElegirInbound(new ConLineaInbound([operacion], [], [TenantTce], suspendidos: [TenantTce]), "Administrador"))
            .Should().BeNull();
        (await ElegirInbound(new ConLineaInbound([operacion], [], [TenantTce], suspendidos: [OperadorCaeExterno]), "Administrador"))
            .Should().BeNull();
    }

    [Fact]
    public async Task La_raiz_y_las_operaciones_internas_Inbound_no_abren_ningun_contexto_delegado()
    {
        var ahora = Ahora;
        var raiz = ConEstado(AsignacionOperacion.Raiz(OperadorCaeExterno, ServicioCae.Inbound, ahora.AddDays(-5), ahora),
            EstadoAsignacion.Vigente);
        var cartera = ConEstado(AsignacionCartera.Interna(
            raiz, Usuario, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora), EstadoAsignacion.Vigente);
        var contexto = new ConLineaInbound([raiz], [cartera], [OperadorCaeExterno]);

        (await TenantsBeneficiariosAutorizados.OperacionInboundQueAutorizaAsync(
            contexto, Usuario, OperadorCaeExterno, OperadorCaeExterno, "Administrador", Ahora, default)).Should().BeNull();
        TenantsBeneficiariosAutorizados.OperacionesInboundQueAutorizan(contexto, Usuario, OperadorCaeExterno, "GestorCae", Ahora)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Una_operacion_Inbound_no_autoriza_por_la_via_de_Outbound_ni_una_Outbound_por_la_de_Inbound()
    {
        var inbound = Inbound();
        var outbound = ConEstado(AsignacionOperacion.Externa(
            TenantTce, OperadorCaeExterno, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora),
            EstadoAsignacion.Vigente);
        // Con la línea Inbound del Tenant TCE vigente y sin ninguna DelegacionTenant del par.
        var contexto = new ConLineaInbound([inbound, outbound], [], [TenantTce]);

        (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            contexto, SinDelegaciones, Usuario, OperadorCaeExterno, TenantTce, "Administrador", Ahora, default))
            .Should().BeNull("la vía de Outbound sigue exigiendo su DelegacionTenant de origen, y la Inbound no cuenta en ella");
        (await ElegirInbound(contexto, "Administrador")).Should().Be(inbound.Id, "la Outbound no entra en la vía Inbound");
        (await Revalidar(contexto, outbound.Id, "Administrador")).Should().BeNull();
    }

    [Fact]
    public async Task Con_las_dos_operaciones_cada_una_revalida_con_su_propio_papel()
    {
        var inbound = Inbound();
        var outbound = ConEstado(AsignacionOperacion.Externa(
            TenantTce, OperadorCaeExterno, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora),
            EstadoAsignacion.Vigente);
        // OperacionesEnMemoria ampara por defecto cada operación externa con una DelegacionTenant de su par.
        var outboundAmparada = new OperacionesEnMemoria([inbound, outbound], []);
        var contexto = new ConLineaInboundSobre(outboundAmparada, [TenantTce]);
        var delegacion = DelegacionesEnMemoria.VigentesDe(outboundAmparada);

        (await TenantsBeneficiariosAutorizados.ServicioQueAutorizaLaOperacionAsync(
            contexto, delegacion, Usuario, OperadorCaeExterno, TenantTce, outbound.Id, "Administrador", Ahora, default))
            .Should().Be(ServicioCae.Outbound);
        (await TenantsBeneficiariosAutorizados.ServicioQueAutorizaLaOperacionAsync(
            contexto, delegacion, Usuario, OperadorCaeExterno, TenantTce, inbound.Id, "Administrador", Ahora, default))
            .Should().Be(ServicioCae.Inbound);
        (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            contexto, delegacion, Usuario, OperadorCaeExterno, TenantTce, "Administrador", Ahora, default))
            .Should().Be(outbound.Id, "sin pedir Inbound, el selector sigue eligiendo la Outbound");
    }

    /// <summary>Un catálogo cualquiera con la línea Inbound vigente en <paramref name="conLinea"/>.</summary>
    private sealed class ConLineaInboundSobre(IOperacionesQueryContext interno, IEnumerable<Guid> conLinea) : IOperacionesQueryContext
    {
        private readonly HashSet<Guid> _conLinea = conLinea.ToHashSet();
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => interno.AsignacionesOperacion;
        public IQueryable<AsignacionCartera> AsignacionesCartera => interno.AsignacionesCartera;
        public IQueryable<Guid> TenantsSuspendidosIds => interno.TenantsSuspendidosIds;
        public IQueryable<DelegacionTenant> DelegacionesTenant => interno.DelegacionesTenant;
        public IQueryable<AsignacionOperacion> OperacionesInboundAmparadas =>
            AsignacionesOperacion.Where(o => o.Servicio == ServicioCae.Inbound && _conLinea.Contains(o.PropietarioTenantId));
    }
}
