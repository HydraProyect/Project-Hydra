using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro de la reclamación por SMTP no lleva datos personales. Un
/// <c>IEmailService</c> que falla compone su error con la dirección, una excepción de envío cita la dirección
/// en su mensaje y una excepción del guardado puede citar el valor que violó una restricción; ninguna de las
/// tres cosas puede llegar al registro. Cada prueba ejercita uno de los puntos de registro reales del servicio
/// con un registro que captura todas las entradas, de cualquier nivel.
/// </summary>
public class RegistroEnvioReclamacionRegistroSinDatosPersonalesTests
{
    private const string DireccionPersonal = "persona.rechazada@example.com";

    [Fact]
    public async Task Una_excepcion_del_transporte_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<RegistroEnvioReclamacionService>();
        var correo = new CorreoConComportamiento((_, _) => throw new FormatException(
            $"La dirección {DireccionPersonal} está mal formada.", new InvalidOperationException($"Causa con {DireccionPersonal}")));

        await Enviar(Construir(correo, registro, new UnitOfWorkFalso()));

        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "mal formada", "Causa con");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Which.Contenido
            .Should().Contain(nameof(FormatException));
    }

    [Fact]
    public async Task Un_envio_fallido_se_registra_con_el_codigo_sin_la_direccion_ni_el_mensaje_del_error()
    {
        var registro = new RegistroQueCapturaTodo<RegistroEnvioReclamacionService>();
        var correo = new CorreoConComportamiento((_, _) => Result.Fallo(
            Error.Crear("Email.ErrorRed", $"Rechazado para {DireccionPersonal}: buzón inexistente")));

        await Enviar(Construir(correo, registro, new UnitOfWorkFalso()));

        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "buzón inexistente");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning).Which.Contenido
            .Should().Contain("Email.ErrorRed", "el código basta para clasificar el fallo");
    }

    [Fact]
    public async Task Un_fallo_al_guardar_tras_enviar_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<RegistroEnvioReclamacionService>();
        var unitOfWork = new UnitOfWorkFalso();
        // El primer guardado (el registro previo del envío) es anterior al correo: el fallo se arma al enviar.
        var correo = new CorreoConComportamiento((_, _) =>
        {
            unitOfWork.ExcepcionAlGuardar = new InvalidOperationException(
                $"Violación de la clave (destinatario)=({DireccionPersonal})", new TimeoutException($"Causa con {DireccionPersonal}"));
            return Result.Exito();
        });

        var resultado = await Enviar(Construir(correo, registro, unitOfWork));

        resultado.Error.Codigo.Should().Be("Reclamacion.RegistroFallido");
        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "Violación de la clave", "Causa con");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Which.Contenido
            .Should().Contain(nameof(InvalidOperationException));
    }

    private static RegistroEnvioReclamacionService Construir(
        IEmailService correo, ILogger<RegistroEnvioReclamacionService> registro, UnitOfWorkFalso unitOfWork) =>
        new(new IntegracionesQueryContextFalso(),
            correo,
            new ReclamacionRepositorio(),
            new CurrentUserServiceFalso(Guid.NewGuid()),
            new CorreoDelActorRealFalso(),
            new MediatorSinUsoFalso(),
            registro,
            unitOfWork,
            new EnvioReclamacionRepositorioFalso(),
            new DescarteCambiosPendientesFalso());

    private static Task<Result> Enviar(RegistroEnvioReclamacionService servicio) =>
        servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(Guid.NewGuid(), "Refrielectric SL", AmbitoAplicacion.Empresa),
            [Guid.NewGuid()],
            [DireccionPersonal],
            "Documentación pendiente",
            "<p>Nos faltan los EPI.</p>",
            CancellationToken.None);

    private sealed class CorreoConComportamiento(Func<string, string, Result> comportamiento) : IEmailService
    {
        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(comportamiento(destinatarioEmail, asunto));
    }

    private sealed class ReclamacionRepositorio : IReclamacionDocumentalRepository
    {
        public void Agregar(ReclamacionDocumental reclamacion) { }
    }

    private sealed class CorreoDelActorRealFalso : ICorreoDelActorReal
    {
        public Task<string?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("marta@arcosspa.example");
    }

    /// <summary>Sin Conexión no se despacha nada por el mediador: cualquier uso revienta.</summary>
    private sealed class MediatorSinUsoFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
