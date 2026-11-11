using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones.Commands.RepararMarcaHueco;

/// <summary>
/// La acción Reparar de Conexiones: registra la marca del hueco que una desconexión dejó por registrar
/// (<see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>) en una conexión que SIGUE Deshabilitada. Es una
/// operación propia, no Desconectar: nunca cambia el estado, nunca libera la reclamación del buzón y nunca llama a Graph.
/// Enviar Desconectar desde Reparar desconectaba la conexión si otra sesión la había reactivado después de cargar la fila
/// (Codex, ronda 19, hallazgo alto).
/// </summary>
public record RepararMarcaHuecoCommand(Guid ConexionId) : ICommand;

public class RepararMarcaHuecoCommandValidator : AbstractValidator<RepararMarcaHuecoCommand>
{
    public RepararMarcaHuecoCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
    }
}

/// <summary>
/// Dentro de <see cref="ICerrojoVerificacionBuzon"/>, el mismo cerrojo por conexión de Desconectar, Reactivar, la
/// verificación de identidad y la reparación del ciclo diario. Con el cerrojo, la conexión se relee de la base y la
/// decisión sale de ahí: solo una conexión Deshabilitada se repara, y mientras dure el cerrojo sigue así, porque la única
/// salida de Deshabilitada es Reactivar, que también lo toma. Si se releyó en otro estado (otra sesión la reactivó), no
/// se toca nada y se devuelve <see cref="CodigoNoDeshabilitada"/>; una reactivación ya registró lo pendiente
/// (<see cref="ConexionIntegracion.Rehabilitar"/>). El registro en sí es el de
/// <see cref="ReparacionMarcaHuecoService.RepararBajoCerrojoAsync"/>, que solo escribe la marca y el valor por registrar.
/// </summary>
public class RepararMarcaHuecoCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    ICerrojoVerificacionBuzon cerrojo,
    ReparacionMarcaHuecoService reparacion,
    ILogger<RepararMarcaHuecoCommandHandler> logger)
    : IRequestHandler<RepararMarcaHuecoCommand, Result>
{
    /// <summary>La conexión ya no está Deshabilitada al tomar el cerrojo: no hay nada que reparar y no se ha tocado.</summary>
    public const string CodigoNoDeshabilitada = "ConexionIntegracion.NoDeshabilitada";

    public async Task<Result> Handle(RepararMarcaHuecoCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || !await DesconectarBuzonCommandHandler.ConexionVisibleAsync(alcanceDatos, conexion, cancellationToken))
            return NoEncontrada();

        Result? resultado = null;
        if (!await cerrojo.IntentarEjecutarAsync(conexion.Id, async ct => resultado = await RepararBajoCerrojoAsync(conexion.Id, ct), cancellationToken))
            return Result.Fallo(Error.Crear(
                DesconectarBuzonCommandHandler.CodigoVerificacionEnCurso,
                "Ya estamos comprobando este buzón en Microsoft. Inténtalo de nuevo en unos segundos."));

        return resultado!;
    }

    private async Task<Result> RepararBajoCerrojoAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var actual = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, cancellationToken);

        // La visibilidad se vuelve a comprobar sobre el estado releído, como en Desconectar y Reactivar.
        if (actual is null || !await DesconectarBuzonCommandHandler.ConexionVisibleAsync(alcanceDatos, actual, cancellationToken))
            return NoEncontrada();

        if (actual.Estado != EstadoConexionIntegracion.Deshabilitada)
            return Result.Fallo(Error.Crear(
                CodigoNoDeshabilitada,
                "Este buzón ya no está desconectado, así que no hay nada que reparar. Actualizamos el listado."));

        // Sin nada por registrar (el ciclo diario ya lo reparó, o otra sesión pulsó Reparar antes): no se guarda nada.
        if (actual.MarcaHuecoPorRegistrarDesdeUtc is null || await reparacion.RepararBajoCerrojoAsync(actual.Id))
            return Result.Exito();

        logger.LogCritical(
            "La conexión {ConexionId} sigue Deshabilitada sin la marca de hueco: no se pudo guardar al repararla.", actual.Id);
        return Result.Fallo(Error.Crear(
            DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada,
            "No hemos podido registrar desde cuándo dejó de recibirse el correo de este buzón. Inténtalo de nuevo."));
    }

    private static Result NoEncontrada() =>
        Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));
}
