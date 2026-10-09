using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios.Queries.ObtenerOperadoresCaeDeMiTenant;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// Quién recibe la lectura «qué Operador CAE gestiona mi Tenant y quién es su Gestor CAE
/// principal» (decisión 2026-10-09): solo el Administrador del Tenant propietario. Es la capa
/// que lo garantiza; la cabecera de Empresas no decide nada.
/// </summary>
public class ObtenerOperadoresCaeDeMiTenantQueryTests
{
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Operacion = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid GestoraPrincipal = Guid.NewGuid();
    private static readonly Guid GestorDeApoyo = Guid.NewGuid();

    private sealed class Sesion(Guid? usuarioId, Guid? tenantOrigenId, string? rolEfectivo) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rolEfectivo);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rolEfectivo);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class Escenario
    {
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public DirectorioRolesEnOrigen Directorio { get; } = new();
        public Guid? TenantOrigen { get; init; } = Propietario;
        public Guid? TenantActivo { get; init; } = Propietario;
        public string? RolEfectivo { get; init; } = "Administrador";
        public string? RolEnIdentity { get; init; } = "Administrador";

        public Escenario ConOperacion(params CarteraVivaDeOperacion[] carteras)
        {
            Catalogo.OperacionesExternas.Add((Propietario, new OperacionExternaSobreTenant(Operacion, Operador, "Prevención Levante", carteras)));
            return this;
        }

        public Task<IReadOnlyList<CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera.CarterasDeOperacion>> LeerAsync()
        {
            if (TenantOrigen is { } origen)
                Directorio.Asignar(Actor, origen, RolEnIdentity);

            return new ObtenerOperadoresCaeDeMiTenantQueryHandler(
                    new Sesion(Actor, TenantOrigen, RolEfectivo), new TenantActualFalso(TenantActivo), Directorio, Catalogo)
                .Handle(new ObtenerOperadoresCaeDeMiTenantQuery(), default);
        }
    }

    private static CarteraVivaDeOperacion Cartera(Guid usuarioId, bool principal, string rol = "GestorCae", DateTime? hasta = null) =>
        new(Operacion, Propietario, string.Empty, usuarioId, rol, principal, hasta);

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_ve_el_Operador_CAE_su_principal_y_los_de_apoyo()
    {
        var e = new Escenario().ConOperacion(
            Cartera(GestoraPrincipal, principal: true),
            Cartera(GestorDeApoyo, principal: false, hasta: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)));
        e.Directorio.Avatares[GestoraPrincipal] = "buho-ambar";

        var operacion = (await e.LeerAsync()).Should().ContainSingle().Subject;

        operacion.AsignacionOperacionId.Should().Be(Operacion);
        operacion.TenantId.Should().Be(Propietario);
        operacion.NombreOperador.Should().Be("Prevención Levante");
        operacion.Principal!.UsuarioId.Should().Be(GestoraPrincipal);
        operacion.Principal.Avatar.Should().Be("buho-ambar");
        operacion.Principal.Nombre.Should().NotBeEmpty();
        var apoyo = operacion.Apoyos.Should().ContainSingle().Subject;
        apoyo.UsuarioId.Should().Be(GestorDeApoyo);
        apoyo.VigenciaHasta.Should().NotBeNull();
        apoyo.Avatar.Should().BeNull();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().Equal(Propietario);
    }

    /// <summary>
    /// Una operación sin principal no desaparece de la lectura: se devuelve con su Operador CAE
    /// y sin persona, para que la cabecera lo diga junto a esa organización.
    /// </summary>
    [Fact]
    public async Task Una_operacion_sin_principal_se_devuelve_con_su_Operador_CAE_y_sin_persona()
    {
        var e = new Escenario().ConOperacion();

        var operacion = (await e.LeerAsync()).Should().ContainSingle().Subject;

        operacion.NombreOperador.Should().Be("Prevención Levante");
        operacion.Principal.Should().BeNull();
        operacion.Apoyos.Should().BeEmpty();
    }

    [Fact]
    public async Task En_un_Tenant_de_operacion_interna_sale_vacia()
    {
        var e = new Escenario();

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().Equal([Propietario], "control: el actor sí estaba autorizado y se llegó a leer");
    }

    /// <summary>
    /// La decisión fue solo sobre el Administrador: los demás roles del Tenant propietario no
    /// reciben nada, y ni siquiera se llega a leer.
    /// </summary>
    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Cliente")]
    public async Task Otro_rol_del_Tenant_propietario_no_recibe_nada(string rol)
    {
        var e = new Escenario { RolEfectivo = rol, RolEnIdentity = rol }.ConOperacion(Cartera(GestoraPrincipal, principal: true));

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().BeEmpty();
    }

    /// <summary>
    /// Quien opera este Tenant desde otra organización no entra por la lectura del plano de
    /// Propiedad, aunque su rol efectivo aquí sea Administrador (Encargo de administración) y lo
    /// sea también en su Tenant de origen.
    /// </summary>
    [Fact]
    public async Task Una_cuenta_de_otro_Tenant_con_rol_efectivo_Administrador_aqui_no_recibe_nada()
    {
        var e = new Escenario { TenantOrigen = OtroTenant, TenantActivo = Propietario }
            .ConOperacion(Cartera(GestoraPrincipal, principal: true));

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().BeEmpty();
    }

    /// <summary>El Administrador de otro Tenant lee el suyo, nunca este: el Tenant no viaja en la petición.</summary>
    [Fact]
    public async Task El_Administrador_de_otro_Tenant_solo_pregunta_por_el_suyo()
    {
        var e = new Escenario { TenantOrigen = OtroTenant, TenantActivo = OtroTenant }
            .ConOperacion(Cartera(GestoraPrincipal, principal: true));

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().Equal(OtroTenant);
    }

    /// <summary>Soporte TALVEG en Sesión Privilegiada no tiene rol de negocio: falla cerrado.</summary>
    [Fact]
    public async Task Sin_rol_efectivo_no_recibe_nada()
    {
        var e = new Escenario { RolEfectivo = null }.ConOperacion(Cartera(GestoraPrincipal, principal: true));

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().BeEmpty();
    }

    /// <summary>
    /// Manda el rol efectivo, no el de Identity: un Administrador con la sesión restringida a
    /// menos (inicio de sesión local) no lee como Administrador.
    /// </summary>
    [Fact]
    public async Task Un_Administrador_con_la_sesion_restringida_a_otro_rol_no_recibe_nada()
    {
        var e = new Escenario { RolEfectivo = "Consulta", RolEnIdentity = "Administrador" }
            .ConOperacion(Cartera(GestoraPrincipal, principal: true));

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().BeEmpty();
    }

    /// <summary>
    /// El rol de la sesión no basta: se vuelve a leer en Identity, que es donde se ve una cuenta
    /// desactivada o degradada después de iniciar sesión.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_sesion_de_Administrador_cuya_cuenta_ya_no_lo_es_o_esta_desactivada_no_recibe_nada(bool desactivada)
    {
        var e = new Escenario { RolEnIdentity = desactivada ? "Administrador" : "Consulta" }
            .ConOperacion(Cartera(GestoraPrincipal, principal: true));
        if (desactivada)
            e.Directorio.Desactivar(Actor);

        (await e.LeerAsync()).Should().BeEmpty();
        e.Catalogo.ConsultasDeOperacionesExternas.Should().BeEmpty();
    }
}
