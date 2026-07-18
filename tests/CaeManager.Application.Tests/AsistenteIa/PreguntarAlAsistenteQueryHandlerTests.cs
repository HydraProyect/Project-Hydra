using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

public class PreguntarAlAsistenteQueryHandlerTests
{
    private sealed class AsistenteIaServiceFalso(Result<string> resultado) : IAsistenteIaService
    {
        public IReadOnlyList<MensajeChatDto>? HistorialRecibido { get; private set; }

        public Task<Result<string>> PreguntarAsync(IReadOnlyList<MensajeChatDto> historial, CancellationToken cancellationToken)
        {
            HistorialRecibido = historial;
            return Task.FromResult(resultado);
        }
    }

    [Fact]
    public async Task Reenvia_el_historial_completo_al_servicio_y_devuelve_su_resultado_exitoso()
    {
        var historial = new List<MensajeChatDto> { new(RolMensajeChat.Usuario, "¿Cada cuánto se renueva un reconocimiento médico?") };
        var servicio = new AsistenteIaServiceFalso(Result.Exito("Depende del puesto de trabajo…"));
        var handler = new PreguntarAlAsistenteQueryHandler(servicio);

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery(historial), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("Depende del puesto de trabajo…");
        servicio.HistorialRecibido.Should().BeEquivalentTo(historial);
    }

    [Fact]
    public async Task Propaga_un_resultado_fallido_del_servicio_sin_modificarlo()
    {
        var error = Error.Crear("AsistenteIa.NoConfigurado", "El asistente no está disponible ahora mismo.");
        var servicio = new AsistenteIaServiceFalso(Result.Fallo<string>(error));
        var handler = new PreguntarAlAsistenteQueryHandler(servicio);

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery([]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(error);
    }
}
