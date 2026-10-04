using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Services;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Caché de vida corta de los pendientes del selector de Tenant beneficiario y cookie de «Recientes».
/// La clave lleva usuario, Tenant activo, alcance y vigencia; nada calculado para uno se sirve a otro, y
/// al leer el resultado se recorta al conjunto autorizado. Que el cálculo sea el de Mi trabajo, con su
/// autorización y su RLS, lo garantiza la Application (<see cref="ObtenerMiTrabajoAgregadoQuery"/>).
/// </summary>
public class ContadorPendientesSelectorTenantTests
{
    private static readonly Guid Usuario = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OtroUsuario = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid Origen = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Norte = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Sur = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

    private sealed class RelojManual(DateTimeOffset inicio) : TimeProvider
    {
        private DateTimeOffset _ahora = inicio;
        public override DateTimeOffset GetUtcNow() => _ahora;
        public void Avanzar(TimeSpan t) => _ahora += t;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    private static ClienteAutorizadoDto Gestionado(Guid id) => new(id, "E" + id.ToString("N")[^2..], false, true, EsCarteraGestorCae: true);

    private static readonly IReadOnlyList<ClienteAutorizadoDto> Alcance =
        [new(Origen, "Origen", true, true), Gestionado(Norte), Gestionado(Sur)];

    private static MiTrabajoTenantDto Cola(Guid tenant, int total, bool origen = false) => new(
        tenant, "x", origen, new BandejaAgrupadaDto([], []), [], [],
        new ResumenMiTrabajoTenantDto(tenant, "x", origen, total, 0, total, 0, 0), AlcanceCero: false);

    private sealed class Calculo
    {
        public int Llamadas;
        public bool Falla;

        public Task<MiTrabajoAgregadoDto> Ejecutar(CancellationToken _)
        {
            Interlocked.Increment(ref Llamadas);
            if (Falla) throw new InvalidOperationException("simulado");
            return Task.FromResult(new MiTrabajoAgregadoDto([Cola(Origen, 5, true), Cola(Norte, 3), Cola(Sur, 1)]));
        }
    }

    private static Task<IReadOnlyDictionary<Guid, int>?> Pedir(
        ContadorPendientesSelectorTenant s, Calculo c, Guid usuario, Guid activo, IReadOnlyList<ClienteAutorizadoDto>? alcance = null) =>
        s.ObtenerAsync(usuario, activo, alcance ?? Alcance, c.Ejecutar, CancellationToken.None);

    [Fact]
    public async Task Una_segunda_peticion_con_la_misma_clave_reutiliza_el_calculo()
    {
        var calculo = new Calculo();
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));

        var primera = await Pedir(servicio, calculo, Usuario, Norte);
        var segunda = await Pedir(servicio, calculo, Usuario, Norte);

