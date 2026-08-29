using CaeManager.Application.Common;
using CaeManager.Domain.BusquedaGlobal;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.BusquedaGlobal.Commands.RegistrarUsoReciente;

/// <summary>
/// Escribe la fila de "reciente" al seleccionar algo desde el Command
/// Palette (ver BuscadorGlobal.razor.cs). El nombre es deliberado: registra
/// uso del palette, no "vistas" — nada en este diseño observa navegación
/// directa fuera del Ctrl+K.
/// </summary>
public record RegistrarUsoRecienteCommand(string Tipo, Guid? EntidadId, string Titulo, string? Subtitulo, string UrlDestino) : ICommand;

public class RegistrarUsoRecienteCommandHandler(
    IEventoRecienteUsuarioRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService usuarioActual)
    : IRequestHandler<RegistrarUsoRecienteCommand, Result>
{
    /// <summary>Cuántos eventos por usuario se conservan — ver "Retención acotada" del plan.</summary>
    private const int MaximoRecientesPorUsuario = 200;

    public async Task<Result> Handle(RegistrarUsoRecienteCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Exito();

        repositorio.Agregar(new EventoRecienteUsuario(
            usuarioId.Value, request.Tipo, request.EntidadId, request.Titulo, request.Subtitulo, request.UrlDestino));

        // La purga consulta directamente la base de datos, así que todavía
        // no ve el evento recién añadido (sigue solo en el ChangeTracker,
        // sin SaveChanges). Conservar MaximoRecientesPorUsuario - 1 de los ya
        // existentes dejo sitio exacto para que, tras el único
        // SaveChangesAsync de abajo (alta + purga juntas), el total quede en
        // MaximoRecientesPorUsuario.
        await repositorio.PurgarExcedentesAsync(usuarioId.Value, MaximoRecientesPorUsuario - 1, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
