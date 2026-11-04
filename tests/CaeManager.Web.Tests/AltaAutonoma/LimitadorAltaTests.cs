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
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Política <c>alta</c> del limitador (ASVS 4.0.3 V2.2.1 y V11.1.4): por IP, una ventana por hora y otra por día en
/// serie, solo para POST. Las ventanas son de una hora y un día de reloj real, así que aquí se prueban los cupos, no su
/// reposición. Que <c>Program.cs</c> registre la política con esta partición lo vigila
/// <c>LimitadorAltaRegistradoEnProgramTests</c> (Architecture.Tests).
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
        builder.Services.AddRateLimiter(opciones =>
        {
            opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opciones.AddPolicy(LimitadorAlta.Politica, contexto =>
                LimitadorAlta.Particion(contexto, new OpcionesAntiabusoAlta { IniciosPorHoraPorIp = 2, IniciosPorDiaPorIp = 20 }));
        });

        await using var app = builder.Build();
        app.UseRateLimiter();
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

    private static RateLimiter Limitador(HttpContext contexto, OpcionesAntiabusoAlta opciones)
    {
        var particion = LimitadorAlta.Particion(contexto, opciones);
        return particion.Factory(particion.PartitionKey);
    }

    private static string Clave(HttpContext contexto, OpcionesAntiabusoAlta opciones) =>
        LimitadorAlta.Particion(contexto, opciones).PartitionKey;
}
