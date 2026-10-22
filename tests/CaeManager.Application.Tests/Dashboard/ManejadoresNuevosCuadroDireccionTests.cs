using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Los tres manejadores nuevos del Cuadro de dirección (serie, comunicaciones y gestiones por día): la autorización de
/// lectura, el Tenant sin alcance sobre su totalidad y los importes solo para quien pasa el guard de tarifas.
/// </summary>
public class ManejadoresNuevosCuadroDireccionTests
{
    private static readonly DateTime Ahora = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantA = Guid.NewGuid();

    private static IMediator Autorizados() => new AutorizadosFalsos([new ClienteAutorizadoDto(TenantA, "Ibertec", EsOrigen: false)]);

    private static readonly IAlcanceDatosService SinAlcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);
    private static readonly IAlcanceDatosService ConAlcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: true);

    // ---------------------------------------------------------------- Serie

    private static ObtenerSerieCuadroDireccionQueryHandler Serie(string? rol, IAlcanceDatosService alcance, bool accesoTotal = false) =>
        new(Autorizados(), alcance, new MedicionFalsa(), null!, null!, new UsuarioConRol(rol), new PoliticaAccesoTotalFalsa(accesoTotal), new RelojFijo(Ahora));

    /// <summary>La Sesión Privilegiada lee la serie, pero sin rol de negocio no pasa el guard de tarifas: sin importes.</summary>
    [Fact]
    public async Task La_serie_en_Sesion_Privilegiada_se_lee_sin_facturacion()
    {
        var resultado = await Serie(null, ConAlcance, accesoTotal: true).Handle(new ObtenerSerieCuadroDireccionQuery(2026, 10, 1, ConFacturacion: true), default);

        resultado.Tenants.Should().ContainSingle().Which.Meses.Should().OnlyContain(m => m.Facturacion == null);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task La_serie_rechaza_un_rol_que_no_es_Direccion_Coordinador_ni_Administrador(string? rol)
    {
        var accion = () => Serie(rol, ConAlcance).Handle(new ObtenerSerieCuadroDireccionQuery(2026, 10, 1, ConFacturacion: true), default);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task La_serie_deja_fuera_al_Tenant_sin_alcance_sobre_el_Tenant_entero()
    {
        var resultado = await Serie("DireccionCae", SinAlcance).Handle(new ObtenerSerieCuadroDireccionQuery(2026, 10, 1), default);

        resultado.Tenants.Should().BeEmpty();
        resultado.TenantsSinAlcanceCompleto.Should().Be(1);
    }

    [Fact]
    public async Task La_serie_de_Coordinador_CAE_no_lleva_facturacion_aunque_se_pida()
    {
        var resultado = await Serie("CoordinadorCae", ConAlcance).Handle(new ObtenerSerieCuadroDireccionQuery(2026, 10, 1, ConFacturacion: true), default);

        var tenant = resultado.Tenants.Should().ContainSingle().Subject;
        tenant.Meses.Should().ContainSingle().Which.Facturacion.Should().BeNull();
        resultado.Total.Should().OnlyContain(p => p.Facturacion == null);
    }

    /// <summary>
    /// Rendimiento (prueba de flujos 2026-10-21, fila 16: 19,9 s en frío): la serie de 36 meses mide cada Tenant
    /// beneficiario en una sola pasada, no una medición por mes, y cada mes lleva su propio corte.
    /// </summary>
    [Fact]
    public async Task La_serie_mide_cada_Tenant_en_una_sola_pasada_para_todos_los_meses()
    {
        var medicion = new MedicionFalsa();
        var handler = new ObtenerSerieCuadroDireccionQueryHandler(
            Autorizados(), ConAlcance, medicion, null!, null!, new UsuarioConRol("CoordinadorCae"), new PoliticaAccesoTotalFalsa(false), new RelojFijo(Ahora));

        var resultado = await handler.Handle(new ObtenerSerieCuadroDireccionQuery(2026, 10, ObtenerSerieCuadroDireccionQuery.MaximoMeses), default);

        var pasada = medicion.Pasadas.Should().ContainSingle("una pasada por Tenant, no una por mes").Subject;
        pasada.Should().HaveCount(ObtenerSerieCuadroDireccionQuery.MaximoMeses);
        pasada[^1].Should().Be(new MesAMedir(new MesCuadroDireccion(2026, 10), Ahora), "el mes en curso se corta ahora");
        pasada[0].CorteUtc.Should().Be(pasada[0].Mes.FinUtc, "un mes terminado se corta en su final");
        resultado.Tenants.Should().ContainSingle().Which.Meses.Should().HaveCount(ObtenerSerieCuadroDireccionQuery.MaximoMeses);
    }

    // ---------------------------------------------------------------- Comunicaciones

    private static ObtenerComunicacionesCuadroDireccionQueryHandler Comunicaciones(string? rol, IAlcanceDatosService alcance) =>
        new(Autorizados(), alcance, null!, new UsuarioConRol(rol), new PoliticaAccesoTotalFalsa(false), new RelojFijo(Ahora));

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Comunicaciones_rechaza_un_rol_que_no_es_Direccion_ni_Coordinador_CAE(string rol)
    {
        var accion = () => Comunicaciones(rol, ConAlcance).Handle(new ObtenerComunicacionesCuadroDireccionQuery(2026, 10), default);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Comunicaciones_deja_fuera_al_Tenant_sin_alcance_sobre_el_Tenant_entero()
    {
        var resultado = await Comunicaciones("CoordinadorCae", SinAlcance).Handle(new ObtenerComunicacionesCuadroDireccionQuery(2026, 10), default);

        resultado.Tenants.Should().BeEmpty();
        resultado.TenantsSinAlcanceCompleto.Should().Be(1);
    }

    // ---------------------------------------------------------------- Gestiones por día

    private static ObtenerGestionesPorDiaCuadroDireccionQueryHandler Gestiones(string? rol, IAlcanceDatosService alcance) =>
        new(Autorizados(), alcance, null!, new UsuarioConRol(rol), new PoliticaAccesoTotalFalsa(false), new RelojFijo(Ahora));

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Gestiones_por_dia_rechaza_un_rol_que_no_es_Direccion_ni_Coordinador_CAE(string rol)
    {
        var accion = () => Gestiones(rol, ConAlcance).Handle(new ObtenerGestionesPorDiaCuadroDireccionQuery(2026, 10, 1), default);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Gestiones_por_dia_deja_fuera_al_Tenant_sin_alcance_sobre_el_Tenant_entero()
    {
        var resultado = await Gestiones("DireccionCae", SinAlcance).Handle(new ObtenerGestionesPorDiaCuadroDireccionQuery(2026, 10, 1), default);

        resultado.Tenants.Should().BeEmpty();
        resultado.TenantsSinAlcanceCompleto.Should().Be(1);
    }

    // ---------------------------------------------------------------- dobles

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);
    }

    private sealed class MedicionFalsa()
        : MedicionMensualCuadroDireccion(null!, null!, null!, null!, null!, null!, null!)
    {
        public List<IReadOnlyList<MesAMedir>> Pasadas { get; } = [];

        public override Task<IReadOnlyList<MedicionCuadroDireccion>> MedirMesesAsync(
            IReadOnlyList<MesAMedir> meses, CancellationToken cancellationToken, bool medirDocumental = true)
        {
            Pasadas.Add(meses);
            return Task.FromResult<IReadOnlyList<MedicionCuadroDireccion>>(meses
                .Select(_ => new MedicionCuadroDireccion(10, 1, 0, 2, 0, 0, 0, [], new KpisDashboardDto(0, 0, 0, 0, 0, 0, 0, 0), 5, null))
                .ToList());
        }
    }

    private sealed class UsuarioConRol(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class AutorizadosFalsos(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            // Solo la query del Cuadro: si un handler volviera a ObtenerClientesAutorizadosQuery, una Sesión Privilegiada
            // recorrería los Tenants autorizados del Actor de Plataforma en vez del Tenant objetivo. Aquí falla.
            request is ObtenerTenantsCuadroDireccionQuery
                ? Task.FromResult((TResponse)(object)autorizados)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
