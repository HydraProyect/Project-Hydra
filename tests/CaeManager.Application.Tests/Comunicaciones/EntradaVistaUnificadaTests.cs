using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// La entrada común de la vista unificada de Comunicaciones (D-6 b, R-4): el filtro por Tenant beneficiario solo
/// estrecha el conjunto calculado en el servidor, y las exclusiones de plano (§ 4.2) las rechazan por igual la lista y
/// el selector. La composición con PostgreSQL y RLS la prueban los tests de integración bajo runtime.
/// </summary>
public class EntradaVistaUnificadaTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Dentro = Guid.NewGuid();
    private static readonly Guid OtroDentro = Guid.NewGuid();
    private static readonly Guid Fuera = Guid.NewGuid();

    private static readonly IReadOnlyList<Guid> Conjunto = [Origen, Dentro, OtroDentro];

    [Fact]
    public void Sin_filtro_es_el_conjunto_entero()
    {
        EntradaVistaUnificada.Intersecar(Conjunto, null).Should().Equal(Conjunto);
        EntradaVistaUnificada.Intersecar(Conjunto, []).Should().Equal(Conjunto);
    }

    [Fact]
    public void El_filtro_estrecha_y_conserva_el_orden_del_conjunto()
    {
        EntradaVistaUnificada.Intersecar(Conjunto, [OtroDentro, Dentro]).Should().Equal(Dentro, OtroDentro);
    }

    [Fact]
    public void Un_Tenant_de_fuera_en_el_filtro_no_entra_ni_mezclado_con_uno_de_dentro()
    {
        EntradaVistaUnificada.Intersecar(Conjunto, [Fuera]).Should().BeEmpty(
            "la coordenada no autoriza: un Tenant de fuera se descarta como uno inexistente");
        EntradaVistaUnificada.Intersecar(Conjunto, [Fuera, Dentro, Fuera]).Should().Equal(Dentro);
        EntradaVistaUnificada.Intersecar([], [Dentro]).Should().BeEmpty("con conjunto vacío no hay filtro que añada nada");
    }

    /// <summary>0, la lista; 1, el selector; 2, los buzones de un Tenant beneficiario (pantalla de Buzones, L-55).</summary>
    public static TheoryData<int> Consultas => [0, 1, 2];

    [Theory]
    [MemberData(nameof(Consultas))]
    public async Task Con_Sesion_Privilegiada_seleccionada_ninguna_consulta_se_abre(int selector)
    {
        var accion = () => ConsultarAsync(selector, new UsuarioDeLaVista(Guid.NewGuid(), Origen, "GestorCae"),
            new TenantsQueryContextFalso(), sesionPrivilegiada: true);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Sesión Privilegiada*");
    }

    [Theory]
    [MemberData(nameof(Consultas))]
    public async Task Con_papel_Inbound_ninguna_consulta_se_abre(int selector)
    {
        var usuario = new UsuarioDeLaVista(Guid.NewGuid(), Origen, "GestorCae", PapelActivo.Inbound);

        var accion = () => ConsultarAsync(selector, usuario, new TenantsQueryContextFalso());

        await accion.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*solo de Outbound*");
    }

    [Theory]
    [MemberData(nameof(Consultas))]
    public async Task El_Usuario_de_Cliente_no_abre_ninguna_consulta(int selector)
    {
        var usuario = new UsuarioDeLaVista(Guid.NewGuid(), Origen, AutoridadSobreCuentas.RolCliente);

        var accion = () => ConsultarAsync(selector, usuario, new TenantsQueryContextFalso());

        await accion.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Usuario de Cliente*");
    }

    [Theory]
    [MemberData(nameof(Consultas))]
    public async Task El_Soporte_TALVEG_no_abre_ninguna_consulta(int selector)
    {
        var plataforma = new Tenant("Plataforma TALVEG de prueba");
        plataforma.MarcarComoPlataforma();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(plataforma);

        var accion = () => ConsultarAsync(selector, new UsuarioDeLaVista(Guid.NewGuid(), plataforma.Id, "Administrador"), tenants);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Soporte TALVEG*");
    }

    [Theory]
    [MemberData(nameof(Consultas))]
    public async Task Sin_usuario_o_sin_Tenant_de_origen_ninguna_consulta_se_abre(int selector)
    {
        var sinUsuario = () => ConsultarAsync(selector, new UsuarioDeLaVista(null, Origen, "GestorCae"), new TenantsQueryContextFalso());
        var sinOrigen = () => ConsultarAsync(selector, new UsuarioDeLaVista(Guid.NewGuid(), null, "GestorCae"), new TenantsQueryContextFalso());

        await sinUsuario.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*sesión de usuario*");
        await sinOrigen.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*sesión de usuario*");
    }

    [Fact]
    public async Task Un_grupo_de_estados_fuera_del_enum_se_rechaza_antes_de_recorrer_ningun_Tenant()
    {
        var handler = new ObtenerConversacionesUnificadasQueryHandler(
            null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ObtenerConversacionesUnificadasQueryHandler>.Instance);

        var accion = () => handler.Handle(
            new ObtenerConversacionesUnificadasQuery(GrupoEstado: (GrupoEstadoConversacion)7), CancellationToken.None);

        await accion.Should().ThrowAsync<ArgumentOutOfRangeException>(
            "un grupo desconocido no puede llegar al recorrido, donde cada Tenant acabaría como «no consultado»");
    }

    /// <summary>
    /// La lista o el selector, con solo lo que las exclusiones tocan: los demás contextos quedan a <c>null</c> a propósito,
    /// porque una exclusión rechaza antes de leer conversaciones, operaciones ni alcance.
    /// </summary>
    private static Task ConsultarAsync(
        int selector, ICurrentUserService usuario, TenantsQueryContextFalso tenants, bool sesionPrivilegiada = false)
    {
        var coordenada = new Coordenada(sesionPrivilegiada);
        return selector switch
        {
            1 => new ObtenerTenantsBeneficiariosVistaUnificadaQueryHandler(
                    null!, null!, null!, tenants, null!, usuario, null!, coordenada,
                    NullLogger<ObtenerTenantsBeneficiariosVistaUnificadaQueryHandler>.Instance)
                .Handle(new ObtenerTenantsBeneficiariosVistaUnificadaQuery(), CancellationToken.None),
            2 => new ObtenerBuzonesVistaUnificadaQueryHandler(
                    null!, null!, null!, null!, tenants, null!, usuario, null!, coordenada, null!)
                .Handle(new ObtenerBuzonesVistaUnificadaQuery(Origen), CancellationToken.None),
            _ => new ObtenerConversacionesUnificadasQueryHandler(
                    null!, null!, null!, tenants, null!, usuario, null!, coordenada,
                    NullLogger<ObtenerConversacionesUnificadasQueryHandler>.Instance)
                .Handle(new ObtenerConversacionesUnificadasQuery(), CancellationToken.None),
        };
    }

    private sealed class Coordenada(bool haySeleccionada) : ICoordenadaSesionPrivilegiada
    {
        public bool HaySeleccionada => haySeleccionada;
    }

    private sealed class UsuarioDeLaVista(
        Guid? usuarioId, Guid? tenantOrigenId, string? rol, PapelActivo papel = PapelActivo.SinPapel)
        : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<bool> SuperoTotpEnSesionAsync() => Task.FromResult(false);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
    }
}
