using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Presentaciones;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.VolverAPresentarDocumentoEnCentro;

/// <summary>
/// «Volver a presentar» (decisión del propietario del producto, 2026-10-04): registra una NUEVA presentación del MISMO Documento a
/// un Centro cuya periodicidad especial lo exige, y con ello reinicia el plazo de ESE Centro
/// (<see cref="ReglaBloqueoDeAcceso.VencimientoEfectivo"/>). No crea un Documento ni lo renueva: el Documento, su archivo y su
/// vigencia propia no cambian, y en los demás Centros no pasa nada.
///
/// <para>
/// Solo se ofrece, y solo se acepta aquí (la pantalla la oculta con la misma función,
/// <see cref="ReglaBloqueoDeAcceso.PuedeVolverAPresentar"/>), si el Centro tiene una fila Incluida para el Tipo con periodicidad
/// especial y el Documento es operativo y sigue vigente por su fecha propia: un Documento vencido se renueva, no se vuelve a
/// presentar. Autorización: el Documento tiene que ser visible, el Centro visible <b>para gestión</b> y con gestión CAE, y el
/// titular del Documento tiene que pertenecer a ese Centro (un Trabajador con Asignación activa en él, o su Empresa); un Documento
/// que no cumple se trata como inexistente. El aislamiento entre Tenants lo dan el filtro de consulta y RLS.
/// </para>
/// </summary>
public record VolverAPresentarDocumentoEnCentroCommand(Guid DocumentoId, Guid CentroId) : ICommand;

public class VolverAPresentarDocumentoEnCentroCommandValidator : AbstractValidator<VolverAPresentarDocumentoEnCentroCommand>
{
    public VolverAPresentarDocumentoEnCentroCommandValidator()
    {
        RuleFor(c => c.DocumentoId).NotEmpty();
        RuleFor(c => c.CentroId).NotEmpty();
    }
}

public class VolverAPresentarDocumentoEnCentroCommandHandler(
    IDocumentoRepository documentoRepositorio, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ICentrosQueryContext centrosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IAsignacionesQueryContext asignacionesContext, ITrabajadoresQueryContext trabajadoresContext,
    IRegistroDePresentaciones presentaciones, IUnitOfWork unitOfWork)
    : IRequestHandler<VolverAPresentarDocumentoEnCentroCommand, Result>
{
    public const string CodigoNoEncontrado = "Presentacion.NoEncontrada";
    public const string CodigoNoAplica = "Presentacion.NoAplica";

    private static readonly Error NoEncontrada = Error.Crear(CodigoNoEncontrado, "No encontramos este documento en este Centro.");

    public async Task<Result> Handle(VolverAPresentarDocumentoEnCentroCommand request, CancellationToken cancellationToken)
    {
        var documento = await documentoRepositorio.ObtenerPorIdAsync(request.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(NoEncontrada);

        if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken)
            || (await CentrosSinGestionCae.FiltrarAsync(centrosContext, [request.CentroId], cancellationToken)).Count > 0
            || !await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo(NoEncontrada);

        if (!DocumentoOperativo.Es(documento))
            return Result.Fallo(DocumentoEnHistorial.NuevoError());

        if (!await PerteneceAlCentroAsync(documento, request.CentroId, cancellationToken))
            return Result.Fallo(NoEncontrada);

        // Las condiciones del Centro salen de la misma fila y de la misma función que usan las pantallas y el bloqueo.
        var fila = await tiposDocumentoContext.TiposDocumentoCentros
            .FirstOrDefaultAsync(tc => tc.TipoDocumentoId == documento.TipoDocumentoId && tc.CentroId == request.CentroId, cancellationToken);
        var condiciones = VigenciaEnCentro.Condiciones(fila, toleranciaDelClienteEmpresarial: null);

        var documentoParaAcceso = new DocumentoParaAcceso(documento.Vigencia, documento.FechaEmision);
        if (!ReglaBloqueoDeAcceso.PuedeVolverAPresentar(documentoParaAcceso, condiciones, DiaDeNegocio.Hoy(), esOperativo: true))
            return Result.Fallo(Error.Crear(
                CodigoNoAplica,
                "Este documento no se puede volver a presentar a este Centro: el Centro no le exige una periodicidad propia o el documento ya no está vigente (en ese caso, renuévalo)."));

        await presentaciones.RegistrarAsync(documento.Id, request.CentroId, OrigenPresentacionDocumentoEnCentro.VolverAPresentar, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    /// <summary>
    /// El titular del Documento pertenece al Centro: un Documento de Trabajador, si tiene Asignación activa en él; uno de Empresa, si
    /// es la Empresa propia del Centro o la de algún Trabajador con Asignación activa en él. Un Documento de otro ámbito no se presenta
    /// a un Centro.
    /// </summary>
    private async Task<bool> PerteneceAlCentroAsync(Documento documento, Guid centroId, CancellationToken cancellationToken)
    {
        if (documento.TrabajadorId is { } trabajadorId)
            return await asignacionesContext.Asignaciones.AnyAsync(
                a => a.CentroId == centroId && a.TrabajadorId == trabajadorId && a.FechaBaja == null, cancellationToken);

        if (documento.EmpresaId is not { } empresaId)
            return false;

        if (await centrosContext.Centros.AnyAsync(c => c.Id == centroId && c.EmpresaId == empresaId, cancellationToken))
            return true;

        return await (
            from asignacion in asignacionesContext.Asignaciones
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where asignacion.CentroId == centroId && asignacion.FechaBaja == null
                && (trabajador.EmpresaId == empresaId || trabajador.SubcontrataId == empresaId)
            select asignacion.Id)
            .AnyAsync(cancellationToken);
    }
}
