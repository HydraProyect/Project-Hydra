using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Calendario.Queries;

/// <summary>
/// Documentos operativos de Empresa y de sus Trabajadores visibles, de lunes a viernes.
/// La propiedad documental y la cartera se comprueban por separado: ver un Trabajador
/// no da acceso a los documentos de su Empresa. No incluye el ámbito Cliente empresarial.
///
/// <para>
/// La fila de los documentos de Trabajador es la Empresa empleadora (<c>SubcontrataId ?? EmpresaId</c>) y solo sale si esa
/// Empresa está en alguno de los alcances de cartera del usuario (Empresa, Cliente empresarial o Subcontrata), como
/// <c>ContarVencenEstaSemanaDeEmpresaQuery</c>: ver a un Trabajador no basta para ver a quién emplea.
/// </para>
///
/// <para>
/// <see cref="DocumentoSemanaDto.Reclamable"/> dice si «Reclamar» puede pedirlo a los contactos de ESA Empresa: solo los
/// documentos de ámbito Empresa, dentro de <see cref="VentanaReclamacion"/>, y con alcance de gestión sobre la Empresa. Los de
/// Trabajador se reclaman al Cliente del Centro donde trabaja cada uno, no a la Empresa.
/// </para>
/// </summary>
public record ObtenerSemanaPorEmpresaQuery(DateOnly Fecha) : IRequest<IReadOnlyList<SemanaEmpresaDto>>;

public record DocumentoSemanaDto(Guid DocumentoId, DateOnly Fecha, bool BloqueaAcceso,
    string TipoDocumentoNombre, string SujetoNombre, bool DeTrabajador, bool Reclamable);
/// <param name="EsSubcontrata">La fila es la Empresa empleadora de Trabajadores de subcontrata (su ficha es <c>/subcontratas/{id}</c>), sin documentos propios de Empresa en la semana.</param>
public record SemanaEmpresaDto(Guid EmpresaId, string EmpresaNombre,
    IReadOnlyList<DocumentoSemanaDto> Documentos, bool ReclamadoHoy, bool EsSubcontrata = false);

