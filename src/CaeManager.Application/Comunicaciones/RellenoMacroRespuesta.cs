using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Rellena una macro con los datos vinculados al hilo: el relleno de <c>AplicarMacroCommand</c> y de la respuesta sugerida.
/// Solo devuelve el cuerpo. Quien llama comprueba antes el alcance de la conversación y decide qué registrar.
/// Una variable sin resolver se queda tal cual, y el validador de envío la bloquea.
/// </summary>
public class RellenoMacroRespuesta(
    IComunicacionesQueryContext comunicaciones,
    IEmpresasQueryContext empresas,
    ICentrosQueryContext centros,
    ITrabajadoresQueryContext trabajadores,
    IVisitasQueryContext visitas,
    IPendientesDeReclamacionService pendientesDeReclamacion,
    IAlcanceDatosService alcanceDatos)
{
    public async Task<Result<string>> RellenarAsync(Conversacion conversacion, Guid macroId, CancellationToken cancellationToken)
    {
        var macro = await comunicaciones.MacrosRespuesta.FirstOrDefaultAsync(m => m.Id == macroId, cancellationToken);
        if (macro is null || (macro.ClienteId is not null && macro.ClienteId != conversacion.ClienteId))
            return Result.Fallo<string>(Error.Crear("Macro.NoEncontrada", "No encontramos esta plantilla para la conversación."));

        var participantes = await comunicaciones.ParticipantesConversacion
            .Where(p => p.ConversacionId == conversacion.Id).ToListAsync(cancellationToken);
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
            .Where(e => e.ConversacionId == conversacion.Id && e.Tipo == TipoEventoConversacion.VisitaCreada)
            .Select(e => e.ReferenciaId);
        var visitasDelHilo = await visitas.Visitas.Where(v => visitaIds.Contains(v.Id))
            .Select(v => new { v.CentroId, v.FechaInicio }).ToListAsync(cancellationToken);
        if (centroId is null && visitasDelHilo.Select(v => v.CentroId).Distinct().Count() == 1)
            centroId = visitasDelHilo[0].CentroId;
        if (centroId is { } centro && !await alcanceDatos.CentroVisibleAsync(centro, cancellationToken)) centroId = null;

        // Regla documental única (vencidos, ausentes y sin confirmar, solo documentos operativos).
        // Los pendientes de Trabajador solo con un Trabajador inequívoco: sin él, un Centro
        // traería los de Trabajadores de otras Empresas.
        var nombresPendientes = new List<string>();
        if (conversacion.ClienteId is { } empresaDestinatariaId && trabajadorId is { } id)
        {
            var pendientes = await pendientesDeReclamacion.ListarParaClientesAsync(
                centroId, empresaDestinatariaId, new[] { id }, null, cancellationToken);
            nombresPendientes.AddRange(pendientes.SelectMany(p => p.Pendientes).Select(p => p.TipoDocumentoNombre));
        }
        if (empresaId != null)
        {
            var pendientes = await pendientesDeReclamacion.ListarParaEmpresasAsync(empresaId, null, cancellationToken);
            nombresPendientes.AddRange(pendientes.SelectMany(p => p.Pendientes).Select(p => p.TipoDocumentoNombre));
        }

        // Solo datos vinculados al hilo. Varias visitas o participantes no se eligen al azar.
        var valores = new Dictionary<string, string?>
        {
            ["Empresa"] = await empresas.Empresas.Where(e => e.Id == empresaId).Select(e => e.RazonSocial).FirstOrDefaultAsync(cancellationToken),
            ["Cliente empresarial"] = await empresas.Empresas.Where(e => e.Id == conversacion.ClienteId).Select(e => e.RazonSocial).FirstOrDefaultAsync(cancellationToken),
            ["Centro"] = await centros.Centros.Where(c => c.Id == centroId).Select(c => c.Nombre).FirstOrDefaultAsync(cancellationToken),
            ["Trabajador"] = await trabajadores.Trabajadores.Where(t => t.Id == trabajadorId).Select(t => t.Nombre + " " + t.Apellidos).FirstOrDefaultAsync(cancellationToken),
            ["fecha de la visita"] = visitasDelHilo.Count == 1 && visitasDelHilo[0].CentroId == centroId ? visitasDelHilo[0].FechaInicio.ToString("dd/MM/yyyy") : null,
            ["documentos pendientes"] = nombresPendientes.Count > 0 ? string.Join(", ", nombresPendientes.Distinct().Order()) : null
        };

        return Result.Exito(VariablesPlantillaRespuesta.Rellenar(macro.CuerpoHtml, valores));
    }
}
