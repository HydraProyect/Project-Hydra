using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.RestaurarCentro;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarCentroCommand(Guid Id) : ICommand;

public class RestaurarCentroCommandHandler(
    ICentrosQueryContext centrosContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    IAltaAcreditacionesPlataformaService altaAcreditaciones, ISerializacionAltasAcreditacionEmpresa serializacionAltas)
    : IRequestHandler<RestaurarCentroCommand, Result>
{
    /// <summary>
    /// Un Centro restaurado vuelve a ser un Centro de su Empresa: sus accesos de plataforma, que la baja no tocó, reciben
    /// los Documentos de Empresa que el Centro exige (regla A-1), también los creados mientras estaba eliminado. Se
    /// serializa con el alta de un Documento de Empresa (ver <see cref="ISerializacionAltasAcreditacionEmpresa"/>), y el
    /// Centro se lee y se restaura bajo el candado.
    /// </summary>
    public async Task<Result> Handle(RestaurarCentroCommand request, CancellationToken cancellationToken) =>
        await serializacionAltas.EjecutarAsync(ct => RestaurarAsync(request, ct), cancellationToken);

    private async Task<Result> RestaurarAsync(RestaurarCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await centrosContext.Centros
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tenantActual.TenantId, cancellationToken);

        if (centro is null || !centro.EstaEliminado)
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro eliminado."));

        // Autoridad por el cliente, no solo tenant (auditoría Módulo 5,
        // hallazgo crítico 8/9): CentroVisibleAsync no sirve aquí porque su
        // consulta pasa por el filtro global de soft delete, que excluye
        // justamente la fila que se está restaurando — el ClienteId
        // persistido es la coordenada estable.
        if (!await alcanceDatos.ClienteVisibleAsync(centro.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro eliminado."));

        centro.Restaurar();
        // Dos guardados en la misma transacción del candado: la regla A-1 lee el Centro, su Empresa y su modalidad de
        // gestión CAE de la base, donde el filtro de borrado lógico lo oculta hasta que la restauración se guarda.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Solo Documentos de Empresa: la baja cerró las Asignaciones del Centro y la restauración no las reabre, así
        // que no hay Trabajadores con actividad en él; los Documentos de Trabajador no cambian.
        var accesosPlataforma = await centrosContext.CanalesGestionDocumental
            .Where(c => c.CentroId == centro.Id && c.Tipo == TipoCanalGestion.Plataforma)
            .ToListAsync(cancellationToken);
        if (accesosPlataforma.Count > 0
            && await altaAcreditaciones.AgregarPendientesAsync(
                new AltasConAcreditacion { Canales = accesosPlataforma, SoloDocumentosDeEmpresa = true }, cancellationToken) > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
