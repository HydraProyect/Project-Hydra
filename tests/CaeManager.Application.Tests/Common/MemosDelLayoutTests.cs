using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
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
    [Fact]
    public async Task Un_fallo_del_calculo_no_se_guarda_y_permite_reintentar()
    {
        var memo = new MemoAvatarPropio();
        var clave = new ClaveContextoMemo(Guid.NewGuid(), Guid.NewGuid(), "GestorCae", Guid.NewGuid(), null);
        var fallo = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var veces = 0;
        Task<string?> Calcular(CancellationToken _) => ++veces == 1 ? fallo.Task : Task.FromResult<string?>("avatar-recuperado");

        var primera = memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None);
        fallo.SetException(new InvalidOperationException("fallo de lectura"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => primera);
        (await memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None)).Should().Be("avatar-recuperado");
        (await memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None)).Should().Be("avatar-recuperado");
        veces.Should().Be(2, "el fallo no se guardó y el valor recuperado sí");
    }

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

    private sealed class AvataresContador : IAvatarDeCuentas
    {
        public int Lecturas { get; private set; }
        public string? Clave { get; set; } = "rana-verde";

        public Task<string?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Lecturas++;
            return Task.FromResult(Clave);
        }

        public Task<Result> GuardarAsync(Guid usuarioId, string? clave, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AutorizacionContador : IAutorizacionAdminPlataforma
    {
        public int Lecturas { get; private set; }
        public bool Permitido { get; set; } = true;

        public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Lecturas++;
            return Task.FromResult(Permitido);
        }

        public Task<bool> PuedeSobreTenantAsync(Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Avatar_reutiliza_el_contexto_y_recalcula_al_cambiar_Tenant_efectivo_o_tras_Command()
    {
        var origen = Guid.NewGuid();
        var tenant = new TenantMutable(origen);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", origen);
        var avatares = new AvataresContador();
        var memo = new MemoAvatarPropio();
        Task<string?> Preguntar() => new ObtenerAvatarPropioQueryHandler(usuario, avatares, tenant, memo)
            .Handle(new ObtenerAvatarPropioQuery(), CancellationToken.None);

        (await Preguntar()).Should().Be("rana-verde");
        avatares.Clave = "zorro-azul";
        (await Preguntar()).Should().Be("rana-verde");
        avatares.Lecturas.Should().Be(1);

        tenant.TenantId = Guid.NewGuid();
        (await Preguntar()).Should().Be("zorro-azul");
        avatares.Lecturas.Should().Be(2);

        avatares.Clave = null;
        await InvalidarConComando(memo);
        (await Preguntar()).Should().BeNull();
        avatares.Lecturas.Should().Be(3);

        avatares.Clave = "buho-gris";
        (await new ObtenerAvatarPropioQueryHandler(
                new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", origen), avatares, tenant, memo)
            .Handle(new ObtenerAvatarPropioQuery(), CancellationToken.None))
            .Should().Be("buho-gris", "otro usuario no reutiliza el avatar del primero");
        avatares.Lecturas.Should().Be(4);
    }

    [Fact]
    public async Task Administrador_plataforma_reutiliza_el_contexto_y_recalcula_al_cambiar_Tenant_efectivo_o_tras_Command()
    {
        var origen = Guid.NewGuid();
        var tenant = new TenantMutable(origen);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", origen);
        var autorizacion = new AutorizacionContador();
        var memo = new MemoAdministradorPlataforma();
        Task<bool> Preguntar() => new EsAdministradorPlataformaQueryHandler(autorizacion, usuario, tenant, memo)
            .Handle(new EsAdministradorPlataformaQuery(), CancellationToken.None);

        (await Preguntar()).Should().BeTrue();
        autorizacion.Permitido = false;
        (await Preguntar()).Should().BeTrue();
        autorizacion.Lecturas.Should().Be(1);

        tenant.TenantId = Guid.NewGuid();
        (await Preguntar()).Should().BeFalse();
        autorizacion.Lecturas.Should().Be(2);

        autorizacion.Permitido = true;
        await InvalidarConComando(memo);
        (await Preguntar()).Should().BeTrue();
        autorizacion.Lecturas.Should().Be(3);

        var otroUsuario = new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", origen);
        autorizacion.Permitido = false;
        (await new EsAdministradorPlataformaQueryHandler(autorizacion, otroUsuario, tenant, memo)
            .Handle(new EsAdministradorPlataformaQuery(), CancellationToken.None))
            .Should().BeFalse("otro usuario no reutiliza la señal del primero");
        autorizacion.Lecturas.Should().Be(4);

        (await new EsAdministradorPlataformaQueryHandler(autorizacion, usuario, tenant, memo)
            .Handle(new EsAdministradorPlataformaQuery(LecturaVigente: true), CancellationToken.None))
            .Should().BeFalse("la lectura vigente consulta la concesión aunque el memo diga que sí");
        autorizacion.Lecturas.Should().Be(5);
    }

    [Fact]
    public async Task Avatar_y_administrador_plataforma_otro_circuito_vuelve_a_leer()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        await using var proveedor = servicios.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var circuito = proveedor.CreateAsyncScope();
        await using var otroCircuito = proveedor.CreateAsyncScope();
        var origen = Guid.NewGuid();
        var tenant = new TenantMutable(origen);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", origen);
        var avatares = new AvataresContador();
        var autorizacion = new AutorizacionContador();

        async Task Preguntar(IServiceProvider scope)
        {
            await new ObtenerAvatarPropioQueryHandler(usuario, avatares, tenant,
                    scope.GetRequiredService<MemoAvatarPropio>())
                .Handle(new ObtenerAvatarPropioQuery(), CancellationToken.None);
            await new EsAdministradorPlataformaQueryHandler(autorizacion, usuario, tenant,
                    scope.GetRequiredService<MemoAdministradorPlataforma>())
                .Handle(new EsAdministradorPlataformaQuery(), CancellationToken.None);
        }

        await Preguntar(circuito.ServiceProvider);
        await Preguntar(circuito.ServiceProvider);
        avatares.Lecturas.Should().Be(1);
        autorizacion.Lecturas.Should().Be(1);

        await Preguntar(otroCircuito.ServiceProvider);
        avatares.Lecturas.Should().Be(2, "el segundo circuito no hereda la entrada del primero");
        autorizacion.Lecturas.Should().Be(2, "el segundo circuito no hereda la entrada del primero");
    }

    [Fact]
    public async Task Avatar_invalidar_durante_calculo_inicia_otro_y_el_anterior_no_pisa_el_nuevo()
    {
        var memo = new MemoAvatarPropio();
        var clave = new ClaveContextoMemo(Guid.NewGuid(), Guid.NewGuid(), "GestorCae", Guid.NewGuid(), null);
        var anterior = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nuevo = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var veces = 0;
        Task<string?> Calcular(CancellationToken _) => ++veces == 1 ? anterior.Task : nuevo.Task;

        var primera = memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None);
        memo.Invalidar();
        var segunda = memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None);
        veces.Should().Be(2, "la invalidación separa las generaciones aunque el cálculo siga pendiente");

        nuevo.SetResult("zorro-azul");
        (await segunda).Should().Be("zorro-azul");
        anterior.SetResult("rana-verde");
        (await primera).Should().Be("rana-verde", "quien inició la lectura conserva su resultado");

        (await memo.ObtenerOCalcularAsync(clave, Calcular, CancellationToken.None)).Should().Be("zorro-azul");
        veces.Should().Be(2, "el cálculo antiguo no reemplaza la entrada vigente");
    }

    private static Task<Result> InvalidarConComando(IMemoPorContexto memo) =>
        new InvalidacionAlcanceBehavior<ComandoFalso, Result>(new InvalidadorAlcanceInerte(), [memo])
            .Handle(new ComandoFalso(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

    private sealed record ComandoFalso : ICommand;

    private static readonly Type[] TiposDeMemo =
    [
        typeof(MemoClientesAutorizados),
        typeof(MemoPerfilVocabularioActual),
        typeof(MemoRotulosPrimeraPersona),
        typeof(MemoParticipaEnIncorporacionCartera),
        typeof(MemoAvatarPropio),
        typeof(MemoAdministradorPlataforma),
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
