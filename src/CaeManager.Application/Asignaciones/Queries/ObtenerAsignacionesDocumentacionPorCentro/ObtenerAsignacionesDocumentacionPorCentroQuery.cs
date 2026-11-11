using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;

/// <summary>
/// Alimenta el acordeón de asignaciones de Centro 360 (<c>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md</c>
/// § 0.1/0.2/0.4): trabajadores con Asignación activa en un Centro, con el
/// estado de la documentación que ESE centro exige de cada uno — ver
/// <see cref="Documentos.ResolucionTipoDocumentoCentro"/> para la semántica
/// compartida con <see cref="Alertas.IDocumentosFaltantesService"/> y
/// <c>ObtenerDocumentacionVisitaQuery</c>. Se calcula de una sola vez para
/// todos los trabajadores del centro al expandir la fila — el tercer nivel
/// (documentos de un trabajador concreto) no vuelve a consultar el servidor,
/// ya tiene el desglose completo en memoria.
///
/// Las filas sin <c>Documento</c> (<see cref="DocumentoRequeridoDto.DocumentoId"/>
/// <c>null</c>) se rotulan "Falta" en la UI — un requisito recién
/// añadido al Centro, no necesariamente un problema urgente; se mantienen
/// como <see cref="EstadoDocumento.Faltante"/> a nivel de dominio/alertas
/// (mismo cálculo que el resto de la app), solo cambia cómo se pinta.
/// </summary>
/// <param name="VentanaVisitaFechaFin">
/// <c>FechaFin</c> de la próxima visita activa del centro, si tiene una
/// (§ 0.3) — permite marcar <see cref="DocumentoRequeridoDto.CaducaEnVentanaVisita"/>
/// sin recalcular el estado del documento: sigue siendo <c>Vigente</c> con
/// fecha de referencia = hoy, solo se compara además contra esta fecha.
/// </param>
public record ObtenerAsignacionesDocumentacionPorCentroQuery(Guid CentroId, DateOnly? VentanaVisitaFechaFin = null, Guid? EmpresaEmpleadoraSubcontratistaId = null)
    : IRequest<IReadOnlyList<TrabajadorAsignacionDocumentacionDto>>;

/// <param name="CaducaEnVentanaVisita">
/// Solo relevante cuando <see cref="EstadoDocumento.Vigente"/>: el documento
/// caduca antes de que termine la próxima visita del centro (comparando
/// <see cref="CalculadoraEstadoDocumento"/> con fecha de referencia = fin de
/// la visita en vez de hoy) — modificador visual "vigente con riesgo en
/// ventana", no un estado nuevo (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.3).
/// </param>
/// <param name="EnToleranciaHasta">
/// Solo con <see cref="EstadoDocumento.EnTolerancia"/> (vista con contexto de Centro, <see cref="VigenciaEnCentro"/>): el
/// último día en que el Documento vencido aún vale para acceder a ese Centro. <c>null</c> en cualquier otro estado.
/// </param>
/// <param name="AcreditacionesEnElCentro">
/// Estado del documento en las plataformas de ESE Centro (documento × canal de plataforma del Centro), el canal
/// principal primero. <c>null</c> cuando no hay ninguna o cuando la vista no fija un Centro: sin Centro no se resume
/// («validado en 3 de 4 centros» está sin decidir), así que quien agrupa por Trabajador sin Centro lo deja vacío.
/// </param>
/// <param name="UltimaReclamacion">
/// La última vez que se reclamó este documento —o, si falta, este tipo a este Trabajador—, entre las reclamaciones
/// que el usuario puede gestionar. <c>null</c> si nunca se reclamó.
/// </param>
public record DocumentoRequeridoDto(
    Guid? DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre, EstadoDocumento Estado,
    DateOnly? FechaVencimiento, bool CaducaEnVentanaVisita = false, DateOnly? EnToleranciaHasta = null,
    IReadOnlyList<AcreditacionResumenDto>? AcreditacionesEnElCentro = null,
    UltimaReclamacionDocumentoDto? UltimaReclamacion = null);

