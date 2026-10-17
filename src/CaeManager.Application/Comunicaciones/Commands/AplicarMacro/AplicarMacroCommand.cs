using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.AplicarMacro;

public record AplicarMacroCommand(Guid ConversacionId, Guid MacroId) : ICommand<string>;

public class AplicarMacroCommandHandler(
    IComunicacionesQueryContext comunicaciones,
    IEmpresasQueryContext empresas,
    ICentrosQueryContext centros,
    ITrabajadoresQueryContext trabajadores,
    IVisitasQueryContext visitas,
    IDocumentosQueryContext documentos,
    ITiposDocumentoQueryContext tiposDocumento,
    IAlcanceDatosService alcanceDatos,
    IEventoConversacionRepository eventos,
    IUnitOfWork unitOfWork) : IRequestHandler<AplicarMacroCommand, Result<string>>
{
    public async Task<Result<string>> Handle(AplicarMacroCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await comunicaciones.Conversaciones.FirstOrDefaultAsync(c => c.Id == request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo<string>(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var macro = await comunicaciones.MacrosRespuesta.FirstOrDefaultAsync(m => m.Id == request.MacroId, cancellationToken);
        if (macro is null || (macro.ClienteId is not null && macro.ClienteId != conversacion.ClienteId))
            return Result.Fallo<string>(Error.Crear("Macro.NoEncontrada", "No encontramos esta plantilla para la conversación."));

        var participantes = await comunicaciones.ParticipantesConversacion
            .Where(p => p.ConversacionId == request.ConversacionId).ToListAsync(cancellationToken);
        Guid? EntidadUnica(TipoParticipanteOrigen tipo)
        {
            var ids = participantes.Where(p => p.TipoOrigen == tipo && p.EntidadRelacionadaId != null)
                .Select(p => p.EntidadRelacionadaId!.Value).Distinct().ToList();
            return ids.Count == 1 ? ids[0] : null;
        }

        var empresaId = conversacion.EmpresaId ?? EntidadUnica(TipoParticipanteOrigen.Empresa);
        var trabajadorId = EntidadUnica(TipoParticipanteOrigen.Trabajador);
        var centroId = EntidadUnica(TipoParticipanteOrigen.Centro);
        if (empresaId is { } empresa && !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa, cancellationToken)) empresaId = null;
        if (trabajadorId is { } trabajador && !await alcanceDatos.TrabajadorVisibleAsync(trabajador, cancellationToken)) trabajadorId = null;
        var visitaIds = comunicaciones.EventosConversacion
            .Where(e => e.ConversacionId == request.ConversacionId && e.Tipo == TipoEventoConversacion.VisitaCreada)
            .Select(e => e.ReferenciaId);
        var visitasDelHilo = await visitas.Visitas.Where(v => visitaIds.Contains(v.Id))
            .Select(v => new { v.CentroId, v.FechaInicio }).ToListAsync(cancellationToken);
        if (centroId is null && visitasDelHilo.Select(v => v.CentroId).Distinct().Count() == 1)
            centroId = visitasDelHilo[0].CentroId;
        if (centroId is { } centro && !await alcanceDatos.CentroVisibleAsync(centro, cancellationToken)) centroId = null;

        var documentosDelHilo = comunicaciones.EventosConversacion
            .Where(e => e.ConversacionId == request.ConversacionId && e.Tipo == TipoEventoConversacion.DocumentoActualizado)
            .Select(e => e.ReferenciaId);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var nombresPendientes = await (
            from documento in documentos.Documentos
            join tipo in tiposDocumento.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
            where documentosDelHilo.Contains(documento.Id) && documento.FechaVencimiento < hoy &&
                ((trabajadorId != null && documento.TrabajadorId == trabajadorId) ||
                 (empresaId != null && documento.EmpresaId == empresaId))
            orderby tipo.Nombre
            select tipo.Nombre).Distinct().ToListAsync(cancellationToken);

        // Solo datos vinculados al hilo. Varias visitas o participantes no se eligen al azar.
        var valores = new Dictionary<string, string?>
        {
            ["Empresa"] = await empresas.Empresas.Where(e => e.Id == empresaId).Select(e => e.RazonSocial).FirstOrDefaultAsync(cancellationToken),
            ["Cliente empresarial"] = await empresas.Empresas.Where(e => e.Id == conversacion.ClienteId).Select(e => e.RazonSocial).FirstOrDefaultAsync(cancellationToken),
            ["Centro"] = await centros.Centros.Where(c => c.Id == centroId).Select(c => c.Nombre).FirstOrDefaultAsync(cancellationToken),
            ["Trabajador"] = await trabajadores.Trabajadores.Where(t => t.Id == trabajadorId).Select(t => t.Nombre + " " + t.Apellidos).FirstOrDefaultAsync(cancellationToken),
            ["fecha de la visita"] = visitasDelHilo.Count == 1 && centroId != null ? visitasDelHilo[0].FechaInicio.ToString("dd/MM/yyyy") : null,
            ["documentos pendientes"] = nombresPendientes.Count > 0 ? string.Join(", ", nombresPendientes) : null
        };

        var cuerpo = VariablesPlantillaRespuesta.Rellenar(macro.CuerpoHtml, valores);
        eventos.Agregar(new EventoConversacion(conversacion.Id, TipoEventoConversacion.PlantillaAplicada, macro.Id, DateTime.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(cuerpo);
    }
}
