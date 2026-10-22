using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// La memoización de <see cref="ObtenerClientesAutorizadosQuery"/> por ámbito: una visita la pedía cinco veces
/// (NavMenu y SelectorClienteActivo en el prerenderizado y en el circuito, más el middleware del Tenant por
/// defecto). Fija que se calcula una vez por contexto y que nunca se sirve de un usuario, un Tenant o un perfil a
/// otro.
/// </summary>
public class MemoClientesAutorizadosTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();

    private static readonly ClaveMemoClientesAutorizados Clave =
        new(Usuario, Origen, "GestorCae", Beneficiario, null);

    private sealed class Calculo
    {
        public int Veces { get; private set; }

        public Task<IReadOnlyList<ClienteAutorizadoDto>> Calcular(CancellationToken _)
        {
            Veces++;
            IReadOnlyList<ClienteAutorizadoDto> lista =
                [new ClienteAutorizadoDto(Origen, $"Origen {Veces}", EsOrigen: true)];
            return Task.FromResult(lista);
        }
    }

    /// <summary>Reloj monotónico que solo avanza cuando el test lo pide.</summary>
    private sealed class RelojManual : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Avanzar(TimeSpan intervalo) => _ticks += intervalo.Ticks;
    }

    [Fact]
    public async Task Las_cinco_peticiones_de_una_visita_con_el_mismo_contexto_leen_la_base_una_sola_vez()
    {
        var memo = new MemoClientesAutorizados();
        var calculo = new Calculo();

        for (var i = 0; i < 5; i++)
            await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);

        calculo.Veces.Should().Be(1, "es la misma visita, el mismo usuario y el mismo contexto");
    }

    public static TheoryData<ClaveMemoClientesAutorizados> OtrosContextos() => new()
    {
        Clave with { UsuarioId = Guid.NewGuid() },
        Clave with { TenantOrigenId = Guid.NewGuid() },
        Clave with { PerfilQueDecideAlcance = "Administrador" },
        Clave with { TenantEfectivo = Guid.NewGuid() },
        Clave with { TenantEfectivo = null },
        Clave with { AmbitoTenantExplicito = Guid.NewGuid() },
    };

    [Theory]
    [MemberData(nameof(OtrosContextos))]
    public async Task Otro_usuario_Tenant_perfil_o_ambito_no_reutiliza_la_lista(ClaveMemoClientesAutorizados otra)
    {
        var memo = new MemoClientesAutorizados();
        var calculo = new Calculo();

        var primera = await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        var segunda = await memo.ObtenerOCalcularAsync(otra, calculo.Calcular, CancellationToken.None);

        calculo.Veces.Should().Be(2, "cada contexto se resuelve con sus propias lecturas");
        segunda.Should().NotBeSameAs(primera);
        (await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None))
            .Should().BeSameAs(primera, "volver al contexto anterior dentro de la caducidad no vuelve a leer");
    }

    [Fact]
    public async Task Dos_ambitos_distintos_nunca_comparten_la_lista()
    {
        var calculo = new Calculo();

        await new MemoClientesAutorizados().ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        await new MemoClientesAutorizados().ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);

        calculo.Veces.Should().Be(2, "la memoización es de la instancia Scoped (petición o circuito), no global");
    }

    private sealed record ComandoFalso : ICommand;

    /// <summary>
    /// La composición real: Scoped (un singleton serviría la lista de un usuario a otro ámbito y un Invalidar de
    /// uno vaciaría el de todos; un Transient dejaría al behavior invalidando otra instancia que la del handler).
    /// </summary>
    [Fact]
    public async Task Con_AddApplication_es_una_instancia_por_ambito_y_la_misma_que_invalida_el_behavior()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.Single(d => d.ServiceType == typeof(MemoClientesAutorizados)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);

        await using var proveedor = servicios.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var ambito = proveedor.CreateAsyncScope();
        await using var otroAmbito = proveedor.CreateAsyncScope();
        var memo = ambito.ServiceProvider.GetRequiredService<MemoClientesAutorizados>();

        ambito.ServiceProvider.GetRequiredService<MemoClientesAutorizados>().Should().BeSameAs(memo);
        otroAmbito.ServiceProvider.GetRequiredService<MemoClientesAutorizados>().Should().NotBeSameAs(memo);

        var calculo = new Calculo();
        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        var behavior = ActivatorUtilities.CreateInstance<InvalidacionAlcanceBehavior<ComandoFalso, Result>>(
            ambito.ServiceProvider);
        await behavior.Handle(new ComandoFalso(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);
        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);

        calculo.Veces.Should().Be(2, "el Command invalidó el memo del mismo ámbito que usa el handler");
    }

    [Fact]
    public async Task Invalidar_obliga_a_leer_de_nuevo()
    {
        var memo = new MemoClientesAutorizados();
        var calculo = new Calculo();

        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        memo.Invalidar();
        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);

        calculo.Veces.Should().Be(2);
    }

    [Fact]
    public async Task Un_calculo_empezado_antes_de_invalidar_se_devuelve_pero_no_se_guarda()
    {
        var memo = new MemoClientesAutorizados();
        var calculo = new Calculo();

        await memo.ObtenerOCalcularAsync(Clave, async ct =>
        {
            memo.Invalidar(); // un Command termina mientras se leía
            return await calculo.Calcular(ct);
        }, CancellationToken.None);
        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);

        calculo.Veces.Should().Be(2, "lo leído antes de la escritura no puede servirse después de ella");
    }

    [Fact]
    public async Task La_entrada_caduca_a_los_diez_segundos_de_empezar_a_calcularse()
    {
        var reloj = new RelojManual();
        var memo = new MemoClientesAutorizados(reloj);
        var calculo = new Calculo();

        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        reloj.Avanzar(MemoClientesAutorizados.Caducidad - TimeSpan.FromTicks(1));
        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        calculo.Veces.Should().Be(1);

        reloj.Avanzar(TimeSpan.FromTicks(1));
        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        calculo.Veces.Should().Be(2, "una cartera cerrada desde otro circuito deja de servirse como mucho a los 10 s");
    }

    [Fact]
    public async Task Un_calculo_cancelado_no_se_guarda()
    {
        var memo = new MemoClientesAutorizados();
        var calculo = new Calculo();

        var cancelado = () => memo.ObtenerOCalcularAsync(
            Clave, _ => throw new OperationCanceledException(), CancellationToken.None);
        await cancelado.Should().ThrowAsync<OperationCanceledException>();

        await memo.ObtenerOCalcularAsync(Clave, calculo.Calcular, CancellationToken.None);
        calculo.Veces.Should().Be(1);
    }

    [Fact]
    public async Task La_lista_memoizada_no_se_puede_modificar_desde_fuera()
    {
        var memo = new MemoClientesAutorizados();
        var lista = await memo.ObtenerOCalcularAsync(Clave, new Calculo().Calcular, CancellationToken.None);

        lista.Should().NotBeAssignableTo<List<ClienteAutorizadoDto>>();
        lista.Should().NotBeOfType<ClienteAutorizadoDto[]>("un array se puede escribir a través de IList");
    }
}