/// <param name="Documentos">
/// Lo que se enseña al expandir al Trabajador: no lista «Sin caducidad» y, si hay un documento vencido y su
/// renovación del mismo tipo, enseña los dos. NO sirve para contar el cumplimiento: para eso está
/// <paramref name="Cumplimiento"/>.
/// </param>
/// <param name="Cumplimiento">
/// Los pares exigidos de este Trabajador en este Centro medidos por <see cref="CumplimientoDocumental"/> (un documento
/// por tipo, el efectivo): la porción de este Trabajador del % del Centro. <see cref="FraccionCumplimiento.SinRequisitos"/>
/// si el Centro no exige nada.
/// </param>
public record TrabajadorAsignacionDocumentacionDto(
    Guid AsignacionId, Guid TrabajadorId, string TrabajadorNombre, DateOnly FechaAlta,
    EstadoDocumento PeorEstado, IReadOnlyList<DocumentoRequeridoDto> Documentos,
    FraccionCumplimiento Cumplimiento);

/// <summary>
/// El mismo cálculo que <see cref="ObtenerAsignacionesDocumentacionPorCentroQuery"/> para varios Centros a la vez, solo con
/// lo que suma el cumplimiento por Centro de una Empresa empleadora (Subcontrata 360): una carga para todos los Centros en
/// vez de una tanda de consultas por Centro. Aplica el mismo alcance: la Empresa empleadora tiene que ser visible y cada
/// Centro que no lo es se omite (no aparece en el resultado). Sin la segunda línea de cada documento (plataformas y
/// reclamaciones), que no cuenta para el cumplimiento ni para el peor estado.
/// </summary>
public record ObtenerCumplimientoDeAsignacionesPorCentrosQuery(IReadOnlyCollection<Guid> CentroIds, Guid EmpresaEmpleadoraSubcontratistaId)
    : IRequest<IReadOnlyDictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>>;

/// <summary>De <see cref="TrabajadorAsignacionDocumentacionDto"/>, lo que pesa en el cumplimiento del Centro.</summary>
public record CumplimientoDeAsignacionDto(Guid TrabajadorId, EstadoDocumento PeorEstado, FraccionCumplimiento Cumplimiento);

