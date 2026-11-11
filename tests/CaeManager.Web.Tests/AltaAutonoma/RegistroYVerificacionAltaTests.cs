using System.Collections.Concurrent;
using Bunit;
using CaeManager.Application.AltaAutonoma;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.AltaAutonoma.Registro;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Antienumeración y uso único del registro del alta autónoma (A3), en la parte que vive en la Web:
/// <list type="bullet">
/// <item>la cola corta en tres registros por correo y hora sin decirlo a quien registra (<c>Encolar</c> no devuelve
/// nada), y el cupo de un correo no toca el de otro;</item>
/// <item>abrir el enlace de verificación (GET) no verifica: solo pinta el botón, y el token viaja en el formulario.</item>
/// </list>
/// </summary>
public class RegistroYVerificacionAltaTests : BunitContext
{
    private static readonly Uri Base = new("https://app.example/");

    private readonly AccesoQueCuenta _acceso = new();

    public RegistroYVerificacionAltaTests()
    {
        Services.AddLocalization();
        Services.AddSingleton(Options.Create(new AltaAutonomaOptions { Habilitada = true }));
        Services.AddSingleton<IAccesoSolicitudesAlta>(_acceso);
        Services.AddSingleton<IExistenciaCuentaPorCorreo>(new SinCuentas());
        Services.AddSingleton<IRemitenteCorreoAlta>(new RemitenteNulo());
        Services.AddSingleton<IPreparacionContrasenaAlta>(new ContrasenasFalsas());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<ServicioRegistroAlta>();
    }

