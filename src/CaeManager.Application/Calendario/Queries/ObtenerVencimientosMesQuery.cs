using CaeManager.Domain.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Calendario.Queries;

/// <summary>
/// Vencimientos de Documento dentro de un mes calendario, para la vista
/// mensual de Calendario (ver Project-Hydra-Negocio/tecnico/ROADMAP.md, Fase 3). Mismo cálculo de estado
/// que Dashboard/Alertas — nunca puede haber dos semáforos distintos para
/// el mismo documento. Solo cubre Documentos de Trabajador — los de
/// Cliente/Empresa no aparecen en el calendario todavía (fuera de alcance).
///
/// <para>
/// Cada vencimiento lleva la Empresa empleadora del Trabajador (<c>SubcontrataId ?? EmpresaId</c>, como
/// <see cref="ObtenerSemanaPorEmpresaQuery"/>) y <see cref="VencimientoCalendarioDto.Reclamable"/>, con LA MISMA regla que la
/// Semana por Empresa: documento de ámbito Trabajador de un Trabajador que no es de Subcontrata ni voluntario inactivo, dentro de
/// <see cref="VentanaReclamacion"/> y con alcance de gestión sobre la Empresa empleadora; y, como el lote que abre el cajón,
/// con una Asignación activa a un Centro visible con gestión CAE (<see cref="AsignacionesReclamables"/>). La Empresa solo se informa (y solo
/// puede ser reclamable) si está en algún alcance de cartera del usuario: ver a un Trabajador no revela a quién emplea. El
/// vencimiento sigue saliendo igual que antes (visibilidad del Trabajador); lo que cambia es que sin esa Empresa en alcance
/// llega sin Empresa y no reclamable. Es una oferta, no una promesa: el envío revalida el alcance de gestión.
/// </para>
/// </summary>
public record ObtenerVencimientosMesQuery(int Anio, int Mes) : IRequest<IReadOnlyList<VencimientoCalendarioDto>>;

/// <param name="EmpresaId">Empresa empleadora (visible para el usuario); null si no está en ningún alcance suyo.</param>
/// <param name="EmpresaNombre">Razón social de <paramref name="EmpresaId"/>.</param>
/// <param name="Reclamable">«Reclamar» puede pedirlo a esa Empresa; nunca true sin <paramref name="EmpresaId"/>.</param>
public record VencimientoCalendarioDto(
    Guid DocumentoId,
    DateOnly FechaVencimiento,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    EstadoDocumento Estado,
    Guid? EmpresaId = null,
    string? EmpresaNombre = null,
    bool Reclamable = false);

public class ObtenerVencimientosMesQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVencimientosMesQuery, IReadOnlyList<VencimientoCalendarioDto>>
{
    public async Task<IReadOnlyList<VencimientoCalendarioDto>> Handle(ObtenerVencimientosMesQuery request, CancellationToken cancellationToken)
    {
        var primerDia = new DateOnly(request.Anio, request.Mes, 1);
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var empresasVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var empresasParaGestion = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy);
        // La condición del lote de reclamación (Asignación activa a un Centro visible con gestión CAE de un Cliente empresarial
        // visible): sin ella el cajón descartaría lo ofrecido y el envío lo rechazaría.
        var asignacionesReclamables = await AsignacionesReclamables.DeAsync(
            asignacionesContext.Asignaciones, centrosContext.Centros, alcanceDatos, cancellationToken);

        var filas = await (
            from documento in documentosContext.Documentos.Operativos()
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where documento.FechaVencimiento != null
                && documento.FechaVencimiento >= primerDia
                && documento.FechaVencimiento <= ultimoDia
            select new
            {
                documento.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                tipoDocumento.Nombre,
                tipoDocumento.AmbitoAplicacion,
                documento.FechaVencimiento,
                // SubcontrataId es deuda terminológica legacy: aquí identifica la Empresa empleadora contraparte.
                EmpresaId = trabajador.SubcontrataId ?? trabajador.EmpresaId,
                // La Semana oculta a los voluntarios inactivos; el Mes los deja ver pero no los ofrece a «Reclamar».
                Inactivo = inactivos.Contains(trabajador.Id),
                ConAsignacionReclamable = asignacionesReclamables.Any(asignacion => asignacion.TrabajadorId == trabajador.Id),
                EsSubcontrata = trabajador.SubcontrataId != null
            })
            .ToListAsync(cancellationToken);

        // La Empresa empleadora tiene que estar en algún alcance del usuario (misma regla que la Semana por Empresa y el
        // contador del palette): la visibilidad del Trabajador no la concede.
        var empresasEnAlcance = new HashSet<Guid>();
        foreach (var empleadora in filas.Where(f => f.EmpresaId != null).Select(f => f.EmpresaId!.Value).Distinct())
        {
            if ((empresasVisibles is null || empresasVisibles.Contains(empleadora))
                || await alcanceDatos.ClienteVisibleAsync(empleadora, cancellationToken)
                || await alcanceDatos.SubcontrataVisibleAsync(empleadora, cancellationToken))
                empresasEnAlcance.Add(empleadora);
        }
        var idsEmpresasEnAlcance = empresasEnAlcance.ToList();
        var nombresEmpresa = idsEmpresasEnAlcance.Count == 0
            ? new Dictionary<Guid, string>()
            : await empresasContext.Empresas.Where(e => idsEmpresasEnAlcance.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        // Misma regla que ObtenerSemanaPorEmpresaQuery.EsReclamable para un documento de Trabajador: ámbito Trabajador,
        // fuera los de Trabajador de Subcontrata (van al Cliente del Centro), dentro de la ventana y con gestión sobre la Empresa;
        // además, la Asignación reclamable del lote, que la Semana deja al cajón.
        bool EsReclamable(AmbitoAplicacion ambito, Guid? empresaId, DateOnly fecha, bool esSubcontrata, bool inactivo, bool conAsignacionReclamable) =>
            empresaId is not null
            && !inactivo
            && conAsignacionReclamable
            && !esSubcontrata
            && ambito == AmbitoAplicacion.Trabajador
            && nombresEmpresa.ContainsKey(empresaId.Value)
            && VentanaReclamacion.EsReclamable(fecha, hoy)
            && (empresasParaGestion is null || empresasParaGestion.Contains(empresaId.Value));

        return filas
            .Select(f =>
            {
                string? nombre = null;
                var empresaVisible = f.EmpresaId is { } empresaId && nombresEmpresa.TryGetValue(empresaId, out nombre);
                return new VencimientoCalendarioDto(
                    f.Id,
                    f.FechaVencimiento!.Value,
                    f.TrabajadorNombre,
                    f.Nombre,
                    CalculadoraEstadoDocumento.Calcular(
                        VigenciaDocumento.VenceEl(f.FechaVencimiento!.Value), hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                    empresaVisible ? f.EmpresaId : null,
                    empresaVisible ? nombre : null,
                    EsReclamable(f.AmbitoAplicacion, f.EmpresaId, f.FechaVencimiento!.Value, f.EsSubcontrata, f.Inactivo, f.ConAsignacionReclamable));
            })
            .OrderBy(v => v.FechaVencimiento)
            .ToList();
    }
}
