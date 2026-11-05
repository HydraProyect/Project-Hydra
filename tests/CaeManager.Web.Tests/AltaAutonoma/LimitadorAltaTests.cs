using System.Net;
using System.Threading.RateLimiting;
using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Política <c>alta</c> del limitador (ASVS 4.0.3 V2.2.1 y V11.1.4): por IP, una ventana por hora y otra por día en
/// serie, solo para POST. Las ventanas reales son de una hora y un día; la reposición se prueba con la misma partición y
/// ventanas de milisegundos dentro del limitador particionado. Que <c>Program.cs</c> registre la política con
/// <see cref="PoliticaLimitadorAlta"/> lo vigila <c>LimitadorAltaRegistradoEnProgramTests</c> (Architecture.Tests).
/// </summary>
public class LimitadorAltaTests
{
    [Fact]
    public void La_hora_corta_al_sexto_inicio_desde_la_misma_IP_con_los_valores_por_defecto()
    {
        using var limitador = Limitador(Post("203.0.113.9"), new OpcionesAntiabusoAlta());

        for (var i = 0; i < 5; i++)
            limitador.AttemptAcquire().IsAcquired.Should().BeTrue($"el inicio {i + 1} de 5 entra");

        limitador.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void El_dia_corta_aunque_la_hora_tenga_margen()
    {
        using var limitador = Limitador(Post("203.0.113.9"), new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 100, IniciosPorDiaPorIp = 3 });

        for (var i = 0; i < 3; i++)
            limitador.AttemptAcquire().IsAcquired.Should().BeTrue();

        limitador.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Un_rechazo_por_la_hora_no_gasta_cupo_del_dia()
    {
        using var limitador = Limitador(Post("203.0.113.9"), new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 2, IniciosPorDiaPorIp = 3 });

        limitador.AttemptAcquire().IsAcquired.Should().BeTrue();
        limitador.AttemptAcquire().IsAcquired.Should().BeTrue();
        for (var i = 0; i < 10; i++)
            limitador.AttemptAcquire().IsAcquired.Should().BeFalse();

        limitador.GetStatistics()!.CurrentAvailablePermits.Should().Be(0, "la hora está agotada");
        limitador.GetStatistics()!.TotalSuccessfulLeases.Should().Be(2);
    }

    [Fact]
    public void La_particion_solo_parece_inactiva_con_las_dos_ventanas_enteras_y_la_repone_el_limitador_particionado()
    {
        using var limitador = Limitador(Post("203.0.113.9"), new OpcionesAntiabusoAlta());
        var reponible = limitador.Should().BeAssignableTo<ReplenishingRateLimiter>().Subject;

        reponible.IsAutoReplenishing.Should().BeFalse("sin temporizador propio por IP: lo repone el ciclo del limitador particionado");
        reponible.ReplenishmentPeriod.Should().Be(TimeSpan.FromHours(1));
        limitador.IdleDuration.Should().NotBeNull("control: sin gastar nada está entera");

        limitador.AttemptAcquire().IsAcquired.Should().BeTrue();

        limitador.IdleDuration.Should().BeNull("con un permiso gastado, retirar la partición devolvería el cupo a la IP");

        // TryReplenish de una ventana fija manual solo repone si la ventana ha pasado (devuelve true por haberlo intentado).
        for (var i = 0; i < 4; i++)
            limitador.AttemptAcquire().IsAcquired.Should().BeTrue();
        reponible.TryReplenish();
        limitador.AttemptAcquire().IsAcquired.Should().BeFalse("reponer dentro de la hora no devuelve el cupo");
        limitador.IdleDuration.Should().BeNull();
    }

