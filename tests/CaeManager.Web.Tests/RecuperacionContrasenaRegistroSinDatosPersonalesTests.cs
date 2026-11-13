using CaeManager.Web.Services;
using CaeManager.Web.Tests.Email;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro de la recuperación de contraseña no escribe la dirección de quien la pidió ni
/// la excepción entera (su mensaje, o el de su causa, puede citarla), y lo que escribe no distingue una cuenta
/// que existe de otra que no (V2.5, enumeración de cuentas).
/// </summary>
public class RecuperacionContrasenaRegistroSinDatosPersonalesTests
{
    private const string Correo = "victima@example.test";
    private static readonly Uri Base = new("https://cuentas.example/");

    private static async Task<RegistroQueCapturaTodo<RecuperacionContrasena>> ProcesarFallandoAsync(Exception fallo)
    {
        var registro = new RegistroQueCapturaTodo<RecuperacionContrasena>();
        var servicio = new RecuperacionContrasena(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, registro)
        {
            ProcesadorDePrueba = (_, _, _) => throw fallo,
        };

        servicio.Solicitar(Correo.ToUpperInvariant(), Base);
        await servicio.StartAsync(CancellationToken.None);
        try
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (registro.Entradas.Count == 0 && DateTime.UtcNow < limite)
                await Task.Delay(10);
        }
        finally
        {
            await servicio.StopAsync(CancellationToken.None);
        }

        return registro;
    }

    [Fact]
    public async Task El_fallo_al_procesar_no_registra_ni_la_direccion_ni_el_mensaje_ni_la_excepcion()
    {
        var registro = await ProcesarFallandoAsync(
            new InvalidOperationException($"El buzón {Correo} no acepta correo; causa: 550 {Correo.ToUpperInvariant()}"));

        registro.Entradas.Should().ContainSingle("una solicitud fallida deja una sola entrada");
        registro.DebeEstarLimpioDeDatosPersonales(Correo, Correo.ToUpperInvariant(), "buzón", "550", "victima");
    }

    [Fact]
    public async Task El_registro_del_fallo_es_el_mismo_con_independencia_del_mensaje_de_la_excepcion()
    {
        var conCuenta = await ProcesarFallandoAsync(new InvalidOperationException("la cuenta existe y el SMTP rechazó el envío"));
        var sinCuenta = await ProcesarFallandoAsync(new InvalidOperationException("sin cuenta"));

        conCuenta.Entradas.Single().Contenido.Should().Be(sinCuenta.Entradas.Single().Contenido,
            "el texto del error no puede servir para distinguir si el correo existe");
    }
}
