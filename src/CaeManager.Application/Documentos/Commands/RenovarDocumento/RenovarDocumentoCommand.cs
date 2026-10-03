using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.RenovarDocumento;

/// <summary>
/// Renovar un Documento. <b>Con archivo nuevo</b> crea un Documento nuevo del mismo titular y Tipo y sustituye
/// al anterior (<see cref="Documento.SustituirPor"/>): el anterior pasa al historial con su archivo, sus fechas y sus
/// acreditaciones intactos, y el nuevo es el único operativo de la unidad. <b>El Id cambia</b> (D8 del diseño del
/// documento efectivo): el comando devuelve el Id del Documento que queda en uso, y quien guarde enlaces tiene que
/// usar ese; un enlace al Id antiguo abre el historial con «sustituido por…». <b>Sin archivo nuevo</b> no hay nada que
/// sustituir: es una corrección de fechas o comentarios del mismo registro (<see cref="Documento.CorregirVigencia"/>) y
/// devuelve el mismo Id. Un Documento que ya está en el historial no se renueva ni se corrige.
///
/// <paramref name="Version"/> es la del registro tal como lo vio quien
/// renueva (llega en <c>DocumentoDetalleDto</c>) — mismo patrón que
/// <see cref="Application.Clientes.Commands.EditarCliente.EditarClienteCommand"/>.
/// <see cref="Guid.Empty"/> significa "sin comprobación", para los
/// llamadores que todavía no la propagan.
///
/// <paramref name="NoCaduca"/> es la confirmación expresa de que el documento
/// renovado no caduca; sin ella y sin fecha, queda
/// <see cref="VigenciaDocumento.SinConfirmar"/> (ver
/// <see cref="CalculadoraEstadoDocumento.ResolverVigencia"/>).
/// </summary>
public record RenovarDocumentoCommand(
    Guid Id, DateOnly FechaEmision, DateOnly? FechaVencimientoManual, string? ArchivoUrl, string? Comentarios,
    Guid Version = default, bool NoCaduca = false) : ICommand<Guid>;

public class RenovarDocumentoCommandValidator : AbstractValidator<RenovarDocumentoCommand>
{
    public RenovarDocumentoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(_ => DiaDeNegocio.Hoy())
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
        RuleFor(c => c)
            .Must(c => !(c.NoCaduca && c.FechaVencimientoManual is not null))
            .WithMessage("Un documento no puede tener fecha de vencimiento y a la vez no caducar: elige una de las dos.");
    }
}

