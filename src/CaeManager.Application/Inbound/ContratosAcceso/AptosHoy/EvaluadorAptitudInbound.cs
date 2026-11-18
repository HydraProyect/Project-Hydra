using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.Requisitos;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Domain.Inbound.Requisitos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;

/// <summary>
/// La aptitud de un Trabajador en el Centro, antes de proyectarla por rol. <paramref name="Dto"/> lleva todos los campos (la
/// proyección completa, sin la cita del contrato del «No apto» ajeno) y <see cref="AptoHoyDto.EmpresasContratistaIds"/> rellenos.
/// </summary>
/// <param name="ContratoOrigenAjenoId">El contrato cuyo «No apto» ajeno provoca el estado de la fila; nulo si procede de lo propio.</param>
/// <param name="ContratoMasReciente">El contrato más reciente que cuenta y donde sale el Trabajador (cita de la proyección restringida).</param>
/// <param name="IdentidadMasReciente">La evidencia más reciente de ese contrato, de donde salen nombre y documento.</param>
internal sealed record AptitudTrabajadorEvaluada(
    AptoHoyDto Dto, Guid? ContratoOrigenAjenoId, ContratoAcceso ContratoMasReciente, EvidenciaInbound IdentidadMasReciente);

/// <summary>El resultado de evaluar el Centro: un Trabajador por entrada y lo necesario para citar su contrato.</summary>
internal sealed record EvaluacionAptitudCentro(
    IReadOnlyList<AptitudTrabajadorEvaluada> Trabajadores,
    IReadOnlyDictionary<Guid, ContratoAcceso> ContratosPorId,
    IReadOnlyDictionary<(Guid Trabajador, Guid Contrato), EvidenciaInbound> Identidades)
{
    public static EvaluacionAptitudCentro Vacia { get; } = new(
        [], new Dictionary<Guid, ContratoAcceso>(), new Dictionary<(Guid Trabajador, Guid Contrato), EvidenciaInbound>());
}