public class ObtenerSemanaPorEmpresaQueryHandler(
    IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext, IProyectosQueryContext proyectosContext,
    IReclamacionesQueryContext reclamacionesContext, ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos, IMediator mediator)
    : IRequestHandler<ObtenerSemanaPorEmpresaQuery, IReadOnlyList<SemanaEmpresaDto>>
{
    public async Task<IReadOnlyList<SemanaEmpresaDto>> Handle(ObtenerSemanaPorEmpresaQuery request, CancellationToken cancellationToken)
    {
        var lunes = request.Fecha.AddDays(-((int)request.Fecha.DayOfWeek + 6) % 7);
        var viernes = lunes.AddDays(4);
        var hoy = DiaDeNegocio.Hoy();
        var empresas = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var empresasParaGestion = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var trabajadores = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy);
        var documentos = documentosContext.Documentos.Operativos()
            .Where(d => d.FechaVencimiento >= lunes && d.FechaVencimiento <= viernes);

        var deEmpresa = await (
            from d in documentos
            where d.EmpresaId != null && (empresas == null || empresas.Contains(d.EmpresaId.Value))
            join e in empresasContext.Empresas on d.EmpresaId equals e.Id
            select new { d.Id, EmpresaId = e.Id, e.RazonSocial, d.TrabajadorId, d.TipoDocumentoId, Fecha = d.FechaVencimiento!.Value, EsSubcontrata = false })
            .ToListAsync(cancellationToken);

        var deTrabajador = await (
            from d in documentos
            where d.TrabajadorId != null && (trabajadores == null || trabajadores.Contains(d.TrabajadorId.Value))
            join t in trabajadoresContext.Trabajadores on d.TrabajadorId equals t.Id
            where !inactivos.Contains(t.Id)
            // SubcontrataId es deuda terminológica legacy: aquí identifica la Empresa empleadora contraparte.
            join e in empresasContext.Empresas on (t.SubcontrataId ?? t.EmpresaId) equals e.Id
            select new { d.Id, EmpresaId = e.Id, e.RazonSocial, d.TrabajadorId, d.TipoDocumentoId, Fecha = d.FechaVencimiento!.Value, EsSubcontrata = t.SubcontrataId != null })
            .ToListAsync(cancellationToken);

        // La Empresa empleadora tiene que estar en algún alcance del usuario (misma regla que el contador del palette): la
        // visibilidad del Trabajador no la concede.
        var empresasEnAlcance = new HashSet<Guid>();
        foreach (var empleadora in deTrabajador.Select(f => f.EmpresaId).Distinct())
        {
            if ((empresas is null || empresas.Contains(empleadora))
                || await alcanceDatos.ClienteVisibleAsync(empleadora, cancellationToken)
                || await alcanceDatos.SubcontrataVisibleAsync(empleadora, cancellationToken))
                empresasEnAlcance.Add(empleadora);
        }
        deTrabajador = deTrabajador.Where(f => empresasEnAlcance.Contains(f.EmpresaId)).ToList();

        var filas = deEmpresa.Concat(deTrabajador).ToList();
        if (filas.Count == 0) return [];

        var tipoIds = filas.Select(f => f.TipoDocumentoId).Distinct().ToList();
        var tipos = await tiposContext.TiposDocumento.Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new { t.Nombre, t.AmbitoAplicacion }, cancellationToken);
        var trabajadorIds = filas.Where(f => f.TrabajadorId != null).Select(f => f.TrabajadorId!.Value).Distinct().ToList();
        var nombres = await trabajadoresContext.Trabajadores.Where(t => trabajadorIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre + " " + t.Apellidos, cancellationToken);

        var bloqueos = await mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(), cancellationToken);
        var ids = filas.Select(f => f.Id).ToList();
        var inicio = DiaDeNegocio.InicioEnUtc(hoy);
        var fin = DiaDeNegocio.InicioEnUtc(hoy.AddDays(1));
        var reclamados = await (
            from linea in reclamacionesContext.ReclamacionesDocumentalesDocumento
            join envio in reclamacionesContext.ReclamacionesDocumentales on linea.ReclamacionDocumentalId equals envio.Id
            where linea.DocumentoId != null && ids.Contains(linea.DocumentoId.Value)
                && envio.FechaEnvioUtc >= inicio && envio.FechaEnvioUtc < fin
            select linea.DocumentoId!.Value).Distinct().ToListAsync(cancellationToken);

        // Reclamable desde la fila: documento de la propia Empresa (ámbito Empresa), dentro de la ventana de reclamación y con
        // alcance de gestión sobre ella. El envío (EnviarReclamacionEmpresaCommand) revalida lo mismo; esto evita ofrecer lo que
        // rechazaría. Los de Trabajador no se reclaman desde esta fila sino desde «Gestionar» de cada Trabajador: a la
        // Delegación de su Empresa empleadora (EnviarReclamacionEmpresaEmpleadoraCommand) o, si es de Subcontrata, al Cliente.
        bool EsReclamable(Guid? trabajadorId, Guid tipoDocumentoId, Guid empresaId, DateOnly fecha) =>
            trabajadorId is null
            && tipos.TryGetValue(tipoDocumentoId, out var tipo) && tipo.AmbitoAplicacion == AmbitoAplicacion.Empresa
            && VentanaReclamacion.EsReclamable(fecha, hoy)
            && (empresasParaGestion is null || empresasParaGestion.Contains(empresaId));

        return filas.GroupBy(f => new { f.EmpresaId, f.RazonSocial })
            .OrderBy(g => g.Key.RazonSocial)
            .Select(g =>
            {
                var documentos = g.Select(f => new DocumentoSemanaDto(f.Id, f.Fecha, bloqueos.Any(b =>
                    b.TipoDocumentoId == f.TipoDocumentoId &&
                    (f.TrabajadorId is { } trabajadorId
                        ? b.Ambito == AmbitoAplicacion.Trabajador && b.TrabajadorId == trabajadorId
                        : b.Ambito == AmbitoAplicacion.Empresa && b.EmpresaId == f.EmpresaId)),
                    tipos.TryGetValue(f.TipoDocumentoId, out var tipo) ? tipo.Nombre : string.Empty,
                    f.TrabajadorId is { } id ? nombres.GetValueOrDefault(id, string.Empty) : f.RazonSocial,
                    f.TrabajadorId is not null,
                    EsReclamable(f.TrabajadorId, f.TipoDocumentoId, f.EmpresaId, f.Fecha)))
                    .OrderBy(d => d.Fecha).ThenBy(d => d.DocumentoId).ToList();
                // «Reclamado hoy» habla solo de lo que se puede reclamar desde la fila.
                var reclamables = documentos.Where(d => d.Reclamable).ToList();
                return new SemanaEmpresaDto(g.Key.EmpresaId, g.Key.RazonSocial, documentos,
                    reclamables.Count > 0 && reclamables.All(d => reclamados.Contains(d.DocumentoId)),
                    g.All(f => f.EsSubcontrata));
            })
            .ToList();
    }
}