    // Sobre el PartitionedRateLimiter real: las ventanas no se reponen solas, así que solo el ciclo del particionado
    // (cada 100 ms) puede devolver el cupo, y lo hace a través de LimitadorEncadenado. En cada prueba, la ventana que no
    // se observa dura una hora y no interviene; la observada dura 2 s, y lo único que depende del reloj es que dos
    // llamadas seguidas no se separen 2 s. La espera de la reposición tiene un plazo holgado.
    [Fact]
    public async Task El_limitador_particionado_repone_la_ventana_corta_en_su_ciclo()
    {
        var opciones = new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 1, IniciosPorDiaPorIp = 1000 };
        using var cupo = CupoDe(opciones);
        using var particionado = PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
            LimitadorAlta.Particion(contexto, opciones, cupo, TimeSpan.FromSeconds(2)));
        var contexto = Post("203.0.113.9");

        particionado.AttemptAcquire(contexto).IsAcquired.Should().BeTrue();
        particionado.AttemptAcquire(contexto).IsAcquired.Should().BeFalse("la ventana corta está agotada");

        (await HastaQueConceda(particionado, contexto, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("el ciclo del particionado repone la ventana corta");
    }

    [Fact]
    public async Task El_limitador_particionado_repone_la_ventana_larga_en_su_ciclo()
    {
        // El día con un permiso y la hora con mil: la configuración no lo admitiría (EsValida), pero la partición se
        // construye aquí sin validar, para que solo el día pueda cerrar.
        var opciones = new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 1000, IniciosPorDiaPorIp = 1 };
        // El día lo repone el ciclo del limitador particionado del cupo compartido, no el de la partición por hora.
        using var cupo = new CupoDiarioAlta(opciones.IniciosPorDiaPorIp, TimeSpan.FromSeconds(2));
        using var particionado = PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
            LimitadorAlta.Particion(contexto, opciones, cupo, TimeSpan.FromHours(1)));
        var contexto = Post("203.0.113.9");

        particionado.AttemptAcquire(contexto).IsAcquired.Should().BeTrue();
        particionado.AttemptAcquire(contexto).IsAcquired.Should().BeFalse("la ventana larga está agotada");

        (await HastaQueConceda(particionado, contexto, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("el ciclo del particionado repone también la ventana larga");
    }

    private static async Task<bool> HastaQueConceda(PartitionedRateLimiter<HttpContext> limitador, HttpContext contexto, TimeSpan plazo)
    {
        var limite = DateTime.UtcNow + plazo;
        while (DateTime.UtcNow < limite)
        {
            using var concesion = limitador.AttemptAcquire(contexto);
            if (concesion.IsAcquired)
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    [Fact]
    public async Task Al_liberarse_suelta_las_dos_ventanas()
    {
        var limitador = Limitador(Post("203.0.113.9"), new OpcionesAntiabusoAlta());
        await limitador.DisposeAsync();

        limitador.Invoking(l => l.AttemptAcquire()).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Las_IPs_distintas_no_comparten_cupo_y_un_mismo_prefijo_IPv6_si()
    {
        var opciones = new OpcionesAntiabusoAlta();

        Clave(Post("203.0.113.9"), opciones).Should().NotBe(Clave(Post("198.51.100.7"), opciones));
        Clave(Post("2001:db8:1:2::1"), opciones).Should().Be(Clave(Post("2001:db8:1:2:ffff::9"), opciones),
            "rotar direcciones dentro del mismo /64 no da cupo nuevo");
        Clave(Post("::ffff:203.0.113.9"), opciones).Should().Be(Clave(Post("203.0.113.9"), opciones));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void Cargar_la_pagina_no_cuenta_como_inicio(string metodo)
    {
        var contexto = Post("203.0.113.9");
        contexto.Request.Method = metodo;

        using var limitador = Limitador(contexto, new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 1, IniciosPorDiaPorIp = 1 });
        for (var i = 0; i < 20; i++)
            limitador.AttemptAcquire().IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task El_middleware_responde_429_sin_cuerpo_ni_pistas_al_superar_el_cupo()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<OpcionesAntiabusoAlta>(o => { o.IniciosPorHoraPorIp = 2; o.IniciosPorDiaPorIp = 20; });
        builder.Services.AddSingleton(sp => new CupoDiarioAlta(sp.GetRequiredService<IOptions<OpcionesAntiabusoAlta>>()));
        builder.Services.AddRateLimiter(opciones =>
        {
            opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opciones.AddPolicy<string, PoliticaLimitadorAlta>(LimitadorAlta.Politica);
        });

        await using var app = builder.Build();
        // Como en Program.cs: las páginas de estado van antes del limitador y reejecutarían el 429 hacia /not-found.
        app.UseStatusCodePagesWithReExecute("/not-found");
        app.UseRateLimiter();
        app.Map("/not-found", () => "pagina de no encontrado");
        app.MapPost("/alta", () => "ok").RequireRateLimiting(LimitadorAlta.Politica);
        app.MapGet("/alta", () => "formulario").RequireRateLimiting(LimitadorAlta.Politica);
        await app.StartAsync();

        try
        {
            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var cliente = new HttpClient { BaseAddress = new Uri(direccion) };

            for (var i = 0; i < 2; i++)
                (await cliente.PostAsync("/alta", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            using var rechazo = await cliente.PostAsync("/alta", null);
            rechazo.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (await rechazo.Content.ReadAsStringAsync()).Should().BeEmpty();
            rechazo.Headers.RetryAfter.Should().BeNull("no se dice qué ventana se agotó ni cuándo vuelve");

            (await cliente.GetAsync("/alta")).StatusCode.Should().Be(HttpStatusCode.OK, "la página se sigue pudiendo cargar");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static DefaultHttpContext Post(string ip)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Method = HttpMethods.Post;
        contexto.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return contexto;
    }

    private static RateLimiter Limitador(HttpContext contexto, OpcionesAntiabusoAlta opciones, CupoDiarioAlta? cupo = null)
    {
        var particion = LimitadorAlta.Particion(contexto, opciones, cupo ?? CupoDe(opciones));
        return particion.Factory(particion.PartitionKey);
    }

    private static CupoDiarioAlta CupoDe(OpcionesAntiabusoAlta opciones) => new(Options.Create(opciones));

    private static string Clave(HttpContext contexto, OpcionesAntiabusoAlta opciones) =>
        LimitadorAlta.Particion(contexto, opciones, CupoDe(opciones)).PartitionKey;

    [Fact]
    public void La_hora_cuenta_por_64_y_el_dia_por_48()
    {
        var opciones = new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 2, IniciosPorDiaPorIp = 3 };
        using var cupo = CupoDe(opciones);
        using var primero = Limitador(Post("2001:db8:1:1::1"), opciones, cupo);
        using var segundo = Limitador(Post("2001:db8:1:2::1"), opciones, cupo);
        using var tercero = Limitador(Post("2001:db8:1:3::1"), opciones, cupo);
        using var otro48 = Limitador(Post("2001:db8:2:1::1"), opciones, cupo);

        primero.AttemptAcquire().IsAcquired.Should().BeTrue();
        primero.AttemptAcquire().IsAcquired.Should().BeTrue();
        primero.AttemptAcquire().IsAcquired.Should().BeFalse("la hora de ese /64 está agotada");
        segundo.AttemptAcquire().IsAcquired.Should().BeTrue("otro /64 tiene su propia hora");
        tercero.AttemptAcquire().IsAcquired.Should().BeFalse("los /64 del mismo /48 comparten el día, ya agotado");
        otro48.AttemptAcquire().IsAcquired.Should().BeTrue("otro /48 tiene su propio día");
    }

    [Fact]
    public void El_prefijo_diario_es_la_IPv4_o_el_48()
    {
        LimitadorAlta.PrefijoDiario(Post("203.0.113.9")).Should().Be("203.0.113.9");
        LimitadorAlta.PrefijoDiario(Post("::ffff:203.0.113.9")).Should().Be("203.0.113.9");
        LimitadorAlta.PrefijoDiario(Post("2001:db8:1:ffff::9")).Should().Be(LimitadorAlta.PrefijoDiario(Post("2001:db8:1:2::1")));
        LimitadorAlta.PrefijoDiario(Post("2001:db8:1:2::1")).Should().Be("2001:db8:1::/48");
    }
}