/// <summary>
/// La evaluación de aptitud de los Trabajadores de un Centro hoy, extraída de <see cref="ObtenerAptosHoyDelCentroQueryHandler"/>
/// (refactor sin cambio de reglas ni de DTO) para que la comprobación de credencial use exactamente el mismo cálculo. La regla, los
/// huecos declarados y la proyección por rol están documentados en el handler; aquí no se decide nada nuevo.
///
/// <para>
/// <b>No autoriza la capacidad.</b> Quien lo llama exige antes <see cref="CapacidadInbound.LeerInbound"/> y decide la
/// proyección (la completa pide <see cref="CapacidadInbound.ValidarInbound"/>): este servicio devuelve datos completos,
/// con requisito, causa y fecha, que no deben salir tal cual a un rol sin esa capacidad. Sí aplica el alcance del actor (lado de
/// gestión, Centro visible y contratos que su Asignación de Cartera deja ver) y da vacío sin decir cuál falla.
/// Es <c>internal</c> y no se registra en DI a propósito: ningún componente de la capa web debe poder inyectarlo y saltarse la puerta de un handler.
/// Quien lo use desde otra feature lee plantillas, tipos, contratos y evidencias de ContratosAcceso: debe declarar esa dependencia entre
/// features como lo hace el ratchet de fronteras para los handlers.
/// </para>
/// </summary>
internal sealed class EvaluadorAptitudInbound(
    IContratosAccesoQueryContext contratosContext,
    IEvidenciasInboundQueryContext evidenciasContext,
    IPlantillasRequisitosQueryContext plantillasContext,
    ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos)
{
    /// <param name="centroId">El Centro.</param>
    /// <param name="trabajadorId">
    /// Si se indica, solo se evalúa a ese Trabajador (el de <c>TrabajadorOrigenId</c> de sus evidencias); su resultado es el mismo
    /// que con toda la lista. Si no tiene evidencia en un contrato que cuenta del Centro, no sale (hueco 1 del handler).
    /// </param>
    public async Task<EvaluacionAptitudCentro> EvaluarAsync(Guid centroId, Guid? trabajadorId, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return EvaluacionAptitudCentro.Vacia;
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centrosVisibles is not null && !centrosVisibles.Contains(centroId))
            return EvaluacionAptitudCentro.Vacia;

        var hoy = DiaDeNegocio.Hoy();
        var candidatos = await contratosContext.ContratosAcceso
            .Where(c => c.CentroId == centroId && c.Estado == EstadoContratoAcceso.Activo && c.TenantContratistaId != null)
            .Where(ContratoAcceso.EstaVigenteEn(hoy))
            .OrderByDescending(c => c.FechaInicio).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var contratos = new List<ContratoAcceso>();
        foreach (var contrato in candidatos)
        {
            if (await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
                contratos.Add(contrato);
        }

        if (contratos.Count == 0)
            return EvaluacionAptitudCentro.Vacia;

        // Con filtro, solo las evidencias de ese Trabajador y las de la Empresa contratista (sin Trabajador): las de otros
        // Trabajadores no entran en su evaluación, así que el resultado es el de la lista completa.
        var contratoIds = contratos.Select(c => c.Id).ToList();
        var evidencias = await evidenciasContext.EvidenciasInbound
            .Where(e => contratoIds.Contains(e.ContratoAccesoId))
            .Where(e => trabajadorId == null || e.TrabajadorOrigenId == null || e.TrabajadorOrigenId == trabajadorId)
            .ToListAsync(cancellationToken);

        // Decisión del 2026-11-17 (P3, revisión del revisor-puente, punto 2): un «No apto» se lee también de los contratos del
        // Centro que no cuentan (Suspendido, Finalizado, Cancelado, no vigente hoy...), con el mismo contexto, el mismo Tenant y
        // la misma RLS, y solo de los que el actor ve. De ellos no sale ninguna fila ni ninguna acreditación positiva.
        var candidatoIds = candidatos.Select(c => c.Id).ToList();
        var noCuentan = await contratosContext.ContratosAcceso
            .Where(c => c.CentroId == centroId && !candidatoIds.Contains(c.Id))
            .ToListAsync(cancellationToken);
        var otrosIds = new List<Guid>();
        foreach (var contrato in noCuentan)
        {
            if (await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
                otrosIds.Add(contrato.Id);
        }

        var evidenciasDeOtros = otrosIds.Count == 0
            ? []
            : await evidenciasContext.EvidenciasInbound
                .Where(e => otrosIds.Contains(e.ContratoAccesoId) && e.SoloConclusionAptitud && e.TrabajadorOrigenId != null
                    && (trabajadorId == null || e.TrabajadorOrigenId == trabajadorId))
                .ToListAsync(cancellationToken);
        // Los «No apto» Aceptados de todos los contratos visibles del Centro, cuenten o no, se suman como ajenos a cada contrato
        // (decisión del 2026-11-17, P3, ronda 7): sirven para el bloqueo y para el corte «solo acredita un apto posterior». Sin
        // los de los contratos que cuentan, un apto anterior a un «No apto» revive en otro contrato. La cita de la fila no se
        // decide aquí sino al escoger el contrato (desempate y origen ajeno, más abajo).
        var noAptosDelCentro = evidencias.Concat(evidenciasDeOtros)
            .Where(e => e.TrabajadorOrigenId is not null && EsNoApto(e) && EvidenciasInboundProyeccion.EstaAceptada(e))
            .ToLookup(e => e.TrabajadorOrigenId!.Value);

        var requisitos = await RequisitosQueBloqueanAsync(centroId, contratos, cancellationToken);
        var tipoIds = requisitos.Select(r => r.TipoDocumentoId).Distinct().ToList();
        var tipos = tipoIds.Count == 0
            ? new Dictionary<Guid, TipoDocumento>()
            : await tiposContext.TiposDocumento.Where(t => tipoIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);

        var exigidos = requisitos
            .Select(r =>
            {
                tipos.TryGetValue(r.TipoDocumentoId, out var tipo);
                // Un tipo que no se encuentra se trata como si revelara salud: falla cerrado.
                return new Exigido(
                    r, tipo?.Nombre ?? "Requisito sin nombre", tipo is null ? 0 : r.ToleranciaEfectiva(tipo), tipo?.RevelaSalud ?? true);
            })
            .OrderBy(x => x.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Requisito.Id)
            .ToList();

        var filas = new Dictionary<Guid, FilaInterna>();
        var contratosPorId = contratos.ToDictionary(c => c.Id);
        var identidades = new Dictionary<(Guid Trabajador, Guid Contrato), EvidenciaInbound>();
        var empresasPorTrabajador = new Dictionary<Guid, List<Guid>>();
        // Los contratos van del más reciente al más antiguo: el primero en el que sale un Trabajador es el más reciente que cuenta.
        // Junto con la identidad (nombre y documento) que trae su evidencia más reciente: la proyección restringida cita ese contrato.
        var masRecientePorTrabajador = new Dictionary<Guid, (ContratoAcceso Contrato, EvidenciaInbound Identidad)>();
        foreach (var contrato in contratos)
        {
            var delContrato = evidencias.Where(e => e.ContratoAccesoId == contrato.Id).ToList();
            var evaluadasEmpresa = Evaluar(
                exigidos.Where(x => x.Requisito.Entidad == EntidadRequisito.Empresa).ToList(),
                delContrato.Where(e => e.TrabajadorOrigenId is null).ToList(), hoy, SujetoDelRequisitoInbound.EmpresaContratista);
            var exigidosTrabajador = exigidos.Where(x => x.Requisito.Entidad == EntidadRequisito.Trabajador).ToList();

            foreach (var porTrabajador in delContrato.Where(e => e.TrabajadorOrigenId is not null).GroupBy(e => e.TrabajadorOrigenId!.Value))
            {
                var propias = porTrabajador.ToList();
                // Los «No apto» Aceptados de los demás contratos visibles del Centro (cuenten o no) se suman a lo propio; nunca una
                // acreditación positiva. Los del propio contrato ya están en `propias`.
                var ajenas = noAptosDelCentro[porTrabajador.Key].Where(e => e.ContratoAccesoId != contrato.Id).ToList();
                var evaluadas = evaluadasEmpresa.Concat(
                    Evaluar(exigidosTrabajador, propias.Concat(ajenas).ToList(), hoy, SujetoDelRequisitoInbound.Trabajador,
                        ajenas.Select(e => e.Id).ToHashSet())).ToList();
                var identidad = propias.OrderByDescending(e => e.ValidadaEnUtc).ThenBy(e => e.Id).First();
                identidades[(porTrabajador.Key, contrato.Id)] = identidad;
                var fila = Resumir(porTrabajador.Key, identidad, contrato, evaluadas, hoy);

                // Decisión del 2026-11-17 (P3, punto 4 y ronda 7): con varios contratos en el Centro, el Trabajador toma el estado
                // menos favorable de todos. Con el mismo estado gana la fila cuyo estado procede de las evidencias propias de su
                // contrato (no de un «No apto» ajeno); si no hay ninguna así, o hay varias, se queda la primera (la más reciente).
                if (!filas.TryGetValue(porTrabajador.Key, out var previa)
                    || fila.Dto.Estado > previa.Dto.Estado
                    || (fila.Dto.Estado == previa.Dto.Estado && previa.ContratoOrigenAjenoId is not null && fila.ContratoOrigenAjenoId is null))
                    filas[porTrabajador.Key] = fila;
                if (!empresasPorTrabajador.TryGetValue(porTrabajador.Key, out var empresas))
                    empresasPorTrabajador[porTrabajador.Key] = empresas = [];
                if (!empresas.Contains(contrato.EmpresaContratistaId))
                    empresas.Add(contrato.EmpresaContratistaId);
                masRecientePorTrabajador.TryAdd(porTrabajador.Key, (contrato, identidad));
            }
        }

        var trabajadores = filas
            .Select(par => new AptitudTrabajadorEvaluada(
                par.Value.Dto with { EmpresasContratistaIds = empresasPorTrabajador[par.Key] },
                par.Value.ContratoOrigenAjenoId,
                masRecientePorTrabajador[par.Key].Contrato,
                masRecientePorTrabajador[par.Key].Identidad))
            .ToList();
        return new EvaluacionAptitudCentro(trabajadores, contratosPorId, identidades);
    }

    private async Task<IReadOnlyList<RequisitoPlantilla>> RequisitosQueBloqueanAsync(
        Guid centroId, IReadOnlyList<ContratoAcceso> contratos, CancellationToken cancellationToken)
    {
        var clientesEmpresariales = contratos.Select(c => c.ClienteEmpresarialId).Distinct().ToList();
        var plantillaIds = await plantillasContext.PlantillasRequisitos
            .Where(p => p.Activa
                && (p.Tipo == TipoPlantillaRequisitos.DeCentro && p.CentroId == centroId
                    || p.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial
                    && p.ClienteEmpresarialId != null && clientesEmpresariales.Contains(p.ClienteEmpresarialId.Value)))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (plantillaIds.Count == 0)
            return [];

        return await plantillasContext.RequisitosPlantilla
            .Where(r => plantillaIds.Contains(r.PlantillaId) && r.Activo && r.BloqueaAcceso
                && r.Entidad != EntidadRequisito.VehiculoOMaquina)
            .ToListAsync(cancellationToken);
    }

    private static List<Evaluado> Evaluar(
        IReadOnlyList<Exigido> exigidos, IReadOnlyList<EvidenciaInbound> delSujeto, DateOnly hoy, SujetoDelRequisitoInbound sujeto,
        IReadOnlySet<Guid>? ajenas = null) =>
        exigidos
            .Select(x =>
            {
                var delTipo = delSujeto.Where(e => e.TipoDocumentoId == x.Requisito.TipoDocumentoId).ToList();
                // Revela salud el tipo del Tenant TCE o cualquier evidencia que llegó como solo conclusión de aptitud: la marca
                // dice de qué clase es el documento, no qué concluye, así que no delata nada.
                var revelaSalud = x.RevelaSalud || delTipo.Any(e => e.SoloConclusionAptitud);
                // R-6: la tolerancia nunca se aplica a la aptitud médica, tampoco cuando lo dice la marca de la evidencia y no
                // el tipo del Tenant TCE.
                var exigido = revelaSalud ? x with { ToleranciaDias = 0 } : x;
                return new Evaluado(
                    x.Nombre, sujeto,
                    MejorDe(exigido, delTipo.Where(EvidenciasInboundProyeccion.EstaAceptada).ToList(), ajenas ?? new HashSet<Guid>(), hoy),
                    revelaSalud);
            })
            .ToList();

    private static FilaInterna Resumir(
        Guid trabajadorId, EvidenciaInbound identidad, ContratoAcceso contrato, IReadOnlyList<Evaluado> evaluadas, DateOnly hoy)
    {
        // Las evaluaciones de la Empresa contratista van primero en la lista: lo que deja fuera a todos (D3) se cita antes que
        // lo propio del Trabajador.
        var peor = evaluadas.Where(e => e.Resultado.Estado == EstadoAptitudInbound.NoApto).Select(e => (Evaluado?)e).FirstOrDefault()
                   ?? evaluadas.Where(e => e.Resultado.Estado == EstadoAptitudInbound.EnTolerancia).Select(e => (Evaluado?)e).FirstOrDefault();

        var estado = peor?.Resultado.Estado ?? EstadoAptitudInbound.Apto;
        var proximo = evaluadas
            .Select(e => e.Resultado.Vencimiento)
            .Where(v => v is { } dia && dia >= hoy)
            .Min();

        // Art. 9 RGPD: en un requisito que revela salud, sin documento, vencido, sin fecha o «No apto» salen igual (una sola
        // causa y sin fecha), para que la causa no delate la conclusión médica.
        var ocultaCausa = estado == EstadoAptitudInbound.NoApto && peor!.RevelaSalud;
        var causa = estado != EstadoAptitudInbound.NoApto ? null
            : ocultaCausa ? CausaNoAptitudInbound.NoAcreditado : peor!.Resultado.Causa;
        var fecha = peor is null ? proximo : ocultaCausa ? null : peor.Resultado.Vencimiento;

        // El estado de la fila depende de un «No apto» ajeno solo si todo lo que lo fija lo cumple (si otro requisito, o la
        // Empresa contratista, ya lo deja en ese estado por lo propio del contrato, procede de lo propio).
        var origenAjeno = peor is not null && evaluadas.Where(e => e.Resultado.Estado == estado).All(e => e.Resultado.ContratoAjenoId is not null)
            ? peor.Resultado.ContratoAjenoId
            : null;

        return new FilaInterna(
            new AptoHoyDto(
                trabajadorId, identidad.SujetoNombre, identidad.SujetoDocumentoIdentidad, contrato.Id, contrato.EmpresaContratistaId,
                contrato.Tipo, estado,
                peor?.Nombre, peor?.Sujeto, causa, fecha),
            origenAjeno);
    }

    /// <summary>
    /// El mejor resultado de un requisito con las evidencias Aceptadas del sujeto, propias y «No apto» ajenos (<paramref name="ajenas"/>,
    /// los ids de estos últimos). Si los ajenos cambian el estado respecto de evaluar solo lo propio, el resultado lleva el contrato
    /// del «No apto» ajeno que lo provoca: el que bloquea y, si no hay, el de la emisión más reciente (el que fija el corte).
    /// </summary>
    private static Resultado MejorDe(
        Exigido exigido, IReadOnlyList<EvidenciaInbound> candidatas, IReadOnlySet<Guid> ajenas, DateOnly hoy)
    {
        var conAjenas = Calcular(exigido, candidatas, hoy);
        var delTipoAjenas = candidatas.Where(e => ajenas.Contains(e.Id)).ToList();
        if (delTipoAjenas.Count == 0)
            return conAjenas;

        var soloPropias = Calcular(exigido, candidatas.Where(e => !ajenas.Contains(e.Id)).ToList(), hoy);
        if (soloPropias.Estado == conAjenas.Estado)
            return conAjenas;

        // El que bloquea (vigente) y, si ninguno bloquea, el que fija el corte (el de emisión más reciente); a igualdad, el de menor Id.
        var bloqueantes = delTipoAjenas.Where(e => EsNoAptoVigente(exigido, e, hoy)).ToList();
        var entre = bloqueantes.Count > 0 ? bloqueantes : delTipoAjenas;
        var emisionMasReciente = entre.Max(e => e.FechaEmision);
        var provoca = entre.Where(e => e.FechaEmision == emisionMasReciente).OrderBy(e => e.Id).First();
        return conAjenas with { ContratoAjenoId = provoca.ContratoAccesoId };
    }

    private static Resultado Calcular(Exigido exigido, IReadOnlyList<EvidenciaInbound> lista, DateOnly hoy)
    {
        if (lista.Count == 0)
            return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.SinDocumentoValidado, null);

        // Decisión del 2026-11-17 (P3, punto 2): un «No apto» transcrito, Aceptado y vigente bloquea siempre al Trabajador, aunque
        // haya otra evidencia mejor del mismo tipo (un apto anterior o posterior). Si ya caducó, deja de bloquear y compite como
        // cualquier otra evidencia (sin acreditar nada).
        if (lista.Any(e => EsNoAptoVigente(exigido, e, hoy)))
            return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.NoAcreditado, null);

        // Lectura que falla cerrado de la misma regla (decisión del 2026-11-17, P3, revisión, punto 1): con un «No apto»
        // Aceptado, vigente o ya caducado, solo acredita un apto emitido después de él. Un apto anterior, o emitido el mismo día,
        // no revive cuando el «No apto» caduca. La vigencia del «No apto» no cambia (D4, sin tolerancia).
        var noAptos = lista.Where(EsNoApto).ToList();
        if (noAptos.Count > 0)
        {
            var corte = noAptos.Max(e => e.FechaEmision);
            lista = lista.Where(e => EsNoApto(e) || e.FechaEmision > corte).ToList();
        }

        var resultados = lista.Select(e => Valorar(exigido, e, hoy)).ToList();

        return resultados
            .OrderBy(r => Rango(r))
            .ThenByDescending(r => r.Vencimiento ?? DateOnly.MaxValue)
            .First();
    }

    private static int Rango(Resultado r) => r.Estado switch
    {
        EstadoAptitudInbound.Apto => 0,
        EstadoAptitudInbound.EnTolerancia => 1,
        _ => r.Causa switch
        {
            CausaNoAptitudInbound.Vencido => 2,
            CausaNoAptitudInbound.SinFechaDeVencimiento => 3,
            _ => 4,
        },
    };

    private static Resultado Valorar(Exigido exigido, EvidenciaInbound evidencia, DateOnly hoy)
    {
        // Una conclusión «No apto» transcrita no acredita nada, aunque la evidencia esté Aceptada y en plazo: solo se sabe
        // que no acredita, no por qué.
        if (evidencia.SoloConclusionAptitud && evidencia.ConclusionAptitud == ConclusionAptitud.NoApto)
            return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.NoAcreditado, null);

        var requisito = exigido.Requisito;
        var vencimiento = VencimientoDe(requisito, evidencia);
        if (vencimiento is not { } dia)
        {
            return requisito.ModoVigencia == ModoVigenciaRequisito.SinVencimiento
                ? new Resultado(EstadoAptitudInbound.Apto, null, null)
                : new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.SinFechaDeVencimiento, null);
        }

        if (hoy.DayNumber <= dia.DayNumber)
            return new Resultado(EstadoAptitudInbound.Apto, null, dia);
        if (hoy.DayNumber <= dia.DayNumber + exigido.ToleranciaDias)
            return new Resultado(EstadoAptitudInbound.EnTolerancia, null, dia);
        return new Resultado(EstadoAptitudInbound.NoApto, CausaNoAptitudInbound.Vencido, dia);
    }

    /// <summary>
    /// El vencimiento de la evidencia para el requisito (D4, sin cambios): con «Fija N meses», la fecha más temprana entre la
    /// emisión más N meses y la del documento; con «Sin vencimiento» o «Lo pone el documento», la del documento si la trae.
    /// </summary>
    private static DateOnly? VencimientoDe(RequisitoPlantilla requisito, EvidenciaInbound evidencia) =>
        requisito.ModoVigencia switch
        {
            ModoVigenciaRequisito.Fija => MasTemprana(
                requisito.VigenciaMeses is { } meses ? evidencia.FechaEmision.AddMonths(meses) : null, evidencia.FechaVencimiento),
            _ => evidencia.FechaVencimiento,
        };

    /// <summary>
    /// Una conclusión «No apto» transcrita sigue vigente hasta su vencimiento, sin tolerancia (R-6). Sin fecha que la caduque
    /// (el requisito no la fija y el documento no la trae) se da por vigente: falla cerrado.
    /// </summary>
    private static bool EsNoAptoVigente(Exigido exigido, EvidenciaInbound evidencia, DateOnly hoy) =>
        EsNoApto(evidencia)
        && (VencimientoDe(exigido.Requisito, evidencia) is not { } dia || hoy.DayNumber <= dia.DayNumber);

    private static bool EsNoApto(EvidenciaInbound evidencia) =>
        evidencia.SoloConclusionAptitud && evidencia.ConclusionAptitud == ConclusionAptitud.NoApto;

    private static DateOnly? MasTemprana(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private sealed record Exigido(RequisitoPlantilla Requisito, string Nombre, int ToleranciaDias, bool RevelaSalud);

    private sealed record Resultado(
        EstadoAptitudInbound Estado, CausaNoAptitudInbound? Causa, DateOnly? Vencimiento, Guid? ContratoAjenoId = null);

    /// <summary>
    /// La fila de un contrato, antes de escoger la del Trabajador. <paramref name="ContratoOrigenAjenoId"/> (dato privado de la
    /// query, no sale en el DTO) es el contrato cuyo «No apto» ajeno provoca el estado de la fila; nulo si procede de lo propio.
    /// </summary>
    private sealed record FilaInterna(AptoHoyDto Dto, Guid? ContratoOrigenAjenoId);

    private sealed record Evaluado(string Nombre, SujetoDelRequisitoInbound Sujeto, Resultado Resultado, bool RevelaSalud);
}
