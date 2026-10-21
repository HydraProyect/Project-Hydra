using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerTenantObjetivoSesion;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Quién lee el Cuadro de dirección (decisión de Chris del 2026-10-19): Dirección CAE, Coordinador CAE y el Administrador
/// del Tenant por rol, y el administrador de TALVEG solo con una Sesión Privilegiada de acceso total al Tenant.
/// </summary>
public class AutorizacionCuadroDireccionTests
{
    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Administrador")]
    public async Task Direccion_Coordinador_y_Administrador_del_Tenant_pueden_leer(string rol)
    {
        var accion = () => AutorizacionCuadroDireccion.ExigirLecturaAsync(new CurrentUserServiceFalso(rol: rol), new PoliticaAccesoTotalFalsa(false));
        await accion.Should().NotThrowAsync();
    }

    [Fact]
    public async Task El_administrador_de_TALVEG_lee_con_una_Sesion_Privilegiada_de_acceso_total_y_sin_rol_de_negocio()
    {
        var accion = () => AutorizacionCuadroDireccion.ExigirLecturaAsync(new CurrentUserServiceFalso(rol: null), new PoliticaAccesoTotalFalsa(true));
        await accion.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Sin_rol_autorizado_ni_Sesion_Privilegiada_se_rechaza_la_lectura(string? rol)
    {
        var accion = () => AutorizacionCuadroDireccion.ExigirLecturaAsync(new CurrentUserServiceFalso(rol: rol), new PoliticaAccesoTotalFalsa(false));
        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData(null)]
    public async Task El_handler_rechaza_antes_de_consultar_datos_incluso_con_mes_invalido(string? rol)
    {
        var handler = CrearHandler(rol, accesoTotal: false);
        var accion = () => handler.Handle(new ObtenerCuadroDireccionQuery(2026, 0), CancellationToken.None);
        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("DireccionCae", false)]
    [InlineData("CoordinadorCae", false)]
    [InlineData("Administrador", false)]
    [InlineData(null, true)]
    public async Task El_handler_admite_a_quien_puede_leer_y_valida_el_mes(string? rol, bool accesoTotal)
    {
        var resultado = await CrearHandler(rol, accesoTotal).Handle(new ObtenerCuadroDireccionQuery(2026, 0), CancellationToken.None);
        resultado.SinCifras.Should().Be(CuadroDireccionSinCifras.MesNoValido);
    }

    // ------------------------------------------------ Tenants que recorre el cuadro

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Administrador")]
    public async Task Por_rol_recorre_los_Tenants_beneficiarios_autorizados(string rol)
    {
        var autorizados = new[] { new ClienteAutorizadoDto(Guid.NewGuid(), "Origen", EsOrigen: true) };
        var (handler, _) = CrearTenantsHandler(rol, accesoTotal: false, autorizados);

        var tenants = await handler.Handle(new ObtenerTenantsCuadroDireccionQuery(), CancellationToken.None);

        tenants.Should().BeEquivalentTo(autorizados);
    }

    /// <summary>
    /// El Actor de Plataforma TALVEG ve solo el Tenant objetivo de su sesión, nunca sus Tenants de origen ni los
    /// beneficiarios que tendría por cartera.
    /// </summary>
    [Fact]
    public async Task Con_Sesion_Privilegiada_recorre_solo_el_Tenant_objetivo()
    {
        var (handler, objetivo) = CrearTenantsHandler(rol: null, accesoTotal: true,
            autorizados: [new ClienteAutorizadoDto(Guid.NewGuid(), "Origen del Actor", EsOrigen: true)]);

        var tenants = await handler.Handle(new ObtenerTenantsCuadroDireccionQuery(), CancellationToken.None);

        tenants.Should().ContainSingle().Which.Should().Match<ClienteAutorizadoDto>(t => t.TenantId == objetivo.Id && t.Nombre == "Objetivo" && !t.EsOrigen);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData(null)]
    public async Task Sin_rol_que_lea_ni_Sesion_Privilegiada_no_recorre_ningun_Tenant(string? rol)
    {
        var (handler, _) = CrearTenantsHandler(rol, accesoTotal: false,
            autorizados: [new ClienteAutorizadoDto(Guid.NewGuid(), "Origen", EsOrigen: true)]);

        var tenants = await handler.Handle(new ObtenerTenantsCuadroDireccionQuery(), CancellationToken.None);

        tenants.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Tenant_objetivo_exige_Sesion_Privilegiada_de_acceso_total()
    {
        var objetivo = new Tenant("Objetivo");
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(objetivo);

        var sinSesion = await new ObtenerTenantObjetivoSesionQueryHandler(new PoliticaAccesoTotalFalsa(false), new TenantActualFijo(objetivo.Id), tenants)
            .Handle(new ObtenerTenantObjetivoSesionQuery(), CancellationToken.None);
        var sinTenant = await new ObtenerTenantObjetivoSesionQueryHandler(new PoliticaAccesoTotalFalsa(true), new TenantActualFijo(null), tenants)
            .Handle(new ObtenerTenantObjetivoSesionQuery(), CancellationToken.None);

        sinSesion.Should().BeNull();
        sinTenant.Should().BeNull();
    }

    private static (ObtenerTenantsCuadroDireccionQueryHandler Handler, Tenant Objetivo) CrearTenantsHandler(
        string? rol, bool accesoTotal, IReadOnlyList<ClienteAutorizadoDto> autorizados)
    {
        var objetivo = new Tenant("Objetivo");
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(objetivo);
        var objetivoSesion = new ObtenerTenantObjetivoSesionQueryHandler(
            new PoliticaAccesoTotalFalsa(accesoTotal), new TenantActualFijo(objetivo.Id), tenants);
        var handler = new ObtenerTenantsCuadroDireccionQueryHandler(
            new MediadorDeAutorizados(autorizados, objetivoSesion), new CurrentUserServiceFalso(rol: rol));
        return (handler, objetivo);
    }

    private sealed class MediadorDeAutorizados(
        IReadOnlyList<ClienteAutorizadoDto> autorizados, ObtenerTenantObjetivoSesionQueryHandler objetivoSesion) : MediatR.IMediator
    {
        public async Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request switch
            {
                ObtenerClientesAutorizadosQuery => (TResponse)(object)autorizados,
                ObtenerTenantObjetivoSesionQuery q => (TResponse)(object)(await objetivoSesion.Handle(q, cancellationToken))!,
                _ => throw new NotSupportedException(request.GetType().Name),
            };

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : MediatR.IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : MediatR.INotification => throw new NotSupportedException();
    }

    private static ObtenerCuadroDireccionQueryHandler CrearHandler(string? rol, bool accesoTotal) => new(
        null!, null!, null!, null!, null!, null!, null!, null!,
        new CurrentUserServiceFalso(rol: rol), new PoliticaAccesoTotalFalsa(accesoTotal), null!, null!, TimeProvider.System);
}

/// <summary>Sesión Privilegiada de acceso total al Tenant actual, o ninguna.</summary>
internal sealed class PoliticaAccesoTotalFalsa(bool accesoTotal) : IPoliticaNotaInterna
{
    public Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default) => Task.FromResult(accesoTotal);
    public Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(accesoTotal);
}