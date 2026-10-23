using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Las Queries del layout que una visita repetía en el prerenderizado y en el circuito (perfil de vocabulario,
/// rótulos en primera persona, participación en la incorporación a cartera) se memoizan por ámbito con la misma
/// clave que la lista de Tenants beneficiarios autorizados. Fija que el handler real reutiliza dentro del mismo
/// contexto, que otro usuario o Tenant efectivo vuelve a leer, y que la composición las registra Scoped y el
/// behavior de Commands las invalida. La lógica común (caducidad, generación, cancelación) la fija
/// <c>MemoClientesAutorizadosTests</c>.
/// </summary>
public class MemosDelLayoutTests
{
    private sealed class TenantMutable(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    private sealed class VistaMutable : IVistaVocabularioPreviewService
    {
        public PerfilVocabularioTenant? PerfilForzado { get; set; }
    }

    // ---- Perfil de vocabulario --------------------------------------------------------------------------------

    private sealed class EscenarioVocabulario
    {
        public Tenant Activo { get; } = new("Tenant activo", PerfilVocabularioTenant.Consultora);
        public Tenant Otro { get; } = new("Otro Tenant", PerfilVocabularioTenant.Consultora);
        public TenantsQueryContextFalso Contexto { get; } = new();
        public TenantMutable TenantActual { get; }
        public VistaMutable Vista { get; } = new();
        public MemoPerfilVocabularioActual Memo { get; } = new();

        public EscenarioVocabulario()
        {
            Contexto.ListaTenants.Add(Activo);
            Contexto.ListaTenants.Add(Otro);
            TenantActual = new TenantMutable(Activo.Id);
        }

        public Task<PerfilVocabularioTenant> Preguntar(Guid usuario) =>
            new ObtenerPerfilVocabularioActualQueryHandler(
                    Contexto, TenantActual, Vista, new CurrentUserServiceFalso(usuario, "GestorCae", Activo.Id), Memo)
                .Handle(new ObtenerPerfilVocabularioActualQuery(), CancellationToken.None);

        /// <summary>Quita los Tenants de la base falsa: lo que se lea después sin memo es el valor por defecto.</summary>
        public void VaciarBase() => Contexto.ListaTenants.Clear();
    }

    [Fact]
    public async Task Perfil_de_vocabulario_el_mismo_contexto_no_vuelve_a_leer()
    {
        var e = new EscenarioVocabulario();
        var usuario = Guid.NewGuid();

        (await e.Preguntar(usuario)).Should().Be(PerfilVocabularioTenant.Consultora);
        e.VaciarBase();

        (await e.Preguntar(usuario)).Should().Be(PerfilVocabularioTenant.Consultora, "mismo usuario y mismo Tenant activo");
    }

    [Fact]
    public async Task Perfil_de_vocabulario_otro_usuario_u_otro_Tenant_activo_vuelve_a_leer()
    {
        var e = new EscenarioVocabulario();
        var usuario = Guid.NewGuid();
        await e.Preguntar(usuario);
        e.TenantActual.TenantId = e.Otro.Id;
        await e.Preguntar(usuario);
        e.VaciarBase();

        (await e.Preguntar(Guid.NewGuid())).Should().Be(default(PerfilVocabularioTenant), "otro usuario es otra entrada");
        e.TenantActual.TenantId = Guid.NewGuid();
        (await e.Preguntar(usuario)).Should().Be(default(PerfilVocabularioTenant), "otro Tenant activo es otra entrada");
        e.TenantActual.TenantId = e.Activo.Id;
        (await e.Preguntar(usuario)).Should().Be(PerfilVocabularioTenant.Consultora, "volver al contexto anterior reutiliza");
    }

    [Fact]
    public async Task Perfil_de_vocabulario_con_ambito_explicito_no_reutiliza_la_entrada_sin_el()
    {
        var e = new EscenarioVocabulario();
        var usuario = Guid.NewGuid();
        await e.Preguntar(usuario);
        e.VaciarBase();

        using (AmbitoTenantExplicito.Establecer(e.Activo.Id))
            (await e.Preguntar(usuario)).Should().Be(default(PerfilVocabularioTenant), "un fan-out es otra entrada");
    }

    [Fact]
    public async Task Perfil_de_vocabulario_la_vista_previa_gana_y_no_se_memoiza()
    {
        var e = new EscenarioVocabulario();
        var usuario = Guid.NewGuid();

        e.Vista.PerfilForzado = PerfilVocabularioTenant.ClienteDirecto;
        (await e.Preguntar(usuario)).Should().Be(PerfilVocabularioTenant.ClienteDirecto);

        e.Vista.PerfilForzado = null;
        (await e.Preguntar(usuario)).Should().Be(PerfilVocabularioTenant.Consultora, "al quitar la vista previa se ve el real");
    }

    // ---- Rótulos en primera persona ----------------------------------------------------------------------------

    /// <summary>ISender que cuenta cuántas veces se pregunta el perfil y responde lo que diga el test.</summary>
    private sealed class EmisorContador : ISender
    {
        public int Veces { get; private set; }
        public PerfilVocabularioTenant Perfil { get; set; } = PerfilVocabularioTenant.ClienteDirecto;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerPerfilVocabularioActualQuery) throw new NotSupportedException(request.GetType().Name);
            Veces++;
            return Task.FromResult((TResponse)(object)Perfil);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Rotulos_el_mismo_contexto_se_calcula_una_vez_y_otro_usuario_o_Tenant_otra()
    {
        var origen = Guid.NewGuid();
        var tenant = new TenantMutable(origen);
        var vista = new VistaMutable();
        var memo = new MemoRotulosPrimeraPersona();
        var emisor = new EmisorContador();
        Task<bool> Preguntar(Guid usuario) =>
            new UsaRotulosPrimeraPersonaQueryHandler(
                    emisor, tenant, new CurrentUserServiceFalso(usuario, "Administrador", origen), vista, memo)
                .Handle(new UsaRotulosPrimeraPersonaQuery(), CancellationToken.None);
        var usuario = Guid.NewGuid();

        (await Preguntar(usuario)).Should().BeTrue();
        (await Preguntar(usuario)).Should().BeTrue();
        emisor.Veces.Should().Be(1, "el menú y las migas de la misma visita preguntan lo mismo");

        await Preguntar(Guid.NewGuid());
        emisor.Veces.Should().Be(2, "otro usuario es otra entrada");

        tenant.TenantId = Guid.NewGuid();
        (await Preguntar(usuario)).Should().BeFalse("en un Tenant beneficiario ajeno no habla en primera persona");
        emisor.Veces.Should().Be(3);
    }

    [Fact]
    public async Task Rotulos_con_vista_previa_activa_no_se_memoizan()
    {
        var origen = Guid.NewGuid();
        var tenant = new TenantMutable(origen);
        var vista = new VistaMutable { PerfilForzado = PerfilVocabularioTenant.ClienteDirecto };
        var memo = new MemoRotulosPrimeraPersona();
        var emisor = new EmisorContador { Perfil = PerfilVocabularioTenant.ClienteDirecto };
        var usuario = Guid.NewGuid();
        Task<bool> Preguntar() =>
            new UsaRotulosPrimeraPersonaQueryHandler(
                    emisor, tenant, new CurrentUserServiceFalso(usuario, "Administrador", origen), vista, memo)
                .Handle(new UsaRotulosPrimeraPersonaQuery(), CancellationToken.None);

        (await Preguntar()).Should().BeTrue();
        vista.PerfilForzado = null;
        emisor.Perfil = PerfilVocabularioTenant.Consultora;

        (await Preguntar()).Should().BeFalse("lo calculado con la vista previa no se guardó");
    }

    // ---- Participación en la incorporación a cartera -----------------------------------------------------------

    [Fact]
    public async Task Participa_el_mismo_contexto_no_vuelve_a_resolver_y_otro_usuario_si()
    {
        var operador = Guid.NewGuid();
        var gestora = Guid.NewGuid();
        var otraGestora = Guid.NewGuid();
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(gestora, operador, "GestorCae");
        directorio.Asignar(otraGestora, operador, "GestorCae");
        var tenant = new TenantMutable(operador);
        var memo = new MemoParticipaEnIncorporacionCartera();
        Task<bool> Preguntar(Guid usuario) =>
            new ParticipaEnIncorporacionCarteraQueryHandler(
                    new CurrentUserServicePorAmbito(usuario, operador, "GestorCae", "GestorCae"), directorio, tenant, memo)
                .Handle(new ParticipaEnIncorporacionCarteraQuery(), CancellationToken.None);

        (await Preguntar(gestora)).Should().BeTrue();
        directorio.Asignar(gestora, operador, null);
        directorio.Asignar(otraGestora, operador, null);

        (await Preguntar(gestora)).Should().BeTrue("misma visita, mismo usuario y mismo contexto");
        (await Preguntar(otraGestora)).Should().BeFalse("otro usuario no reutiliza la respuesta de la primera");
        tenant.TenantId = Guid.NewGuid();
        (await Preguntar(gestora)).Should().BeFalse("otro Tenant efectivo es otra entrada");
    }

    // ---- Composición ------------------------------------------------------------------------------------------

    private sealed record ComandoFalso : ICommand;

    private static readonly Type[] TiposDeMemo =
    [
        typeof(MemoClientesAutorizados),
        typeof(MemoPerfilVocabularioActual),
        typeof(MemoRotulosPrimeraPersona),
        typeof(MemoParticipaEnIncorporacionCartera),
    ];

    /// <summary>
    /// Scoped y la misma instancia como tipo concreto y como <see cref="IMemoPorContexto"/>: un singleton serviría
    /// el valor de un usuario a otro ámbito; dos instancias dejarían al behavior invalidando otra que la del handler.
    /// </summary>
    [Fact]
    public async Task Con_AddApplication_cada_memo_es_Scoped_y_el_behavior_invalida_la_instancia_del_handler()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        foreach (var tipo in TiposDeMemo)
            servicios.Single(d => d.ServiceType == tipo).Lifetime.Should().Be(ServiceLifetime.Scoped, tipo.Name);
        servicios.Where(d => d.ServiceType == typeof(IMemoPorContexto))
            .Should().HaveCount(TiposDeMemo.Length).And.OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);

        await using var proveedor = servicios.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var ambito = proveedor.CreateAsyncScope();
        await using var otroAmbito = proveedor.CreateAsyncScope();

        var invalidables = ambito.ServiceProvider.GetServices<IMemoPorContexto>().ToList();
        foreach (var tipo in TiposDeMemo)
        {
            var concreto = ambito.ServiceProvider.GetRequiredService(tipo);
            invalidables.Should().ContainSingle(m => ReferenceEquals(m, concreto), tipo.Name);
        }
        foreach (var tipo in TiposDeMemo)
            otroAmbito.ServiceProvider.GetRequiredService(tipo).Should().NotBeSameAs(ambito.ServiceProvider.GetRequiredService(tipo));

        var clave = new ClaveContextoMemo(Guid.NewGuid(), Guid.NewGuid(), "GestorCae", Guid.NewGuid(), null);
        var memo = ambito.ServiceProvider.GetRequiredService<MemoParticipaEnIncorporacionCartera>();
        var veces = 0;
        Task<bool> Calcular(CancellationToken _) { veces++; return Task.FromResult(true); }
        await memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None);

        var behavior = ActivatorUtilities.CreateInstance<InvalidacionAlcanceBehavior<ComandoFalso, Result>>(
            ambito.ServiceProvider);
        await behavior.Handle(new ComandoFalso(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);
        await memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None);

        veces.Should().Be(2, "el Command invalidó el memo del mismo ámbito que usa el handler");
    }
}