public class RenovarDocumentoCommandHandler(
    IDocumentoRepository repositorio, ITiposDocumentoQueryContext dbContext,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ITrabajoAnalisisDocumentoRepository colaAnalisis, ICurrentUserService currentUserService,
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IPublisher publisher, IUnitOfWork unitOfWork)
    : IRequestHandler<RenovarDocumentoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RenovarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);

        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Documento.NoEncontrado", "No encontramos este documento."));

        // El historial es inmutable (D5/D8): lo que ya sustituyó otro documento no se renueva ni se corrige; se
        // renueva el vigente. Antes de la comprobación de versión: un documento sustituido ha cambiado de versión
        // (SustituirPor lo modificó) y el aviso de «otra persona lo editó» confundiría más que esta explicación.
        if (!DocumentoOperativo.Es(documento))
            return Result.Fallo<Guid>(DocumentoEnHistorial.NuevoError());

        if (ConcurrenciaOptimista.Verificar(documento, request.Version, "este documento") is { } conflicto)
            return Result.Fallo<Guid>(conflicto);

        var tipoDocumento = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null)
            return Result.Fallo<Guid>(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento asociado."));

        var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
            tipoDocumento.AplicaVencimientoAutomatico, tipoDocumento.VigenciaMeses,
            request.FechaEmision, request.FechaVencimientoManual, request.NoCaduca);

        // Un archivo «nuevo» es uno distinto del que ya tiene el documento: el formulario de edición reenvía el existente
        // cuando solo se corrigen fechas, y sustituir por un registro que comparte el blob rompería «un blob, un propietario».
        var hayArchivoNuevo = !string.IsNullOrWhiteSpace(request.ArchivoUrl) && request.ArchivoUrl != documento.ArchivoUrl;

        return !hayArchivoNuevo
            ? await CorregirEnElMismoRegistroAsync(documento, request, vigencia, cancellationToken)
            : await SustituirPorUnoNuevoAsync(documento, tipoDocumento, request, vigencia, cancellationToken);
    }

    /// <summary>
    /// Sin archivo nuevo no hay nada que sustituir: se corrigen las fechas y los comentarios del mismo registro y se
    /// reinician sus acreditaciones (los datos que se validaron en el portal ya no son estos). El Id no cambia.
    /// </summary>
    private async Task<Result<Guid>> CorregirEnElMismoRegistroAsync(
        Documento documento, RenovarDocumentoCommand request, VigenciaDocumento vigencia, CancellationToken cancellationToken)
    {
        documento.CorregirVigencia(request.FechaEmision, vigencia);
        documento.ActualizarComentarios(request.Comentarios);

        // Invariante de Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b): la versión
        // anterior de los datos ya no es la que hay que validar en ningún portal — corregirlos reinicia todas las
        // acreditaciones del documento a Pendiente de subir. El historial de rechazos no se toca (sigue siendo un hecho
        // pasado real).
        var acreditaciones = await acreditacionRepositorio.ObtenerPorDocumentoIdAsync(documento.Id, cancellationToken);
        foreach (var acreditacion in acreditaciones)
            acreditacion.ReiniciarPorRenovacionDocumento();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await publisher.Publish(new DocumentacionCambiadaEvent(documento.Id), cancellationToken);

        return Result.Exito(documento.Id);
    }

    /// <summary>
    /// Con archivo nuevo: Documento nuevo + sustitución del anterior en el mismo <c>SaveChangesAsync</c>, o todo o
    /// nada. <b>No se borra nada</b>: el archivo del anterior queda en almacenamiento, referenciado por su fila, que es
    /// historial (decisión 7: sin borrado automático). Las acreditaciones del anterior quedan como historial —no se
    /// reinician—; las del nuevo nacen en Pendiente de subir por la regla única de
    /// <see cref="IAltaAcreditacionesPlataformaService"/>, la misma del alta.
    /// </summary>
    private async Task<Result<Guid>> SustituirPorUnoNuevoAsync(
        Documento anterior, TipoDocumento tipoDocumento, RenovarDocumentoCommand request,
        VigenciaDocumento vigencia, CancellationToken cancellationToken)
    {
        var nuevo = anterior.NuevoDelMismoTitular(request.FechaEmision, vigencia, request.ArchivoUrl, request.Comentarios);

        repositorio.Agregar(nuevo);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);

        await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [nuevo] }, cancellationToken);

        // La mensualidad entra por aquí, no por Crear: el certificado de agosto reemplaza al de julio renovándolo.
        // Sin este reencolado, el archivo nuevo jamás se validaría. Mismo SaveChangesAsync que el resto del cambio —
        // se confirman juntos o ninguno, la garantía de CrearDocumentoCommand. Los análisis IA
        // (VerificacionIa/DeteccionTrabajadores) siguen sin reencolarse al renovar — decisión pendiente aparte, por su
        // coste de LLM en cada renovación (plan de la épica, PR-3).
        if (tipoDocumento.PerfilDocumentoOficial != PerfilDocumentoOficial.Ninguno)
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            colaAnalisis.Agregar(new TrabajoAnalisisDocumento(
                nuevo.Id, usuarioId, TipoAnalisisDocumento.VerificacionFirmaDigital));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Renovar puede sacar de "Vencido" el último documento que bloqueaba el expediente de una visita pendiente. El
        // evento lleva el Id del documento que queda en uso.
        await publisher.Publish(new DocumentacionCambiadaEvent(nuevo.Id), cancellationToken);

        return Result.Exito(nuevo.Id);
    }
}
