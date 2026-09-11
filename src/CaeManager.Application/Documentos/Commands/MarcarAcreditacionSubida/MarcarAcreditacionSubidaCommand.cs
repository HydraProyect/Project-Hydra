using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;

/// <summary>
/// "Marcar subido" del drill-down por plataforma (mockup Documentos TALVEG,
/// pestaña Plataforma — cierra el hallazgo P-04): el domino ya sabía pasar
/// una AcreditacionDocumentoPlataforma a Subida (Lote 2-C), pero ningún
/// Command de Application la disparaba todavía (Lote 2-D, "todavía sin
/// construir" según el propio comentario de la entidad).
///
/// <see cref="ExigirProveedorActivo"/> (MVP2 § 14.5, *kill switch* remoto):
/// solo la extensión de navegador lo pasa a <c>true</c>
/// (<c>MarcarAcreditacionSubidaEndpoints</c>) — el drill-down interno
/// (<c>PlataformaTab.razor</c>) deja el valor por defecto porque "marcar
/// subido" ahí registra una subida que el gestor ya hizo A MANO en la
/// plataforma, sin pasar por la extensión ni por su inyección; bloquearla
/// por un proveedor inactivo rompería ese registro sin motivo — el
/// conector inactivo es sobre la extensión, no sobre la plataforma en sí.
/// Nótese que para cuando este Command corre, la extensión YA descargó el
/// PDF e YA lo inyectó en el DOM de la plataforma (ver
/// <c>extension/background.js</c>, <c>subirDocumento</c>) — el freno real
/// que evita la inyección vive en el propio popup, con el dato que ya trae
/// <c>ObtenerAcreditacionesPorProveedorQuery</c>. Esta comprobación es la
/// segunda línea: que el registro de Hydra nunca diga "subida" para un
/// conector que la plataforma declaró inactivo, ni siquiera si un cliente
/// desactualizado o modificado se saltó el freno del popup.
/// </summary>
public record MarcarAcreditacionSubidaCommand(Guid AcreditacionId, bool ExigirProveedorActivo = false) : ICommand;

public class MarcarAcreditacionSubidaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionSubidaCommand, Result>
{
    public async Task<Result> Handle(MarcarAcreditacionSubidaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await acreditacionRepositorio.ObtenerPorIdAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        if (request.ExigirProveedorActivo && !await ProveedorActivoAsync(acreditacion.CanalGestionDocumentalId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Acreditacion.ConectorInactivo", "Este conector está desactivado temporalmente."));

        acreditacion.MarcarSubida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    private async Task<bool> ProveedorActivoAsync(Guid canalGestionDocumentalId, CancellationToken cancellationToken)
    {
        var proveedorId = await centrosContext.CanalesGestionDocumental
            .Where(c => c.Id == canalGestionDocumentalId)
            .Select(c => c.ProveedorPlataformaCaeId)
            .SingleOrDefaultAsync(cancellationToken);

        if (proveedorId is null) return true; // sin proveedor de catálogo, nada que apagar

        return await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => p.Id == proveedorId.Value)
            .Select(p => p.Activo)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
