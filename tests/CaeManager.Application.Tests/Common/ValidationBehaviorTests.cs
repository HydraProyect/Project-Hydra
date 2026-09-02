using CaeManager.Application.Common;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Sentry DOTNET-4, en producción: el circuito de Blazor se desconecta (y
/// con él el scope de DI) mientras un componente todavía está mid-flight
/// despachando un Send de MediatR — la resolución de lo que sigue del
/// pipeline revienta contra un IServiceProvider ya disposed, y sin
/// distinguirlo de un fallo real, el resultado es ruido en Sentry
/// (UnobservedTaskException) para nadie. Estas pruebas cubren la distinción:
/// esa carrera concreta se convierte en OperationCanceledException (que
/// LoggingBehavior ya trata como benigna), pero un ObjectDisposedException
/// de cualquier otro objeto sigue siendo un fallo real sin tocar.
/// </summary>
public class ValidationBehaviorTests
{
    private sealed record PeticionFalsa : IRequest<string>;

    private static ValidationBehavior<PeticionFalsa, string> Comportamiento(
        IEnumerable<IValidator<PeticionFalsa>> validadores) => new(validadores);

    [Fact]
    public async Task Sin_validadores_el_disposed_del_ServiceProvider_se_convierte_en_cancelacion()
    {
        var comportamiento = Comportamiento([]);

        Task<string> Next(CancellationToken _) =>
            throw new ObjectDisposedException(nameof(IServiceProvider));

        var accion = async () => await comportamiento.Handle(new PeticionFalsa(), Next, CancellationToken.None);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Un_disposed_de_otro_objeto_no_se_toca_control_positivo()
    {
        // Control positivo: la conversión está acotada al IServiceProvider
        // exacto del evento real. Un Stream/HttpClient liberado por error
        // dentro de un handler sigue siendo un fallo real, no una
        // desconexión benigna.
        var comportamiento = Comportamiento([]);

        Task<string> Next(CancellationToken _) =>
            throw new ObjectDisposedException("System.Net.Http.HttpClient");

        var accion = async () => await comportamiento.Handle(new PeticionFalsa(), Next, CancellationToken.None);

        (await accion.Should().ThrowAsync<ObjectDisposedException>()).Which.ObjectName.Should().Be("System.Net.Http.HttpClient");
    }

    [Fact]
    public async Task Con_validadores_que_pasan_el_disposed_del_ServiceProvider_tambien_se_convierte()
    {
        var validador = new InlineValidator<PeticionFalsa>();
        var comportamiento = Comportamiento([validador]);

        Task<string> Next(CancellationToken _) =>
            throw new ObjectDisposedException(nameof(IServiceProvider));

        var accion = async () => await comportamiento.Handle(new PeticionFalsa(), Next, CancellationToken.None);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Un_fallo_de_validacion_real_sigue_lanzando_ValidationException()
    {
        var validador = new InlineValidator<PeticionFalsa>();
        validador.RuleFor(_ => 1).Must(_ => false).WithMessage("siempre falla");
        var comportamiento = Comportamiento([validador]);

        var accion = async () => await comportamiento.Handle(
            new PeticionFalsa(), _ => Task.FromResult("no debería llegar aquí"), CancellationToken.None);

        await accion.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Sin_fallos_devuelve_la_respuesta_del_handler_sin_tocar()
    {
        var comportamiento = Comportamiento([]);

        var resultado = await comportamiento.Handle(
            new PeticionFalsa(), _ => Task.FromResult("respuesta real"), CancellationToken.None);

        resultado.Should().Be("respuesta real");
    }
}
