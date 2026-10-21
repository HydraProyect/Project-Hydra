using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;

/// <summary>
/// Centro 360 (bloque 3 de la sesión del 21-10): el Gestor CAE sube a mano el documento a la plataforma CAE del Cliente
/// y anota aquí el estado que ve allí. Lo usan «Marcar subido», «Marcar validado» y el modal «Cambiar». Solo cambia
/// la acreditación de ese documento en el canal de plataforma de ESE Centro (identidad: Centro, plataforma, titular y
/// tipo de documento), nunca el Documento ni su vigencia en TALVEG. Devuelve el cambio y la versión de la acreditación
/// tras guardarlo, que es lo que pide <see cref="DeshacerCambioEstadoAcreditacionCommand"/>.
/// </summary>
/// <param name="CentroId">El Centro desde el que se anota: la acreditación tiene que ser de un canal suyo.</param>
/// <param name="Fecha">El día que dice el Gestor CAE (por defecto, hoy en la pantalla).</param>
/// <param name="Motivo">Obligatorio con <see cref="EstadoAcreditacion.Rechazada"/>; en otro estado se ignora.</param>
public record CambiarEstadoAcreditacionEnCentroCommand(
    Guid CentroId, Guid AcreditacionId, EstadoAcreditacion Estado, DateOnly Fecha, string? Motivo, string? Nota)
    : ICommand<CambioEstadoAnotadoDto>;

/// <summary>El cambio anotado y la versión de la acreditación justo después: lo que el «Deshacer» del aviso tiene que devolver.</summary>
public record CambioEstadoAnotadoDto(Guid CambioId, Guid Version);

/// <summary>
/// «Deshacer» del aviso que sigue a «Marcar subido» o «Marcar validado»: devuelve el estado anterior que guardó el cambio.
/// <paramref name="VersionEsperada"/> es la versión que devolvió el cambio: si la acreditación se tocó después, aunque
/// haya vuelto al mismo estado (A → B → A), no se deshace (concurrencia optimista, como RestaurarAnotacionAcreditacionCommand).
/// </summary>
public record DeshacerCambioEstadoAcreditacionCommand(Guid CentroId, Guid AcreditacionId, Guid CambioId, Guid VersionEsperada) : ICommand;

public class CambiarEstadoAcreditacionEnCentroCommandValidator : AbstractValidator<CambiarEstadoAcreditacionEnCentroCommand>
{
    public const string MensajeMotivoObligatorio = "Escribe el motivo: es lo que verá quien tenga que corregir el documento.";

    public CambiarEstadoAcreditacionEnCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.AcreditacionId).NotEmpty();
        RuleFor(c => c.Estado).Must(AcreditacionDocumentoPlataforma.EsEstadoAnotableAMano)
            .WithMessage("Elige uno de los cuatro estados de la plataforma.");
        RuleFor(c => c.Fecha).NotEqual(default(DateOnly)).WithMessage("Escribe la fecha.");
        RuleFor(c => c.Motivo).NotEmpty().When(c => c.Estado == EstadoAcreditacion.Rechazada)
            .WithMessage(MensajeMotivoObligatorio);
        RuleFor(c => c.Motivo).MaximumLength(RechazoAcreditacionDocumentoPlataforma.LongitudMaximaMotivoLiteral);
        RuleFor(c => c.Nota).MaximumLength(CambioEstadoAcreditacionPlataforma.LongitudMaximaNota);
    }
}

public class DeshacerCambioEstadoAcreditacionCommandValidator : AbstractValidator<DeshacerCambioEstadoAcreditacionCommand>
{
    public DeshacerCambioEstadoAcreditacionCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.AcreditacionId).NotEmpty();
        RuleFor(c => c.CambioId).NotEmpty();
        // Guid.Empty en ConcurrenciaOptimista significa «sin comprobación»: aquí no vale.
        RuleFor(c => c.VersionEsperada).NotEmpty();
    }
}

