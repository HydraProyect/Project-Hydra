using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;

/// <summary>
/// «Lo he enviado» del panel de la extensión de navegador (D-4 del plan de la extensión, incremento E1): la extensión
/// puso el documento en el formulario de la plataforma CAE, el Gestor CAE lo envió allí y lo confirma. La acreditación
/// queda Subida (Pendiente de validar) con un <see cref="CambioEstadoAcreditacionPlataforma"/> de origen
/// <see cref="OrigenCambioEstadoAcreditacion.Extension"/>: sale en el Historial del Centro y se puede deshacer
/// (<see cref="DeshacerSubidaDesdeExtensionCommand"/>). Poner el archivo en el formulario no anota nada.
///
/// <para>
/// Mismo alcance que «Marcar subido» (<see cref="AcreditacionSubible"/>: el Centro del canal tiene que estar entre los
/// que gestiona quien pide y el Documento en su cartera; lo demás sale como «no encontrada») y la misma regla de la
/// rechazada sin versión nueva. Además exige
/// el proveedor activo (MVP2 § 14.5, kill switch remoto): el servidor ya lo comprobó antes de entregar el archivo
/// (<c>PrepararEntregaArchivoExtensionQuery</c>), y esta es la segunda línea, para que TALVEG nunca anote una subida por
/// un conector que se apagó entre medias.
/// </para>
///
/// <para>
/// Plataforma correcta (incremento E2): la extensión declara en <see cref="DominioPortal"/> el host de la pestaña donde
/// puso el documento, y el servidor lo compara con los dominios del Proveedor del canal
/// (<c>DominioProveedorPlataformaCae</c>, mismo criterio de sufijo que
/// <see cref="ResolucionProveedorPlataformaCaeService.CoincideHost"/>). Es la segunda línea tras el filtro de la propia
/// extensión: protege frente a un cliente desactualizado o con un error, que ya no puede anotar como subido en una
/// plataforma lo que puso en otra. NO es una frontera frente a un cliente hostil con un token válido: el dominio lo
/// declara el propio cliente y podría mentir. Sin dominio declarado, o con un canal sin Proveedor o sin dominios en el
/// catálogo, se rechaza (no hay forma de comprobarlo). «Marcar subido» a mano (<c>MarcarAcreditacionSubidaCommand</c>)
/// no lo pide: ahí el gestor registra una subida que hizo él.
/// </para>
/// </summary>
public record ConfirmarSubidaDesdeExtensionCommand(Guid AcreditacionId, string? DominioPortal = null) : ICommand<CambioEstadoAnotadoDto>;

/// <summary>
/// «Deshacer» del aviso que sigue a «Lo he enviado» en la extensión: devuelve el estado anterior que guardó el cambio.
/// Solo deshace un cambio de origen Extensión (uno de Centro 360 se deshace desde Centro 360), y solo si sigue siendo el
/// que dejó el estado actual y la acreditación no se ha tocado desde entonces (<paramref name="VersionEsperada"/>, la que
/// devolvió la confirmación).
/// </summary>
public record DeshacerSubidaDesdeExtensionCommand(Guid AcreditacionId, Guid CambioId, Guid VersionEsperada) : ICommand;

public class ConfirmarSubidaDesdeExtensionCommandValidator : AbstractValidator<ConfirmarSubidaDesdeExtensionCommand>
{
    public ConfirmarSubidaDesdeExtensionCommandValidator() => RuleFor(c => c.AcreditacionId).NotEmpty();
}

public class DeshacerSubidaDesdeExtensionCommandValidator : AbstractValidator<DeshacerSubidaDesdeExtensionCommand>
{
    public DeshacerSubidaDesdeExtensionCommandValidator()
    {
        RuleFor(c => c.AcreditacionId).NotEmpty();
        RuleFor(c => c.CambioId).NotEmpty();
        // Guid.Empty en ConcurrenciaOptimista significa «sin comprobación»: aquí no vale.
        RuleFor(c => c.VersionEsperada).NotEmpty();
    }
}

public class ConfirmarSubidaDesdeExtensionCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
    ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarSubidaDesdeExtensionCommand, Result<CambioEstadoAnotadoDto>>
{
    public const string CodigoConectorInactivo = "Acreditacion.ConectorInactivo";
    public const string CodigoPortalNoDeclarado = "Acreditacion.PortalNoDeclarado";
    public const string CodigoPortalAjeno = "Acreditacion.PortalAjeno";

    public async Task<Result<CambioEstadoAnotadoDto>> Handle(ConfirmarSubidaDesdeExtensionCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext, centrosContext,
            recargar: true, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo<CambioEstadoAnotadoDto>(AcreditacionSubible.NoEncontrada);

        if (!ReglaSubidaAcreditacion.PermiteSubir(acreditacion))
            return Result.Fallo<CambioEstadoAnotadoDto>(AcreditacionSubible.RechazadaSinVersionNueva);

        if (!await PortalDelCanalAcreditacion.ProveedorActivoAsync(
                acreditacion.CanalGestionDocumentalId, centrosContext, proveedoresContext, cancellationToken))
            return Result.Fallo<CambioEstadoAnotadoDto>(Error.Crear(
                CodigoConectorInactivo, "Este conector está desactivado temporalmente."));

        var host = ResolucionProveedorPlataformaCaeService.ExtraerHost(request.DominioPortal);
        if (host is null)
            return Result.Fallo<CambioEstadoAnotadoDto>(Error.Crear(
                CodigoPortalNoDeclarado, "La extensión no indicó en qué portal puso el documento. Actualiza la extensión e inténtalo de nuevo."));

        if (!await PortalDelCanalAcreditacion.HostEsDelProveedorAsync(
                acreditacion.CanalGestionDocumentalId, host, centrosContext, proveedoresContext, cancellationToken))
            return Result.Fallo<CambioEstadoAnotadoDto>(Error.Crear(
                CodigoPortalAjeno, await MensajePortalAjenoAsync(acreditacion.CanalGestionDocumentalId, cancellationToken)));

        var cambio = acreditacion.ConfirmarEnvioDesdeExtension(
            DiaDeNegocio.Hoy(), await currentUserService.ObtenerUsuarioActualIdAsync(), DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // La versión se lee DESPUÉS de guardar: el interceptor de concurrencia la renueva al escribir.
        return Result.Exito(new CambioEstadoAnotadoDto(cambio.Id, acreditacion.Version));
    }

    /// <summary>
    /// El detalle que ve la persona al anotar en un portal que no es el de su documento. Sin nombre del Proveedor en el
    /// catálogo sale el genérico.
    /// </summary>
    private async Task<string> MensajePortalAjenoAsync(Guid canalGestionDocumentalId, CancellationToken cancellationToken)
    {
        var nombre = await PortalDelCanalAcreditacion.NombreProveedorAsync(
            canalGestionDocumentalId, centrosContext, proveedoresContext, cancellationToken);

        return nombre is null
            ? "Este documento es de otra Plataforma CAE: no se puede anotar como enviado en este portal."
            : $"Este documento es de otra Plataforma CAE: debe subirse en la Plataforma CAE {nombre}, no se puede anotar como enviado en este portal.";
    }
}

public class DeshacerSubidaDesdeExtensionCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<DeshacerSubidaDesdeExtensionCommand, Result>
{
    public const string CodigoYaNoSePuedeDeshacer = DeshacerCambioEstadoAcreditacionCommandHandler.CodigoYaNoSePuedeDeshacer;

    public async Task<Result> Handle(DeshacerSubidaDesdeExtensionCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext, centrosContext,
            recargar: true, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(AcreditacionSubible.NoEncontrada);

        // Un cambio que no es de la extensión, o que no es de esta acreditación, no existe para este comando.
        if (!acreditacion.CambiosEstado.Any(c => c.Id == request.CambioId && c.Origen == OrigenCambioEstadoAcreditacion.Extension))
            return Result.Fallo(AcreditacionSubible.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(acreditacion, request.VersionEsperada, "esta acreditación") is not null)
            return Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto,
                "No se puede deshacer: el estado cambió después en TALVEG. Corrígelo desde Centro 360."));

        if (!acreditacion.PuedeDeshacerCambioEstado(request.CambioId))
            return Result.Fallo(Error.Crear(CodigoYaNoSePuedeDeshacer,
                "El estado ya ha vuelto a cambiar: no se puede deshacer. Corrígelo desde Centro 360."));

        acreditacion.DeshacerCambioEstado(request.CambioId, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