    [Fact]
    public void Abrir_el_enlace_no_verifica_y_el_token_va_en_el_formulario()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"alta/verificar?token={token}");

        var corte = Render<VerificarCorreoAlta>();

        _acceso.Verificaciones.Should().BeEmpty("un antivirus o una vista previa que abre el enlace no gasta el uso único");
        corte.Find("form[method=post] button[type=submit]").Should().NotBeNull();
        corte.Find("form input[type=hidden]").GetAttribute("value").Should().Be(token);
    }

    [Fact]
    public void El_formulario_de_verificacion_pide_la_contrasena_y_no_la_rellena()
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"alta/verificar?token={ServicioRegistroAlta.NuevoToken()}");

        var corte = Render<VerificarCorreoAlta>();

        var campo = corte.Find("form input#alta-verificar-contrasena");
        campo.GetAttribute("type").Should().Be("password");
        campo.GetAttribute("autocomplete").Should().Be("current-password");
        (campo.GetAttribute("value") ?? string.Empty).Should().BeEmpty();
        corte.Find("label[for=alta-verificar-contrasena]").Should().NotBeNull();
    }

    [Fact]
    public void Una_contrasena_que_no_casa_vuelve_a_pintar_el_formulario_con_la_alerta_generica_y_sin_gastar_el_enlace()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"alta/verificar?token={token}");
        var corte = Render<VerificarCorreoAlta>();

        corte.Find("#alta-verificar-contrasena").Change("la-mala");
        corte.Find("form").Submit();

        _acceso.Consultas.Should().ContainSingle();
        _acceso.Verificaciones.Should().BeEmpty("con la contraseña errónea el enlace no se gasta");
        corte.Find("[role=alert]").Should().NotBeNull();
        (corte.Find("#alta-verificar-contrasena").GetAttribute("value") ?? string.Empty).Should().BeEmpty(
            "la contraseña no vuelve a la página");
        corte.Find("form input[type=hidden]").GetAttribute("value").Should().Be(token);
    }

    [Fact]
    public void Con_la_bandera_apagada_el_enlace_de_verificacion_no_pinta_nada()
    {
        Services.AddSingleton(Options.Create(new AltaAutonomaOptions { Habilitada = false }));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"alta/verificar?token={ServicioRegistroAlta.NuevoToken()}");

        var corte = Render<VerificarCorreoAlta>();

        corte.FindAll("form").Should().BeEmpty();
        _acceso.Consultas.Should().BeEmpty();
    }

    [Fact]
    public async Task La_cola_corta_en_tres_por_correo_sin_decirlo_a_quien_registra()
    {
        var procesados = new ConcurrentQueue<string>();
        var cola = new ColaRegistroAlta(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<ColaRegistroAlta>.Instance)
        {
            ProcesadorDePrueba = (datos, _, _) =>
            {
                procesados.Enqueue(datos.Correo);
                return Task.CompletedTask;
            },
        };

        await cola.StartAsync(CancellationToken.None);
        try
        {
            // Encolar no devuelve nada: la página no puede distinguir un registro aceptado de uno descartado.
            typeof(ColaRegistroAlta).GetMethod(nameof(ColaRegistroAlta.Encolar))!.ReturnType.Should().Be(typeof(void));

            for (var i = 0; i < 5; i++)
                cola.Encolar(Datos("ana@empresa.test"), SolicitudAlta.NormalizarCorreo("ana@empresa.test"), Base);
            cola.Encolar(Datos("otra@empresa.test"), SolicitudAlta.NormalizarCorreo("otra@empresa.test"), Base);

            var limite = DateTime.UtcNow.AddSeconds(10);
            while (procesados.Count < 4 && DateTime.UtcNow < limite)
                await Task.Delay(20);
            await Task.Delay(100);

            procesados.Count(c => c == "ana@empresa.test").Should().Be(3, "tres registros por correo y hora");
            procesados.Count(c => c == "otra@empresa.test").Should().Be(1, "el cupo de un correo no toca el de otro");
        }
        finally
        {
            await cola.StopAsync(CancellationToken.None);
        }
    }

    private static DatosRegistroAlta Datos(string correo) => new(correo, "AQAAAAIAAYagAAAAEHashDePrueba", "203.0.113.9", "Navegador de prueba");

    internal sealed class AccesoQueCuenta : IAccesoSolicitudesAlta
    {
        public ConcurrentQueue<string> Consultas { get; } = new();
        public ConcurrentQueue<string> Verificaciones { get; } = new();
        public ConcurrentQueue<DatosOrganizacionAlta> Completados { get; } = new();
        public ResultadoDatosOrganizacionAlta ResultadoDatos { get; set; } = ResultadoDatosOrganizacionAlta.DatosCompletos;

        public Task IniciarAsync(SolicitudAlta solicitud, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<EnlaceVerificacionAlta> ConsultarEnlaceAsync(string token, CancellationToken cancellationToken)
        {
            Consultas.Enqueue(token);
            return Task.FromResult(new EnlaceVerificacionAlta(null, "hash-guardado"));
        }

        public Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(
            string token, string hashContrasenaComprobado, string credencialContinuacion, CancellationToken cancellationToken)
        {
            Verificaciones.Enqueue(token);
            return Task.FromResult(ResultadoVerificacionCorreoAlta.Verificado);
        }

        public Task<TransicionDatosOrganizacionAlta> CompletarDatosOrganizacionAsync(
            string credencialContinuacion, DatosOrganizacionAlta datos, CancellationToken cancellationToken)
        {
            Completados.Enqueue(datos);
            return Task.FromResult(new TransicionDatosOrganizacionAlta(ResultadoDatos, Guid.NewGuid()));
        }
    }

    /// <summary>Solo «la-buena» casa con el hash que devuelve <see cref="AccesoQueCuenta"/>.</summary>
    internal sealed class ContrasenasFalsas : IPreparacionContrasenaAlta
    {
        public Task<string?> ValidarYHashearAsync(string correo, string contrasena) => Task.FromResult<string?>("hash-guardado");

        public bool Coincide(string hashContrasena, string contrasena) => hashContrasena == "hash-guardado" && contrasena == "la-buena";
    }

    private sealed class SinCuentas : IExistenciaCuentaPorCorreo
    {
        public Task<bool> ExisteAsync(string correoNormalizado, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RemitenteNulo : IRemitenteCorreoAlta
    {
        public Task<Result> EnviarAsync(string destinatario, string asunto, string cuerpoHtml, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito());
    }
}
