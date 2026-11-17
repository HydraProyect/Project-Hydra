using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;

/// <summary>Nivel 1 (Administrador): activa/desactiva la lectura automática por IA de un TipoDocumento para todos los Clientes — ver TipoDocumento.LecturaIaActiva. Nunca la activa en un tipo que revela salud.</summary>
[AreaDelEncargo("TiposDocumento")]
public record ActualizarLecturaIaGlobalCommand(Guid TipoDocumentoId, bool Activa) : ICommand;

public class ActualizarLecturaIaGlobalCommandHandler(
    ITipoDocumentoRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<ActualizarLecturaIaGlobalCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(ActualizarLecturaIaGlobalCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol != RolAdministrador)
            return Result.Fallo(Error.Crear("LecturaIa.SoloAdministrador", "Solo Administrador puede cambiar este interruptor."));

        var tipoDocumento = await repositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("TipoDocumento.NoEncontrado", "No encontramos este tipo de documento."));

        // Decisión de Legal del 2026-11-17: un tipo que revela salud no admite lectura por IA (misma condición que
        // Traducir PDF, hoy nunca cumplida). El dominio lo repite; aquí se devuelve como fallo y no se guarda nada.
        if (request.Activa && tipoDocumento.RevelaSalud)
            return Result.Fallo(Error.Crear("LecturaIa.BloqueadaPorSalud", CaeManager.Domain.Documentos.TipoDocumento.MensajeLecturaIaBloqueadaPorSalud));

        tipoDocumento.EstablecerLecturaIaActiva(request.Activa);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
