using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// A dónde va un lote de documentos de Trabajador que se reclama a la Empresa que los emplea: a la Delegación del
/// Trabajador (<paramref name="DelegacionEmpresaId"/> informado) o, si no tiene Delegación o su Delegación no tiene
/// correo de reclamación, a la agenda de la Empresa (<paramref name="DelegacionEmpresaId"/> nulo).
/// </summary>
public sealed record DestinoEmpresaEmpleadoraDto(Guid? DelegacionEmpresaId, string? DelegacionEmpresaNombre);

/// <summary>Los lotes que se reclaman a la Empresa empleadora y los que siguen yendo al Cliente (Trabajadores de Subcontrata).</summary>
public sealed record ReagrupacionPorEmpresaEmpleadora(
    IReadOnlyList<LoteReclamacionAgrupadoDto> LotesEmpresaEmpleadora,
    IReadOnlyList<LoteReclamacionClienteDto> LotesCliente);

/// <summary>A quién se envía un lote de la Empresa empleadora, ya resuelto.</summary>
public sealed record DestinoResuelto(IReadOnlyList<DestinatarioAgendaDto> Destinatarios, DestinoEmpresaEmpleadoraDto Destino);

/// <summary>
/// «Gestionar» reclama los documentos de un Trabajador a la Empresa que lo emplea, nunca al Trabajador (decisión de Chris,
/// 2026-10-18), y dentro de ella al contacto de reclamación de su Delegación (<see cref="DelegacionEmpresa"/>). Sin
/// Delegación, o con una Delegación sin correo, va a quien la Empresa recibe hoy: los contactos de su agenda
/// (<see cref="IResolucionDestinatariosAgendaService.ResolverParaEmpresaAsync"/>). Lo de varios Trabajadores de una misma
/// Delegación sale en un único correo, y lo de todos los que caen en la agenda de la Empresa, en otro.
///
/// <para>
/// <b>Qué se ofrece.</b> No decide qué es reclamable: parte de los lotes de <c>ObtenerLoteReclamacionQuery</c> (ventana,
/// Asignación activa, Centro con gestión CAE, alcance visible y lo pendiente sin fecha) y solo cambia a quién se dirigen.
/// Un Trabajador de Subcontrata no tiene Empresa empleadora en el modelo, así que lo suyo sigue yendo al Cliente.
/// </para>
///
/// <para>
/// <b>Alcance.</b> Reclamar a la Empresa empleadora es operar sobre ella: solo entran las Empresas del alcance de gestión
/// (Asignación de Cartera). Lo de un Trabajador cuya Empresa queda fuera no se ofrece a nadie, ni a la Empresa ni al
/// Cliente: el fallo cerrado. El contacto de la Delegación es un dato personal de un tercero y solo se enseña con el mismo
/// criterio que <c>ObtenerCatalogoTrabajadoresEmpresaQuery</c>: quien lee la «Nota interna» y gestiona la Empresa. A
/// quien no lo cumple el lote le llega sin destinatario y el envío se rechaza.
/// </para>
/// </summary>
public class ReclamacionEmpresaEmpleadoraService(
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    ICatalogoTrabajadoresEmpresaRepository catalogo,
    IAlcanceDatosService alcanceDatos,
    IPoliticaNotaInterna politicaNotaInterna,
    IResolucionDestinatariosAgendaService resolucionDestinatarios,
    DestinoDeReclamacionesEnviadasService reclamacionesEnviadas)
{
    private sealed record TrabajadorDeEmpresa(Guid Id, Guid EmpresaId, Guid? DelegacionEmpresaId);

    /// <summary>El motivo con el que se enseña el contacto de la Delegación junto a los de la agenda.</summary>
    public static string MotivoDelegacionEmpresa(string nombreDelegacionEmpresa) => $"Contacto de reclamación de la Delegación {nombreDelegacionEmpresa}";

    public async Task<ReagrupacionPorEmpresaEmpleadora> ReagruparAsync(
        IReadOnlyList<LoteReclamacionClienteDto> lotesCliente, CancellationToken cancellationToken)
    {
        var trabajadorIds = lotesCliente
            .SelectMany(l => l.Documentos.Select(d => d.TrabajadorId).Concat((l.PendientesSinFecha ?? []).Select(p => p.TrabajadorId)))
            .OfType<Guid>().Distinct().ToList();
        if (trabajadorIds.Count == 0)
            return new([], lotesCliente);

        var deEmpresa = (await trabajadoresContext.Trabajadores
                .Where(t => trabajadorIds.Contains(t.Id) && t.EmpresaId != null)
                .Select(t => new { t.Id, EmpresaId = t.EmpresaId!.Value, t.DelegacionEmpresaId })
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.Id, t => new TrabajadorDeEmpresa(t.Id, t.EmpresaId, t.DelegacionEmpresaId));

        bool EsDeEmpresa(Guid? trabajadorId) => trabajadorId is { } id && deEmpresa.ContainsKey(id);

        // Lo que no es de un Trabajador de Empresa sigue yendo al Cliente, como hasta ahora.
        var restoCliente = lotesCliente
            .Select(l => l with
            {
                Documentos = [.. l.Documentos.Where(d => !EsDeEmpresa(d.TrabajadorId))],
                PendientesSinFecha = [.. (l.PendientesSinFecha ?? []).Where(p => !EsDeEmpresa(p.TrabajadorId))],
            })
            .Where(l => l.Documentos.Count > 0 || l.PendientesSinFecha!.Count > 0)
            .ToList();

        if (deEmpresa.Count == 0)
            return new([], restoCliente);

        var empresaIdsGestion = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var empresaIds = deEmpresa.Values.Select(t => t.EmpresaId).Distinct()
            .Where(id => empresaIdsGestion is null || empresaIdsGestion.Contains(id))
            .ToList();
        var razonesSociales = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var delegaciones = await DelegacionesEmpresaConCorreoAsync(razonesSociales.Keys, cancellationToken);
        var conContacto = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);

        (Guid EmpresaId, Guid? DelegacionEmpresaId)? Destino(Guid? trabajadorId)
        {
            if (trabajadorId is not { } id || !deEmpresa.TryGetValue(id, out var trabajador) || !razonesSociales.ContainsKey(trabajador.EmpresaId))
                return null;
            return (trabajador.EmpresaId,
                trabajador.DelegacionEmpresaId is { } delegacionId && delegaciones.ContainsKey(delegacionId) ? delegacionId : null);
        }

        var documentos = lotesCliente.SelectMany(l => l.Documentos).DistinctBy(d => d.DocumentoId)
            .Select(d => (Destino: Destino(d.TrabajadorId), Documento: d))
            .Where(x => x.Destino is not null)
            .ToList();
        var pendientes = lotesCliente.SelectMany(l => l.PendientesSinFecha ?? []).DistinctBy(p => p.Clave)
            .Select(p => (Destino: Destino(p.TrabajadorId), Pendiente: p))
            .Where(x => x.Destino is not null)
            .ToList();

        var claves = documentos.Select(x => x.Destino!.Value).Concat(pendientes.Select(x => x.Destino!.Value)).Distinct().ToList();

        // La agenda de la Empresa se resuelve de una vez para todos los lotes que caen en ella, con los Tipos de cada lote.
        var tiposPorEmpresaSinDelegacionEmpresa = claves
            .Where(c => c.DelegacionEmpresaId is null)
            .ToDictionary(
                c => c.EmpresaId,
                IReadOnlyList<Guid> (c) => documentos.Where(x => x.Destino == c).Select(x => x.Documento.TipoDocumentoId)
                    .Concat(pendientes.Where(x => x.Destino == c).Select(x => x.Pendiente.TipoDocumentoId))
                    .Distinct().ToList());
        var agendaPorEmpresa = await resolucionDestinatarios.ResolverParaEmpresasAsync(tiposPorEmpresaSinDelegacionEmpresa, cancellationToken);

        // La última reclamación de cada lote es la de su mismo destino (Empresa y Delegación) y de documentos de Trabajador:
        // una de documentos de la propia Empresa, o la de otra Delegación, es otro asunto con otros destinatarios.
        var ultimas = await reclamacionesEnviadas.UltimasPorDestinoAsync([.. claves.Select(c => c.EmpresaId)], cancellationToken);

        return new(
            [.. claves
                .Select(c =>
                {
                    var delegacion = c.DelegacionEmpresaId is { } delegacionId ? delegaciones[delegacionId] : null;
                    IReadOnlyList<DestinatarioAgendaDto> destinatarios = delegacion is null
                        ? agendaPorEmpresa.GetValueOrDefault(c.EmpresaId, [])
                        : conContacto ? [DestinatarioDe(delegacion)] : [];
                    var ultima = ultimas.GetValueOrDefault((c.EmpresaId, c.DelegacionEmpresaId));

                    return new LoteReclamacionAgrupadoDto(
                        c.EmpresaId, razonesSociales[c.EmpresaId], AmbitoAplicacion.Trabajador,
                        ultima?.FechaEnvioUtc,
                        [.. documentos.Where(x => x.Destino == c).Select(x => x.Documento).OrderBy(d => d.FechaVencimiento)],
                        ultima?.ConversacionId,
                        destinatarios,
                        [.. pendientes.Where(x => x.Destino == c).Select(x => x.Pendiente)],
                        new DestinoEmpresaEmpleadoraDto(delegacion?.Id, delegacion?.Nombre));
                })
                .OrderByDescending(l => l.Documentos.Any(d => d.Estado == EstadoDocumento.Vencido))
                .ThenBy(l => l.TitularNombre)
                .ThenBy(l => l.EmpresaEmpleadora!.DelegacionEmpresaNombre is null)
                .ThenBy(l => l.EmpresaEmpleadora!.DelegacionEmpresaNombre)],
            restoCliente);
    }

    /// <summary>
    /// Los Trabajadores cuyo lote va a <paramref name="delegacionEmpresaId"/>: los de esa Delegación; con <c>null</c>, los de la
    /// Empresa sin Delegación o con una Delegación sin correo. Lo usa el envío para revalidar que cada línea pedida es de ese lote.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> TrabajadoresDelDestinoAsync(
        Guid empresaId, Guid? delegacionEmpresaId, CancellationToken cancellationToken)
    {
        var conCorreo = (await DelegacionesEmpresaConCorreoAsync([empresaId], cancellationToken)).Keys.ToList();

        return await trabajadoresContext.Trabajadores
            .Where(t => t.EmpresaId == empresaId)
            .Where(t => delegacionEmpresaId != null
                ? t.DelegacionEmpresaId == delegacionEmpresaId
                : t.DelegacionEmpresaId == null || !conCorreo.Contains(t.DelegacionEmpresaId.Value))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// A quién se envía el lote, resuelto contra la base y no contra lo que trajo la pantalla. Una Delegación que ya no es de
    /// esta Empresa o que perdió el correo hace fallar el envío (todo o nada, como un documento renovado), y su contacto solo
    /// se usa si quien envía puede verlo.
    /// </summary>
    public async Task<Result<DestinoResuelto>> ResolverDestinoAsync(
        Guid empresaId, Guid? delegacionEmpresaId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken)
    {
        if (delegacionEmpresaId is null)
        {
            var agenda = await resolucionDestinatarios.ResolverParaEmpresaAsync(empresaId, tipoDocumentoIds, cancellationToken);
            return Result.Exito(new DestinoResuelto(agenda, new DestinoEmpresaEmpleadoraDto(null, null)));
        }

        var delegacion = await catalogo.ObtenerDelegacionEmpresaAsync(delegacionEmpresaId.Value, cancellationToken);
        if (delegacion is null || delegacion.EmpresaId != empresaId || !TieneCorreo(delegacion))
        {
            return Result.Fallo<DestinoResuelto>(Error.Crear(
                "Reclamacion.DelegacionEmpresaDesactualizada",
                "La delegación de estos trabajadores ya no tiene un contacto de reclamación con correo. Actualiza la vista antes de volver a intentarlo."));
        }

        if (!await politicaNotaInterna.PuedeLeerAsync(cancellationToken))
            return Result.Fallo<DestinoResuelto>(Error.Crear("Reclamacion.SinAcceso", "No tienes acceso a esta empresa."));

        return Result.Exito(new DestinoResuelto([DestinatarioDe(delegacion)], new DestinoEmpresaEmpleadoraDto(delegacion.Id, delegacion.Nombre)));
    }

    private async Task<Dictionary<Guid, DelegacionEmpresa>> DelegacionesEmpresaConCorreoAsync(
        IEnumerable<Guid> empresaIds, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, DelegacionEmpresa>();
        foreach (var empresaId in empresaIds)
        {
            foreach (var delegacion in await catalogo.ListarDelegacionesEmpresaAsync(empresaId, cancellationToken))
            {
                if (TieneCorreo(delegacion))
                    resultado[delegacion.Id] = delegacion;
            }
        }

        return resultado;
    }

    private static bool TieneCorreo(DelegacionEmpresa delegacion) =>
        DestinatariosExtraDeReclamacion.EsCorreoUtilizable(delegacion.ContactoReclamacionCorreo ?? string.Empty);

    /// <summary>
    /// El contacto de la Delegación como destinatario. Su Id es el de la Delegación: no es un contacto de la agenda, pero
    /// la pantalla lo marca y el envío lo revalida igual que a uno de ella.
    /// </summary>
    private static DestinatarioAgendaDto DestinatarioDe(DelegacionEmpresa delegacion) =>
        new(delegacion.Id, delegacion.ContactoReclamacionNombre ?? delegacion.Nombre, delegacion.ContactoReclamacionCorreo!,
            [MotivoDelegacionEmpresa(delegacion.Nombre)]);
}