        calculo.Llamadas.Should().Be(1);
        primera.Should().Equal(new Dictionary<Guid, int> { [Norte] = 3, [Sur] = 1 }, "el origen no es una empresa gestionada");
        segunda.Should().Equal(primera!);
    }

    [Fact]
    public async Task Cambiar_de_usuario_de_Tenant_activo_o_de_alcance_es_otra_entrada()
    {
        var calculo = new Calculo();
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));

        await Pedir(servicio, calculo, Usuario, Norte);
        await Pedir(servicio, calculo, OtroUsuario, Norte);
        await Pedir(servicio, calculo, Usuario, Sur);
        await Pedir(servicio, calculo, Usuario, Norte, [Alcance[0], Alcance[1]]);
        await Pedir(servicio, calculo, Usuario, Norte, [Alcance[0], Alcance[1] with { EsCarteraGestorCae = false }, Alcance[2]]);

        calculo.Llamadas.Should().Be(5, "usuario, Tenant activo, Tenants alcanzables y rol de cartera forman parte de la clave");
    }

    [Fact]
    public async Task Un_Tenant_que_ya_no_esta_en_el_alcance_no_aparece_aunque_siga_en_la_entrada_calculada()
    {
        var calculo = new Calculo();
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));
        await Pedir(servicio, calculo, Usuario, Norte);

        var recortado = await Pedir(servicio, calculo, Usuario, Norte);
        recortado.Should().ContainKey(Sur, "control positivo: con Sur autorizado sí cuenta");

        // Misma clave forzada a coincidir no es posible sin cambiar el alcance; la defensa es el recorte al leer.
        var soloNorte = await servicio.ObtenerAsync(Usuario, Norte, [Alcance[0], Alcance[1]], calculo.Ejecutar, CancellationToken.None);
        soloNorte.Should().Equal(new Dictionary<Guid, int> { [Norte] = 3 });
    }

    [Fact]
    public async Task La_entrada_caduca_con_su_vigencia()
    {
        var calculo = new Calculo();
        var reloj = new RelojManual(T0);
        var servicio = new ContadorPendientesSelectorTenant(reloj);

        await Pedir(servicio, calculo, Usuario, Norte);
        reloj.Avanzar(ContadorPendientesSelectorTenant.Vida + TimeSpan.FromSeconds(1));
        await Pedir(servicio, calculo, Usuario, Norte);

        calculo.Llamadas.Should().Be(2);
    }

    [Fact]
    public async Task Invalidar_descarta_lo_del_usuario_y_no_toca_el_de_otro()
    {
        var calculo = new Calculo();
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));
        await Pedir(servicio, calculo, Usuario, Norte);
        await Pedir(servicio, calculo, OtroUsuario, Norte);

        servicio.Invalidar(Usuario);
        await Pedir(servicio, calculo, Usuario, Norte);
        await Pedir(servicio, calculo, OtroUsuario, Norte);

        calculo.Llamadas.Should().Be(3, "el usuario invalidado recalcula; el otro sigue en caché");
    }

    [Fact]
    public async Task Un_fallo_devuelve_null_y_no_se_cachea()
    {
        var calculo = new Calculo { Falla = true };
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));

        (await Pedir(servicio, calculo, Usuario, Norte)).Should().BeNull();
        calculo.Falla = false;
        (await Pedir(servicio, calculo, Usuario, Norte)).Should().NotBeNull("el fallo anterior no se quedó en la caché");
        calculo.Llamadas.Should().Be(2);
    }

    [Fact]
    public async Task Un_resultado_parcial_sirve_pero_no_se_cachea()
    {
        var llamadas = 0;
        Task<MiTrabajoAgregadoDto> Parcial(CancellationToken _)
        {
            llamadas++;
            return Task.FromResult(new MiTrabajoAgregadoDto([Cola(Norte, 3)], [new TenantNoConsultadoDto(Sur, "Sur", false)]));
        }

        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));
        var primera = await servicio.ObtenerAsync(Usuario, Norte, Alcance, Parcial, CancellationToken.None);
        await servicio.ObtenerAsync(Usuario, Norte, Alcance, Parcial, CancellationToken.None);

        primera.Should().Equal(new Dictionary<Guid, int> { [Norte] = 3 });
        llamadas.Should().Be(2, "Sur no se pudo consultar: no es «al día», y no se recuerda como tal");
    }

    [Fact]
    public async Task El_tope_cancela_el_calculo_en_vez_de_dejarlo_ocupando_la_puerta()
    {
        CancellationToken recibido = default;
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));

        await servicio.ObtenerAsync(Usuario, Norte, Alcance, ct =>
        {
            recibido = ct;
            return Task.FromResult(new MiTrabajoAgregadoDto([]));
        }, CancellationToken.None);

        recibido.CanBeCanceled.Should().BeTrue("el cálculo recibe un token con tope; sin él nada lo corta");
    }

    [Fact]
    public async Task Los_calculos_simultaneos_de_una_misma_clave_se_comparten()
    {
        var calculo = new Calculo();
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Pedir(servicio, calculo, Usuario, Norte)));

        calculo.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Quien_deja_de_esperar_recibe_null_y_el_calculo_compartido_sigue()
    {
        var liberar = new TaskCompletionSource<MiTrabajoAgregadoDto>();
        var servicio = new ContadorPendientesSelectorTenant(new RelojManual(T0));
        using var cancelar = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var resultado = await servicio.ObtenerAsync(Usuario, Norte, Alcance, _ => liberar.Task, cancelar.Token);

        resultado.Should().BeNull();
        liberar.SetResult(new MiTrabajoAgregadoDto([Cola(Norte, 2)]));
        (await servicio.ObtenerAsync(Usuario, Norte, Alcance, _ => throw new InvalidOperationException("no debe recalcular"), CancellationToken.None))
            .Should().Equal(new Dictionary<Guid, int> { [Norte] = 2 });
    }

    private sealed class SinContextoHttp : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
    }

    // --- Recientes (cookie) ---------------------------------------------------------------------

    private static string Valor(Guid usuario, params Guid[] ids) => $"{usuario:N}|{string.Join(',', ids.Select(i => i.ToString("N")))}";

    [Fact]
    public void Los_recientes_se_leen_en_orden_y_con_tope()
    {
        var cuatro = new[] { Norte, Sur, Origen, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004") };

        RecientesSelectorTenant.Interpretar(Valor(Usuario, cuatro), Usuario).Should().Equal(Norte, Sur, Origen);
    }

    [Fact]
    public void El_lector_real_lee_la_cookie_de_la_peticion_y_sin_contexto_HTTP_no_da_recientes()
    {
        var contexto = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        contexto.Request.Headers.Cookie = $"{RecientesSelectorTenant.NombreCookie}={Uri.EscapeDataString(Valor(Usuario, Sur, Norte))}";

        new LectorRecientesSelectorTenant(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = contexto })
            .Leer(Usuario).Should().Equal(Sur, Norte);
        new LectorRecientesSelectorTenant(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = contexto })
            .Leer(OtroUsuario).Should().BeEmpty();
        new LectorRecientesSelectorTenant(new SinContextoHttp()).Leer(Usuario).Should().BeEmpty();
    }

    [Fact]
    public void Una_cookie_de_otro_usuario_o_malformada_no_da_recientes()
    {
        RecientesSelectorTenant.Interpretar(Valor(OtroUsuario, Norte), Usuario).Should().BeEmpty();
        RecientesSelectorTenant.Interpretar("basura", Usuario).Should().BeEmpty();
        RecientesSelectorTenant.Interpretar(null, Usuario).Should().BeEmpty();
        RecientesSelectorTenant.Interpretar($"{Usuario:N}|no-es-guid,{Norte:N},{Norte:N}", Usuario).Should().Equal(Norte);
        RecientesSelectorTenant.Interpretar(Valor(Usuario, Norte), Usuario).Should().Equal(Norte);
    }
}