/// <summary>
/// Quién puede anotar: el mismo alcance que el «Gestionar» de la acreditación (su Documento se ve, que es la
/// comprobación de cartera del Gestor CAE, y el permiso de escritura lo pone <c>AutorizacionEscrituraBehavior</c>), más
/// que el Centro esté entre los que el usuario gestiona: en el Tenant propietario o en el Workspace operativo derivado
/// que abre una Asignación de Operación (lo resuelve <see cref="IAlcanceDatosService"/>). RLS acota todo al Tenant
/// operado. Lo que no cumple sale como «no encontrada», sin decir por qué.
/// </summary>
internal static class AcreditacionEnCentroGestionable
{
    public static readonly Error NoEncontrada = Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación.");

    public static async Task<AcreditacionDocumentoPlataforma?> CargarAsync(
        Guid centroId, Guid acreditacionId,
        IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
        ICentrosQueryContext centrosContext, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
        CancellationToken cancellationToken)
    {
        var centrosParaGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        if (centrosParaGestion is not null && !centrosParaGestion.Contains(centroId))
            return null;

        var acreditacion = await acreditacionRepositorio.ObtenerPorIdActualizadoAsync(acreditacionId, cancellationToken);
        if (acreditacion is null)
            return null;

        var esDelCentro = await centrosContext.CanalesGestionDocumental
            .AnyAsync(c => c.Id == acreditacion.CanalGestionDocumentalId && c.CentroId == centroId, cancellationToken);
        if (!esDelCentro)
            return null;

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        return documento is not null && await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken)
            ? acreditacion
            : null;
    }
}

public class CambiarEstadoAcreditacionEnCentroCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    ICentrosQueryContext centrosContext, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarEstadoAcreditacionEnCentroCommand, Result<CambioEstadoAnotadoDto>>
{
    public async Task<Result<CambioEstadoAnotadoDto>> Handle(CambiarEstadoAcreditacionEnCentroCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionEnCentroGestionable.CargarAsync(
            request.CentroId, request.AcreditacionId, acreditacionRepositorio, documentoRepositorio,
            centrosContext, alcanceDatos, proyectosContext, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo<CambioEstadoAnotadoDto>(AcreditacionEnCentroGestionable.NoEncontrada);

        var cambio = acreditacion.CambiarEstadoAMano(
            request.Estado, request.Fecha, request.Motivo, request.Nota,
            await currentUserService.ObtenerUsuarioActualIdAsync(), DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // La versión se lee DESPUÉS de guardar: el interceptor de concurrencia la renueva al escribir.
        return Result.Exito(new CambioEstadoAnotadoDto(cambio.Id, acreditacion.Version));
    }
}

public class DeshacerCambioEstadoAcreditacionCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    ICentrosQueryContext centrosContext, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<DeshacerCambioEstadoAcreditacionCommand, Result>
{
    public const string CodigoYaNoSePuedeDeshacer = "Acreditacion.CambioNoDeshacible";

    public async Task<Result> Handle(DeshacerCambioEstadoAcreditacionCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionEnCentroGestionable.CargarAsync(
            request.CentroId, request.AcreditacionId, acreditacionRepositorio, documentoRepositorio,
            centrosContext, alcanceDatos, proyectosContext, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(AcreditacionEnCentroGestionable.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(acreditacion, request.VersionEsperada, "esta acreditación") is not null)
            return Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto,
                "No se puede deshacer: el estado cambió después en esta plataforma. Usa «Cambiar» para corregirlo."));

        if (!acreditacion.PuedeDeshacerCambioEstado(request.CambioId))
            return Result.Fallo(Error.Crear(CodigoYaNoSePuedeDeshacer,
                "El estado ya ha vuelto a cambiar: no se puede deshacer. Usa «Cambiar» para corregirlo."));

        acreditacion.DeshacerCambioEstado(request.CambioId, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
