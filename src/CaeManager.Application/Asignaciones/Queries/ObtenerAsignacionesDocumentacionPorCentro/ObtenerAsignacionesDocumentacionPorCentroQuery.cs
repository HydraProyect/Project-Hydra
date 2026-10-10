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
public record ObtenerAsignacionesDocumentacionPorCentroQuery(Guid CentroId, DateOnly? VentanaVisitaFechaFin = null)
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

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where asignacion.CentroId == request.CentroId && asignacion.FechaBaja == null
            orderby trabajador.Apellidos, trabajador.Nombre
            select new
            {
                AsignacionId = asignacion.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                asignacion.FechaAlta
            })
            .ToListAsync(cancellationToken);

        if (asignaciones.Count == 0)
            return [];

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        // Tipos requeridos por ESTE centro — ver ResolucionTipoDocumentoCentro:
        // fila explícita manda, sin fila sigue el criterio global EsObligatorio.
        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();
        var filasDelCentro = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && tc.CentroId == request.CentroId)
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var tiposRequeridosPorCentro = tiposCandidatos
            .Where(t => ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, request.CentroId, t.CuentaParaCumplimiento))
            .Select(t => new { t.Id, t.Nombre })
            .ToList();

        // P1-X2: un Centro sin gestión CAE no exige ningún tipo; sus requisitos
        // se conservan y vuelven a contar si se restaura la gestión.
        var centroSinGestionCae = (await CentrosSinGestionCae.FiltrarAsync(
            centrosContext, [request.CentroId], cancellationToken)).Count > 0;

        if (centroSinGestionCae || tiposRequeridosPorCentro.Count == 0)
        {
            return asignaciones
                .Select(a => new TrabajadorAsignacionDocumentacionDto(
                    a.AsignacionId, a.TrabajadorId, a.TrabajadorNombre, a.FechaAlta, EstadoDocumento.Vigente, [],
                    FraccionCumplimiento.SinRequisitos))
                .ToList();
        }

        var tipoIdsRequeridos = tiposRequeridosPorCentro.Select(t => t.Id).ToHashSet();
        var trabajadorIds = asignaciones.Select(a => a.TrabajadorId).Distinct().ToList();

        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsRequeridos.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // «En tolerancia» es un estado de contexto: la tolerancia que rige en ESTE Centro (la suya, o la de su Cliente
        // empresarial titular) decide si un vencido todavía vale para acceder. El estado de vigencia del Documento no cambia.
        var clienteEmpresarialId = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => c.ClienteId)
            .SingleAsync(cancellationToken);
        var toleranciasDelCliente = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, [clienteEmpresarialId], tipoIdsRequeridos, cancellationToken);

        // El documento que representa a cada par Trabajador×Tipo: el mismo que usa el % del Centro.
        var preferidosPorPar = DocumentoEfectivo.UnoPorClave(
            documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);

        var documentosPorTrabajador = documentosExistentes
            .GroupBy(d => d.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Segunda línea de cada documento: estado en las plataformas de ESTE Centro y última reclamación. Una carga
        // para todo el Centro, no una por Trabajador ni por documento.
        var situacion = await situacionDocumentos.CargarAsync(
            [request.CentroId],
            documentosExistentes.Select(d => d.Id).ToList(),
            trabajadorIds,
            tipoIdsRequeridos,
            cancellationToken);

        var resultado = new List<TrabajadorAsignacionDocumentacionDto>();

        foreach (var asignacion in asignaciones)
        {
            var documentosDelTrabajador = documentosPorTrabajador.GetValueOrDefault(asignacion.TrabajadorId, []);
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
                    && request.VentanaVisitaFechaFin is { } finVisita
                    && CalculadoraEstadoDocumento.Calcular(documento.EstadoVigencia, documento.FechaVencimiento, finVisita, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                        is not (EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad);

                filasDelCentro.TryGetValue((documento.TipoDocumentoId, request.CentroId), out var filaDelCentro);
                var condiciones = VigenciaEnCentro.Condiciones(
                    filaDelCentro,
                    toleranciasDelCliente.TryGetValue((clienteEmpresarialId, documento.TipoDocumentoId), out var delCliente) ? delCliente : null);
                var (estadoEnCentro, enToleranciaHasta) = VigenciaEnCentro.Aplicar(
                    estado, documento.EstadoVigencia, documento.FechaVencimiento, documento.FechaEmision, condiciones, hoy);

                var nombreTipo = tiposRequeridosPorCentro.First(t => t.Id == documento.TipoDocumentoId).Nombre;
                items.Add(new DocumentoRequeridoDto(
                    documento.Id, documento.TipoDocumentoId, nombreTipo, estadoEnCentro, documento.FechaVencimiento, caducaEnVentana, enToleranciaHasta,
                    situacion.AcreditacionesEn(request.CentroId, documento.Id),
                    situacion.UltimaReclamacionDe(documento.Id)));
            }

            foreach (var tipo in tiposRequeridosPorCentro)
            {
                if (tipoIdsConDocumento.Contains(tipo.Id)) continue;
                items.Add(new DocumentoRequeridoDto(
                    null, tipo.Id, tipo.Nombre, EstadoDocumento.Faltante, null,
                    UltimaReclamacion: situacion.UltimaReclamacionDeAusente(asignacion.TrabajadorId, tipo.Id)));
            }

            var ordenados = items.OrderBy(i => SeveridadEstadoDocumento.Rango(i.Estado)).ThenBy(i => i.TipoDocumentoNombre).ToList();
            var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

            var cumplimiento = CumplimientoDocumental.Evaluar(tiposRequeridosPorCentro.Select(tipo =>
                preferidosPorPar.TryGetValue((asignacion.TrabajadorId, tipo.Id), out var preferido)
                    ? CalculadoraEstadoDocumento.Calcular(
                        preferido.EstadoVigencia, preferido.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                    : EstadoDocumento.Faltante));

            resultado.Add(new TrabajadorAsignacionDocumentacionDto(
                asignacion.AsignacionId, asignacion.TrabajadorId, asignacion.TrabajadorNombre, asignacion.FechaAlta,
                peorEstado, ordenados, cumplimiento));
        }

        return resultado;
    }
}
