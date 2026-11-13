using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Seguridad.Incidentes.Aviso;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Seguridad;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro del envío del aviso de un incidente de seguridad no lleva datos
/// personales. Una excepción del correo cita la dirección del destinatario en su mensaje, un
/// <c>IEmailService</c> que falla compone su error con ella y una excepción de datos puede citar valores de
/// la fila del envío; ninguna de las tres cosas puede llegar al registro. Cada prueba ejercita uno de los
/// puntos de registro reales con un registro que captura todas las entradas, de cualquier nivel.
/// </summary>
public class EnvioAvisosIncidenteSeguridadRegistroSinDatosPersonalesTests
{
    private const string DireccionPersonal = "persona.rechazada@example.com";
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Una_excepcion_del_correo_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<EnvioAvisosIncidenteSeguridadRegistroSinDatosPersonalesTests>();
        var correo = new CorreoFalso(_ => throw new FormatException(
            $"La dirección {DireccionPersonal} no se puede analizar.", new InvalidOperationException($"Causa con {DireccionPersonal}")));

        var resultado = await EnviarAsync(new PlataformaFalsa(), correo, registro);

        resultado.Valor.Fallidos.Should().Be(1, "el fallo de un destinatario se cuenta, no corta el envío");
        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "no se puede analizar", "Causa con");
        registro.Entradas.Should().Contain(e => e.Nivel == LogLevel.Error && e.Contenido.Contains(nameof(FormatException)));
    }

    [Fact]
    public async Task Un_envio_fallido_se_registra_con_el_codigo_sin_la_direccion_ni_el_mensaje_del_error()
    {
        var registro = new RegistroQueCapturaTodo<EnvioAvisosIncidenteSeguridadRegistroSinDatosPersonalesTests>();
        var correo = new CorreoFalso(_ => Result.Fallo(
            Error.Crear("Email.ErrorRed", $"Rechazado para {DireccionPersonal}: buzón inexistente")));

        var resultado = await EnviarAsync(new PlataformaFalsa(), correo, registro);

        resultado.Valor.Fallidos.Should().Be(1);
        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "buzón inexistente");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning).Which.Contenido
            .Should().Contain("Email.ErrorRed", "el código basta para clasificar el fallo");
    }

    [Fact]
    public async Task Un_fallo_al_anotar_el_envio_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<EnvioAvisosIncidenteSeguridadRegistroSinDatosPersonalesTests>();
        var plataforma = new PlataformaFalsa
        {
            ExcepcionAlResolver = new InvalidOperationException(
                $"Violación de la clave (correo)=({DireccionPersonal})", new TimeoutException($"Causa con {DireccionPersonal}")),
        };

        await EnviarAsync(plataforma, new CorreoFalso(_ => Result.Exito()), registro);

        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "Violación de la clave", "Causa con");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Which.Contenido
            .Should().Contain(nameof(InvalidOperationException));
    }

    private static Task<Result<ResumenEnvioAviso>> EnviarAsync(
        PlataformaFalsa plataforma, CorreoFalso correo, ILogger registro)
    {
        var incidente = new IncidenteSeguridadDto(
            Guid.NewGuid(), SenalIncidenteSeguridad.InicioSesionNoReconocido, EstadoIncidenteSeguridad.Confirmado,
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1), 1, [TenantId], "{}",
            DateTime.UtcNow.AddMinutes(-30), [], []);
        var contenido = new ContenidoAvisoIncidente(
            "Naturaleza del incidente", "Datos e interesados", "Consecuencias probables", "Medidas adoptadas", "Contacto de TALVEG");

        return EnvioAvisosIncidenteSeguridad.EnviarAsync(
            plataforma,
            contactos: null!, // EnviarAsync no lo usa: los destinatarios ya vienen resueltos.
            correo,
            new OpcionesIncidentesSeguridad { CorreoContactoSeguridadTalveg = "seguridad@talveg.test" },
            registro,
            incidente,
            DateTime.UtcNow.AddMinutes(-30),
            PlantillaAvisoIncidenteSeguridad.PrimerAviso,
            contenido,
            [new DestinatariosAvisoSeguridadTenant(TenantId, false, [new DestinatarioAvisoSeguridad("Marta Gómez", DireccionPersonal, null)])],
            CancellationToken.None);
    }

    private sealed class CorreoFalso(Func<string, Result> comportamiento) : IEmailService
    {
        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(comportamiento(destinatarioEmail));
    }

    private sealed class PlataformaFalsa : IIncidentesSeguridadPlataforma
    {
        public Exception? ExcepcionAlResolver { get; init; }

        public Task<IncidenteSeguridadDto?> ObtenerAsync(Guid incidenteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, string>> NombresTenantsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string> { [TenantId] = "Org A" });

        public Task<Result> DecidirAsync(Guid incidenteId, DecisionIncidenteSeguridad decision, string? motivo, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<EnvioAvisoReclamado>> ReclamarEnvioAsync(EnvioAvisoSolicitado envio, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito(new EnvioAvisoReclamado(
                Guid.NewGuid(), true, EstadoEnvioAvisoIncidenteSeguridad.Enviando, envio.Asunto, envio.Cuerpo)));

        public Task<Result> ResolverEnvioAsync(Guid envioId, bool enviado, string? error, CancellationToken cancellationToken) =>
            ExcepcionAlResolver is { } excepcion ? Task.FromException<Result>(excepcion) : Task.FromResult(Result.Exito());
    }
}
