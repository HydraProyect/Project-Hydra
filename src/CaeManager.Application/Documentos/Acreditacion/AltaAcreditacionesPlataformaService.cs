using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Lo que un Command acaba de dar de alta (agregado a su repositorio y todavía
/// sin guardar) y que puede hacer nacer una acreditación de plataforma. Cada
/// colección es opcional: el Command pasa solo lo que ha creado.
/// </summary>
public sealed record AltasConAcreditacion
{
    /// <summary>Documentos nuevos. Solo los de Trabajador y Empresa se acreditan ante una plataforma.</summary>
    public IReadOnlyCollection<Documento> Documentos { get; init; } = [];

    /// <summary>Asignaciones nuevas (Trabajador → Centro). Las cerradas no cuentan.</summary>
    public IReadOnlyCollection<Asignacion> Asignaciones { get; init; } = [];

    /// <summary>
    /// Trabajadores nuevos del mismo alta, solo para conocer su Empresa: la
    /// importación crea el Trabajador y su Asignación en el mismo guardado, así
    /// que la base todavía no sabe a qué Empresa pertenece.
    /// </summary>
    public IReadOnlyCollection<Trabajador> Trabajadores { get; init; } = [];

    /// <summary>Accesos nuevos de un Centro. Solo los de tipo Plataforma se acreditan.</summary>
    public IReadOnlyCollection<CanalGestionDocumental> Canales { get; init; } = [];

    /// <summary>
    /// Posiciones nuevas o editadas de un Centro sobre un TipoDocumento. Las que
    /// quedan en <c>Incluido</c> hacen nacer acreditaciones; todas prevalecen
    /// sobre la fila guardada al resolver qué exige el Centro.
    /// </summary>
    public IReadOnlyCollection<TipoDocumentoCentro> Requisitos { get; init; } = [];

    /// <summary>
    /// Posiciones de un Centro sobre un TipoDocumento que este alta elimina (EliminarDocumentacionRequeridaCentro): el
    /// Centro vuelve al valor general del tipo, así que, si ese valor es <see cref="RequisitoDocumental.Si"/>, sus
    /// Documentos de Empresa de ese tipo se evalúan como si el Centro hubiera pasado a exigirlo. Solo los de Empresa: los
    /// Documentos de Trabajador no se acreditan por esta vía, igual que antes de la regla A-1 (mismo criterio que
    /// <see cref="TiposQuePasanAExigirseDeSerie"/>). Prevalecen sobre la fila guardada, que el Command borra en el mismo
    /// guardado.
    /// </summary>
    public IReadOnlyCollection<TipoDocumentoCentro> RequisitosRetirados { get; init; } = [];

    /// <summary>
    /// Centros que este alta devuelve a <see cref="ModalidadGestionCae.ConGestionCae"/>
    /// (P1-X2). Prevalecen sobre la modalidad guardada, igual que
    /// <see cref="Requisitos"/> sobre la fila guardada: el Command los cambia y
    /// confirma sus acreditaciones en el mismo guardado, así que la base todavía
    /// los tiene como sin gestión CAE.
    /// </summary>
    public IReadOnlyCollection<Guid> CentrosQueVuelvenAGestionCae { get; init; } = [];

    /// <summary>
    /// TiposDocumento cuyo valor general <see cref="TipoDocumento.Requerido"/> acaba de pasar a
    /// <see cref="RequisitoDocumental.Si"/> en este alta. Cada Centro con acceso de plataforma que no tiene fila
    /// propia sobre el tipo pasa a exigirlo, así que sus Documentos de Empresa de ese tipo se evalúan como si ese Centro
    /// hubiera pasado a exigirlo (<see cref="Requisitos"/>). Solo los de Empresa: los Documentos de Trabajador no se
    /// acreditan por esta vía, igual que antes de la regla A-1 (decisión del propietario del producto del 2026-11-08,
    /// revisión Codex de la PR #2074, ronda 5); su serie por tipo de contratación tampoco entra en juego. Prevalece sobre
    /// el valor guardado: el Command lo cambia y confirma sus acreditaciones en el mismo guardado. La fila explícita del
    /// Centro, si existe, sigue mandando.
    /// </summary>
    public IReadOnlyCollection<Guid> TiposQuePasanAExigirseDeSerie { get; init; } = [];

    /// <summary>
    /// Centros que se concilian enteros: cada Documento operativo de quien tiene actividad en ellos, de cualquier
    /// tipo, ante cada uno de sus accesos de plataforma. No es un alta: lo usa la conciliación
    /// (<see cref="IConciliacionAcreditacionesEmpresaService"/>) para reparar lo que ya estaba guardado.
    /// </summary>
    public IReadOnlyCollection<Guid> CentrosAConciliar { get; init; } = [];

    /// <summary>
    /// Solo agrega acreditaciones de Documentos de Empresa: la conciliación de la regla A-1
    /// (<see cref="IConciliacionAcreditacionesEmpresaService"/>) no repara los de Trabajador, cuya regla no cambió.
    /// </summary>
    public bool SoloDocumentosDeEmpresa { get; init; }
}

/// <summary>
/// Regla única de alta de <see cref="AcreditacionDocumentoPlataforma"/> (P0-7).
/// Un Documento se acredita ante un acceso de plataforma
/// (<see cref="CanalGestionDocumental"/> de tipo
/// <see cref="TipoCanalGestion.Plataforma"/>) de un Centro exactamente cuando:
/// <list type="number">
/// <item>su propietario tiene actividad en ese Centro: un Trabajador, si tiene
/// una Asignación activa allí; una Empresa, si el Centro es suyo
/// (<see cref="Centro.EmpresaId"/>), tenga o no Trabajadores asignados, o si
/// algún Trabajador suyo está asignado allí. Un Centro gestionado por plataforma
/// exige la documentación de su Empresa siempre al día, no solo cuando se
/// programa una Visita (decisión del propietario del producto del 2026-11-08);
/// el acceso por correo nunca acredita;</item>
/// <item>ese Centro exige su tipo, con el mismo criterio que el resto del
/// producto (<see cref="ResolucionTipoDocumentoCentro.Aplica"/>: la fila de
/// <see cref="TipoDocumentoCentro"/> si existe, y si no
/// <see cref="TipoDocumento.CuentaParaCumplimiento"/>).</item>
/// </list>
/// Un Centro sin ningún acceso de tipo Plataforma no recibe acreditaciones.
/// Solo Trabajador y Empresa tienen accesos de plataforma que acreditar
/// (Cliente, Vehículo y Proyecto quedan fuera a propósito).
///
/// Todos los caminos de alta —Documento creado a mano, importado o generado
/// desde plantilla; Asignación individual, en lote, importada o reabierta; acceso de
/// plataforma nuevo; tipo que un Centro pasa a exigir, o que pasa a exigirse de
/// serie (<see cref="AltasConAcreditacion.TiposQuePasanAExigirseDeSerie"/>) o al retirar la posición del
/// Centro (<see cref="AltasConAcreditacion.RequisitosRetirados"/>); Centro que vuelve a gestión CAE (también
/// desde la importación maestra) o que se restaura; Documento de Empresa restaurado— llaman aquí. Cada
/// camino evalúa solo lo que su alta pone en juego: un Documento nuevo, ante
/// todos los Centros donde su propietario tiene actividad; una Asignación nueva, los Documentos
/// del Trabajador y de su Empresa ante ese Centro; un acceso nuevo, solo ese
/// acceso; un requisito nuevo, solo ese tipo en ese Centro; un tipo que pasa a
/// exigirse de serie, los Documentos de Empresa de ese tipo en cada Centro con acceso
/// de plataforma (nunca los de Trabajador). Nunca repara
/// acreditaciones ajenas al alta que la invoca: lo que ya existía antes de la regla, o lo
/// que una carrera entre dos altas simultáneas dejó sin acreditar, lo repara la
/// conciliación explícita (<see cref="IConciliacionAcreditacionesEmpresaService"/>).
///
/// Idempotente: no agrega una acreditación si el par (Documento, acceso) ya
/// existe (índice único <c>(TenantId, DocumentoId, CanalGestionDocumentalId)</c>).
/// Si otra operación simultánea guarda el mismo par entre esta lectura y el
/// guardado, el choque con ese índice se trata como «ya existe» al guardar
/// (<c>CaeManagerDbContext.SaveChangesAsync</c>), no como error del alta.
/// No guarda: agrega al repositorio para que el Command confirme el alta y sus
/// acreditaciones en el mismo <c>SaveChangesAsync</c>, o ninguna. Lee solo con
/// los contextos de consulta del Tenant en curso, bajo RLS: no cruza Tenants.
///
/// No da de baja acreditaciones cuando la obligación desaparece (Asignación
/// cerrada, acceso retirado, tipo que deja de exigirse): eso es otra regla de
/// producto, todavía sin decidir.
/// </summary>
public interface IAltaAcreditacionesPlataformaService
{
    /// <returns>Número de acreditaciones agregadas.</returns>
    Task<int> AgregarPendientesAsync(AltasConAcreditacion altas, CancellationToken cancellationToken = default);
}

public class AltaAcreditacionesPlataformaService(
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio)
    : IAltaAcreditacionesPlataformaService
{
    private sealed record DocumentoCandidato(Guid Id, Guid TipoDocumentoId, Guid? TrabajadorId, Guid? EmpresaId);

    public async Task<int> AgregarPendientesAsync(AltasConAcreditacion altas, CancellationToken cancellationToken = default)
    {
        var documentosNuevos = altas.Documentos
            .Where(d => d.TrabajadorId is not null || d.EmpresaId is not null)
            .Select(d => new DocumentoCandidato(d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId))
            .ToList();
        var asignacionesNuevas = altas.Asignaciones.Where(a => a.FechaBaja is null).ToList();
        var canalesNuevos = altas.Canales.Where(c => c.Tipo == TipoCanalGestion.Plataforma).ToList();
        var requisitosIncluidos = altas.Requisitos.Where(r => r.Incluido).ToList();
        var requisitosRetirados = altas.RequisitosRetirados.ToList();
        var tiposExigidosDeSerie = altas.TiposQuePasanAExigirseDeSerie.ToHashSet();
        var centrosAConciliar = altas.CentrosAConciliar.ToHashSet();

        if (documentosNuevos.Count == 0 && asignacionesNuevas.Count == 0 && canalesNuevos.Count == 0
            && requisitosIncluidos.Count == 0 && requisitosRetirados.Count == 0 && tiposExigidosDeSerie.Count == 0
            && centrosAConciliar.Count == 0)
            return 0;

        // Un tipo que pasa a exigirse de serie pone en juego cada Centro con acceso de plataforma (bajo RLS, del
        // Tenant en curso) para los Documentos de Empresa: los que no tienen fila propia pasan a exigirlo; la fila, si
        // existe, decide abajo.
        List<Guid> centrosConPlataforma = tiposExigidosDeSerie.Count == 0
            ? []
            : await centrosContext.CanalesGestionDocumental
                .Where(c => c.Tipo == TipoCanalGestion.Plataforma)
                .Select(c => c.CentroId)
                .Distinct()
                .ToListAsync(cancellationToken);

        // Centros donde el alta pone en juego a todos los propietarios con actividad.
        var centrosPorCompleto = canalesNuevos.Select(c => c.CentroId)
            .Concat(requisitosIncluidos.Select(r => r.CentroId))
            .Concat(requisitosRetirados.Select(r => r.CentroId))
            .Concat(centrosConPlataforma)
            .Concat(centrosAConciliar)
            .ToHashSet();

        // Empresa de cada Trabajador: la base, más los Trabajadores del propio alta.
        var empresasDeDocumentos = documentosNuevos.Where(d => d.EmpresaId is not null).Select(d => d.EmpresaId!.Value).ToHashSet();
        var trabajadoresDeInteres = documentosNuevos.Where(d => d.TrabajadorId is not null).Select(d => d.TrabajadorId!.Value)
            .Concat(asignacionesNuevas.Select(a => a.TrabajadorId))
            .ToHashSet();
        var empresaDeTrabajador = await CargarEmpresaDeTrabajadorAsync(
            altas.Trabajadores, trabajadoresDeInteres, empresasDeDocumentos, cancellationToken);

        // La Empresa de cada Centro (Centro.EmpresaId) tiene actividad en él aunque no tenga a nadie asignado: los
        // Centros de las Empresas de los Documentos nuevos, y la Empresa de cada Centro puesto en juego por completo.
        var empresaDeCentro = await CargarEmpresaDeCentroAsync(empresasDeDocumentos, centrosPorCompleto, cancellationToken);

        // Pares (Trabajador, Centro) activos: la base, más las Asignaciones del propio alta.
        var trabajadoresConsultados = empresaDeTrabajador.Keys.Concat(trabajadoresDeInteres).Distinct().ToArray();
        var centrosPorCompletoConsulta = centrosPorCompleto.ToArray();
        var paresActivos = (await asignacionesContext.Asignaciones
                .Where(a => a.FechaBaja == null
                    && (trabajadoresConsultados.Contains(a.TrabajadorId) || centrosPorCompletoConsulta.Contains(a.CentroId)))
                .Select(a => new { a.TrabajadorId, a.CentroId })
                .ToListAsync(cancellationToken))
            .Select(a => (a.TrabajadorId, a.CentroId))
            .Concat(asignacionesNuevas.Select(a => (a.TrabajadorId, a.CentroId)))
            .ToHashSet();

        // Los Trabajadores asignados a un Centro puesto en juego por completo también
        // necesitan su Empresa, para acreditar los Documentos de esa Empresa.
        var trabajadoresSinEmpresaConocida = paresActivos
            .Where(p => centrosPorCompleto.Contains(p.CentroId) && !empresaDeTrabajador.ContainsKey(p.TrabajadorId))
            .Select(p => p.TrabajadorId)
            .ToHashSet();
        if (trabajadoresSinEmpresaConocida.Count > 0)
            foreach (var (trabajadorId, empresaId) in await CargarEmpresaDeTrabajadorAsync(
                         altas.Trabajadores, trabajadoresSinEmpresaConocida, new HashSet<Guid>(), cancellationToken))
                empresaDeTrabajador[trabajadorId] = empresaId;

        // Candidatos (Documento, Centro, acceso concreto o null = cualquiera del Centro).
        var candidatos = new List<(DocumentoCandidato Documento, Guid CentroId, Guid? CanalId)>();

        foreach (var documento in documentosNuevos)
            foreach (var centroId in CentrosDelPropietario(documento, paresActivos, empresaDeTrabajador, empresaDeCentro))
                candidatos.Add((documento, centroId, null));

        var documentosExistentes = await CargarDocumentosDePropietariosAsync(
            asignacionesNuevas, centrosPorCompleto, paresActivos, empresaDeTrabajador, empresaDeCentro,
            altas.SoloDocumentosDeEmpresa, cancellationToken);
        var documentosDisponibles = documentosExistentes
            .Concat(documentosNuevos)
            .DistinctBy(d => d.Id)
            .ToList();

        foreach (var asignacion in asignacionesNuevas)
        {
            empresaDeTrabajador.TryGetValue(asignacion.TrabajadorId, out var empresaId);
            foreach (var documento in documentosDisponibles.Where(d =>
                         d.TrabajadorId == asignacion.TrabajadorId || (empresaId is not null && d.EmpresaId == empresaId)))
                candidatos.Add((documento, asignacion.CentroId, null));
        }

        foreach (var canal in canalesNuevos)
            foreach (var documento in DocumentosConActividadEn(canal.CentroId, documentosDisponibles, paresActivos, empresaDeTrabajador, empresaDeCentro))
                candidatos.Add((documento, canal.CentroId, canal.Id));

        foreach (var requisito in requisitosIncluidos)
            foreach (var documento in DocumentosConActividadEn(requisito.CentroId, documentosDisponibles, paresActivos, empresaDeTrabajador, empresaDeCentro)
                         .Where(d => d.TipoDocumentoId == requisito.TipoDocumentoId))
                candidatos.Add((documento, requisito.CentroId, null));

        // Solo Documentos de Empresa, por la misma razón que la serie de abajo: retirar la posición del Centro le aplica
        // el valor general, y por esa vía nunca se acreditaron los de Trabajador.
        foreach (var retirado in requisitosRetirados)
            foreach (var documento in DocumentosConActividadEn(retirado.CentroId, documentosDisponibles, paresActivos, empresaDeTrabajador, empresaDeCentro)
                         .Where(d => d.EmpresaId is not null && d.TipoDocumentoId == retirado.TipoDocumentoId))
                candidatos.Add((documento, retirado.CentroId, null));

        // Solo Documentos de Empresa: el valor general nunca propagó los de Trabajador, y la regla A-1 no lo cambia
        // (decisión del propietario del producto del 2026-11-08, revisión Codex de la PR #2074, ronda 5). Un Documento
        // de Trabajador cargado por otra vía de este mismo alta tampoco entra por aquí.
        foreach (var centroId in centrosConPlataforma)
            foreach (var documento in DocumentosConActividadEn(centroId, documentosDisponibles, paresActivos, empresaDeTrabajador, empresaDeCentro)
                         .Where(d => d.EmpresaId is not null && tiposExigidosDeSerie.Contains(d.TipoDocumentoId)))
                candidatos.Add((documento, centroId, null));

        foreach (var centroId in centrosAConciliar)
            foreach (var documento in DocumentosConActividadEn(centroId, documentosDisponibles, paresActivos, empresaDeTrabajador, empresaDeCentro))
                candidatos.Add((documento, centroId, null));

        // La conciliación de la regla A-1 solo repara Documentos de Empresa.
        if (altas.SoloDocumentosDeEmpresa)
            candidatos.RemoveAll(c => c.Documento.EmpresaId is null);

        if (candidatos.Count == 0)
            return 0;

        // P1-X2: un Centro sin gestión CAE no exige nada, así que no acredita
        // ante sus accesos de plataforma aunque los conserve (CentrosSinGestionCae).
        // Salvo que este mismo alta lo devuelva a gestión CAE.
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(
            centrosContext, candidatos.Select(c => c.CentroId), cancellationToken);
        if (sinGestionCae.Count > 0)
            candidatos.RemoveAll(c => sinGestionCae.Contains(c.CentroId)
                && !altas.CentrosQueVuelvenAGestionCae.Contains(c.CentroId));

        // ¿Exige el Centro el tipo? Misma regla que el cumplimiento del Centro.
        var centroIds = candidatos.Select(c => c.CentroId).Distinct().ToArray();
        var tipoIds = candidatos.Select(c => c.Documento.TipoDocumentoId).Distinct().ToArray();
        var obligatorioPorDefecto = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Requerido, t.TiposContratacionDeSerie })
            .ToDictionaryAsync(t => t.Id, t => (Obligatorio: t.Requerido == RequisitoDocumental.Si, t.TiposContratacionDeSerie), cancellationToken);
        // El valor general que este mismo alta cambia prevalece sobre el guardado.
        foreach (var tipoId in tiposExigidosDeSerie.Where(obligatorioPorDefecto.ContainsKey))
            obligatorioPorDefecto[tipoId] = (true, obligatorioPorDefecto[tipoId].TiposContratacionDeSerie);
        // La serie por tipo de contratación solo filtra los Documentos de un Trabajador: el de una Empresa no tiene.
        var tipoContratacionDeTrabajador = await CargarTipoContratacionDeTrabajadorAsync(
            altas.Trabajadores,
            candidatos.Where(c => c.Documento.TrabajadorId is not null).Select(c => c.Documento.TrabajadorId!.Value).ToHashSet(),
            cancellationToken);
        // Proyección, no entidades: una consulta con seguimiento devolvería la instancia que el contexto (uno por
        // circuito en Blazor Server) rastreó antes del candado, con el Incluido de entonces, y no la fila que otra alta
        // confirmó mientras esta esperaba; el par se perdería (revisión Codex de la PR #2074, ronda 4, hallazgo 1). Lo
        // que el propio alta cambia llega por Requisitos, no por el rastreo.
        var incluidoPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => centroIds.Contains(tc.CentroId) && tipoIds.Contains(tc.TipoDocumentoId))
                .Select(tc => new { tc.TipoDocumentoId, tc.CentroId, tc.Incluido })
                .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId), tc => tc.Incluido);
        // Una fila Incluido=true que el mismo alta elimina (EditarTipoDocumento) puede seguir aquí sin cambiar nada: solo
        // pone en juego Documentos de Empresa si el tipo pasa a exigirse de serie, y entonces el valor general también
        // es Sí; los de Trabajador, los únicos a los que la serie dejaría fuera, no entran por esa vía.
        foreach (var requisito in altas.Requisitos)
            incluidoPorPar[(requisito.TipoDocumentoId, requisito.CentroId)] = requisito.Incluido;
        // La fila que el mismo alta borra todavía está en la base: sin ella, el Centro sigue el valor general.
        foreach (var retirado in requisitosRetirados)
            incluidoPorPar.Remove((retirado.TipoDocumentoId, retirado.CentroId));

        // Accesos de plataforma de cada Centro: la base, más los del propio alta.
        var canalesPorCentro = (await centrosContext.CanalesGestionDocumental
                .Where(c => centroIds.Contains(c.CentroId) && c.Tipo == TipoCanalGestion.Plataforma)
                .Select(c => new { c.Id, c.CentroId })
                .ToListAsync(cancellationToken))
            .Select(c => (c.Id, c.CentroId))
            .Concat(canalesNuevos.Select(c => (c.Id, c.CentroId)))
            .Distinct()
            .ToLookup(c => c.CentroId, c => c.Id);

        var documentoIds = candidatos.Select(c => c.Documento.Id).Distinct().ToArray();
        var yaAcreditados = (await documentosContext.AcreditacionesDocumentoPlataforma
                .Where(a => documentoIds.Contains(a.DocumentoId))
                .Select(a => new { a.DocumentoId, a.CanalGestionDocumentalId })
                .ToListAsync(cancellationToken))
            .Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .ToHashSet();

        var agregadas = 0;
        foreach (var (documento, centroId, canalId) in candidatos)
        {
            var (esObligatorio, serie) = obligatorioPorDefecto.GetValueOrDefault(documento.TipoDocumentoId);
            var aplica = documento.TrabajadorId is { } trabajadorId
                         && tipoContratacionDeTrabajador.TryGetValue(trabajadorId, out var tipoContratacion)
                ? ResolucionTipoDocumentoCentro.Aplica(
                    incluidoPorPar, documento.TipoDocumentoId, centroId, esObligatorio, serie, tipoContratacion)
                : ResolucionTipoDocumentoCentro.Aplica(incluidoPorPar, documento.TipoDocumentoId, centroId, esObligatorio);
            if (!aplica)
                continue;

            foreach (var canal in canalesPorCentro[centroId])
            {
                if (canalId is not null && canal != canalId) continue;
                if (!yaAcreditados.Add((documento.Id, canal))) continue;

                acreditacionRepositorio.Agregar(new AcreditacionDocumentoPlataforma(documento.Id, canal));
                agregadas++;
            }
        }

        return agregadas;
    }

    private async Task<Dictionary<Guid, TipoContratacion>> CargarTipoContratacionDeTrabajadorAsync(
        IReadOnlyCollection<Trabajador> trabajadoresNuevos, IReadOnlySet<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, TipoContratacion>();
        var trabajadorIdsConsulta = trabajadorIds.ToArray();
        if (trabajadorIdsConsulta.Length > 0)
            foreach (var t in await trabajadoresContext.Trabajadores
                         .Where(t => trabajadorIdsConsulta.Contains(t.Id))
                         .Select(t => new { t.Id, t.TipoContratacion })
                         .ToListAsync(cancellationToken))
                resultado[t.Id] = t.TipoContratacion;

        foreach (var t in trabajadoresNuevos.Where(t => trabajadorIds.Contains(t.Id)))
            resultado[t.Id] = t.TipoContratacion;

        return resultado;
    }

    /// <summary>
    /// Empresa empleadora de cada Trabajador: <c>EmpresaId ?? SubcontrataId</c>, la misma que usan el cumplimiento del
    /// Centro (<c>RequisitosObligatoriosIncumplidos</c>) y los pendientes en plataforma (<c>PendientesEnPlataformaDeCentros</c>).
    /// Un Trabajador creado con <see cref="Trabajador.DeSubcontrata"/> tiene <c>EmpresaId</c> nulo y su Empresa en
    /// <c>SubcontrataId</c> (deuda terminológica: la FK apunta a Empresas, ver <c>TrabajadorConfiguration</c>); sin ella,
    /// los Documentos de esa Empresa no llegaban por la vía de la Asignación (revisión Codex de la PR #2074, ronda 3,
    /// hallazgo 1).
    /// </summary>
    private async Task<Dictionary<Guid, Guid?>> CargarEmpresaDeTrabajadorAsync(
        IReadOnlyCollection<Trabajador> trabajadoresNuevos, IReadOnlySet<Guid> trabajadorIds, IReadOnlySet<Guid> empresaIds,
        CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, Guid?>();
        var trabajadorIdsConsulta = trabajadorIds.ToArray();
        var empresaIdsConsulta = empresaIds.ToArray();
        if (trabajadorIds.Count > 0 || empresaIds.Count > 0)
            foreach (var t in await trabajadoresContext.Trabajadores
                         .Where(t => trabajadorIdsConsulta.Contains(t.Id)
                             || (t.EmpresaId != null && empresaIdsConsulta.Contains(t.EmpresaId.Value))
                             || (t.SubcontrataId != null && empresaIdsConsulta.Contains(t.SubcontrataId.Value)))
                         .Select(t => new { t.Id, EmpresaEmpleadoraId = t.EmpresaId ?? t.SubcontrataId })
                         .ToListAsync(cancellationToken))
                resultado[t.Id] = t.EmpresaEmpleadoraId;

        foreach (var t in trabajadoresNuevos)
        {
            var empresaEmpleadoraId = t.EmpresaId ?? t.SubcontrataId;
            if (trabajadorIds.Contains(t.Id) || (empresaEmpleadoraId is not null && empresaIds.Contains(empresaEmpleadoraId.Value)))
                resultado[t.Id] = empresaEmpleadoraId;
        }

        return resultado;
    }

    /// <summary>
    /// Empresa de cada Centro (<see cref="Centro.EmpresaId"/>) que importa a este alta: los Centros con algún acceso de
    /// plataforma de las Empresas de los Documentos nuevos (uno gestionado solo por correo nunca acredita), y los Centros
    /// puestos en juego por completo, cuyo acceso puede ser el nuevo de este mismo alta. Bajo RLS, solo Centros del
    /// Tenant en curso.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> CargarEmpresaDeCentroAsync(
        IReadOnlySet<Guid> empresaIds, IReadOnlySet<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (empresaIds.Count == 0 && centroIds.Count == 0) return [];

        var empresaIdsConsulta = empresaIds.ToArray();
        var centroIdsConsulta = centroIds.ToArray();
        return await centrosContext.Centros
            .Where(c => centroIdsConsulta.Contains(c.Id)
                || (empresaIdsConsulta.Contains(c.EmpresaId)
                    && centrosContext.CanalesGestionDocumental.Any(k => k.CentroId == c.Id && k.Tipo == TipoCanalGestion.Plataforma)))
            .Select(c => new { c.Id, c.EmpresaId })
            .ToDictionaryAsync(c => c.Id, c => c.EmpresaId, cancellationToken);
    }

    /// <summary>Documentos ya guardados de los propietarios que las Asignaciones nuevas y los Centros puestos en juego por completo afectan.</summary>
    private async Task<List<DocumentoCandidato>> CargarDocumentosDePropietariosAsync(
        IReadOnlyCollection<Asignacion> asignacionesNuevas, IReadOnlySet<Guid> centrosPorCompleto,
        IReadOnlySet<(Guid TrabajadorId, Guid CentroId)> paresActivos, IReadOnlyDictionary<Guid, Guid?> empresaDeTrabajador,
        IReadOnlyDictionary<Guid, Guid> empresaDeCentro, bool soloDeEmpresa, CancellationToken cancellationToken)
    {
        var trabajadoresConActividad = asignacionesNuevas.Select(a => a.TrabajadorId)
            .Concat(paresActivos.Where(p => centrosPorCompleto.Contains(p.CentroId)).Select(p => p.TrabajadorId))
            .Distinct().ToArray();
        // Sin Documentos de Trabajador que acreditar, sus Trabajadores solo cuentan para llegar a su Empresa.
        Guid[] trabajadorIds = soloDeEmpresa ? [] : trabajadoresConActividad;

        // La Empresa de un Centro puesto en juego por completo cuenta aunque nadie esté asignado allí.
        var empresaIds = trabajadoresConActividad
            .Select(id => empresaDeTrabajador.GetValueOrDefault(id))
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .Concat(centrosPorCompleto.Where(empresaDeCentro.ContainsKey).Select(c => empresaDeCentro[c]))
            .Distinct()
            .ToArray();
        if (trabajadorIds.Length == 0 && empresaIds.Length == 0) return [];

        return (await documentosContext.Documentos.Operativos()
                .Where(d => (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))
                    || (d.EmpresaId != null && empresaIds.Contains(d.EmpresaId.Value)))
                .Select(d => new { d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId })
                .ToListAsync(cancellationToken))
            .Select(d => new DocumentoCandidato(d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId))
            .ToList();
    }

    /// <summary>
    /// Centros donde el propietario del Documento tiene actividad: los de sus Asignaciones activas (Trabajador), o los
    /// suyos (<see cref="Centro.EmpresaId"/>) más los de las Asignaciones activas de sus Trabajadores (Empresa).
    /// </summary>
    private static IEnumerable<Guid> CentrosDelPropietario(
        DocumentoCandidato documento, IReadOnlySet<(Guid TrabajadorId, Guid CentroId)> paresActivos,
        IReadOnlyDictionary<Guid, Guid?> empresaDeTrabajador, IReadOnlyDictionary<Guid, Guid> empresaDeCentro)
    {
        if (documento.TrabajadorId is not null)
            return paresActivos.Where(p => p.TrabajadorId == documento.TrabajadorId).Select(p => p.CentroId).Distinct();

        return paresActivos
            .Where(p => empresaDeTrabajador.GetValueOrDefault(p.TrabajadorId) == documento.EmpresaId)
            .Select(p => p.CentroId)
            .Concat(empresaDeCentro.Where(c => c.Value == documento.EmpresaId).Select(c => c.Key))
            .Distinct();
    }

    private static IEnumerable<DocumentoCandidato> DocumentosConActividadEn(
        Guid centroId, IReadOnlyCollection<DocumentoCandidato> documentos,
        IReadOnlySet<(Guid TrabajadorId, Guid CentroId)> paresActivos, IReadOnlyDictionary<Guid, Guid?> empresaDeTrabajador,
        IReadOnlyDictionary<Guid, Guid> empresaDeCentro)
    {
        var trabajadores = paresActivos.Where(p => p.CentroId == centroId).Select(p => p.TrabajadorId).ToHashSet();
        var empresas = trabajadores
            .Select(t => empresaDeTrabajador.GetValueOrDefault(t))
            .Where(e => e is not null)
            .ToHashSet();
        // La Empresa del Centro, tenga o no a alguien asignado allí.
        if (empresaDeCentro.TryGetValue(centroId, out var empresaDelCentro))
            empresas.Add(empresaDelCentro);

        return documentos.Where(d =>
            (d.TrabajadorId is not null && trabajadores.Contains(d.TrabajadorId.Value))
            || (d.EmpresaId is not null && empresas.Contains(d.EmpresaId)));
    }
}
