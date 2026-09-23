using CaeManager.Application.Common;
using CaeManager.Application.Notificaciones.Commands.MarcarNotificacionLeida;
using CaeManager.Domain.Notificaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Notificaciones;

/// <summary>
/// <see cref="MarcarNotificacionLeidaCommand"/> es de autoservicio
/// (<see cref="IComandoDeAutoservicio"/>): cualquier rol reconocido lo ejecuta, así
/// que el handler es quien impide tocar la notificación de otro usuario, y lo hace
/// sin revelar si ese Id existe.
/// </summary>
public class MarcarNotificacionLeidaCommandHandlerTests
{
    private readonly NotificacionUsuarioRepositorioFalso _repositorio = new();

    [Fact]
    public async Task Marca_como_leida_la_notificacion_propia()
    {
        var usuarioId = Guid.NewGuid();
        var propia = new NotificacionUsuario(usuarioId, "Documento vencido", "Revisa el documento.");
        _repositorio.Agregar(propia);

        var resultado = await Handler(usuarioId).Handle(new MarcarNotificacionLeidaCommand(propia.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        propia.Leida.Should().BeTrue();
    }

    [Fact]
    public async Task La_notificacion_de_otro_usuario_responde_igual_que_una_inexistente_y_no_se_marca()
    {
        var ajena = new NotificacionUsuario(Guid.NewGuid(), "Documento vencido", "Revisa el documento.");
        _repositorio.Agregar(ajena);
        var handler = Handler(Guid.NewGuid());

        var sobreAjena = await handler.Handle(new MarcarNotificacionLeidaCommand(ajena.Id), CancellationToken.None);
        var sobreInexistente = await handler.Handle(new MarcarNotificacionLeidaCommand(Guid.NewGuid()), CancellationToken.None);

        ajena.Leida.Should().BeFalse();
        sobreAjena.EsFallido.Should().BeTrue();
        sobreAjena.Error.Should().BeEquivalentTo(sobreInexistente.Error,
            "un mensaje distinto para «es de otro» delataría que el Id existe");
    }

    private MarcarNotificacionLeidaCommandHandler Handler(Guid usuarioId) =>
        new(_repositorio, new UnidadDeTrabajoNula(), new CurrentUserServiceFalso(usuarioId, "Consulta"));

    private sealed class UnidadDeTrabajoNula : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