public class ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    ISituacionDocumentosEnCentrosService situacionDocumentos)
    : IRequestHandler<ObtenerAsignacionesDocumentacionPorCentroQuery, IReadOnlyList<TrabajadorAsignacionDocumentacionDto>>
{
    public async Task<IReadOnlyList<TrabajadorAsignacionDocumentacionDto>> Handle(
        ObtenerAsignacionesDocumentacionPorCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];
        // El parámetro solo acota una lectura: la columna legacy SubcontrataId identifica a la Empresa
        // empleadora en una Relación de subcontratación, no un tipo permanente de Empresa.
        if (request.EmpresaEmpleadoraSubcontratistaId is { } empresaId && !await alcanceDatos.SubcontrataVisibleAsync(empresaId, cancellationToken))
            return [];

        var porCentro = await CalcularAsync(
            [request.CentroId], request.VentanaVisitaFechaFin, request.EmpresaEmpleadoraSubcontratistaId, conSegundaLinea: true, cancellationToken);
        return porCentro.GetValueOrDefault(request.CentroId) ?? [];
    }

    /// <summary>
    /// El cálculo de los Centros pedidos (ya autorizados por quien llama), con una carga para todos: cada Centro conserva sus
    /// propios tipos exigidos, filas de TipoDocumentoCentro, Cliente empresarial y tolerancias, y sus Trabajadores en el orden
    /// de siempre. Un Centro sin ninguna Asignación activa no aparece. <paramref name="conSegundaLinea"/> en falso no carga
    /// plataformas ni reclamaciones: esas columnas quedan vacías y solo puede usarse para el cumplimiento y el peor estado.
    /// </summary>
    internal async Task<Dictionary<Guid, List<TrabajadorAsignacionDocumentacionDto>>> CalcularAsync(
        IReadOnlyCollection<Guid> centroIdsPedidos, DateOnly? ventanaVisitaFechaFin, Guid? empresaEmpleadoraSubcontratistaId,
        bool conSegundaLinea, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, List<TrabajadorAsignacionDocumentacionDto>>();
        var centroIdsDeLaConsulta = centroIdsPedidos.Distinct().ToList();

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where centroIdsDeLaConsulta.Contains(asignacion.CentroId) && asignacion.FechaBaja == null
                && (empresaEmpleadoraSubcontratistaId == null || trabajador.SubcontrataId == empresaEmpleadoraSubcontratistaId)
            orderby trabajador.Apellidos, trabajador.Nombre
            select new
            {
                asignacion.CentroId,
                AsignacionId = asignacion.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                trabajador.TipoContratacion,
                asignacion.FechaAlta
            })
            .ToListAsync(cancellationToken);

        if (asignaciones.Count == 0)
            return resultado;

        // Por Centro, en el orden de la consulta (apellidos y nombre del Trabajador).
        var asignacionesPorCentro = asignaciones
            .GroupBy(a => a.CentroId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var centroIds = asignacionesPorCentro.Keys.ToList();

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si, t.TiposContratacionDeSerie })
            .ToListAsync(cancellationToken);

        // Tipos requeridos por CADA centro — ver ResolucionTipoDocumentoCentro:
        // fila explícita manda, sin fila sigue el criterio global CuentaParaCumplimiento.
        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();
        var filasDelCentro = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        //
        // La serie de cada tipo se filtra por el tipo de contratación de cada Trabajador (decisión 2026-10-16): lo que se
        // exige es distinto para un Voluntario, un Autónomo y un Trabajador por cuenta ajena del mismo Centro. El
        // conjunto del Centro es la unión de lo que se exige a alguno de ellos (lo que hay que cargar y tolerar).
        var tiposRequeridosPorContratacion = asignaciones
            .Select(a => (a.CentroId, a.TipoContratacion))
            .Distinct()
            .ToDictionary(
                clave => clave,
                clave => tiposCandidatos
                    .Where(t => ResolucionTipoDocumentoCentro.Aplica(
                        filasDelCentro, t.Id, clave.CentroId, t.CuentaParaCumplimiento, t.TiposContratacionDeSerie, clave.TipoContratacion))
                    .Select(t => new { t.Id, t.Nombre })
                    .ToList());
        var tiposRequeridosPorCentro = tiposRequeridosPorContratacion
            .GroupBy(par => par.Key.CentroId)
            .ToDictionary(g => g.Key, g => g.SelectMany(par => par.Value).DistinctBy(t => t.Id).ToList());

        // P1-X2: un Centro sin gestión CAE no exige ningún tipo; sus requisitos
        // se conservan y vuelven a contar si se restaura la gestión.
        var centrosSinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centroIds, cancellationToken);

        var centrosConRequisitos = new List<Guid>();
        foreach (var centroId in centroIds)
        {
            if (centrosSinGestionCae.Contains(centroId) || tiposRequeridosPorCentro[centroId].Count == 0)
            {
                resultado[centroId] = asignacionesPorCentro[centroId]
                    .Select(a => new TrabajadorAsignacionDocumentacionDto(
                        a.AsignacionId, a.TrabajadorId, a.TrabajadorNombre, a.FechaAlta, EstadoDocumento.Vigente, [],
                        FraccionCumplimiento.SinRequisitos))
                    .ToList();
            }
            else
            {
                centrosConRequisitos.Add(centroId);
            }
        }

        if (centrosConRequisitos.Count == 0)
            return resultado;

        // La unión de lo que exige cada Centro: cada par se vuelve a acotar más abajo a los tipos de su Centro y Trabajador.
        var tipoIdsRequeridos = centrosConRequisitos.SelectMany(c => tiposRequeridosPorCentro[c]).Select(t => t.Id).ToHashSet();
        var trabajadorIds = centrosConRequisitos.SelectMany(c => asignacionesPorCentro[c]).Select(a => a.TrabajadorId).Distinct().ToList();

        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsRequeridos.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc, d.EsRenuncia })
            .ToListAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // «En tolerancia» es un estado de contexto: la tolerancia que rige en ESTE Centro (la suya, o la de su Cliente
        // empresarial titular) decide si un vencido todavía vale para acceder. El estado de vigencia del Documento no cambia.
        var clientePorCentro = (await centrosContext.Centros
                .Where(c => centrosConRequisitos.Contains(c.Id))
                .Select(c => new { c.Id, Titular = c.ClienteId })
                .ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.Titular);
        // Como el SingleAsync de un solo Centro: un Centro pedido que no se puede leer es un error, no un Centro sin Cliente.
        if (centrosConRequisitos.FirstOrDefault(c => !clientePorCentro.ContainsKey(c)) is var sinFila && sinFila != Guid.Empty)
            throw new InvalidOperationException($"El Centro {sinFila} no se puede leer.");
        var toleranciasDelCliente = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, clientePorCentro.Values.Distinct().ToList(), tipoIdsRequeridos, cancellationToken);

        // El documento que representa a cada par Trabajador×Tipo: el mismo que usa el % del Centro.
        var preferidosPorPar = DocumentoEfectivo.UnoPorClave(
            documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, d => d.EsRenuncia, hoy);

        var documentosPorTrabajador = documentosExistentes
            .GroupBy(d => d.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Segunda línea de cada documento: estado en las plataformas de cada Centro y última reclamación. Una carga
        // para todos los Centros, no una por Trabajador ni por documento.
        var situacion = conSegundaLinea
            ? await situacionDocumentos.CargarAsync(
                centrosConRequisitos,
                documentosExistentes.Select(d => d.Id).ToList(),
                trabajadorIds,
                tipoIdsRequeridos,
                cancellationToken)
            : SituacionDocumentosEnCentros.Vacia;

        // Pendiente en la plataforma (decisión 2026-10-10): la misma carga que el % del Centro (PendientesEnPlataformaDeCentros);
        // solo baja el cumplimiento el de un tipo que el Centro marca «bloquea el acceso», en ESE Centro.
        var pendientesQueBajanElPorcentaje = (await PendientesEnPlataformaDeCentros.CargarAsync(
                centrosContext, documentosContext, tiposDocumentoContext, trabajadoresContext, asignacionesContext.Asignaciones,
                centrosConRequisitos, hoy, cancellationToken))
            .Where(p => p.TrabajadorId is not null && p.BloqueaAcceso)
            .Select(p => (p.CentroId, TrabajadorId: p.TrabajadorId!.Value, p.TipoDocumentoId))
            .ToHashSet();

        foreach (var centroId in centrosConRequisitos)
        {
            var clienteEmpresarialId = clientePorCentro[centroId];
            var tiposDelCentro = tiposRequeridosPorCentro[centroId];
            var delCentro = new List<TrabajadorAsignacionDocumentacionDto>();

            foreach (var asignacion in asignacionesPorCentro[centroId])
            {
                var tiposRequeridosDelTrabajador = tiposRequeridosPorContratacion[(centroId, asignacion.TipoContratacion)];
                var tipoIdsRequeridosDelTrabajador = tiposRequeridosDelTrabajador.Select(t => t.Id).ToHashSet();
                var documentosDelTrabajador = documentosPorTrabajador
                    .GetValueOrDefault(asignacion.TrabajadorId, [])
                    .Where(d => tipoIdsRequeridosDelTrabajador.Contains(d.TipoDocumentoId))
                    .ToList();
                var tipoIdsConDocumento = documentosDelTrabajador.Select(d => d.TipoDocumentoId).ToHashSet();

                var items = new List<DocumentoRequeridoDto>();

                foreach (var documento in documentosDelTrabajador)
                {
                    var estado = CalculadoraEstadoDocumento.Calcular(
                        documento.EstadoVigencia, documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                    // Solo se omite lo confirmado como que no caduca; un documento
                    // sin vigencia confirmada sí se lista, porque no está en regla.
                    if (estado == EstadoDocumento.SinCaducidad) continue;

                    // Solo tiene sentido avisar de un documento que hoy está bien
                    // pero no aguantará hasta el final de la visita — uno que ya
                    // está en Próximo/Urgente/Vencido no necesita un modificador
                    // aparte, ya se ve en su propio badge.
                    var caducaEnVentana = estado == EstadoDocumento.Vigente
                        && ventanaVisitaFechaFin is { } finVisita
                        && CalculadoraEstadoDocumento.Calcular(documento.EstadoVigencia, documento.FechaVencimiento, finVisita, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                            is not (EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad);

                    filasDelCentro.TryGetValue((documento.TipoDocumentoId, centroId), out var filaDelCentro);
                    var condiciones = VigenciaEnCentro.Condiciones(
                        filaDelCentro,
                        toleranciasDelCliente.TryGetValue((clienteEmpresarialId, documento.TipoDocumentoId), out var delCliente) ? delCliente : null);
                    var (estadoEnCentro, enToleranciaHasta) = VigenciaEnCentro.Aplicar(
                        estado, documento.EstadoVigencia, documento.FechaVencimiento, documento.FechaEmision, condiciones, hoy);

                    var nombreTipo = tiposDelCentro.First(t => t.Id == documento.TipoDocumentoId).Nombre;
                    items.Add(new DocumentoRequeridoDto(
                        documento.Id, documento.TipoDocumentoId, nombreTipo, estadoEnCentro, documento.FechaVencimiento, caducaEnVentana, enToleranciaHasta,
                        situacion.AcreditacionesEn(centroId, documento.Id),
                        situacion.UltimaReclamacionDe(documento.Id)));
                }

                foreach (var tipo in tiposRequeridosDelTrabajador)
                {
                    if (tipoIdsConDocumento.Contains(tipo.Id)) continue;
                    items.Add(new DocumentoRequeridoDto(
                        null, tipo.Id, tipo.Nombre, EstadoDocumento.Faltante, null,
                        UltimaReclamacion: situacion.UltimaReclamacionDeAusente(asignacion.TrabajadorId, tipo.Id)));
                }

                var ordenados = items.OrderBy(i => SeveridadEstadoDocumento.Rango(i.Estado)).ThenBy(i => i.TipoDocumentoNombre).ToList();
                var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

                // Los mismos pares y la misma regla que el % del Centro (CalcularCumplimientoAsync): un Pendiente en la
                // plataforma de un tipo que el Centro marca «bloquea el acceso» no cuenta como al día en este contexto.
                var cumplimiento = CumplimientoDocumental.Evaluar(
                    tiposRequeridosDelTrabajador.Select(tipo => new ParDocumentalExigido(
                        centroId, clienteEmpresarialId, EmpresaId: null, asignacion.TrabajadorId, tipo.Id,
                        preferidosPorPar.TryGetValue((asignacion.TrabajadorId, tipo.Id), out var preferido)
                            ? CalculadoraEstadoDocumento.Calcular(
                                preferido.EstadoVigencia, preferido.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                            : EstadoDocumento.Faltante,
                        pendientesQueBajanElPorcentaje.Contains((centroId, asignacion.TrabajadorId, tipo.Id)),
                        preferido is { EsRenuncia: true })),
                    ContextoCumplimiento.Centro);

                delCentro.Add(new TrabajadorAsignacionDocumentacionDto(
                    asignacion.AsignacionId, asignacion.TrabajadorId, asignacion.TrabajadorNombre, asignacion.FechaAlta,
                    peorEstado, ordenados, cumplimiento));
            }

            resultado[centroId] = delCentro;
        }

        return resultado;
    }
}

/// <summary>
/// El lote de <see cref="ObtenerCumplimientoDeAsignacionesPorCentrosQuery"/>: el cálculo es el de
/// <see cref="ObtenerAsignacionesDocumentacionPorCentroQueryHandler"/> (el mismo método), y aquí solo el alcance y la proyección.
/// </summary>
public class ObtenerCumplimientoDeAsignacionesPorCentrosQueryHandler(
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    ISituacionDocumentosEnCentrosService situacionDocumentos)
    : IRequestHandler<ObtenerCumplimientoDeAsignacionesPorCentrosQuery, IReadOnlyDictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>>
{
    private readonly ObtenerAsignacionesDocumentacionPorCentroQueryHandler calculo = new(
        asignacionesContext, trabajadoresContext, tiposDocumentoContext, documentosContext, configuracionContext, centrosContext,
        alcanceDatos, situacionDocumentos);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>> Handle(
        ObtenerCumplimientoDeAsignacionesPorCentrosQuery request, CancellationToken cancellationToken)
    {
        // El mismo alcance que la consulta de un Centro, Centro a Centro: la Empresa empleadora visible y solo los Centros
        // visibles (CentroVisibleAsync es esta misma comprobación sobre ObtenerCentroIdsVisiblesAsync).
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.EmpresaEmpleadoraSubcontratistaId, cancellationToken))
            return new Dictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>();
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var centroIds = request.CentroIds
            .Where(id => centroIdsVisibles is null || centroIdsVisibles.Contains(id))
            .Distinct()
            .ToList();
        if (centroIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>();

        var porCentro = await calculo.CalcularAsync(
            centroIds, ventanaVisitaFechaFin: null, request.EmpresaEmpleadoraSubcontratistaId, conSegundaLinea: false, cancellationToken);
        return porCentro.ToDictionary(
            par => par.Key,
            par => (IReadOnlyList<CumplimientoDeAsignacionDto>)par.Value
                .Select(t => new CumplimientoDeAsignacionDto(t.TrabajadorId, t.PeorEstado, t.Cumplimiento))
                .ToList());
    }
}